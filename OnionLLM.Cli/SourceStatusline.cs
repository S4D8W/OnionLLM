using System.Text.Json.Nodes;
using OnionLLM.Core;
using OnionLLM.Core.Export;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;

namespace OnionLLM.Cli;

/// <summary>
/// Jednowierszowe podsumowanie źródła innego niż Claude Code, np. <c>onionllm codex statusline</c>
/// (ccusage <c>codex statusline</c> w trybie podsumowania): dziś, zakres <c>--since/--until</c> albo jedna sesja <c>--id</c>.
/// Tryb MCP natywnego statusline Codex jest świadomie pominięty (10.2).
/// </summary>
internal static class SourceStatusline
{
    public static async Task<int> RunAsync(UsageEngine engine, CliArguments arguments, string source)
    {
        if (arguments.Has("--mcp"))
        {
            throw new ArgumentException("Tryb --mcp (natywny statusline Codex) nie jest obsługiwany.");
        }

        if (arguments.Get("--last") is not null)
        {
            throw new ArgumentException("--last działa tylko w raportach okresowych.");
        }

        if (!arguments.Has("--no-sync"))
        {
            await engine.SyncAsync(force: true);
        }

        var summary = Summarize(engine, arguments, source, DateTimeOffset.Now);
        if (summary is null)
        {
            Console.Error.WriteLine($"Nie znaleziono sesji '{arguments.Get("--id")}'.");
            return 1;
        }

        var includeCost = !arguments.Has("--no-cost");
        if (arguments.Has("--json") || arguments.Get("--jq") is not null)
        {
            return await JsonOutput.WriteAsync(ReportExporter.Serialize(ToJson(summary, includeCost)), arguments.Get("--jq"));
        }

        Console.WriteLine(FormatLine(summary, engine.FindSource(source)?.DisplayName ?? source, includeCost));
        return 0;
    }

    internal sealed record Summary(string Scope, string? SessionId, TokenCounts Totals, IReadOnlyList<string> UnpricedModels, IReadOnlyList<string> EstimatedModels);

    /// <returns>Null, gdy <c>--id</c> nie wskazuje żadnej sesji.</returns>
    internal static Summary? Summarize(UsageEngine engine, CliArguments arguments, string source, DateTimeOffset now)
    {
        if (arguments.Get("--id") is { } id)
        {
            // --id zawsze obejmuje całą sesję i ignoruje filtry dat.
            var sessions = engine.BuildSessions(new ReportQuery { Sources = [source] }, null, now);
            var matching = sessions.Sessions.Where(session => session.SessionId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
            {
                matching = sessions.Sessions.Where(session => session.SessionId.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (matching.Count == 0)
            {
                return null;
            }

            if (matching.Select(session => session.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                throw new ArgumentException($"ID '{id}' pasuje do wielu sesji: {string.Join(", ", matching.Select(session => session.SessionId).Take(5))}.");
            }

            var totals = new TokenCounts();
            foreach (var session in matching)
            {
                totals.Add(session.Totals);
            }

            return new Summary("session", matching[0].SessionId, totals,
                matching.SelectMany(session => session.UnpricedModels).Distinct().Order(StringComparer.Ordinal).ToArray(),
                matching.SelectMany(session => session.EstimatedModels).Distinct().Order(StringComparer.Ordinal).ToArray());
        }

        var since = DateArgs.ParseOptional(arguments.Get("--since"), "data --since");
        var until = DateArgs.ParseOptional(arguments.Get("--until"), "data --until");
        var scope = since is not null || until is not null ? "range" : "today";
        if (scope == "today")
        {
            since = until = engine.Reports.Today(now);
        }

        var report = engine.BuildReport(ReportPeriod.Daily, new ReportQuery { Since = since, Until = until, Sources = [source] }, now);
        return new Summary(scope, null, report.Totals, report.UnpricedModels, report.EstimatedModels);
    }

    internal static JsonObject ToJson(Summary summary, bool includeCost)
    {
        var node = new JsonObject
        {
            ["cacheCreationTokens"] = summary.Totals.CacheCreationTokens,
            ["cacheReadTokens"] = summary.Totals.CacheReadTokens,
            ["inputTokens"] = summary.Totals.InputTokens,
            ["outputTokens"] = summary.Totals.OutputTokens,
            ["reasoningOutputTokens"] = summary.Totals.ReasoningTokens,
            ["scope"] = summary.Scope,
            ["totalTokens"] = summary.Totals.TotalTokens
        };
        if (includeCost)
        {
            node["costUSD"] = summary.Totals.CostUsd;
        }

        if (summary.SessionId is not null)
        {
            node["sessionId"] = summary.SessionId;
        }

        if (summary.UnpricedModels.Count > 0)
        {
            node["unpricedModels"] = new JsonArray(summary.UnpricedModels.Select(model => (JsonNode)model!).ToArray());
        }

        if (summary.EstimatedModels.Count > 0)
        {
            node["estimatedModels"] = new JsonArray(summary.EstimatedModels.Select(model => (JsonNode)model!).ToArray());
        }

        return node;
    }

    internal static string FormatLine(Summary summary, string displayName, bool includeCost)
    {
        var label = summary.Scope switch { "session" => "Sesja", "range" => "Zakres", _ => "Dziś" };
        var text = $"{displayName} | {label}: {Format.Number(summary.Totals.TotalTokens)} tokenów";
        if (includeCost)
        {
            text += $" | szac. {Format.Cost(summary.Totals.CostUsd)}";
            if (summary.UnpricedModels.Count > 0)
            {
                text += " (niepełny cennik)";
            }
            else if (summary.EstimatedModels.Count > 0)
            {
                text += " (ceny szacowane)";
            }
        }

        return text;
    }
}
