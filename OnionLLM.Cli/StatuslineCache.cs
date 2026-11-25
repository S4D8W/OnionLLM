using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnionLLM.Core.Util;

namespace OnionLLM.Cli;

/// <summary>
/// Pamięć podręczna linii statusu — osobny plik dla każdej sesji Claude Code, z blokadą <c>isUpdating</c> + PID
/// przed równoległym przeliczaniem (ccusage <c>claude/src/commands/statusline/cache.rs</c>).
/// Claude Code wywołuje statusline często i równolegle; gdy inny proces właśnie liczy, pokazywany jest ostatni wynik.
/// </summary>
internal sealed class StatuslineCache
{
    /// <summary>Blokada starsza niż ten czas jest ignorowana (proces mógł zawisnąć, a PID zostać użyty ponownie).</summary>
    internal static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Date { get; set; } = "";
    public string LastOutput { get; set; } = "";
    public long LastUpdateTime { get; set; }
    public string? TranscriptPath { get; set; }
    public long TranscriptMtime { get; set; }
    public bool IsUpdating { get; set; }
    public int? Pid { get; set; }
    public long UpdatingSince { get; set; }

    /// <summary>Skrót opcji wywołania — zmiana opcji (np. <c>--cost-source</c>) unieważnia wynik.</summary>
    public string OptionsKey { get; set; } = "";

    /// <summary>Wynik z pamięci, jeśli nadal aktualny albo jeśli inny żywy proces właśnie go przelicza.</summary>
    public string? CachedOutput(long transcriptMtime, string optionsKey, long nowMs, double refreshSeconds, Func<int, bool>? isAlive = null)
    {
        if (string.IsNullOrEmpty(LastOutput))
        {
            return null;
        }

        var expired = nowMs - LastUpdateTime >= refreshSeconds * 1000;
        var modified = TranscriptMtime != transcriptMtime || OptionsKey != optionsKey;
        if (!expired && !modified)
        {
            return LastOutput;
        }

        var lockValid = IsUpdating && Pid is { } pid && nowMs - UpdatingSince < StaleLock.TotalMilliseconds && (isAlive ?? ProcessIsAlive)(pid);
        return lockValid ? LastOutput : null;
    }

    public static StatuslineCache Updating(StatuslineCache? previous, string? transcriptPath, long transcriptMtime, string optionsKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new StatuslineCache
        {
            Date = Core.Export.ReportExporter.Timestamp(now),
            LastOutput = previous?.LastOutput ?? "",
            LastUpdateTime = previous?.LastUpdateTime ?? 0,
            TranscriptPath = transcriptPath,
            TranscriptMtime = transcriptMtime,
            IsUpdating = true,
            Pid = Environment.ProcessId,
            UpdatingSince = now.ToUnixTimeMilliseconds(),
            OptionsKey = previous?.OptionsKey ?? optionsKey
        };
    }

    public static StatuslineCache Completed(string output, string? transcriptPath, long transcriptMtime, string optionsKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new StatuslineCache
        {
            Date = Core.Export.ReportExporter.Timestamp(now),
            LastOutput = output,
            LastUpdateTime = now.ToUnixTimeMilliseconds(),
            TranscriptPath = transcriptPath,
            TranscriptMtime = transcriptMtime,
            OptionsKey = optionsKey
        };
    }

    /// <summary>Plik pamięci dla sesji: <c>&lt;katalog danych&gt;/statusline/&lt;sesja&gt;.json</c>.</summary>
    public static string PathFor(string sessionId)
    {
        var name = new string(sessionId.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character == '.' ? '_' : character).ToArray());
        if (name.Length == 0)
        {
            name = "_default";
        }
        else if (name.Length > 120)
        {
            name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..32];
        }

        return Path.Combine(FileUtil.DataDirectory, "statusline", name + ".json");
    }

    public static long ReadTranscriptMtime(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds()
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    public static string KeyFor(CliArguments arguments)
    {
        var options = arguments.OptionNames
            .Where(name => name is not "--refresh-interval" and not "--no-cache" and not "--cache")
            .Order(StringComparer.Ordinal)
            .Select(name => name + "=" + arguments.Get(name));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', options))))[..16];
    }

    public static StatuslineCache? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<StatuslineCache>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string path, StatuslineCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Zapis przez plik tymczasowy, aby równoległy odczyt nie trafił na połowę JSON.
            var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Zwolnienie blokady po błędzie (ostatni wynik zostaje).</summary>
    public static void Release(string path)
    {
        if (Read(path) is { } cache)
        {
            cache.IsUpdating = false;
            cache.Pid = null;
            Write(path, cache);
        }
    }

    [JsonIgnore]
    internal static Func<int, bool> ProcessIsAlive { get; set; } = pid =>
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    };
}
