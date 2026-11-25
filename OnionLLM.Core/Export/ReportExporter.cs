using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;

namespace OnionLLM.Core.Export;

/// <summary>Opcje eksportu JSON.</summary>
public sealed record JsonExportOptions
{
    /// <summary>Pola kosztu (<c>totalCost</c>, <c>cost</c>, <c>storedCost</c>, <c>costUSD</c>); false = ccusage <c>--no-cost</c>.</summary>
    public bool IncludeCost { get; init; } = true;

    /// <summary>
    /// Raport jednego źródła (ccusage „focused”, np. <c>ccusage claude daily</c>): wiersze z kluczem <c>date</c>/<c>week</c>/<c>month</c>
    /// lub tablica <c>sessions</c>, bez <c>agent</c> i <c>metadata</c>. Null = raport zbiorczy (<c>period</c>, <c>agent</c>, <c>metadata.agents</c>).
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>Tablica <c>agents</c> w wierszach raportów okresowych (ccusage <c>--by-agent</c>).</summary>
    public bool ByAgent { get; init; }

    public bool IsFocused => !string.IsNullOrEmpty(Agent);
}

/// <summary>
/// Eksport raportów do JSON zgodnego z ccusage (<c>docs/guide/json-output.md</c>) i do CSV.
/// <list type="bullet">
/// <item>Raport zbiorczy: tablica <c>daily</c>/<c>weekly</c>/<c>monthly</c> z wierszami <c>period</c>, <c>agent: "all"</c>,
/// <c>metadata.agents</c>; sesje: tablica <c>session</c> z <c>agent</c> = źródło i <c>period</c> = ID sesji.</item>
/// <item>Raport jednego źródła: <c>date</c>/<c>week</c>/<c>month</c>, sesje w tablicy <c>sessions</c>.</item>
/// <item><c>--instances</c>: obiekt <c>projects</c> z wierszami każdego projektu.</item>
/// <item><c>totals</c> z <c>unpricedModels</c> i <c>estimatedModels</c> (tylko gdy niepuste); <c>modelBreakdowns</c> zawsze,
/// z <c>missingPricing</c>, <c>estimatedPricing</c>, <c>storedCost</c>, <c>cacheCreation1hTokens</c>, gdy dotyczą.</item>
/// </list>
/// Pola dodatkowe OnionLLM (poza ccusage): <c>reasoningOutputTokens</c>, <c>credits</c>, <c>requests</c> w wierszach
/// oraz <c>source</c>, <c>project</c>, <c>firstActivity</c> w metadanych sesji. Klucze obiektów są posortowane alfabetycznie, jak w ccusage.
/// </summary>
public static class ReportExporter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string ToJson(UsageReport report, bool includeCost = true) =>
        ToJson(report, new JsonExportOptions { IncludeCost = includeCost });

    public static string ToJson(UsageReport report, JsonExportOptions options) => Serialize(BuildJson(report, options));

    public static string ToJson(SessionReport report, bool includeCost = true) =>
        ToJson(report, new JsonExportOptions { IncludeCost = includeCost });

    public static string ToJson(SessionReport report, JsonExportOptions options) => Serialize(BuildJson(report, options));

    public static string Serialize(JsonNode node) => node.ToJsonString(Indented);

    /// <summary>Nazwa sekcji JSON raportu okresowego (<c>daily</c>, <c>weekly</c>, <c>monthly</c>).</summary>
    public static string SectionKey(ReportPeriod period) => period switch
    {
        ReportPeriod.Daily => "daily",
        ReportPeriod.Weekly => "weekly",
        _ => "monthly"
    };

    /// <summary>Raport okresowy jako obiekt JSON: <c>{ sekcja | projects, totals }</c>.</summary>
    public static JsonObject BuildJson(UsageReport report, JsonExportOptions options)
    {
        var periodField = !options.IsFocused
            ? "period"
            : report.Period switch
            {
                ReportPeriod.Daily => "date",
                ReportPeriod.Weekly => "week",
                _ => "month"
            };

        var root = new JsonObject();
        if (report.Rows.Any(row => row.Project is not null))
        {
            var projects = new JsonObject();
            foreach (var group in report.Rows.GroupBy(row => row.Project ?? "")
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                projects[group.Key] = new JsonArray(group.Select(row => (JsonNode)PeriodRow(row, periodField, options)).ToArray());
            }

            root["projects"] = projects;
        }
        else
        {
            root[SectionKey(report.Period)] = new JsonArray(report.Rows.Select(row => (JsonNode)PeriodRow(row, periodField, options)).ToArray());
        }

        root["totals"] = Totals(report.Totals, report.UnpricedModels, report.EstimatedModels, options);
        return root;
    }

    /// <summary>Raport sesji jako obiekt JSON: <c>{ session | sessions, totals }</c>.</summary>
    public static JsonObject BuildJson(SessionReport report, JsonExportOptions options)
    {
        var rows = new JsonArray();
        foreach (var session in report.Sessions)
        {
            rows.Add(options.IsFocused ? FocusedSessionRow(session, options) : UnifiedSessionRow(session, options));
        }

        return new JsonObject
        {
            [options.IsFocused ? "sessions" : "session"] = rows,
            ["totals"] = Totals(report.Totals, report.UnpricedModels, report.EstimatedModels, options)
        };
    }

    /// <summary>
    /// Łączy kilka sekcji (ccusage <c>--sections</c>): pierwsza sekcja to sekcja wywołanej komendy i to jej <c>totals</c>
    /// trafia do wyniku; pozostałe sekcje w kolejności <c>daily</c>, <c>weekly</c>, <c>monthly</c>, <c>session</c>; <c>totals</c> na końcu.
    /// </summary>
    public static JsonObject CombineSections(JsonObject invoked, IEnumerable<JsonObject> others)
    {
        var root = new JsonObject();
        JsonNode? totals = null;
        foreach (var section in new[] { invoked }.Concat(others))
        {
            foreach (var (key, value) in section.ToArray())
            {
                if (key == "totals")
                {
                    totals ??= value?.DeepClone();
                    continue;
                }

                if (!root.ContainsKey(key))
                {
                    root[key] = value?.DeepClone();
                }
            }
        }

        root["totals"] = totals;
        return root;
    }

    public static string ToJson(BlocksReport report, DateTimeOffset now, bool includeCost = true)
    {
        var blocks = new JsonArray();
        foreach (var block in report.Blocks)
        {
            var node = new JsonObject
            {
                ["id"] = block.IsGap ? "gap-" + Timestamp(block.Start) : Timestamp(block.Start),
                ["startTime"] = Timestamp(block.Start),
                ["endTime"] = Timestamp(block.End),
                ["actualEndTime"] = block.IsGap || block.LastActivity is null ? null : Timestamp(block.LastActivity.Value),
                ["isActive"] = block.IsActive,
                ["isGap"] = block.IsGap,
                ["entries"] = block.EntryCount,
                ["tokenCounts"] = new JsonObject
                {
                    ["inputTokens"] = block.Totals.InputTokens,
                    ["outputTokens"] = block.Totals.OutputTokens,
                    ["cacheCreationInputTokens"] = block.Totals.CacheCreationTokens,
                    ["cacheReadInputTokens"] = block.Totals.CacheReadTokens
                },
                ["totalTokens"] = block.Totals.TotalTokens,
                ["models"] = Strings(block.Models),
                ["burnRate"] = null,
                ["projection"] = null
            };
            if (includeCost)
            {
                node["costUSD"] = block.Totals.CostUsd;
            }

            if (block.BurnRate is { } burn)
            {
                var burnNode = new JsonObject
                {
                    ["tokensPerMinute"] = burn.TokensPerMinute,
                    ["tokensPerMinuteForIndicator"] = burn.NonCacheTokensPerMinute
                };
                if (includeCost)
                {
                    burnNode["costPerHour"] = burn.CostPerHour;
                }

                node["burnRate"] = burnNode;
            }

            if (block.Projection is { } projection)
            {
                var projectionNode = new JsonObject
                {
                    ["totalTokens"] = projection.TotalTokens,
                    ["remainingMinutes"] = projection.RemainingMinutes
                };
                if (includeCost)
                {
                    projectionNode["totalCost"] = projection.TotalCostUsd;
                }

                node["projection"] = projectionNode;
            }

            if (block.IsActive && report.TokenLimit is { } limit and > 0)
            {
                var projected = block.Projection?.TotalTokens ?? block.Totals.TotalTokens;
                node["tokenLimitStatus"] = new JsonObject
                {
                    ["limit"] = limit,
                    ["projectedUsage"] = projected,
                    ["percentUsed"] = Math.Round(projected * 100.0 / limit, 1),
                    ["status"] = BlocksReport.Status(projected, limit).ToString().ToLowerInvariant()
                };
            }

            blocks.Add(SortKeys(node));
        }

        return Serialize(new JsonObject { ["blocks"] = blocks });
    }

    /// <summary>Znacznik czasu RFC 3339 z milisekundami w UTC (jak ccusage: <c>2026-01-02T10:00:00.000Z</c>).</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject PeriodRow(ReportRow row, string periodField, JsonExportOptions options)
    {
        var node = new JsonObject { [periodField] = row.Period };
        if (row.Project is not null)
        {
            node["project"] = row.Project;
        }

        AddCounts(node, row.Totals, options.IncludeCost);
        node["modelsUsed"] = Strings(row.Models);
        node["modelBreakdowns"] = Breakdowns(row.ModelBreakdowns, options.IncludeCost);
        if (!options.IsFocused)
        {
            node["agent"] = "all";
            node["metadata"] = new JsonObject { ["agents"] = Strings(row.Sources) };
            if (options.ByAgent)
            {
                var agents = new JsonArray();
                foreach (var source in row.SourceBreakdowns.OrderBy(source => source.Source, StringComparer.Ordinal))
                {
                    var agent = new JsonObject { ["agent"] = source.Source };
                    AddCounts(agent, source.Totals, options.IncludeCost);
                    agent["modelsUsed"] = Strings(source.Models);
                    agent["modelBreakdowns"] = Breakdowns(source.ModelBreakdowns, options.IncludeCost);
                    agents.Add(SortKeys(agent));
                }

                node["agents"] = agents;
            }
        }

        return SortKeys(node);
    }

    private static JsonObject UnifiedSessionRow(SessionRow session, JsonExportOptions options)
    {
        var node = new JsonObject
        {
            ["agent"] = session.Source,
            ["period"] = session.SessionId
        };
        AddCounts(node, session.Totals, options.IncludeCost);
        node["modelsUsed"] = Strings(session.Models);
        node["modelBreakdowns"] = Breakdowns(session.ModelBreakdowns, options.IncludeCost);

        var metadata = new JsonObject
        {
            ["lastActivity"] = Timestamp(session.LastActivity),
            ["firstActivity"] = Timestamp(session.FirstActivity)
        };
        if (session.ProjectPath is not null)
        {
            metadata["projectPath"] = session.ProjectPath;
        }

        if (session.Project is not null)
        {
            metadata["project"] = session.Project;
        }

        if (session.Totals.Credits != 0)
        {
            metadata["credits"] = session.Totals.Credits;
        }

        if (session.Totals.ReasoningTokens > 0)
        {
            metadata["reasoningOutputTokens"] = session.Totals.ReasoningTokens;
        }

        node["metadata"] = metadata;
        return SortKeys(node);
    }

    private static JsonObject FocusedSessionRow(SessionRow session, JsonExportOptions options)
    {
        var node = new JsonObject
        {
            ["sessionId"] = session.SessionId,
            ["projectPath"] = session.ProjectPath ?? session.Project,
            ["firstActivity"] = Timestamp(session.FirstActivity),
            ["lastActivity"] = Timestamp(session.LastActivity),
            ["source"] = session.Source
        };
        if (session.Project is not null)
        {
            node["project"] = session.Project;
        }

        AddCounts(node, session.Totals, options.IncludeCost);
        node["modelsUsed"] = Strings(session.Models);
        node["modelBreakdowns"] = Breakdowns(session.ModelBreakdowns, options.IncludeCost);
        return SortKeys(node);
    }

    private static JsonObject Totals(TokenCounts counts, IReadOnlyList<string> unpriced, IReadOnlyList<string> estimated, JsonExportOptions options)
    {
        var node = new JsonObject
        {
            ["inputTokens"] = counts.InputTokens,
            ["outputTokens"] = counts.OutputTokens,
            ["cacheCreationTokens"] = counts.CacheCreationTokens,
            ["cacheReadTokens"] = counts.CacheReadTokens,
            ["totalTokens"] = counts.TotalTokens
        };
        if (options.IncludeCost)
        {
            node["totalCost"] = counts.CostUsd;
        }

        // Opisują dostępność cen, nie kwoty — zostają także przy --no-cost (jak w ccusage).
        if (unpriced.Count > 0)
        {
            node["unpricedModels"] = Strings(unpriced.Order(StringComparer.Ordinal));
        }

        if (estimated.Count > 0)
        {
            node["estimatedModels"] = Strings(estimated.Order(StringComparer.Ordinal));
        }

        return SortKeys(node);
    }

    private static JsonArray Breakdowns(IEnumerable<ModelBreakdown> breakdowns, bool includeCost)
    {
        var result = new JsonArray();
        foreach (var breakdown in breakdowns)
        {
            var totals = breakdown.Totals;
            var node = new JsonObject
            {
                ["modelName"] = breakdown.Model,
                ["inputTokens"] = totals.InputTokens,
                ["outputTokens"] = totals.OutputTokens,
                ["cacheCreationTokens"] = totals.CacheCreationTokens,
                ["cacheReadTokens"] = totals.CacheReadTokens
            };
            if (totals.CacheCreation1hTokens > 0)
            {
                node["cacheCreation1hTokens"] = totals.CacheCreation1hTokens;
            }

            if (includeCost)
            {
                node["cost"] = totals.CostUsd;
                if (totals.HasStoredCost)
                {
                    node["storedCost"] = totals.StoredCostUsd;
                }
            }

            if (breakdown.IsUnpriced)
            {
                node["missingPricing"] = true;
            }

            if (breakdown.IsEstimated)
            {
                node["estimatedPricing"] = true;
            }

            result.Add(SortKeys(node));
        }

        return result;
    }

    private static void AddCounts(JsonObject node, TokenCounts counts, bool includeCost)
    {
        node["inputTokens"] = counts.InputTokens;
        node["outputTokens"] = counts.OutputTokens;
        node["cacheCreationTokens"] = counts.CacheCreationTokens;
        node["cacheReadTokens"] = counts.CacheReadTokens;
        node["totalTokens"] = counts.TotalTokens;
        node["requests"] = counts.Requests;
        if (counts.ReasoningTokens > 0)
        {
            node["reasoningOutputTokens"] = counts.ReasoningTokens;
        }

        if (counts.Credits != 0)
        {
            node["credits"] = counts.Credits;
        }

        if (includeCost)
        {
            node["totalCost"] = counts.CostUsd;
        }
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode)value!).ToArray());

    /// <summary>Kopia obiektu z kluczami w kolejności alfabetycznej (ccusage serializuje <c>BTreeMap</c>).</summary>
    private static JsonObject SortKeys(JsonObject node)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in node.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
        {
            node.Remove(key);
            sorted[key] = value;
        }

        return sorted;
    }

    public static string ToCsv(UsageReport report, bool includeCost = true)
    {
        var builder = new StringBuilder();
        var header = new List<string> { "period", "project", "inputTokens", "outputTokens", "cacheCreationTokens", "cacheReadTokens", "totalTokens" };
        if (includeCost)
        {
            header.Add("costUSD");
        }

        header.AddRange(["models", "sources"]);
        builder.AppendLine(string.Join(',', header));
        foreach (var row in report.Rows)
        {
            AppendCsvRow(builder, includeCost, row.Period, row.Project, row.Totals, string.Join(';', row.Models), string.Join(';', row.Sources));
        }

        AppendCsvRow(builder, includeCost, "TOTAL", null, report.Totals, "", "");
        return builder.ToString();
    }

    public static string ToCsv(SessionReport report, bool includeCost = true)
    {
        var builder = new StringBuilder();
        var header = new List<string> { "sessionId", "source", "project", "firstActivity", "lastActivity", "inputTokens", "outputTokens", "cacheCreationTokens", "cacheReadTokens", "totalTokens" };
        if (includeCost)
        {
            header.Add("costUSD");
        }

        header.Add("models");
        builder.AppendLine(string.Join(',', header));
        foreach (var session in report.Sessions)
        {
            var values = new List<string>
            {
                Csv(session.SessionId), Csv(session.Source), Csv(session.Project),
                session.FirstActivity.ToString("O", CultureInfo.InvariantCulture),
                session.LastActivity.ToString("O", CultureInfo.InvariantCulture)
            };
            values.AddRange(CountValues(session.Totals, includeCost));
            values.Add(Csv(string.Join(';', session.Models)));
            builder.AppendLine(string.Join(',', values));
        }

        return builder.ToString();
    }

    private static void AppendCsvRow(StringBuilder builder, bool includeCost, string period, string? project, TokenCounts totals, string models, string sources)
    {
        var values = new List<string> { Csv(period), Csv(project) };
        values.AddRange(CountValues(totals, includeCost));
        values.Add(Csv(models));
        values.Add(Csv(sources));
        builder.AppendLine(string.Join(',', values));
    }

    private static IEnumerable<string> CountValues(TokenCounts totals, bool includeCost)
    {
        yield return totals.InputTokens.ToString(CultureInfo.InvariantCulture);
        yield return totals.OutputTokens.ToString(CultureInfo.InvariantCulture);
        yield return totals.CacheCreationTokens.ToString(CultureInfo.InvariantCulture);
        yield return totals.CacheReadTokens.ToString(CultureInfo.InvariantCulture);
        yield return totals.TotalTokens.ToString(CultureInfo.InvariantCulture);
        if (includeCost)
        {
            yield return totals.CostUsd.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
