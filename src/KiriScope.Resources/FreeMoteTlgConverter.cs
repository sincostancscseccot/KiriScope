using System.Diagnostics;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Invokes an explicitly supplied FreeMote EmtConvert executable against a temporary copy of a TLG file.
/// The original file is never passed to a tool that may write adjacent outputs.
/// </summary>
public static class FreeMoteTlgConverter
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);

    public static async Task<FreeMoteTlgConversionResult> ConvertAsync(
        string tlgPath,
        string outputPath,
        string toolPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tlgPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);
        var input = Path.GetFullPath(tlgPath);
        var output = Path.GetFullPath(outputPath);
        var tool = Path.GetFullPath(toolPath);
        if (!File.Exists(input)) return Failure("FREEMOTE_INPUT_MISSING", "TLG input file does not exist.", tool);
        if (!File.Exists(tool)) return Failure("FREEMOTE_TOOL_MISSING", "The specified FreeMote EmtConvert executable does not exist.", tool);
        if (File.Exists(output)) return Failure("FREEMOTE_OUTPUT_EXISTS", "Output file already exists and will not be overwritten.", tool);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "KiriScope", "FreeMote", Guid.NewGuid().ToString("N"));
        var temporaryInput = Path.Combine(temporaryDirectory, "input.tlg");
        var temporaryOutput = Path.ChangeExtension(temporaryInput, ".png");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            File.Copy(input, temporaryInput, overwrite: false);
            var process = await RunToolAsync(tool, temporaryInput, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new FreeMoteTlgConversionResult(EvidenceStage.ContainerIdentified, false, 0, tool, process.ExitCode, process.StandardOutput, process.StandardError,
                    [new KiriScopeDiagnostic("FREEMOTE_PROCESS_FAILED", DiagnosticSeverity.Error, "FreeMote did not complete the TLG conversion successfully.")]);
            }

            if (!File.Exists(temporaryOutput))
            {
                return new FreeMoteTlgConversionResult(EvidenceStage.ContainerIdentified, false, 0, tool, process.ExitCode, process.StandardOutput, process.StandardError,
                    [new KiriScopeDiagnostic("FREEMOTE_OUTPUT_MISSING", DiagnosticSeverity.Error, "FreeMote completed without producing its expected PNG output.")]);
            }

            await using (var png = new FileStream(temporaryOutput, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var validation = await PngValidator.ValidateAsync(png, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    return new FreeMoteTlgConversionResult(EvidenceStage.ContentUsable, false, 0, tool, process.ExitCode, process.StandardOutput, process.StandardError,
                        [new KiriScopeDiagnostic("FREEMOTE_PNG_VALIDATION_FAILED", DiagnosticSeverity.Error, "FreeMote output did not pass KiriScope PNG validation.")]);
                }
            }

            var outputDirectory = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentException("Output path must have a parent directory.", nameof(outputPath));
            Directory.CreateDirectory(outputDirectory);
            File.Move(temporaryOutput, output, overwrite: false);
            return new FreeMoteTlgConversionResult(EvidenceStage.FormatValidated, true, new FileInfo(output).Length, tool, process.ExitCode, process.StandardOutput, process.StandardError,
                [new KiriScopeDiagnostic("FREEMOTE_TLG_CONVERTED", DiagnosticSeverity.Info, "FreeMote converted a temporary TLG copy and KiriScope validated the resulting PNG before writing the requested output.")]);
        }
        catch (TimeoutException exception)
        {
            return new FreeMoteTlgConversionResult(EvidenceStage.ContainerIdentified, false, 0, tool, null, string.Empty, string.Empty,
                [new KiriScopeDiagnostic("FREEMOTE_PROCESS_TIMEOUT", DiagnosticSeverity.Error, exception.Message)]);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new FreeMoteTlgConversionResult(EvidenceStage.ContainerIdentified, false, 0, tool, null, string.Empty, string.Empty,
                [new KiriScopeDiagnostic("FREEMOTE_PROCESS_START_FAILED", DiagnosticSeverity.Error, exception.Message)]);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<ProcessTranscript> RunToolAsync(string toolPath, string inputPath, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(toolPath)!,
            },
        };
        process.StartInfo.ArgumentList.Add(inputPath);
        if (!process.Start()) throw new System.ComponentModel.Win32Exception("FreeMote process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var exit = process.WaitForExitAsync(cancellationToken);
        if (await Task.WhenAny(exit, Task.Delay(ToolTimeout)).ConfigureAwait(false) != exit)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException($"FreeMote conversion exceeded the {ToolTimeout.TotalMinutes:0}-minute limit.");
        }

        await exit.ConfigureAwait(false);
        return new ProcessTranscript(process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private static FreeMoteTlgConversionResult Failure(string code, string message, string toolPath) =>
        new(EvidenceStage.ContainerIdentified, false, 0, toolPath, null, string.Empty, string.Empty, [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private sealed record ProcessTranscript(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record FreeMoteTlgConversionResult(
    EvidenceStage Stage,
    bool Succeeded,
    long BytesWritten,
    string ToolPath,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
