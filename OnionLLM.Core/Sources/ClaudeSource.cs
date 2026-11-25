using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Claude Code — transkrypty JSONL w <c>projects/**/*.jsonl</c> (ccusage <c>rust/adapters/claude</c>).
/// Katalogi: <c>CLAUDE_CONFIG_DIR</c> (lista) lub <c>$XDG_CONFIG_HOME/claude</c> (domyślnie <c>~/.config/claude</c>) i <c>~/.claude</c>.
/// <list type="bullet">
/// <item>ID sesji pochodzi ze ścieżki pliku (także <c>{sesja}/subagents/…</c>); <c>sessionId</c> z linii służy tylko
/// deduplikacji (<c>paths.rs:extract_session_parts</c>, <c>load.rs</c>).</item>
/// <item>Rekordy AgentProgress (<c>data.message.message</c>, #977) i iteracje advisora (<c>msg:advisor:N</c>, #1423)
/// są osobnymi wpisami.</item>
/// <item>Wpisy fast mają model <c>&lt;model&gt;-fast</c> (osobny wiersz w podziale), wyceniany jako model bazowy.</item>
/// <item><c>&lt;synthetic&gt;</c> jest zachowywany bez nazwy modelu (<c>daily.rs:171-173</c>).</item>
/// </list>
/// </summary>
public sealed class ClaudeSource : UsageSourceBase
{
    public const string SourceId = "claude";

    /// <summary>Przyrostek modelu wpisów fast (ccusage <c>daily.rs:174-175</c>).</summary>
    public const string FastSuffix = "-fast";


    public override string Id => SourceId;
    public override string DisplayName => "Claude Code";
    public override string Vendor => "Anthropic";
    public override string AccentColor => "#D97757";
    public override int ParserVersion => 3;

    protected override string[] EnvironmentVariables => ["CLAUDE_CONFIG_DIR"];

    protected override string[] Executables => ["claude"];

    protected override IEnumerable<string> DefaultRoots()
    {
        // ccusage-cli/src/home.rs:135 — $XDG_CONFIG_HOME/claude, gdy zmienna jest niepusta.
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(FileUtil.Home, ".config") : FileUtil.ExpandHome(xdg.Trim());
        return
        [
            Path.Combine(configHome, "claude"),
            Path.Combine(FileUtil.Home, ".claude")
        ];
    }

    /// <summary>Katalogi <c>projects</c> dla podanych korzeni (korzeń może być też samym <c>projects</c>).</summary>
    public IReadOnlyList<string> ProjectsRoots(OnionConfig config)
    {
        var result = new List<string>();
        foreach (var root in ResolveRoots(config))
        {
            if (ProjectsDirectory(root) is { } projects)
            {
                result.Add(projects);
            }
        }

        // Najgłębszy korzeń wygrywa (np. katalog i jego podkatalog na liście; #2374) — sortujemy od najdłuższego.
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length).ToArray();
    }

    /// <summary>
    /// Błędy konfiguracji katalogów: <c>CLAUDE_CONFIG_DIR</c> wskazuje katalog, który nie istnieje albo nie zawiera
    /// <c>projects/</c> (ccusage zwraca wtedy błąd, <c>paths.rs:claude_paths</c>). Pusta lista, gdy zmienna nie jest używana.
    /// </summary>
    public override IReadOnlyList<string> ConfigurationProblems(OnionConfig config)
    {
        if (config.GetSource(Id).Paths.Any(path => !string.IsNullOrWhiteSpace(path)))
        {
            return [];
        }

        return FileUtil.EnvPaths("CLAUDE_CONFIG_DIR")
            .Where(path => ProjectsDirectory(FileUtil.NormalizePath(path)) is null)
            .Select(path => $"CLAUDE_CONFIG_DIR wskazuje katalog, który nie istnieje lub nie zawiera 'projects/': {path}")
            .ToArray();
    }

    private static string? ProjectsDirectory(string root)
    {
        var projects = Path.Combine(root, "projects");
        if (Directory.Exists(projects))
        {
            return projects;
        }

        return Path.GetFileName(root).Equals("projects", StringComparison.OrdinalIgnoreCase) && Directory.Exists(root) ? root : null;
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projects in ProjectsRoots(config))
        {
            foreach (var file in FileUtil.SafeEnumerateFilesFollowingLinks(projects, "*.jsonl"))
            {
                if (seen.Add(file))
                {
                    yield return SourceFile.Create(file, projects);
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var project = ProjectKey(file);
        var pathSession = SessionFromPath(file.Root, file.Path);
        var chains = new ClaudeResponseChains(pathSession);

        foreach (var line in FileUtil.ReadLines(file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Szybki filtr: linie z usage oraz — gdy śledzimy łańcuchy odpowiedzi — ogniwa z parentUuid.
            var maybeUsage = line.Contains("\"usage\"", StringComparison.Ordinal);
            var maybeLink = chains.IsTracking &&
                            (line.Contains("parentUuid", StringComparison.Ordinal) || line.Contains("\\u00", StringComparison.Ordinal));
            if (!maybeUsage && !maybeLink)
            {
                continue;
            }

            if (!JsonUtil.TryParse(line, out var document))
            {
                if (maybeUsage)
                {
                    diagnostics.Skip("niepoprawny JSON");
                }

                continue;
            }

            using (document)
            {
                foreach (var entry in ParseLine(document.RootElement, project, pathSession, chains, diagnostics, maybeUsage))
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<UsageEntry> ParseLine(JsonElement root, string? project, string pathSession,
        ClaudeResponseChains chains, ParseDiagnostics diagnostics, bool maybeUsage)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!maybeUsage || !ClaudeRecordReader.HasUsageObject(root))
        {
            if (chains.IsTracking)
            {
                chains.Link(root);
            }

            return [];
        }

        switch (ClaudeRecordReader.Read(root, out var record, out var reason))
        {
            case ClaudeOutcome.Admitted:
                break;
            case ClaudeOutcome.Malformed or ClaudeOutcome.Unrepresentable:
                diagnostics.Skip(reason);
                if (chains.IsTracking)
                {
                    chains.Link(root);
                }

                return [];
            default:
                // Kontrakt null i zły timestamp: bez ogniwa łańcucha (daily/read.rs:read_dedup_records).
                diagnostics.Skip(reason);
                return [];
        }

        for (var index = 0; index < record!.MalformedAdvisors; index++)
        {
            diagnostics.Skip("niepoprawne zużycie advisora (usage.iterations)");
        }

        var session = record.SessionId ?? pathSession;
        var response = record is { RequestId: null, MessageId: { } messageId }
            ? chains.ResponseKey(root, session, messageId, record.Usage)
            : null;
        var cwd = root.Str("cwd");

        var entries = new List<UsageEntry>(1 + record.Advisors.Count)
        {
            Build(record, record.Usage, record.Model, record.MessageId, record.CostUsd, session, response, pathSession, project, cwd)
        };
        for (var index = 0; index < record.Advisors.Count; index++)
        {
            var advisor = record.Advisors[index];
            var advisorMessage = record.MessageId is null ? null : $"{record.MessageId}:advisor:{index}";
            entries.Add(Build(record, advisor.Usage, advisor.Model, advisorMessage, null, session, response, pathSession, project, cwd));
        }

        return entries;
    }

    private static UsageEntry Build(ClaudeRecord record, ClaudeUsage usage, string? rawModel, string? messageId, double? cost,
        string session, string? response, string pathSession, string? project, string? cwd) => new()
    {
        Source = SourceId,
        TimestampMs = record.TimestampMs,
        SessionId = pathSession,
        Project = project,
        ProjectPath = cwd,
        Model = DisplayModel(rawModel, usage.Speed),
        InputTokens = usage.Input,
        OutputTokens = usage.Output,
        CacheCreationTokens = usage.CacheCreation,
        CacheCreation1hTokens = usage.CacheCreation1h,
        CacheReadTokens = usage.CacheRead,
        ReasoningTokens = usage.Reasoning,
        RecordedCostUsd = cost,
        Speed = usage.Speed,
        Preference = record.IsSidechain ? 0 : 1,
        DedupeKey = messageId is null
            ? null
            : new ClaudeDedupeKey(messageId, record.RequestId, session, record.TimestampMs, response, record.IsSidechain, usage.HasCacheSplit).Format()
    };

    /// <summary>Model do wyświetlenia: bez <c>&lt;synthetic&gt;</c>, z przyrostkiem <c>-fast</c> dla wpisów fast.</summary>
    internal static string? DisplayModel(string? rawModel, SpeedTier speed) => rawModel switch
    {
        null or "<synthetic>" => null,
        _ when speed == SpeedTier.Fast => rawModel + FastSuffix,
        _ => rawModel
    };

    /// <summary>Wpis fast wyceniany jako model bazowy (bez <c>-fast</c>); szybkość zostaje <see cref="SpeedTier.Fast"/>.</summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        if (entry.Model is null)
        {
            return [];
        }

        return entry.Speed == SpeedTier.Fast && entry.Model.EndsWith(FastSuffix, StringComparison.Ordinal)
            ? [entry.Model[..^FastSuffix.Length]]
            : [entry.Model];
    }

    /// <summary>Wybór między duplikatami jak w ccusage (<c>dedupe.rs:462-475</c>).</summary>
    public override IComparer<UsageEntry>? DuplicateComparer => ClaudeDedupe.Comparer;

    /// <summary>Kopie między transkryptami, bloki jednej odpowiedzi bez <c>requestId</c> i replaye sidechain (<c>dedupe.rs</c>).</summary>
    public override IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries) => ClaudeDedupe.Apply(entries);

    /// <summary>
    /// ID sesji ze ścieżki względem <c>projects</c> (<c>paths.rs:extract_session_parts</c>):
    /// <c>{projekt}/{sesja}.jsonl</c> → nazwa pliku; <c>…/{sesja}/subagents/…</c> → katalog przed <c>subagents</c>;
    /// w pozostałych przypadkach katalog nadrzędny pliku.
    /// </summary>
    internal static string SessionFromPath(string projectsRoot, string path)
    {
        var relative = Path.GetRelativePath(projectsRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return "unknown";
        }

        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "unknown";
        }

        var last = parts[^1];
        var fileSession = last.EndsWith(".jsonl", StringComparison.Ordinal) && last.Length > ".jsonl".Length
            ? last[..^".jsonl".Length]
            : null;
        if (parts.Length == 2 && fileSession is not null)
        {
            return fileSession;
        }

        for (var index = 2; index < parts.Length - 1; index++)
        {
            if (parts[index] == "subagents")
            {
                return parts[index - 1];
            }
        }

        return parts[Math.Max(0, parts.Length - 2)];
    }

    /// <summary>Pierwszy segment ścieżki pliku względem katalogu <c>projects</c>.</summary>
    private static string? ProjectKey(SourceFile file)
    {
        var relative = Path.GetRelativePath(file.Root, file.Path);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator > 0 ? relative[..separator] : null;
    }
}
