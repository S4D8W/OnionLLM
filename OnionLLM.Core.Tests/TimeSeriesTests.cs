using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;

namespace OnionLLM.Core.Tests;

/// <summary>Time series for the UI: session timeline, block details, days × hours heatmap, dashboard daily series.</summary>
public class TimeSeriesTests
{
    private static readonly CostCalculator Calculator = new(PricingCatalog.Embedded(), CostMode.Calculate);

    [Fact]
    public void Cumulative_series_grows_in_time_order()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:30:00Z", 200, 0),
            TestData.Entry("2026-11-24T10:00:00Z", 100, 0)
        };

        var points = TimeSeries.Cumulative(entries, Calculator);

        Assert.Equal(2, points.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T10:00:00Z"), points[0].At);
        Assert.Equal(100, points[0].Tokens);
        Assert.Equal(300, points[1].Tokens);
        Assert.True(points[1].CostUsd >= points[0].CostUsd);
    }

    [Fact]
    public void Resample_spreads_the_cumulative_series_evenly_in_time()
    {
        var start = DateTimeOffset.Parse("2026-11-24T10:00:00Z");
        CumulativePoint[] points =
        [
            new(start.AddMinutes(10), 1, 10),
            new(start.AddMinutes(40), 3, 30)
        ];

        var samples = TimeSeries.Resample(points, start, start.AddMinutes(60), 4); // 0, 20, 40, 60 min

        Assert.Equal([0d, 1d, 3d, 3d], samples);
    }

    [Fact]
    public void Hourly_sums_by_local_weekday_and_hour()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:05:00Z", 100, 0), // Tuesday 10:00
            TestData.Entry("2026-11-24T10:55:00Z", 50, 0),
            TestData.Entry("2026-11-29T23:10:00Z", 7, 0)    // Sunday 23:00
        };

        var report = TimeSeries.Hourly(entries, Calculator, TimeZoneInfo.Utc, null, null);

        Assert.Equal(150, report.Tokens[(int)DayOfWeek.Tuesday, 10]);
        Assert.Equal(7, report.Tokens[(int)DayOfWeek.Sunday, 23]);
        Assert.Equal(0, report.Tokens[(int)DayOfWeek.Monday, 10]);
        Assert.True(report.Max > 0);
    }

    [Fact]
    public void Block_details_group_sessions_by_cost()
    {
        var entries = new[]
        {
            TestData.Entry("2026-11-24T10:00:00Z", 100, 0, session: "a"),
            TestData.Entry("2026-11-24T10:10:00Z", 5000, 0, session: "b"),
            TestData.Entry("2026-11-24T10:20:00Z", 100, 0, session: "a")
        };

        var details = TimeSeries.Block(entries, Calculator);

        Assert.Equal(3, details.Points.Count);
        Assert.Equal(["b", "a"], details.Sessions.Select(session => session.SessionId));
        Assert.Equal(200, details.Sessions[1].Totals.InputTokens);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T10:20:00Z"), details.Sessions[1].LastActivity);
    }

    [Fact]
    public void Dashboard_comparisons_come_from_the_daily_series()
    {
        var series = Enumerable.Range(0, 30).Select(day => (double)day).ToArray(); // today = 29, yesterday = 28
        var snapshot = new DashboardSnapshot { DailyCost = series };

        Assert.Equal(28, snapshot.YesterdayCost);
        Assert.Equal(25, snapshot.Average7DaysCost); // mean of days 22–28 (the 7 days before today)
        Assert.Null(new DashboardSnapshot().YesterdayCost);
    }
}
