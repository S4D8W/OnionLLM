using System.Text.Json;
using System.Text.Json.Nodes;
using OnionLLM.Core.Config;
using OnionLLM.Core.Export;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;

namespace OnionLLM.Core.Tests;

/// <summary>JSON zgodny z ccusage (<c>docs/guide/json-output.md</c>).</summary>
public class ExportTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

    private static ReportBuilder Builder(CostMode mode = CostMode.Calculate)
    {
        var config = new OnionConfig { Timezone = "UTC", CostMode = mode };
        return new ReportBuilder(config, new CostCalculator(PricingCatalog.Embedded(), config.CostMode));
    }

    private static UsageEntry[] Entries() =>
    [
        TestData.Entry("2026-11-24T10:00:00Z", 1000, 100, model: "claude-sonnet-4-5", cacheWrite: 600) with { CacheCreation1hTokens = 400 },
        TestData.Entry("2026-11-24T11:00:00Z", 300, 70, model: "mystery-model-9000"),
        TestData.Entry("2026-11-24T11:30:00Z", 50, 5, model: "gpt-5", source: "codex", session: "c1") with { Credits = 2.5 }
    ];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Unified_daily_rows_have_period_agent_metadata_and_breakdowns_without_breakdown_flag()
    {
        var report = Builder().Build(Entries(), ReportPeriod.Daily, new ReportQuery(), Now);
        var root = Parse(ReportExporter.ToJson(report, new JsonExportOptions()));

        Assert.Equal(["daily", "totals"], root.EnumerateObject().Select(property => property.Name));
        var row = root.GetProperty("daily")[0];
        Assert.Equal("2026-11-24", row.GetProperty("period").GetString());
        Assert.Equal("all", row.GetProperty("agent").GetString());
        Assert.Equal(["claude", "codex"], row.GetProperty("metadata").GetProperty("agents").EnumerateArray().Select(item => item.GetString()));
        Assert.False(row.TryGetProperty("date", out _));
        Assert.Equal(2.5, row.GetProperty("credits").GetDouble());

        var breakdowns = row.GetProperty("modelBreakdowns").EnumerateArray().ToDictionary(item => item.GetProperty("modelName").GetString()!);
        Assert.Equal(3, breakdowns.Count);
        Assert.True(breakdowns["mystery-model-9000"].GetProperty("missingPricing").GetBoolean());
        Assert.False(breakdowns["claude-sonnet-4-5"].TryGetProperty("missingPricing", out _));
        Assert.Equal(400, breakdowns["claude-sonnet-4-5"].GetProperty("cacheCreation1hTokens").GetInt64());
        Assert.False(breakdowns["gpt-5"].TryGetProperty("cacheCreation1hTokens", out _));
        Assert.True(breakdowns["claude-sonnet-4-5"].TryGetProperty("cost", out _));

        // Klucze w kolejności alfabetycznej, jak w ccusage.
        var keys = row.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);

        var totals = root.GetProperty("totals");
        Assert.Equal(["mystery-model-9000"], totals.GetProperty("unpricedModels").EnumerateArray().Select(item => item.GetString()));
        Assert.False(totals.TryGetProperty("estimatedModels", out _));
        Assert.False(root.TryGetProperty("unpricedModels", out _));
    }

    [Fact]
    public void Focused_report_uses_date_key_and_no_agent()
    {
        var report = Builder().Build(Entries(), ReportPeriod.Monthly, new ReportQuery { Sources = ["claude"] }, Now);
        var row = Parse(ReportExporter.ToJson(report, new JsonExportOptions { Agent = "claude" })).GetProperty("monthly")[0];

        Assert.Equal("2026-11", row.GetProperty("month").GetString());
        Assert.False(row.TryGetProperty("agent", out _));
        Assert.False(row.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void By_agent_adds_per_source_rows_with_breakdowns()
    {
        var report = Builder().Build(Entries(), ReportPeriod.Daily, new ReportQuery(), Now);
        var agents = Parse(ReportExporter.ToJson(report, new JsonExportOptions { ByAgent = true })).GetProperty("daily")[0].GetProperty("agents");

        Assert.Equal(["claude", "codex"], agents.EnumerateArray().Select(agent => agent.GetProperty("agent").GetString()));
        Assert.Equal(2, agents[0].GetProperty("modelBreakdowns").GetArrayLength());
        Assert.Equal(["gpt-5"], agents[1].GetProperty("modelsUsed").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void No_cost_removes_cost_fields_but_keeps_pricing_flags()
    {
        var report = Builder().Build(Entries(), ReportPeriod.Daily, new ReportQuery(), Now);
        var json = ReportExporter.ToJson(report, new JsonExportOptions { IncludeCost = false });

        Assert.DoesNotContain("\"totalCost\"", json);
        Assert.DoesNotContain("\"cost\"", json);
        Assert.Contains("\"missingPricing\": true", json);
        Assert.Contains("\"unpricedModels\"", json);
    }

    [Fact]
    public void Stored_cost_is_reported_in_auto_mode_only()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 1000, 0, recorded: 0.5),
            TestData.Entry("2026-11-24T10:05:00Z", 1000, 0)
        };

        var auto = Builder(CostMode.Auto).Build(entries, ReportPeriod.Daily, new ReportQuery(), Now);
        var breakdown = Parse(ReportExporter.ToJson(auto)).GetProperty("daily")[0].GetProperty("modelBreakdowns")[0];
        Assert.Equal(0.5, breakdown.GetProperty("storedCost").GetDouble(), 9);
        Assert.Equal(0.5 + 1000 * 3e-6, breakdown.GetProperty("cost").GetDouble(), 9);
        Assert.Equal(0.5, auto.Totals.StoredCostUsd, 9);
        Assert.Equal(1000 * 3e-6, auto.Totals.CalculatedCostUsd, 9);

        var calculate = Builder().Build(entries, ReportPeriod.Daily, new ReportQuery(), Now);
        Assert.DoesNotContain("storedCost", ReportExporter.ToJson(calculate));
    }

    [Fact]
    public void Estimated_models_are_listed_in_totals_and_breakdowns()
    {
        var totals = new TokenCounts { InputTokens = 10 };
        var report = new UsageReport
        {
            Period = ReportPeriod.Daily,
            Rows =
            [
                new ReportRow
                {
                    Period = "2026-11-24", Totals = totals, Models = ["kyma/glm-4.7-flash"], Sources = ["opencode"],
                    EstimatedModels = ["kyma/glm-4.7-flash"],
                    ModelBreakdowns = [new ModelBreakdown("kyma/glm-4.7-flash", totals, IsUnpriced: false, IsEstimated: true)]
                }
            ],
            Totals = totals,
            EstimatedModels = ["kyma/glm-4.7-flash"]
        };

        var root = Parse(ReportExporter.ToJson(report));
        Assert.True(root.GetProperty("daily")[0].GetProperty("modelBreakdowns")[0].GetProperty("estimatedPricing").GetBoolean());
        Assert.Equal(["kyma/glm-4.7-flash"], root.GetProperty("totals").GetProperty("estimatedModels").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Instances_group_rows_into_projects_object()
    {
        var entries = Entries().Select((entry, index) => entry with { Project = index == 0 ? "alpha" : "beta" });
        var report = Builder().Build(entries, ReportPeriod.Daily, new ReportQuery { GroupByProject = true }, Now);
        var root = Parse(ReportExporter.ToJson(report, new JsonExportOptions { Agent = "claude" }));

        Assert.False(root.TryGetProperty("daily", out _));
        var projects = root.GetProperty("projects");
        Assert.Equal("alpha", projects.GetProperty("alpha")[0].GetProperty("project").GetString());
        Assert.Equal("2026-11-24", projects.GetProperty("beta")[0].GetProperty("date").GetString());
    }

    [Fact]
    public void Unified_session_rows_have_agent_period_and_metadata()
    {
        var sessions = Builder().BuildSessions(Entries(), new ReportQuery(), Now);
        var root = Parse(ReportExporter.ToJson(sessions));
        var codex = root.GetProperty("session").EnumerateArray().Single(row => row.GetProperty("agent").GetString() == "codex");

        Assert.Equal("c1", codex.GetProperty("period").GetString());
        Assert.Equal("2026-11-24T11:30:00.000Z", codex.GetProperty("metadata").GetProperty("lastActivity").GetString());
        Assert.Equal(2.5, codex.GetProperty("metadata").GetProperty("credits").GetDouble());
        Assert.Equal(["mystery-model-9000"], root.GetProperty("totals").GetProperty("unpricedModels").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Sections_put_invoked_section_first_and_its_totals_last()
    {
        var builder = Builder();
        var weekly = ReportExporter.BuildJson(builder.Build(Entries(), ReportPeriod.Weekly, new ReportQuery(), Now), new JsonExportOptions());
        var daily = ReportExporter.BuildJson(builder.Build(Entries(), ReportPeriod.Daily, new ReportQuery(), Now), new JsonExportOptions());
        var session = ReportExporter.BuildJson(builder.BuildSessions(Entries(), new ReportQuery(), Now), new JsonExportOptions());
        weekly["totals"]!["marker"] = "weekly";

        var combined = ReportExporter.CombineSections(weekly, [daily, session]);

        Assert.Equal(["weekly", "daily", "session", "totals"], combined.Select(pair => pair.Key));
        Assert.Equal("weekly", combined["totals"]!["marker"]!.GetValue<string>());
    }

    [Fact]
    public void Blocks_json_matches_ccusage_shape()
    {
        var entries = new[] { TestData.Entry("2026-11-24T10:10:00Z", 100, 10), TestData.Entry("2026-11-24T10:40:00Z", 100, 10) };
        var blocks = BlockBuilder.Build(entries, new CostCalculator(PricingCatalog.Embedded(), CostMode.Calculate), Now.AddDays(1)); // blok zakończony
        var block = Parse(ReportExporter.ToJson(blocks, Now)).GetProperty("blocks")[0];

        Assert.Equal("2026-11-24T10:00:00.000Z", block.GetProperty("id").GetString());
        Assert.Equal(100 + 100, block.GetProperty("tokenCounts").GetProperty("inputTokens").GetInt64());
        Assert.Equal(220, block.GetProperty("totalTokens").GetInt64());
        Assert.True(block.GetProperty("costUSD").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("projection").ValueKind);
        Assert.DoesNotContain("costUSD", ReportExporter.ToJson(blocks, Now, includeCost: false));
    }

    [Fact]
    public void Timestamps_are_rfc3339_with_milliseconds_in_utc() =>
        Assert.Equal("2026-01-02T10:00:00.000Z", ReportExporter.Timestamp(DateTimeOffset.Parse("2026-01-02T11:00:00+01:00")));

    [Fact]
    public void Combine_handles_missing_totals_gracefully()
    {
        var combined = ReportExporter.CombineSections(new JsonObject { ["daily"] = new JsonArray() }, []);
        Assert.Equal(["daily", "totals"], combined.Select(pair => pair.Key));
    }
}
