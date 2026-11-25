using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Tymczasowy katalog usuwany po teście.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onionllm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class TestData
{
    public static OnionConfig ConfigWith(string sourceId, string root, string? timezone = "UTC")
    {
        var config = new OnionConfig { Timezone = timezone };
        config.Sources[sourceId] = new SourceSettings { Paths = [root] };
        return config;
    }

    public static List<UsageEntry> ParseAll(IUsageSource source, OnionConfig config, ParseDiagnostics? diagnostics = null)
    {
        diagnostics ??= new ParseDiagnostics();
        return source.EnumerateFiles(config).SelectMany(file => source.Parse(file, diagnostics, CancellationToken.None)).ToList();
    }

    public static UsageEntry Entry(string timestamp, long input = 0, long output = 0, string model = "claude-sonnet-4-5",
        string source = "claude", string session = "s1", long cacheWrite = 0, long cacheRead = 0, double? recorded = null) => new()
    {
        Source = source,
        TimestampMs = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
        SessionId = session,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CacheCreationTokens = cacheWrite,
        CacheReadTokens = cacheRead,
        RecordedCostUsd = recorded
    };

    public static string ClaudeLine(string timestamp, string messageId, string? requestId, long input, long output,
        long cacheWrite = 0, long cacheRead = 0, string model = "claude-sonnet-4-5", string session = "session-a",
        string? speed = null, long cache1h = 0, double? costUsd = null)
    {
        var request = requestId is null ? "" : $"\"requestId\":\"{requestId}\",";
        var speedField = speed is null ? "" : $",\"speed\":\"{speed}\"";
        var cost = costUsd is null ? "" : $",\"costUSD\":{costUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var cacheDetails = cache1h > 0
            ? $",\"cache_creation\":{{\"ephemeral_1h_input_tokens\":{cache1h},\"ephemeral_5m_input_tokens\":{cacheWrite - cache1h}}}"
            : "";
        return $"{{\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"sessionId\":\"{session}\",{request}\"cwd\":\"C:\\\\work\\\\demo\"," +
               $"\"message\":{{\"id\":\"{messageId}\",\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output}," +
               $"\"cache_creation_input_tokens\":{cacheWrite},\"cache_read_input_tokens\":{cacheRead}{cacheDetails}{speedField}}}}}{cost}}}";
    }
}
