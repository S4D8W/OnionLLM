using System.Text;

namespace OnionLLM.Core.Util;

public static class FileUtil
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Katalog danych OnionLLM: <c>ONIONLLM_DATA_DIR</c> lub <c>%LOCALAPPDATA%\OnionLLM</c>.</summary>
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("ONIONLLM_DATA_DIR");
            var directory = string.IsNullOrWhiteSpace(overridden)
                ? System.IO.Path.Combine(LocalAppData, "OnionLLM")
                : ExpandHome(overridden.Trim());
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static FileStream OpenShared(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        64 * 1024,
        FileOptions.SequentialScan);

    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public static bool IsZstd(string path) =>
        path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Linie pliku tekstowego lub skompresowanego zstd (<c>.zst</c>/<c>.zstd</c>).
    /// Dla uciętego pliku zstd zwraca linie odczytane przed błędem i ustawia <paramref name="truncated"/>.
    /// </summary>
    public static List<string> ReadLinesAuto(string path, out bool truncated)
    {
        truncated = false;
        if (!IsZstd(path))
        {
            return ReadLines(path).ToList();
        }

        var lines = new List<string>();
        try
        {
            using var stream = OpenShared(path);
            using var zstd = new System.IO.Compression.ZstandardStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(zstd, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (InvalidDataException)
        {
            truncated = true;
        }
        catch (IOException) when (lines.Count > 0)
        {
            truncated = true;
        }

        return lines;
    }

    public static string ReadAllText(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Lista ścieżek ze zmiennej środowiskowej (separator <c>,</c> lub <c>;</c>), z rozwinięciem <c>~</c>.</summary>
    public static IReadOnlyList<string> EnvPaths(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return SplitPaths(value);
    }

    public static IReadOnlyList<string> SplitPaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ExpandHome)
            .ToArray();
    }

    public static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Home;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return System.IO.Path.Combine(Home, path[2..]);
        }

        return Environment.ExpandEnvironmentVariables(path);
    }

    public static IEnumerable<string> SafeEnumerateFiles(string root, string pattern, SearchOption option = SearchOption.AllDirectories)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Rekurencyjna lista plików pasujących do <paramref name="pattern"/>, z podążaniem za dowiązaniami symbolicznymi
    /// i junction (do katalogów i plików). Dowiązania są odwiedzane po zwykłym drzewie, a cel już odwiedzony
    /// (także przez cykl) jest pomijany — jak <c>adapters/common/src/walk.rs</c> w ccusage. Zwracane ścieżki prowadzą
    /// przez dowiązanie. Kolejność w katalogu: porządkowa (ordinal) po nazwie.
    /// </summary>
    public static IReadOnlyList<string> SafeEnumerateFilesFollowingLinks(string root, string pattern)
    {
        var result = new List<string>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new Queue<string>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };

        void VisitDirectory(string directory, string identity)
        {
            if (!seen.Add(identity))
            {
                return;
            }

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options)
                    .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (IsLink(entry))
                {
                    links.Enqueue(entry.FullName);
                }
                else if (entry is DirectoryInfo)
                {
                    VisitDirectory(entry.FullName, System.IO.Path.Combine(identity, entry.Name));
                }
                else if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, entry.Name) &&
                         seen.Add(System.IO.Path.Combine(identity, entry.Name)))
                {
                    result.Add(entry.FullName);
                }
            }
        }

        VisitDirectory(root, LinkIdentity(new DirectoryInfo(root)) ?? NormalizePath(root));
        while (links.TryDequeue(out var link))
        {
            FileSystemInfo info = Directory.Exists(link) ? new DirectoryInfo(link) : new FileInfo(link);
            var identity = LinkIdentity(info);
            if (identity is null)
            {
                continue;
            }

            if (Directory.Exists(identity))
            {
                VisitDirectory(link, identity);
            }
            else if (File.Exists(identity) &&
                     System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, System.IO.Path.GetFileName(link)) &&
                     seen.Add(identity))
            {
                result.Add(link);
            }
        }

        return result;
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ostateczny cel dowiązania (pełna ścieżka) albo null, gdy to nie dowiązanie lub cel nie istnieje.</summary>
    private static string? LinkIdentity(FileSystemInfo info)
    {
        try
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is { Exists: true } ? NormalizePath(target.FullName) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsToolOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(System.IO.Path.Combine(directory, tool + ".exe")) ||
                    File.Exists(System.IO.Path.Combine(directory, tool + ".cmd")) ||
                    File.Exists(System.IO.Path.Combine(directory, tool)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return false;
    }

    public static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
