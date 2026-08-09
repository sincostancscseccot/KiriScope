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
            Title = "选择要验证的资源",
            Filter = "资源文件|*.png;*.bmp;*.tlg;*.psb;*.pimg;*.ogg;*.wav;*.jpg;*.jpeg|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        selectedResourcePath = dialog.FileName;
        SelectedFileText.Text = selectedResourcePath;
        StatusText.Text = "正在验证资源…";
        ReportText.Text = string.Empty;
        PreviewImage.Source = null;
        ConvertBmpButton.IsEnabled = false;
        try
        {
            var report = await VerifyResourceAsync(selectedResourcePath);
            selectedFormat = report.Format;
            ReportText.Text = report.Text;
            StatusText.Text = $"{FormatName(report.Format)} — 证据等级：{EvidenceStageName(report.Stage)}";
            ConvertBmpButton.IsEnabled = report.CanConvert;
            await LoadPreviewIfSupportedAsync(selectedResourcePath, report.Format, report.Stage);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            selectedResourcePath = null;
            selectedFormat = ResourceFormat.Unknown;
            StatusText.Text = "无法读取该资源。";
            ReportText.Text = $"错误：{exception.Message}";
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
            Title = "保存已验证的 PNG 转换结果",
            Filter = "PNG 图像|*.png",
            AddExtension = true,
            DefaultExt = ".png",
            OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(selectedResourcePath) + ".png",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StatusText.Text = "正在将资源转换为 PNG…";
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
                ? $"已创建并验证 PNG：{dialog.FileName}"
                : $"转换在证据等级“{EvidenceStageName(stage)}”处停止。";
            ReportText.Text = FormatDiagnostics(diagnostics);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = "转换未创建输出文件。";
            ReportText.Text = $"错误：{exception.Message}";
        }
    }

    private void RuntimeConsentCheck_Changed(object sender, RoutedEventArgs e)
    {
        CaptureRuntimeButton.IsEnabled = RuntimeConsentCheck.IsChecked == true;
        RuntimeStatusText.Text = CaptureRuntimeButton.IsEnabled
            ? "已准备好通过架构匹配的只读工作进程创建新归档。"
            : "必须显式确认授权后才能启用运行时采集。";
    }

    private void InspectRuntimeTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RuntimePidText.Text, out var processId) || processId <= 0)
        {
            RuntimeStatusText.Text = "请输入正整数 PID，以在不启动工作进程的情况下检查其架构。";
            return;
        }

        var inspection = RuntimeArchitectureInspector.Inspect(processId);
        RuntimeStatusText.Text = inspection.Architecture == KiriScope.Worker.Protocol.RuntimeTargetArchitecture.Unknown
            ? $"无法为 PID {processId} 准备运行时观察。未启动工作进程。"
            : $"PID {processId} 的架构为 {inspection.Architecture}。后续采集将使用匹配的工作进程，只读取进程和模块元数据。";
        ReportText.Text = FormatDiagnostics(inspection.Diagnostics);
    }

    private async void CaptureRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RuntimePidText.Text, out var processId) || processId <= 0 || RuntimeConsentCheck.IsChecked != true)
        {
            RuntimeStatusText.Text = "请输入正整数 PID，并在采集前显式确认授权。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存运行时证据归档",
            Filter = "JSON 归档|*.json",
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
        RuntimeStatusText.Text = $"正在通过隔离工作进程采集 PID {processId}…";
        try
        {
            var bundledWorkers = await BundledRuntimeWorkers.ExtractAsync();
            var capture = await RuntimeWorkerLauncher.CaptureAsync(new RuntimeCaptureLaunchRequest(
                processId,
                ExplicitlyEnabled: true,
                WorkerX86Path: bundledWorkers?.X86Path,
                WorkerX64Path: bundledWorkers?.X64Path));
            var archivePath = await RuntimeResearchArchiveWriter.WriteNewAsync(
                dialog.FileName,
                new RuntimeProcessResearchArchive(
                    RuntimeProcessResearchArchive.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    $"kiriscope analyze runtime snapshot {processId} \"{Path.GetFullPath(dialog.FileName)}\" --enable-runtime-capture",
                    capture));
            var process = capture.Response?.Process;
            RuntimeStatusText.Text = capture.Succeeded
                ? $"已采集 PID {processId}（{process?.Architecture}），共 {process?.Modules.Count ?? 0} 个模块，归档已写入：{archivePath}。"
                : $"运行时采集已停止；可追溯归档已写入：{archivePath}。";
            ReportText.Text = FormatDiagnostics(capture.Diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            RuntimeStatusText.Text = "运行时采集未完成；不会覆盖已存在的归档。";
            ReportText.Text = $"运行时错误：{exception.Message}";
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
                format == ResourceFormat.Unknown ? "未检测到受支持的格式签名。" : "已检测到格式签名；当前尚无此格式的结构验证器。"),
        };
    }

    private static ResourceVerificationPresentation PresentPng(ResourceFormat format, PngValidationResult result) =>
        new(format, result.Stage, $"宽度：{result.Width}\n高度：{result.Height}\n位深：{result.BitDepth}\n颜色类型：{result.ColorType}\nIDAT：{result.IdatCompressedBytes} 个压缩字节，{result.IdatDecompressedBytes} 个解压字节\n\n{FormatDiagnostics(result.Diagnostics)}");

    private static ResourceVerificationPresentation PresentBmp(ResourceFormat format, BmpValidationResult result) =>
        new(format, result.Stage, $"宽度：{result.Width}\n高度：{result.Height}\n位深：{result.BitCount}\n压缩方式：{result.Compression}\n像素数据：偏移 {result.PixelDataOffset}，长度 {result.PixelDataLength}\n\n{FormatDiagnostics(result.Diagnostics)}", result.IsValid);

    private static ResourceVerificationPresentation PresentWave(ResourceFormat format, WaveValidationResult result) =>
        new(format, result.Stage, $"格式标签：{result.FormatTag}\n声道数：{result.ChannelCount}\n采样率：{result.SampleRate}\n每采样位数：{result.BitsPerSample}\n数据字节数：{result.DataBytes}\n\n{FormatDiagnostics(result.Diagnostics)}");

    private static ResourceVerificationPresentation PresentJpeg(ResourceFormat format, JpegValidationResult result) =>
        new(format, result.Stage, $"宽度：{result.Width}\n高度：{result.Height}\n精度：{result.Precision}\n分量数：{result.ComponentCount}\n扫描数：{result.ScanCount}\n\n{FormatDiagnostics(result.Diagnostics)}");

    private static ResourceVerificationPresentation PresentTlg(ResourceFormat format, TlgValidationResult result) =>
        new(format, result.Stage, $"版本：{result.Version}\n宽度：{result.Width}\n高度：{result.Height}\n颜色通道：{result.ColorChannels}\n数据偏移：{result.DataOffset}\nSDS 包装：{result.HasSdsWrapper}\n\n{FormatDiagnostics(result.Diagnostics)}", result.IsRecognized && result.Version == 5);

    private static async Task<ResourceVerificationPresentation> PresentPsbAsync(ResourceFormat format, Stream input)
    {
        var header = await PsbHeaderReader.ReadAsync(input);
        input.Position = 0;
        var structure = await PsbStructureProbe.ProbeAsync(input);
        var details = new StringBuilder();
        details.AppendLine($"版本：{header.Version}");
        details.AppendLine($"头部可能已加密：{header.HeaderMayBeEncrypted}");
        details.AppendLine($"PIMG 签名：{structure.IsPimgCandidate}");
        details.AppendLine($"根键：{string.Join(", ", structure.RootKeys)}");
        foreach (var value in structure.RootUnsignedIntegers)
        {
            details.AppendLine($"值：{structure.RootKeys[value.RootKeyIndex]} = {value.Value}");
        }
        foreach (var resource in structure.RootResources)
        {
            details.AppendLine($"资源：{structure.RootKeys[resource.RootKeyIndex]} → 索引 {resource.ResourceIndex}，偏移 {resource.Offset}，长度 {resource.Length}");
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
                    ReportText.Text += $"{Environment.NewLine}{Environment.NewLine}无法预览：{FormatDiagnostics(diagnostics)}";
                    return;
                }

                var preview = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, ToBgra(image.Pixels), image.Width * 4);
                preview.Freeze();
                PreviewImage.Source = preview;
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                ReportText.Text += $"{Environment.NewLine}{Environment.NewLine}无法预览：{exception.Message}";
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
            ReportText.Text += $"{Environment.NewLine}{Environment.NewLine}无法预览：{exception.Message}";
        }
    }

    private static string FormatDiagnostics(IEnumerable<KiriScopeDiagnostic> diagnostics)
    {
        var formatted = diagnostics
            .Select(diagnostic => $"{DiagnosticSeverityName(diagnostic.Severity)} [{diagnostic.Code}] {diagnostic.Message}")
            .ToArray();
        return formatted.Length == 0 ? "无诊断信息。" : string.Join(Environment.NewLine, formatted);
    }

    private static string FormatName(ResourceFormat format) => format switch
    {
        ResourceFormat.Unknown => "未知格式",
        ResourceFormat.Png => "PNG",
        ResourceFormat.Tlg => "TLG",
        ResourceFormat.Psb => "PSB",
        ResourceFormat.Pimg => "PIMG",
        ResourceFormat.Ogg => "Ogg",
        ResourceFormat.Wave => "WAVE",
        ResourceFormat.Jpeg => "JPEG",
        ResourceFormat.Bmp => "BMP",
        _ => format.ToString(),
    };

    private static string EvidenceStageName(EvidenceStage stage) => stage switch
    {
        EvidenceStage.Unidentified => "未识别",
        EvidenceStage.ContainerIdentified => "容器已识别",
        EvidenceStage.IndexParsed => "索引已解析",
        EvidenceStage.EntryLocated => "条目已定位",
        EvidenceStage.RawDataExtracted => "原始数据已提取",
        EvidenceStage.ContentFilterApplied => "已应用内容过滤器",
        EvidenceStage.FormatValidated => "格式已验证",
        EvidenceStage.ContentUsable => "内容可用",
        _ => stage.ToString(),
    };

    private static string DiagnosticSeverityName(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Info => "信息",
        DiagnosticSeverity.Warning => "警告",
        DiagnosticSeverity.Error => "错误",
        _ => severity.ToString(),
    };

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
