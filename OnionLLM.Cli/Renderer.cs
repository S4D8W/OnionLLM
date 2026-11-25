using OnionLLM.Core;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;

namespace OnionLLM.Cli;

internal static class Renderer
{
    private static bool AutoCompact
    {
        get
        {
            try
            {
                return !Console.IsOutputRedirected && Console.WindowWidth is > 0 and < 120;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public static void PrintReport(UsageReport report, ReportQuery query, bool includeCost, string? source, UsageEngine engine, bool forceCompact = false)
    {
        var title = report.Period switch
        {
            ReportPeriod.Daily => "Dzienne",
            ReportPeriod.Weekly => "Tygodniowe",
            _ => "Miesięczne"
        };
        var sourceName = source is null ? "wszystkie źródła" : engine.FindSource(source)?.DisplayName ?? source;
        Console.WriteLine(Ansi.Bold($"\n OnionLLM — zużycie {title.ToLowerInvariant()} ({sourceName})\n"));

        if (report.Rows.Count == 0)
        {
            Console.WriteLine(" Brak danych w wybranym zakresie. Sprawdź `onionllm sources` i `onionllm sync --debug`.");
            return;
        }

        var compact = forceCompact || AutoCompact;
        var periodHeader = report.Period switch
        {
            ReportPeriod.Daily => "Data",
            ReportPeriod.Weekly => "Tydzień od",
            _ => "Miesiąc"
        };

        var columns = new List<(string, Align)> { (periodHeader, Align.Left) };
        if (query.GroupByProject)
        {
            columns.Add(("Projekt", Align.Left));
        }

        if (source is null)
        {
            columns.Add(("Źródła", Align.Left));
        }

        columns.Add(("Modele", Align.Left));
        columns.Add(("Wejście", Align.Right));
        columns.Add(("Wyjście", Align.Right));
        if (!compact)
        {
            columns.Add(("Cache zapis", Align.Right));
            columns.Add(("Cache odczyt", Align.Right));
        }

        columns.Add(("Razem", Align.Right));
        if (includeCost)
        {
            columns.Add(("Koszt (USD)", Align.Right));
        }

        var table = new Table(columns);
        foreach (var row in report.Rows)
        {
            var cells = new List<string> { row.Period };
            if (query.GroupByProject)
            {
                cells.Add(row.Project ?? "");
            }

            if (source is null)
            {
                cells.Add(string.Join(", ", row.Sources));
            }

            cells.Add(Models(row.Models, row.UnpricedModels, row.EstimatedModels));
            cells.AddRange(Counts(row.Totals, includeCost, compact, Marker(row.UnpricedModels, row.EstimatedModels)));
            table.Add(null, cells.ToArray());

            if (query.BySource)
            {
                foreach (var sourceBreakdown in row.SourceBreakdowns)
                {
                    var sourceCells = new List<string> { "  ▸ " + (engine.FindSource(sourceBreakdown.Source)?.DisplayName ?? sourceBreakdown.Source) };
                    if (query.GroupByProject)
                    {
                        sourceCells.Add("");
                    }

                    if (source is null)
                    {
                        sourceCells.Add("");
                    }

                    var unpriced = sourceBreakdown.ModelBreakdowns.Where(model => model.IsUnpriced).Select(model => model.Model).ToArray();
                    var estimated = sourceBreakdown.ModelBreakdowns.Where(model => model.IsEstimated).Select(model => model.Model).ToArray();
                    sourceCells.Add(Models(sourceBreakdown.Models, unpriced, estimated));
                    sourceCells.AddRange(Counts(sourceBreakdown.Totals, includeCost, compact, Marker(unpriced, estimated)));
                    table.Add(Ansi.Cyan, sourceCells.ToArray());
                }
            }

            foreach (var breakdown in query.Breakdown ? row.ModelBreakdowns : [])
            {
                var breakdownCells = new List<string> { "  └ " + Format.Model(breakdown.Model) + ModelMarker(breakdown) };
                if (query.GroupByProject)
                {
                    breakdownCells.Add("");
                }

                if (source is null)
                {
                    breakdownCells.Add("");
                }

                breakdownCells.Add("");
                breakdownCells.AddRange(Counts(breakdown.Totals, includeCost, compact, ModelMarker(breakdown)));
                table.Add(Ansi.Grey, breakdownCells.ToArray());
            }
        }

        table.Separator();
        var totalCells = new List<string> { Ansi.Bold("Razem") };
        if (query.GroupByProject)
        {
            totalCells.Add("");
        }

        if (source is null)
        {
            totalCells.Add("");
        }

        totalCells.Add("");
        totalCells.AddRange(Counts(report.Totals, includeCost, compact, Marker(report.UnpricedModels, report.EstimatedModels)).Select(Ansi.Bold));
        table.Add(Ansi.Yellow, totalCells.ToArray());
        Console.WriteLine(table.Render());
        PrintPricingNotes(report.UnpricedModels, report.EstimatedModels);
    }

    public static void PrintSessions(SessionReport report, ReportQuery query, bool includeCost, bool forceCompact = false)
    {
        Console.WriteLine(Ansi.Bold("\n OnionLLM — sesje\n"));
        if (report.Sessions.Count == 0)
        {
            Console.WriteLine(" Brak sesji w wybranym zakresie.");
            return;
        }

        var compact = forceCompact || AutoCompact;
        var columns = new List<(string, Align)>
        {
            ("Sesja", Align.Left), ("Źródło", Align.Left), ("Projekt", Align.Left), ("Ostatnia aktywność", Align.Left), ("Modele", Align.Left),
            ("Wejście", Align.Right), ("Wyjście", Align.Right)
        };
        if (!compact)
        {
            columns.Add(("Cache zapis", Align.Right));
            columns.Add(("Cache odczyt", Align.Right));
        }

        columns.Add(("Razem", Align.Right));
        if (includeCost)
        {
            columns.Add(("Koszt (USD)", Align.Right));
        }

        var table = new Table(columns);
        foreach (var session in report.Sessions)
        {
            var cells = new List<string>
            {
                Format.SessionId(session.SessionId),
                session.Source,
                session.Project ?? "",
                session.LastActivity.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Models(session.Models, session.UnpricedModels, session.EstimatedModels)
            };
            cells.AddRange(Counts(session.Totals, includeCost, compact, Marker(session.UnpricedModels, session.EstimatedModels)));
            table.Add(null, cells.ToArray());
            foreach (var breakdown in query.Breakdown ? session.ModelBreakdowns : [])
            {
                var row = new List<string> { "", "", "", "", "  └ " + Format.Model(breakdown.Model) + ModelMarker(breakdown) };
                row.AddRange(Counts(breakdown.Totals, includeCost, compact, ModelMarker(breakdown)));
                table.Add(Ansi.Grey, row.ToArray());
            }
        }

        table.Separator();
        var total = new List<string> { Ansi.Bold("Razem"), "", "", "", "" };
        total.AddRange(Counts(report.Totals, includeCost, compact, Marker(report.UnpricedModels, report.EstimatedModels)).Select(Ansi.Bold));
        table.Add(Ansi.Yellow, total.ToArray());
        Console.WriteLine(table.Render());
        PrintPricingNotes(report.UnpricedModels, report.EstimatedModels);
    }

    public static void PrintBlocks(BlocksReport report, DateTimeOffset now, bool includeCost)
    {
        Console.WriteLine(Ansi.Bold("\n OnionLLM — bloki rozliczeniowe Claude\n"));
        if (report.Blocks.Count == 0)
        {
            Console.WriteLine(" Brak danych Claude Code.");
            return;
        }

        var columns = new List<(string, Align)>
        {
            ("Start bloku", Align.Left), ("Czas / stan", Align.Left), ("Modele", Align.Left), ("Tokeny", Align.Right), ("%", Align.Right)
        };
        if (includeCost)
        {
            columns.Add(("Koszt", Align.Right));
        }

        var table = new Table(columns);
        foreach (var block in report.Blocks)
        {
            if (block.IsGap)
            {
                var gap = block.End - block.Start;
                var cells = new List<string>
                {
                    $"{block.Start.ToLocalTime():yyyy-MM-dd HH:mm} – {block.End.ToLocalTime():yyyy-MM-dd HH:mm} ({Format.Duration(gap)} przerwy)",
                    "(nieaktywny)", "-", "-", "-"
                };
                if (includeCost)
                {
                    cells.Add("-");
                }

                table.Add(Ansi.Grey, cells.ToArray());
                continue;
            }

            var percent = report.TokenLimit is { } limit and > 0 ? $"{block.Totals.TotalTokens * 100.0 / limit:0.0}%" : "-";
            var state = block.IsActive
                ? $"AKTYWNY ({Format.Duration(block.Elapsed(now))} / zostało {Format.Duration(block.Remaining(now))})"
                : Format.Duration((block.LastActivity ?? block.End) - block.Start);
            var row = new List<string>
            {
                block.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                state,
                string.Join(", ", block.Models.Select(Format.Model)),
                Format.Number(block.Totals.TotalTokens),
                percent
            };
            if (includeCost)
            {
                row.Add(Format.Cost(block.Totals.CostUsd));
            }

            table.Add(block.IsActive ? Ansi.Green : null, row.ToArray());

            if (block.IsActive)
            {
                if (report.TokenLimit is { } tokenLimit)
                {
                    var remaining = Math.Max(0, tokenLimit - block.Totals.TotalTokens);
                    var remainingRow = new List<string>
                    {
                        $"(limit {Format.Number(tokenLimit)}{(report.TokenLimitIsMax ? ", max" : "")})", "POZOSTAŁO", "",
                        Format.Number(remaining), $"{remaining * 100.0 / tokenLimit:0.0}%"
                    };
                    if (includeCost)
                    {
                        remainingRow.Add("");
                    }

                    table.Add(Ansi.Blue, remainingRow.ToArray());
                }

                if (block.Projection is { } projection)
                {
                    var status = BlocksReport.Status(projection.TotalTokens, report.TokenLimit);
                    var projectedRow = new List<string>
                    {
                        "(przy obecnym tempie)", "PROGNOZA", "", Format.Number(projection.TotalTokens),
                        report.TokenLimit is { } projectedLimit and > 0 ? $"{projection.TotalTokens * 100.0 / projectedLimit:0.0}%" : "-"
                    };
                    if (includeCost)
                    {
                        projectedRow.Add(Format.Cost(projection.TotalCostUsd));
                    }

                    table.Add(status switch { LimitStatus.Exceeds => Ansi.Red, LimitStatus.Warning => Ansi.Yellow, _ => Ansi.Cyan }, projectedRow.ToArray());
                }
            }
        }

        Console.WriteLine(table.Render());
    }

    public static void PrintActiveBlock(BlocksReport report, DateTimeOffset now, bool includeCost)
    {
        if (report.Active is not { } block)
        {
            Console.WriteLine("Brak aktywnego bloku.");
            return;
        }

        Console.WriteLine(Ansi.Bold("\n Aktywny blok Claude\n"));
        Console.WriteLine($" Start bloku:      {block.Start.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($" Upłynęło:         {Format.Duration(block.Elapsed(now))}");
        Console.WriteLine($" Pozostało:        {Format.Duration(block.Remaining(now))} (do {block.End.ToLocalTime():HH:mm})");
        Console.WriteLine();
        Console.WriteLine(" Bieżące zużycie:");
        Console.WriteLine($"   Wejście:        {Format.Number(block.Totals.InputTokens)}");
        Console.WriteLine($"   Wyjście:        {Format.Number(block.Totals.OutputTokens)}");
        Console.WriteLine($"   Cache zapis:    {Format.Number(block.Totals.CacheCreationTokens)}");
        Console.WriteLine($"   Cache odczyt:   {Format.Number(block.Totals.CacheReadTokens)}");
        if (includeCost)
        {
            Console.WriteLine($"   Koszt:          {Format.Cost(block.Totals.CostUsd)}");
        }

        if (block.BurnRate is { } burn)
        {
            Console.WriteLine();
            Console.WriteLine(" Tempo:");
            Console.WriteLine($"   Tokeny/min:     {Format.Number((long)burn.TokensPerMinute)} ({LevelText(burn.Level)})");
            if (includeCost)
            {
                Console.WriteLine($"   Koszt/godz.:    {Format.Cost(burn.CostPerHour)}");
            }
        }

        if (block.Projection is { } projection)
        {
            Console.WriteLine();
            Console.WriteLine(" Prognoza na koniec bloku:");
            Console.WriteLine($"   Tokeny:         {Format.Number(projection.TotalTokens)}");
            if (includeCost)
            {
                Console.WriteLine($"   Koszt:          {Format.Cost(projection.TotalCostUsd)}");
            }

            if (report.TokenLimit is { } limit)
            {
                var status = BlocksReport.Status(projection.TotalTokens, limit);
                var text = $"   Limit:          {Format.Number(limit)} → {projection.TotalTokens * 100.0 / limit:0.0}% ({status})";
                Console.WriteLine(status switch { LimitStatus.Exceeds => Ansi.Color(Ansi.Red, text), LimitStatus.Warning => Ansi.Color(Ansi.Yellow, text), _ => text });
            }
        }

        Console.WriteLine();
    }

    public static string LevelText(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "wysokie",
        BurnRateLevel.Moderate => "umiarkowane",
        _ => "normalne"
    };

    private static IEnumerable<string> Counts(TokenCounts totals, bool includeCost, bool compact, string marker = "")
    {
        yield return Format.Number(totals.InputTokens);
        yield return Format.Number(totals.OutputTokens);
        if (!compact)
        {
            yield return Format.Number(totals.CacheCreationTokens);
            yield return Format.Number(totals.CacheReadTokens);
        }

        yield return Format.Number(totals.TotalTokens);
        if (includeCost)
        {
            yield return Format.Cost(totals.CostUsd) + marker;
        }
    }

    /// <summary>Znacznik kosztu: <c>*</c> — część modeli bez ceny, <c>~</c> — część cen szacowana.</summary>
    private static string Marker(IReadOnlyCollection<string> unpriced, IReadOnlyCollection<string> estimated) =>
        (unpriced.Count > 0 ? "*" : "") + (estimated.Count > 0 ? "~" : "");

    private static string ModelMarker(ModelBreakdown breakdown) => (breakdown.IsUnpriced ? "*" : "") + (breakdown.IsEstimated ? "~" : "");

    private static string Models(IReadOnlyList<string> models, IReadOnlyCollection<string> unpriced, IReadOnlyCollection<string> estimated)
    {
        var names = models.Take(3).Select(model => Format.Model(model) + (unpriced.Contains(model) ? "*" : "") + (estimated.Contains(model) ? "~" : "")).ToList();
        if (models.Count > 3)
        {
            names.Add($"+{models.Count - 3}");
        }

        return string.Join(", ", names);
    }

    private static void PrintPricingNotes(IReadOnlyList<string> unpriced, IReadOnlyList<string> estimated)
    {
        if (unpriced.Count > 0)
        {
            Console.WriteLine(Ansi.Color(Ansi.Yellow, $" * brak ceny dla: {string.Join(", ", unpriced)} (koszt pominięty lub z danych narzędzia; dodaj pricingOverrides w konfiguracji)"));
        }

        if (estimated.Count > 0)
        {
            Console.WriteLine(Ansi.Color(Ansi.Cyan, $" ~ cena szacowana dla: {string.Join(", ", estimated)} (najbliższy płatny wpis cennika)"));
        }
    }
}
