using System.Text;
using System.Text.Json.Nodes;
using OnionLLM.Cli;
using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Export;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Storage;

Console.OutputEncoding = Encoding.UTF8;
Ansi.Configure();

try
{
    return await CliApp.RunAsync(args);
}
catch (Exception exception) when (exception is ArgumentException or ConfigException)
{
    Console.Error.WriteLine(Ansi.Color(Ansi.Red, "Błąd: " + exception.Message));
    return 2;
}

namespace OnionLLM.Cli
{
    internal static class CliApp
    {
        private static readonly string[] Commands = ["daily", "weekly", "monthly", "session", "blocks", "statusline", "sources", "sync", "pricing", "help"];

        /// <summary>Kolejność sekcji w JSON z <c>--sections</c> (po sekcji wywołanej komendy).</summary>
        private static readonly string[] SectionOrder = ["daily", "weekly", "monthly", "session"];

        private static readonly string[] Speeds = ["auto", "standard", "flex", "fast"];

        public static async Task<int> RunAsync(string[] rawArgs)
        {
            var arguments = CliArguments.Parse(rawArgs);
            if (arguments.Has("--version"))
            {
                Console.WriteLine("onionllm " + typeof(CliApp).Assembly.GetName().Version);
                return 0;
            }

            if (arguments.Has("--color"))
            {
                Ansi.Enabled = true;
            }

            if (arguments.Has("--no-color"))
            {
                Ansi.Enabled = false;
            }

            var positionals = new Queue<string>(arguments.Positionals);
            string? source = null;
            if (positionals.TryPeek(out var first) && !Commands.Contains(first, StringComparer.OrdinalIgnoreCase))
            {
                source = positionals.Dequeue().ToLowerInvariant();
            }

            var command = positionals.TryDequeue(out var name) ? name.ToLowerInvariant() : "daily";
            if (arguments.Has("--help") || command == "help")
            {
                PrintHelp();
                return 0;
            }

            var config = ConfigLoader.Load(arguments.Get("--config"));
            ApplyOverrides(config, arguments, command);
            ConfigLoader.Validate(config);

            using var engine = new UsageEngine(config);
            if (source is not null && engine.FindSource(source) is null)
            {
                throw new ArgumentException($"Nieznane źródło '{source}'. Dostępne: {string.Join(", ", engine.Sources.Select(item => item.Id))}.");
            }

            if (command == "statusline")
            {
                return source is null or "claude"
                    ? await Statusline.RunAsync(engine, arguments)
                    : await SourceStatusline.RunAsync(engine, arguments, source);
            }

            if (command != "pricing" && !arguments.Has("--no-sync"))
            {
                var sync = await engine.SyncAsync(force: true);
                if (arguments.Has("--diagnostics") && sync is not null)
                {
                    PrintDiagnostics(sync);
                }
            }

            if (command is "daily" or "weekly" or "monthly" or "session" or "blocks")
            {
                await engine.LoadPricingAsync();
            }

            var query = BuildQuery(arguments, source);
            var includeCost = !arguments.Has("--no-cost");
            var json = arguments.Has("--json") || arguments.Get("--jq") is not null;
            var jsonOptions = new JsonExportOptions { IncludeCost = includeCost, Agent = source, ByAgent = arguments.Has("--by-agent") };
            var sections = ParseSections(arguments.Get("--sections"), command);
            int code;
            switch (command)
            {
                case "daily":
                case "weekly":
                case "monthly":
                case "session":
                    if (command == "session" && arguments.Get("--id") is { } id)
                    {
                        if (sections.Count > 0 || arguments.Has("--by-agent"))
                        {
                            throw new ArgumentException("Opcji --sections i --by-agent nie można łączyć z session --id.");
                        }

                        var single = engine.BuildSessions(query, id);
                        if (single.Sessions.Count == 0)
                        {
                            Console.Error.WriteLine($"Nie znaleziono sesji '{id}'.");
                            return 1;
                        }

                        code = WriteSessions(single, arguments, query, includeCost, json, jsonOptions);
                        break;
                    }

                    code = await WriteReportsAsync(engine, arguments, [command, .. sections], query, source, includeCost, json, jsonOptions);
                    break;

                case "blocks":
                    if (source is not null && source != "claude")
                    {
                        throw new ArgumentException("Bloki rozliczeniowe istnieją tylko dla źródła claude.");
                    }

                    code = await PrintBlocksAsync(engine, arguments, includeCost);
                    query = query with { Sources = ["claude"] };
                    break;

                case "sources":
                    PrintSources(engine);
                    return 0;

                case "sync":
                    var result = engine.LastSync!;
                    PrintDiagnostics(result);
                    var (files, entries) = engine.Ledger.Statistics();
                    Console.WriteLine($"Ledger: {engine.Ledger.DatabasePath} · plików: {files} · wpisów: {entries} · czas: {result.Duration.TotalSeconds:0.0}s");
                    return 0;

                case "pricing":
                    return await PricingCommandAsync(engine, positionals, arguments);

                default:
                    throw new ArgumentException($"Nieznana komenda '{command}'. Użyj: onionllm help");
            }

            if (arguments.Has("--debug"))
            {
                var samples = arguments.GetInt("--debug-samples") ?? PricingMismatchAnalyzer.DefaultSamples;
                if (samples < 0)
                {
                    throw new ArgumentException("--debug-samples musi być nieujemne.");
                }

                var mismatches = PricingMismatchAnalyzer.Analyze(engine, query, samples);
                Console.Error.WriteLine();
                Console.Error.WriteLine(mismatches.Format(engine.Reports.TimeZone));
            }

            return code;
        }

        /// <summary>Raporty okresowe i sesji, także kilka sekcji naraz (<c>--sections</c>).</summary>
        private static async Task<int> WriteReportsAsync(UsageEngine engine, CliArguments arguments, IReadOnlyList<string> sections, ReportQuery query,
            string? source, bool includeCost, bool json, JsonExportOptions jsonOptions)
        {
            var built = new List<(string Section, JsonObject? Json)>();
            foreach (var section in sections)
            {
                if (section == "session")
                {
                    var sessions = engine.BuildSessions(query);
                    if (json)
                    {
                        built.Add((section, ReportExporter.BuildJson(sessions, jsonOptions)));
                    }
                    else if (arguments.Has("--csv"))
                    {
                        Console.Write(ReportExporter.ToCsv(sessions, includeCost));
                    }
                    else
                    {
                        Renderer.PrintSessions(sessions, query, includeCost, arguments.Has("--compact"));
                    }

                    continue;
                }

                var period = section switch { "daily" => ReportPeriod.Daily, "weekly" => ReportPeriod.Weekly, _ => ReportPeriod.Monthly };
                var report = engine.BuildReport(period, query);
                if (json)
                {
                    built.Add((section, ReportExporter.BuildJson(report, jsonOptions)));
                }
                else if (arguments.Has("--csv"))
                {
                    Console.Write(ReportExporter.ToCsv(report, includeCost));
                }
                else
                {
                    Renderer.PrintReport(report, query with { BySource = query.BySource || arguments.Has("--by-agent") }, includeCost, source, engine,
                        arguments.Has("--compact"));
                }
            }

            if (json)
            {
                var root = built.Count == 1
                    ? built[0].Json!
                    : ReportExporter.CombineSections(built[0].Json!, built.Skip(1)
                        .OrderBy(item => Array.IndexOf(SectionOrder, item.Section))
                        .Select(item => item.Json!));
                return await JsonOutput.WriteAsync(ReportExporter.Serialize(root), arguments.Get("--jq"));
            }

            return 0;
        }

        private static int WriteSessions(SessionReport sessions, CliArguments arguments, ReportQuery query, bool includeCost, bool json, JsonExportOptions options)
        {
            if (json)
            {
                return JsonOutput.WriteAsync(ReportExporter.ToJson(sessions, options), arguments.Get("--jq")).GetAwaiter().GetResult();
            }

            if (arguments.Has("--csv"))
            {
                Console.Write(ReportExporter.ToCsv(sessions, includeCost));
            }
            else
            {
                Renderer.PrintSessions(sessions, query, includeCost, arguments.Has("--compact"));
            }

            return 0;
        }

        /// <summary><c>--sections daily,weekly,monthly,session</c>: dodatkowe sekcje poza sekcją wywołanej komendy.</summary>
        internal static IReadOnlyList<string> ParseSections(string? value, string command)
        {
            if (value is null)
            {
                return [];
            }

            if (command is not ("daily" or "weekly" or "monthly" or "session"))
            {
                throw new ArgumentException("--sections działa tylko z daily, weekly, monthly i session.");
            }

            var result = new List<string>();
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var section = token.ToLowerInvariant();
                if (!SectionOrder.Contains(section))
                {
                    throw new ArgumentException($"Niepoprawna sekcja '{token}' w --sections (daily, weekly, monthly, session).");
                }

                if (section != command && !result.Contains(section))
                {
                    result.Add(section);
                }
            }

            return result.OrderBy(section => Array.IndexOf(SectionOrder, section)).ToArray();
        }

        private static void ApplyOverrides(OnionConfig config, CliArguments arguments, string command)
        {
            if (arguments.Get("--timezone") is { } timezone)
            {
                config.Timezone = timezone;
            }

            if (arguments.Get("--mode") is { } mode)
            {
                config.CostMode = mode.ToLowerInvariant() switch
                {
                    "auto" => CostMode.Auto,
                    "calculate" => CostMode.Calculate,
                    "display" => CostMode.Display,
                    _ => throw new ArgumentException($"Nieznany tryb kosztu '{mode}' (auto|calculate|display).")
                };
            }

            // statusline domyślnie offline (szybkość), jak w ccusage.
            if (command == "statusline")
            {
                config.Offline = true;
            }

            if (arguments.Has("--offline"))
            {
                config.Offline = true;
            }

            if (arguments.Has("--no-offline"))
            {
                config.Offline = false;
            }

            if (arguments.Get("--start-of-week") is { } startOfWeek)
            {
                config.StartOfWeek = Enum.TryParse<DayOfWeek>(startOfWeek, ignoreCase: true, out var day)
                    ? day
                    : throw new ArgumentException($"Nieznany dzień tygodnia '{startOfWeek}'.");
            }

            if (arguments.GetDouble("--session-length") is { } length)
            {
                config.SessionLengthHours = length;
            }

            if (arguments.Get("--token-limit") is { } limit)
            {
                config.TokenLimit = limit;
            }

            if (arguments.Get("--project-aliases") is { } aliases)
            {
                config.ProjectAliases = aliases;
            }

            // Ustawienia źródeł (odpowiedniki sekcji <źródło>.defaults w configu ccusage; w aplikacji: Ustawienia → źródło).
            if (arguments.Get("--speed") is { } speed)
            {
                var value = speed.ToLowerInvariant();
                if (!Speeds.Contains(value))
                {
                    throw new ArgumentException($"Nieznany poziom szybkości '{speed}' (auto|standard|flex|fast).");
                }

                SourceSettingsFor(config, "codex").Speed = value == "auto" ? null : value;
            }

            if (arguments.Has("--include-claude-agent"))
            {
                SourceSettingsFor(config, "opencode").IncludeClaudeAgent = true;
            }

            if (arguments.Get("--pi-agent-dir") is { } agentDir)
            {
                SourceSettingsFor(config, "pi").AgentDirs = [agentDir];
            }

            if (arguments.Get("--pi-path") is { } piPath)
            {
                SourceSettingsFor(config, "pi").Paths = Core.Util.FileUtil.SplitPaths(piPath).ToList();
            }
        }

        private static SourceSettings SourceSettingsFor(OnionConfig config, string id)
        {
            if (!config.Sources.TryGetValue(id, out var settings))
            {
                config.Sources[id] = settings = new SourceSettings();
            }

            return settings;
        }

        private static ReportQuery BuildQuery(CliArguments arguments, string? source)
        {
            var order = arguments.Get("--order")?.ToLowerInvariant() switch
            {
                null or "asc" => SortOrder.Asc,
                "desc" => SortOrder.Desc,
                var other => throw new ArgumentException($"Nieznana kolejność '{other}' (asc|desc).")
            };

            var query = new ReportQuery
            {
                Since = DateArgs.ParseOptional(arguments.Get("--since"), "data --since"),
                Until = DateArgs.ParseOptional(arguments.Get("--until"), "data --until"),
                Last = arguments.GetInt("--last"),
                Sources = source is null ? [] : [source],
                Project = arguments.Get("--project"),
                Order = order,
                Breakdown = arguments.Has("--breakdown"),
                BySource = arguments.Has("--by-agent"),
                GroupByProject = arguments.Has("--instances")
            };
            if (query.Last is not null && arguments.Get("--sections") is not null)
            {
                throw new ArgumentException("Opcji --last nie można łączyć z --sections.");
            }

            query.Validate();
            return query;
        }

        private static async Task<int> PrintBlocksAsync(UsageEngine engine, CliArguments arguments, bool includeCost)
        {
            var now = DateTimeOffset.Now;
            var since = DateArgs.ParseOptional(arguments.Get("--since"), "data --since");
            if (arguments.Has("--recent"))
            {
                since = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-3);
            }

            DateTimeOffset? resetsAt = null;
            if (!engine.Config.Offline)
            {
                var limits = await engine.ReadLimitsAsync();
                if (limits.TryGetValue("claude", out var claude) && claude.IsAvailable)
                {
                    resetsAt = claude.FiveHourReset;
                }
            }

            var report = engine.BuildBlocks(since, DateArgs.ParseOptional(arguments.Get("--until"), "data --until"), now,
                activeResetsAt: resetsAt, project: arguments.Get("--project"));
            if (arguments.Has("--active"))
            {
                report = report with { Blocks = report.Active is { } active ? [active] : [] };
            }

            if (arguments.Get("--order")?.Equals("desc", StringComparison.OrdinalIgnoreCase) == true)
            {
                report = report with { Blocks = report.Blocks.Reverse().ToArray() };
            }

            if (arguments.Has("--json") || arguments.Get("--jq") is not null)
            {
                return await JsonOutput.WriteAsync(ReportExporter.ToJson(report, now, includeCost), arguments.Get("--jq"));
            }

            if (arguments.Has("--active"))
            {
                Renderer.PrintActiveBlock(report, now, includeCost);
            }
            else
            {
                Renderer.PrintBlocks(report, now, includeCost);
            }

            return 0;
        }

        private static async Task<int> PricingCommandAsync(UsageEngine engine, Queue<string> positionals, CliArguments arguments)
        {
            var action = positionals.TryDequeue(out var value) ? value : "info";
            switch (action)
            {
                case "info":
                    var catalog = await engine.LoadPricingAsync(forceRefresh: arguments.Has("--force"));
                    Console.WriteLine($"Cennik {PricingStatus.LiteLlmName}: {PricingStatus.DescribeLiteLlm(catalog)}");
                    Console.WriteLine($"Cennik {PricingStatus.ModelsDevName}: {PricingStatus.DescribeModelsDev(catalog)}");
                    if (engine.Config.PricingOverrides.Count > 0 || engine.Config.ModelAliases.Count > 0)
                    {
                        Console.WriteLine($"Nadpisania cen: {engine.Config.PricingOverrides.Count} · aliasy modeli: {engine.Config.ModelAliases.Count}");
                    }
                    if (positionals.TryDequeue(out var model))
                    {
                        var pricing = catalog.Resolve(model);
                        Console.WriteLine(pricing is null
                            ? $"{model}: brak ceny"
                            : $"{model} → {pricing.Key}: in {pricing.Base.Input * 1e6:0.###} $/M, out {pricing.Base.Output * 1e6:0.###} $/M, " +
                              $"cache write {pricing.Base.CacheWrite * 1e6:0.###} $/M, cache read {pricing.Base.CacheRead * 1e6:0.###} $/M, max input {pricing.MaxInputTokens}");
                    }

                    return 0;

                case "update-snapshot":
                    // Narzędzie deweloperskie: pobiera LiteLLM i zapisuje przefiltrowany snapshot do wbudowania.
                    var output = positionals.TryDequeue(out var path) ? path : "OnionLLM.Core/Pricing/pricing-snapshot.json";
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                    {
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionLLM/1.0");
                        var json = await client.GetStringAsync(PricingCatalog.LiteLlmUrl);
                        using var document = System.Text.Json.JsonDocument.Parse(json);
                        await File.WriteAllTextAsync(output, PricingCatalog.SerializeFiltered(document.RootElement));
                    }

                    Console.WriteLine($"Zapisano {output}");
                    return 0;

                default:
                    throw new ArgumentException("Użycie: onionllm pricing [info [model]|update-snapshot [ścieżka]] [--force]");
            }
        }

        private static void PrintSources(UsageEngine engine)
        {
            var table = new Table([("Źródło", Align.Left), ("Nazwa", Align.Left), ("Stan", Align.Left), ("Katalogi", Align.Left)]);
            foreach (var source in engine.Sources)
            {
                var enabled = engine.Config.IsSourceEnabled(source.Id);
                var installed = source.IsInstalled(engine.Config);
                var state = !enabled ? "wyłączone" : installed ? "wykryte" : "nie wykryto";
                var roots = source.ResolveRoots(engine.Config);
                var existing = roots.Where(Directory.Exists).ToArray();
                var rootsText = existing.Length > 0
                    ? string.Join("; ", existing)
                    : roots.Count == 0 ? "-" : roots[0] + (roots.Count > 1 ? $" (+{roots.Count - 1} innych; brak)" : " (brak)");
                table.Add(installed && enabled ? null : Ansi.Grey, source.Id, source.DisplayName, state, rootsText);
            }

            Console.WriteLine(table.Render());
        }

        private static void PrintDiagnostics(SyncReport report)
        {
            foreach (var source in report.Sources)
            {
                Console.Error.WriteLine($"[{source.Source}] plików: {source.Files}, przetworzonych: {source.ParsedFiles}, pominiętych linii: {source.SkippedLines}");
                foreach (var root in source.Roots)
                {
                    Console.Error.WriteLine($"    katalog: {root}{(Directory.Exists(root) ? "" : " (nie istnieje)")}");
                }

                foreach (var (reason, count) in source.SkipReasons)
                {
                    Console.Error.WriteLine($"    pominięto {count}× — {reason}");
                }

                foreach (var problem in source.Problems)
                {
                    Console.Error.WriteLine($"    błąd: {problem.Path}: {problem.Error}");
                }
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("""
                onionllm — zużycie tokenów i koszty narzędzi AI (Claude Code, Codex, Copilot, …)

                Użycie:  onionllm [źródło] <komenda> [opcje]

                Komendy:
                  daily | weekly | monthly   raport okresowy (domyślnie daily)
                  session [--id ID]          raport sesji / jedna sesja
                  blocks [--active|--recent] okna 5-godzinne Claude (burn rate, projekcja)
                  statusline                 linia statusu dla Claude Code (JSON z stdin)
                  <źródło> statusline        podsumowanie źródła, np. codex statusline [--id ID] [--json]
                  sources                    wykryte źródła i katalogi
                  sync                       synchronizacja bazy z logami (+ diagnostyka)
                  pricing [info [model]]     stan cennika / cena modelu (--force: odśwież)

                Źródła: claude, codex, copilot, gemini, qwen, opencode, kilo, cline, amp, droid,
                        codebuff, goose, kimi, pi, openclaw, hermes, antigravity, grok, zcode, dsh

                Opcje:
                  -s, --since DATA           od (YYYY-MM-DD lub YYYYMMDD)
                  -u, --until DATA           do (włącznie)
                      --last N               N ostatnich okresów
                  -o, --order asc|desc       kolejność
                  -z, --timezone STREFA      np. Europe/Warsaw, UTC
                  -m, --mode auto|calculate|display   tryb kosztu
                  -O, --offline / --no-offline        cennik bez sieci / z siecią
                  -b, --breakdown            podział na modele
                      --by-agent             podział każdego okresu na źródła (JSON: tablica agents)
                      --sections a,b,…       kilka sekcji naraz: daily,weekly,monthly,session
                  -i, --instances            grupowanie po projekcie (session: -i = --id)
                  -p, --project NAZWA        filtr projektu
                      --project-aliases "a=Nazwa,b=Inna"
                  -w, --start-of-week DZIEŃ  np. monday, sunday
                  -t, --token-limit N|max    limit tokenów bloku
                  -n, --session-length H     długość bloku w godzinach
                      --no-cost              bez kolumn kosztu
                  -j, --json / --csv         eksport (JSON zgodny z ccusage)
                  -q, --jq FILTR             JSON przez jq (jq w PATH)
                      --compact              wąska tabela
                      --color / --no-color   wymuś / wyłącz kolory
                      --config PLIK          plik konfiguracji
                  -d, --debug                rozbieżności kosztu zapisanego i wyliczonego (stderr)
                      --debug-samples N      liczba przykładów rozbieżności (domyślnie 5)
                      --diagnostics          diagnostyka parsowania logów (stderr)
                      --no-sync              bez synchronizacji (tylko dane z bazy)
                  -v, --version              wersja

                Źródła — ustawienia (jak w Ustawieniach aplikacji):
                      --speed auto|standard|flex|fast   Codex: poziom szybkości do wyceny
                      --include-claude-agent            OpenCode: licz tury claude-agent
                      --pi-agent-dir KATALOG            pi: katalog agenta (czytany <katalog>/sessions)
                      --pi-path KATALOGI                pi: katalogi sesji

                Statusline (Claude Code):
                      --cost-source auto|onion|cc|both
                  -B, --visual-burn-rate off|emoji|text|emoji-text
                      --refresh-interval SEKUNDY   (domyślnie 1)
                      --no-cache                   bez pamięci podręcznej wyniku
                      --model-label-aliases "id=Etykieta,id2=Inna"
                      --context-low-threshold 50 --context-medium-threshold 80
                """);
        }
    }
}
