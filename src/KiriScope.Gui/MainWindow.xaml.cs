using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.Resources;
using KiriScope.Runtime;
using Microsoft.Win32;

namespace KiriScope.Gui;

public partial class MainWindow : Window
{
    private string? selectedResourcePath;
    private ResourceFormat selectedFormat;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OpenResourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a resource for validation",
            Filter = "Resource files|*.png;*.bmp;*.tlg;*.psb;*.pimg;*.ogg;*.wav;*.jpg;*.jpeg|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        selectedResourcePath = dialog.FileName;
        SelectedFileText.Text = selectedResourcePath;
        StatusText.Text = "Validating resource…";
        ReportText.Text = string.Empty;
        PreviewImage.Source = null;
        ConvertBmpButton.IsEnabled = false;
        try
        {
            var report = await VerifyResourceAsync(selectedResourcePath);
            selectedFormat = report.Format;
            ReportText.Text = report.Text;
            StatusText.Text = $"{report.Format} — evidence stage: {report.Stage}";
            ConvertBmpButton.IsEnabled = report.CanConvert;
            await LoadPreviewIfSupportedAsync(selectedResourcePath, report.Format, report.Stage);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            selectedResourcePath = null;
            selectedFormat = ResourceFormat.Unknown;
            StatusText.Text = "The resource could not be read.";
            ReportText.Text = $"Error: {exception.Message}";
        }
    }

    private async void ConvertBmpButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedResourcePath is null || selectedFormat is not (ResourceFormat.Bmp or ResourceFormat.Tlg))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save verified PNG conversion",
            Filter = "PNG image|*.png",
            AddExtension = true,
            DefaultExt = ".png",
            OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(selectedResourcePath) + ".png",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StatusText.Text = "Converting resource to PNG…";
        try
        {
            EvidenceStage stage;
            bool succeeded;
            IReadOnlyList<KiriScopeDiagnostic> diagnostics;
            if (selectedFormat == ResourceFormat.Bmp)
            {
                var result = await BmpPngConverter.ConvertAsync(selectedResourcePath, dialog.FileName);
                stage = result.Stage;
                succeeded = result.Succeeded;
                diagnostics = result.Diagnostics;
            }
            else
            {
                var result = await Tlg5PngConverter.ConvertAsync(selectedResourcePath, dialog.FileName);
                stage = result.Stage;
                succeeded = result.Succeeded;
                diagnostics = result.Diagnostics;
            }

            StatusText.Text = succeeded
                ? $"PNG created and verified: {dialog.FileName}"
                : $"Conversion stopped at evidence stage {stage}.";
            ReportText.Text = FormatDiagnostics(diagnostics);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = "Conversion did not create an output file.";
            ReportText.Text = $"Error: {exception.Message}";
        }
    }

    private void RuntimeConsentCheck_Changed(object sender, RoutedEventArgs e)
    {
        CaptureRuntimeButton.IsEnabled = RuntimeConsentCheck.IsChecked == true;
        RuntimeStatusText.Text = CaptureRuntimeButton.IsEnabled
            ? "Ready to create a new archive through the architecture-matched, read-only worker."
            : "Runtime capture is disabled until authorization is explicitly confirmed.";
    }

    private void InspectRuntimeTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RuntimePidText.Text, out var processId) || processId <= 0)
        {
            RuntimeStatusText.Text = "Enter a positive PID to inspect its architecture without starting a worker.";
            return;
        }

        var inspection = RuntimeArchitectureInspector.Inspect(processId);
        RuntimeStatusText.Text = inspection.Architecture == KiriScope.Worker.Protocol.RuntimeTargetArchitecture.Unknown
            ? $"PID {processId} could not be prepared for runtime observation. No worker was launched."
            : $"PID {processId} is {inspection.Architecture}. A later capture will use a matching worker to read process and module metadata only.";
        ReportText.Text = FormatDiagnostics(inspection.Diagnostics);
    }

    private async void CaptureRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RuntimePidText.Text, out var processId) || processId <= 0 || RuntimeConsentCheck.IsChecked != true)
        {
            RuntimeStatusText.Text = "Enter a positive PID and explicitly confirm authorization before capture.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save runtime evidence archive",
            Filter = "JSON archive|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = false,
            FileName = $"runtime-pid-{processId}.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        CaptureRuntimeButton.IsEnabled = false;
        RuntimeStatusText.Text = $"Capturing PID {processId} through an isolated worker...";
        try
        {
            var capture = await RuntimeWorkerLauncher.CaptureAsync(new RuntimeCaptureLaunchRequest(processId, ExplicitlyEnabled: true));
            var archivePath = await RuntimeResearchArchiveWriter.WriteNewAsync(
                dialog.FileName,
                new RuntimeProcessResearchArchive(
                    RuntimeProcessResearchArchive.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    $"kiriscope analyze runtime snapshot {processId} \"{Path.GetFullPath(dialog.FileName)}\" --enable-runtime-capture",
                    capture));
            var process = capture.Response?.Process;
            RuntimeStatusText.Text = capture.Succeeded
                ? $"Captured PID {processId} ({process?.Architecture}) with {process?.Modules.Count ?? 0} module(s) to {archivePath}."
                : $"Runtime capture stopped; the traceable archive was written to {archivePath}.";
            ReportText.Text = FormatDiagnostics(capture.Diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RuntimeStatusText.Text = "Runtime capture did not overwrite an existing archive.";
            ReportText.Text = $"Runtime error: {exception.Message}";
        }
        finally
        {
            CaptureRuntimeButton.IsEnabled = RuntimeConsentCheck.IsChecked == true;
        }
    }

    private static async Task<ResourceVerificationPresentation> VerifyResourceAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[32];
        var read = await input.ReadAsync(header);
        var format = ResourceFormatDetector.Detect(header.AsSpan(0, read));
        input.Position = 0;
        return format switch
        {
            ResourceFormat.Png => PresentPng(format, await PngValidator.ValidateAsync(input)),
            ResourceFormat.Bmp => PresentBmp(format, await BmpValidator.ValidateAsync(input)),
            ResourceFormat.Wave => PresentWave(format, await WaveValidator.ValidateAsync(input)),
            ResourceFormat.Jpeg => PresentJpeg(format, await JpegValidator.ValidateAsync(input)),
            ResourceFormat.Tlg => PresentTlg(format, await TlgMetadataReader.ReadAsync(input)),
            ResourceFormat.Psb => await PresentPsbAsync(format, input),
            _ => new ResourceVerificationPresentation(format, format == ResourceFormat.Unknown ? EvidenceStage.Unidentified : EvidenceStage.RawDataExtracted,
                format == ResourceFormat.Unknown ? "No supported format signature was detected." : "Format signature detected; a structural validator is not available for this format yet."),
        };
    }

    private static ResourceVerificationPresentation PresentPng(ResourceFormat format, PngValidationResult result) =>
        new(format, result.Stage, $"Width: {result.Width}\nHeight: {result.Height}\nBit depth: {result.BitDepth}\nColor type: {result.ColorType}\nIDAT: {result.IdatCompressedBytes} compressed bytes, {result.IdatDecompressedBytes} decompressed bytes\n\n{FormatDiagnostics(result.Diagnostics)}");

    private static ResourceVerificationPresentation PresentBmp(ResourceFormat format, BmpValidationResult result) =>
        new(format, result.Stage, $"Width: {result.Width}\nHeight: {result.Height}\nBit depth: {result.BitCount}\nCompression: {result.Compression}\nPixel data: offset {result.PixelDataOffset}, length {result.PixelDataLength}\n\n{FormatDiagnostics(result.Diagnostics)}", result.IsValid);

    private static ResourceVerificationPresentation PresentWave(ResourceFormat format, WaveValidationResult result) =>
        new(format, result.Stage, $"Format tag: {result.FormatTag}\nChannels: {result.ChannelCount}\nSample rate: {result.SampleRate}\nBits per sample: {result.BitsPerSample}\nData bytes: {result.DataBytes}\n\n{FormatDiagnostics(result.Diagnostics)}");

    private static ResourceVerificationPresentation PresentJpeg(ResourceFormat format, JpegValidationResult result) =>
        new(format, result.Stage, $"Width: {result.Width}\nHeight: {result.Height}\nPrecision: {result.Precision}\nComponents: {result.ComponentCount}\nScans: {result.ScanCount}\n\n{FormatDiagnostics(result.Diagnostics)}");

    private static ResourceVerificationPresentation PresentTlg(ResourceFormat format, TlgValidationResult result) =>
        new(format, result.Stage, $"Version: {result.Version}\nWidth: {result.Width}\nHeight: {result.Height}\nColor channels: {result.ColorChannels}\nData offset: {result.DataOffset}\nSDS wrapper: {result.HasSdsWrapper}\n\n{FormatDiagnostics(result.Diagnostics)}", result.IsRecognized && result.Version == 5);

    private static async Task<ResourceVerificationPresentation> PresentPsbAsync(ResourceFormat format, Stream input)
    {
        var header = await PsbHeaderReader.ReadAsync(input);
        input.Position = 0;
        var structure = await PsbStructureProbe.ProbeAsync(input);
        var details = new StringBuilder();
        details.AppendLine($"Version: {header.Version}");
        details.AppendLine($"Header may be encrypted: {header.HeaderMayBeEncrypted}");
        details.AppendLine($"PIMG signature: {structure.IsPimgCandidate}");
        details.AppendLine($"Root keys: {string.Join(", ", structure.RootKeys)}");
        foreach (var value in structure.RootUnsignedIntegers)
        {
            details.AppendLine($"Value: {structure.RootKeys[value.RootKeyIndex]} = {value.Value}");
        }
        foreach (var resource in structure.RootResources)
        {
            details.AppendLine($"Resource: {structure.RootKeys[resource.RootKeyIndex]} → index {resource.ResourceIndex}, offset {resource.Offset}, length {resource.Length}");
        }

        details.AppendLine();
        details.Append(FormatDiagnostics(header.Diagnostics.Concat(structure.Diagnostics)));
        return new ResourceVerificationPresentation(format, header.Stage, details.ToString());
    }

    private async Task LoadPreviewIfSupportedAsync(string path, ResourceFormat format, EvidenceStage stage)
    {
        if (format is (ResourceFormat.Tlg or ResourceFormat.Bmp) && stage >= EvidenceStage.ContainerIdentified)
        {
            try
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                RgbaImage? image;
                IReadOnlyList<KiriScopeDiagnostic> diagnostics;
                if (format == ResourceFormat.Tlg)
                {
                    var decoded = await Tlg5Decoder.DecodeAsync(input);
                    image = decoded.Image;
                    diagnostics = decoded.Diagnostics;
                }
                else
                {
                    var decoded = await BmpImageDecoder.DecodeAsync(input);
                    image = decoded.Image;
                    diagnostics = decoded.Diagnostics;
                }

                if (image is null)
                {
                    ReportText.Text += $"{Environment.NewLine}{Environment.NewLine}Preview unavailable: {FormatDiagnostics(diagnostics)}";
                    return;
                }

                var preview = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, ToBgra(image.Pixels), image.Width * 4);
                preview.Freeze();
                PreviewImage.Source = preview;
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                ReportText.Text += $"{Environment.NewLine}{Environment.NewLine}Preview unavailable: {exception.Message}";
                return;
            }
        }

        if (stage < EvidenceStage.FormatValidated || format is not (ResourceFormat.Png or ResourceFormat.Bmp or ResourceFormat.Jpeg))
        {
            return;
        }

        try
        {
            using var input = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();
            PreviewImage.Source = image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            ReportText.Text += $"{Environment.NewLine}{Environment.NewLine}Preview unavailable: {exception.Message}";
        }
    }

    private static string FormatDiagnostics(IEnumerable<KiriScopeDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"{diagnostic.Severity} [{diagnostic.Code}] {diagnostic.Message}"));

    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            bgra[offset] = rgba[offset + 2];
            bgra[offset + 1] = rgba[offset + 1];
            bgra[offset + 2] = rgba[offset];
            bgra[offset + 3] = rgba[offset + 3];
        }

        return bgra;
    }
}

public sealed record ResourceVerificationPresentation(ResourceFormat Format, EvidenceStage Stage, string Text, bool CanConvert = false);
