using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Core.Diagnostics;
using KiriScope.Worker.Protocol;

namespace KiriScope.Runtime;

/// <summary>Single-message stdin/stdout host shared by the x86 and x64 worker executables.</summary>
public static class RuntimeWorkerCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = false,
    };

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        RuntimeCaptureRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RuntimeCaptureRequest>(Console.OpenStandardInput(), JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync($"Invalid runtime worker request: {exception.Message}").ConfigureAwait(false);
            return 2;
        }

        if (request is null)
        {
            await Console.Error.WriteLineAsync("Runtime worker request was empty.").ConfigureAwait(false);
            return 2;
        }

        var response = await RuntimeEvidenceCollector.CollectAsync(request, cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), response, JsonOptions, cancellationToken).ConfigureAwait(false);
        return response.Succeeded ? 0 : 3;
    }
}
