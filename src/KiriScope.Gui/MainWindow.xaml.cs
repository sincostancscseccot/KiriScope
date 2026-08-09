using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.Filters.BuiltIn;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Resources;
using KiriScope.Runtime;
using KiriScope.Xp3;
using Microsoft.Win32;

namespace KiriScope.Gui;

public partial class MainWindow : Window
{
    private const int MaximumDiscoveredXp3Archives = 2_048;
    private const int MaximumDisplayedXp3Entries = 5_000;
    private string? selectedResourcePath;
    private ResourceFormat selectedFormat;
    private string? selectedXp3ArchivePath;
    private Xp3ArchiveIndex? selectedXp3Index;
    private ValidatedXp3Scheme? validatedXp3Scheme;

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

    private void SelectGameDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 XP3 归档的游戏目录",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var discovery = DiscoverXp3Archives(dialog.FolderName);
            Xp3ArchiveList.ItemsSource = discovery.Archives;
            ClearXp3Selection();
            if (discovery.Archives.Count > 0)
            {
                Xp3ArchiveList.SelectedIndex = 0;
            }

            Xp3StatusText.Text = discovery.IsTruncated
                ? $"已发现前 {discovery.Archives.Count:N0} 个 XP3 归档；已达到安全显示上限，请直接选择特定 XP3 文件。"
                : $"已在“{dialog.FolderName}”中发现 {discovery.Archives.Count:N0} 个 XP3 归档。";
            Xp3ReportText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Xp3StatusText.Text = "无法枚举该游戏目录中的 XP3 归档。";
            Xp3ReportText.Text = $"错误：{exception.Message}";
        }
    }

    private void SelectXp3ArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 XP3 归档",
            Filter = "XP3 归档|*.xp3|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        Xp3ArchiveList.ItemsSource = new[] { dialog.FileName };
        Xp3ArchiveList.SelectedIndex = 0;
        Xp3StatusText.Text = "已选择 XP3 归档；请读取索引。";
    }

    private void Xp3ArchiveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        selectedXp3ArchivePath = Xp3ArchiveList.SelectedItem as string;
        selectedXp3Index = null;
        validatedXp3Scheme = null;
        Xp3EntryList.ItemsSource = null;
        Xp3IndexSummaryText.Text = string.IsNullOrWhiteSpace(selectedXp3ArchivePath)
            ? "尚未选择 XP3 归档。"
            : $"已选择：{selectedXp3ArchivePath}";
        Xp3ReportText.Text = string.Empty;
        UpdateXp3ActionState();
    }

    private async void ReadXp3IndexButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(selectedXp3ArchivePath) || !File.Exists(selectedXp3ArchivePath))
        {
            Xp3StatusText.Text = "请先选择存在的 XP3 归档。";
            return;
        }

        ReadXp3IndexButton.IsEnabled = false;
        validatedXp3Scheme = null;
        Xp3EntryList.ItemsSource = null;
        Xp3StatusText.Text = "正在以只读方式解析 XP3 索引…";
        Xp3ReportText.Text = string.Empty;
        try
        {
            await using var input = new FileStream(
                selectedXp3ArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var index = await Xp3ArchiveReader.ReadIndexAsync(input);
            selectedXp3Index = index;
            var profile = Xp3ArchiveProfile.FromIndex(index);
            Xp3EntryList.ItemsSource = index.Entries
                .Take(MaximumDisplayedXp3Entries)
                .Select(static entry => Xp3EntryPresentation.FromEntry(entry))
                .ToArray();
            Xp3IndexSummaryText.Text = FormatXp3IndexSummary(profile, index.Entries.Count > MaximumDisplayedXp3Entries);
            Xp3ReportText.Text = FormatDiagnostics(index.Diagnostics);
            Xp3StatusText.Text = index.Stage >= EvidenceStage.IndexParsed
                ? $"已解析索引，共 {index.Entries.Count:N0} 个条目。选择一个已标记加密的条目以验证方案。"
                : $"XP3 索引未能完整解析；当前证据等级为“{EvidenceStageName(index.Stage)}”。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            selectedXp3Index = null;
            Xp3StatusText.Text = "无法读取 XP3 索引。";
            Xp3ReportText.Text = $"错误：{exception.Message}";
        }
        finally
        {
            UpdateXp3ActionState();
        }
    }

    private void Xp3EntryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        validatedXp3Scheme = null;
        if (SelectedXp3Entry is { } selected)
        {
            Xp3StatusText.Text = selected.Entry.IsMarkedEncrypted
                ? $"已选择已标记加密条目“{selected.Entry.Name}”。选择方案 JSON 后可验证。"
                : $"已选择未标记加密条目“{selected.Entry.Name}”。可直接导出，或选择其他已标记加密条目验证方案。";
        }

        UpdateXp3ActionState();
    }

    private void SelectXp3SchemeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择内容过滤方案 JSON",
            Filter = "方案 JSON|*.json|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            Xp3SchemePathText.Text = dialog.FileName;
        }
    }

    private void Xp3SchemePathText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        validatedXp3Scheme = null;
        UpdateXp3ActionState();
    }

    private async void ValidateXp3SchemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedXp3ArchivePath is null || SelectedXp3Entry is not { } selected || !selected.Entry.IsMarkedEncrypted)
        {
            Xp3StatusText.Text = "请选择一个已标记加密的 XP3 条目。";
            return;
        }

        if (selected.Entry.UnpackedSize > ContentFilterCandidatePipeline.DefaultMaximumInputBytes)
        {
            Xp3StatusText.Text = $"所选条目大于方案验证上限（{ContentFilterCandidatePipeline.DefaultMaximumInputBytes:N0} 字节）；请选择较小的图像或脚本条目。";
            return;
        }

        ValidateXp3SchemeButton.IsEnabled = false;
        validatedXp3Scheme = null;
        Xp3StatusText.Text = $"正在验证“{selected.Entry.Name}”上的方案…";
        try
        {
            var scheme = LoadSelectedXp3Scheme();
            await using var archive = new FileStream(
                selectedXp3ArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new MemoryStream(checked((int)selected.Entry.UnpackedSize));
            var extraction = await Xp3EntryExtractor.ExtractAsync(
                archive,
                selected.Entry,
                output,
                new Xp3EntryExtractionOptions { ContentFilter = scheme.Filter });
            if (!extraction.Succeeded)
            {
                Xp3StatusText.Text = "方案未能完成所选条目的受控提取。";
                Xp3ReportText.Text = FormatExtractionResult(extraction);
                return;
            }

            var score = await ResourceFormatScorer.ScoreAsync(output.ToArray());
            Xp3ReportText.Text = FormatSchemeValidation(scheme, extraction, score);
            if (score.IsAccepted)
            {
                validatedXp3Scheme = new ValidatedXp3Scheme(
                    Path.GetFullPath(selectedXp3ArchivePath),
                    Path.GetFullPath(scheme.SourcePath),
                    selected.Entry.Name);
                Xp3StatusText.Text = $"方案验证通过：输出识别为 {FormatName(score.Format)}，证据等级为“{EvidenceStageName(score.Stage)}”。现在可以导出全部条目。";
            }
            else
            {
                Xp3StatusText.Text = $"方案未达到完整格式验证（当前为“{EvidenceStageName(score.Stage)}”）；不会将其视为解密成功，也不会启用方案导出。";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or ContentFilterException)
        {
            Xp3StatusText.Text = "无法验证所选方案。";
            Xp3ReportText.Text = $"错误：{exception.Message}";
        }
        finally
        {
            UpdateXp3ActionState();
        }
    }

    private void SelectXp3OutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择新导出目录的父目录（不得位于归档目录内）",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var archiveName = selectedXp3ArchivePath is null ? "xp3" : Path.GetFileNameWithoutExtension(selectedXp3ArchivePath);
        var suggestedName = $"{archiveName}-extracted";
        var candidate = Path.Combine(dialog.FolderName, suggestedName);
        if (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(dialog.FolderName, $"{suggestedName}-{DateTime.Now:yyyyMMdd-HHmmss}");
        }

        Xp3OutputDirectoryText.Text = candidate;
        Xp3StatusText.Text = "已设置新的导出目录。开始导出前仍会再次检查路径和覆盖风险。";
    }

    private async void ExtractXp3Button_Click(object sender, RoutedEventArgs e)
    {
        if (selectedXp3ArchivePath is null || selectedXp3Index is null || selectedXp3Index.Stage < EvidenceStage.IndexParsed)
        {
            Xp3StatusText.Text = "请先成功读取 XP3 索引。";
            return;
        }

        if (!TryGetSafeXp3OutputDirectory(selectedXp3ArchivePath, Xp3OutputDirectoryText.Text, out var outputDirectory, out var outputError))
        {
            Xp3StatusText.Text = outputError;
            return;
        }

        Xp3EntryExtractionOptions? options = null;
        var hasScheme = !string.IsNullOrWhiteSpace(Xp3SchemePathText.Text);
        if (hasScheme)
        {
            if (!HasValidatedXp3Scheme(selectedXp3ArchivePath))
            {
                Xp3StatusText.Text = "当前方案尚未在所选加密条目上通过完整格式验证；为避免生成未经证实的结果，已阻止导出。";
                return;
            }

            try
            {
                options = new Xp3EntryExtractionOptions { ContentFilter = LoadSelectedXp3Scheme().Filter };
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or ContentFilterException)
            {
                Xp3StatusText.Text = "无法加载用于导出的方案。";
                Xp3ReportText.Text = $"错误：{exception.Message}";
                return;
            }
        }

        ExtractXp3Button.IsEnabled = false;
        Xp3StatusText.Text = hasScheme ? "正在按已验证方案导出全部 XP3 条目…" : "正在导出未标记加密的 XP3 条目…";
        try
        {
            var result = await Xp3EntryExtractor.ExtractAllAsync(selectedXp3ArchivePath, outputDirectory, options);
            Xp3ReportText.Text = FormatArchiveExtraction(result);
            Xp3StatusText.Text = result.SkippedEntryCount == 0
                ? $"导出完成：{result.ExtractedEntryCount:N0} 个条目已写入“{outputDirectory}”。"
                : $"导出完成：{result.ExtractedEntryCount:N0} 个条目已写入；{result.SkippedEntryCount:N0} 个条目被跳过。请查看报告，不要将跳过项视为已解密。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Xp3StatusText.Text = "XP3 导出未完成。";
            Xp3ReportText.Text = $"错误：{exception.Message}";
        }
        finally
        {
            UpdateXp3ActionState();
        }
    }

    private void UpdateXp3ActionState()
    {
        ReadXp3IndexButton.IsEnabled = !string.IsNullOrWhiteSpace(selectedXp3ArchivePath) && File.Exists(selectedXp3ArchivePath);
        var selectedEntry = SelectedXp3Entry;
        ValidateXp3SchemeButton.IsEnabled = selectedXp3Index?.Stage >= EvidenceStage.IndexParsed &&
            selectedEntry?.Entry.IsMarkedEncrypted == true &&
            !string.IsNullOrWhiteSpace(Xp3SchemePathText.Text);
        var hasScheme = !string.IsNullOrWhiteSpace(Xp3SchemePathText.Text);
        ExtractXp3Button.IsEnabled = selectedXp3Index?.Stage >= EvidenceStage.IndexParsed &&
            (!hasScheme || HasValidatedXp3Scheme(selectedXp3ArchivePath));
    }

    private BuiltInContentFilterScheme LoadSelectedXp3Scheme()
    {
        var schemePath = Xp3SchemePathText.Text.Trim();
        if (string.IsNullOrWhiteSpace(schemePath) || !File.Exists(schemePath))
        {
            throw new ArgumentException("请选择存在的方案 JSON 文件。", nameof(Xp3SchemePathText));
        }

        return BuiltInContentFilterSchemeLoader.Load(schemePath);
    }

    private bool HasValidatedXp3Scheme(string? archivePath)
    {
        if (validatedXp3Scheme is null || string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(Xp3SchemePathText.Text))
        {
            return false;
        }

        try
        {
            return string.Equals(validatedXp3Scheme.ArchivePath, Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(validatedXp3Scheme.SchemePath, Path.GetFullPath(Xp3SchemePathText.Text.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Xp3DiscoveryResult DiscoverXp3Archives(string rootDirectory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var archives = new List<string>();
        var isTruncated = false;
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.xp3", options))
        {
            if (archives.Count >= MaximumDiscoveredXp3Archives)
            {
                isTruncated = true;
                break;
            }

            archives.Add(Path.GetFullPath(path));
        }

        archives.Sort(StringComparer.OrdinalIgnoreCase);
        return new Xp3DiscoveryResult(archives, isTruncated);
    }

    private void ClearXp3Selection()
    {
        selectedXp3ArchivePath = null;
        selectedXp3Index = null;
        validatedXp3Scheme = null;
        Xp3EntryList.ItemsSource = null;
        Xp3IndexSummaryText.Text = "尚未读取 XP3 索引。";
        UpdateXp3ActionState();
    }

    private static bool TryGetSafeXp3OutputDirectory(
        string archivePath,
        string requestedOutputDirectory,
        out string outputDirectory,
        out string error)
    {
        outputDirectory = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedOutputDirectory))
        {
            error = "请选择新的导出目录。";
            return false;
        }

        try
        {
            outputDirectory = Path.GetFullPath(requestedOutputDirectory);
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            {
                error = "导出目录或同名文件已存在；请选择一个全新的目录名称。";
                return false;
            }

            var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            if (string.IsNullOrEmpty(archiveDirectory) || IsPathContainedBy(archiveDirectory, outputDirectory))
            {
                error = "导出目录必须位于 XP3 归档目录之外，以免修改游戏目录。";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"导出目录无效：{exception.Message}";
            return false;
        }
    }

    private static bool IsPathContainedBy(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatXp3IndexSummary(Xp3ArchiveProfile profile, bool entriesAreTruncated) =>
        $"证据等级：{EvidenceStageName(profile.Stage)}；条目：{profile.EntryCount:N0}；已标记加密：{profile.EncryptedEntryCount:N0}；未标记加密：{profile.UnencryptedEntryCount:N0}；" +
        $"压缩索引：{profile.IsIndexCompressed}；多段条目：{profile.MultiSegmentEntryCount:N0}；解压总大小：{profile.UnpackedBytes:N0} 字节。" +
        (entriesAreTruncated ? $" 界面仅显示前 {MaximumDisplayedXp3Entries:N0} 个条目。" : string.Empty);

    private static string FormatSchemeValidation(
        BuiltInContentFilterScheme scheme,
        Xp3EntryExtractionResult extraction,
        ResourceFormatScore score) =>
        $"方案：{scheme.Descriptor.DisplayName}（{scheme.Descriptor.Id}）\n" +
        $"算法：{scheme.Descriptor.AlgorithmId} {scheme.Descriptor.AlgorithmVersion}\n" +
        $"条目：{extraction.EntryName}\n" +
        $"提取字节数：{extraction.BytesWritten:N0}\n" +
        $"识别格式：{FormatName(score.Format)}\n" +
        $"证据等级：{EvidenceStageName(score.Stage)}\n" +
        $"接受为已验证候选：{score.IsAccepted}\n\n" +
        FormatDiagnostics(extraction.Diagnostics.Concat(score.Diagnostics));

    private static string FormatArchiveExtraction(Xp3ArchiveExtractionResult result)
    {
        var lines = new List<string>
        {
            $"索引已解析：{result.IndexWasParsed}",
            $"已导出条目：{result.ExtractedEntryCount:N0}",
            $"跳过/失败条目：{result.SkippedEntryCount:N0}",
            string.Empty,
            FormatDiagnostics(result.Diagnostics),
        };
        foreach (var entry in result.Entries.Where(static entry => !entry.Succeeded).Take(100))
        {
            lines.Add($"{entry.EntryName}：{FormatDiagnostics(entry.Diagnostics)}");
        }

        if (result.Entries.Count(static entry => !entry.Succeeded) > 100)
        {
            lines.Add("仅显示前 100 个跳过/失败条目的诊断。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatExtractionResult(Xp3EntryExtractionResult result) =>
        $"条目：{result.EntryName}\n证据等级：{EvidenceStageName(result.Stage)}\n已写入字节数：{result.BytesWritten:N0}\n\n{FormatDiagnostics(result.Diagnostics)}";

    private Xp3EntryPresentation? SelectedXp3Entry => Xp3EntryList.SelectedItem as Xp3EntryPresentation;

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

    private sealed record ValidatedXp3Scheme(string ArchivePath, string SchemePath, string EntryName);

    private sealed record Xp3DiscoveryResult(IReadOnlyList<string> Archives, bool IsTruncated);
}

public sealed record ResourceVerificationPresentation(ResourceFormat Format, EvidenceStage Stage, string Text, bool CanConvert = false);

public sealed record Xp3EntryPresentation(
    Xp3Entry Entry,
    string Name,
    string Encryption,
    string UnpackedSize,
    string Adler32)
{
    public static Xp3EntryPresentation FromEntry(Xp3Entry entry) => new(
        entry,
        entry.Name,
        entry.IsMarkedEncrypted ? "已标记加密" : "未标记加密",
        $"{entry.UnpackedSize:N0} 字节",
        entry.Adler32 is uint adler32 ? $"0x{adler32:X8}" : "—");
}
