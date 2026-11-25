using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class ReportTests
{
    private static ReportBuilder Builder(string timezone = "UTC", DayOfWeek startOfWeek = DayOfWeek.Monday)
    {
        var config = new OnionConfig { Timezone = timezone, StartOfWeek = startOfWeek, CostMode = CostMode.Calculate };
        return new ReportBuilder(config, new CostCalculator(PricingCatalog.Embedded(), config.CostMode));
    }

    [Fact]
    public void Days_are_grouped_in_configured_timezone()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-23T22:30:00Z", 1, 1), // 23:30 w Warszawie (UTC+1)
            TestData.Entry("2026-11-23T23:30:00Z", 1, 1)  // 00:30 następnego dnia w Warszawie
        };

        var utc = Builder("UTC").Build(entries, ReportPeriod.Daily, new ReportQuery(), DateTimeOffset.Parse("2026-11-24T12:00:00Z"));
        var warsaw = Builder("Europe/Warsaw").Build(entries, ReportPeriod.Daily, new ReportQuery(), DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

        Assert.Single(utc.Rows);
        Assert.Equal(["2026-11-23", "2026-11-24"], warsaw.Rows.Select(row => row.Period));
    }

    [Fact]
    public void Weeks_start_on_configured_day()
    {
        var entries = new[] { TestData.Entry("2026-11-22T10:00:00Z", 1, 1) }; // niedziela

        var monday = Builder(startOfWeek: DayOfWeek.Monday).Build(entries, ReportPeriod.Weekly, new ReportQuery(), DateTimeOffset.Parse("2026-11-24T12:00:00Z"));
        var sunday = Builder(startOfWeek: DayOfWeek.Sunday).Build(entries, ReportPeriod.Weekly, new ReportQuery(), DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

        Assert.Equal("2026-11-16", monday.Rows[0].Period);
        Assert.Equal("2026-11-22", sunday.Rows[0].Period);
    }

    [Fact]
    public void Last_n_filters_and_order_desc_sorts()
    {
        var entries = Enumerable.Range(0, 10).Select(day => TestData.Entry($"2026-11-{15 + day:00}T10:00:00Z", 1, 1)).ToArray();

        var report = Builder().Build(entries, ReportPeriod.Daily, new ReportQuery { Last = 3, Order = SortOrder.Desc },
            DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

        Assert.Equal(["2026-11-24", "2026-11-23", "2026-11-22"], report.Rows.Select(row => row.Period));
        Assert.Equal(6, report.Totals.TotalTokens);
    }

    [Fact]
    public void Breakdown_and_project_grouping()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 10, 0, model: "claude-sonnet-4-5") with { Project = "a" },
            TestData.Entry("2026-11-24T11:00:00Z", 20, 0, model: "claude-haiku-4-5") with { Project = "b" }
        };

        var report = Builder().Build(entries, ReportPeriod.Daily, new ReportQuery { Breakdown = true, GroupByProject = true },
            DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

        Assert.Equal(2, report.Rows.Count);
        Assert.All(report.Rows, row => Assert.Single(row.ModelBreakdowns));
    }

    [Fact]
    public void Sessions_report_aggregates_per_session()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 10, 0, session: "x"),
            TestData.Entry("2026-11-24T10:30:00Z", 5, 0, session: "x"),
            TestData.Entry("2026-11-24T11:00:00Z", 1, 0, session: "y")
        };

        var report = Builder().BuildSessions(entries, new ReportQuery { Order = SortOrder.Desc }, DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

        Assert.Equal(["y", "x"], report.Sessions.Select(session => session.SessionId));
        Assert.Equal(15, report.Sessions[1].Totals.InputTokens);
        Assert.Equal(TimeSpan.FromMinutes(30), report.Sessions[1].Duration);
    }

    [Fact]
    public void Rows_always_carry_model_and_source_breakdowns_with_credits_and_1h_cache()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 10, 0, cacheWrite: 30) with { CacheCreation1hTokens = 20 },
            TestData.Entry("2026-11-24T11:00:00Z", 5, 0, model: "gpt-5", source: "amp") with { Credits = 1.5 },
            TestData.Entry("2026-11-24T11:30:00Z", 1, 0, model: "mystery-model-9000", source: "amp")
        };

        var row = Builder().Build(entries, ReportPeriod.Daily, new ReportQuery(), DateTimeOffset.Parse("2026-11-24T12:00:00Z")).Rows.Single();

        Assert.Equal(3, row.ModelBreakdowns.Count);
        Assert.Equal(20, row.Totals.CacheCreation1hTokens);
        Assert.Equal(1.5, row.Totals.Credits);
        var amp = row.SourceBreakdowns.Single(source => source.Source == "amp");
        Assert.Equal(["gpt-5", "mystery-model-9000"], amp.ModelBreakdowns.Select(model => model.Model).Order());
        Assert.True(amp.ModelBreakdowns.Single(model => model.Model == "mystery-model-9000").IsUnpriced);
        Assert.Equal(["mystery-model-9000"], row.UnpricedModels);
    }

    [Theory]
    [InlineData("2026-11-24")]
    [InlineData("20261124")]
    public void Dates_accept_both_formats(string value) => Assert.Equal(new DateOnly(2026, 11, 24), DateArgs.Parse(value));

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("24.11.2026")]
    public void Invalid_dates_are_rejected(string value) => Assert.Throws<ArgumentException>(() => DateArgs.Parse(value));

    [Fact]
    public void Since_after_until_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new ReportQuery { Since = new DateOnly(2026, 2, 1), Until = new DateOnly(2026, 1, 1) }.Validate());
}
