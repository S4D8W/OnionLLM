using OnionLLM.Core.Limits;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>Limit history in the ledger and the linear forecast to the reset (Limity, DESIGN.md §6.2).</summary>
public class LimitHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

    [Fact]
    public void Snapshots_are_recorded_deduplicated_and_read_back()
    {
        using var directory = new TempDirectory();
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        var limits = new Dictionary<string, ProviderLimits>
        {
            ["claude"] = new() { IsAvailable = true, FiveHourUtilization = 0.4, SevenDayUtilization = 0.2, FiveHourReset = Now.AddHours(2) },
            ["codex"] = new() { IsAvailable = false }
        };

        ledger.RecordLimits(limits, Now.AddMinutes(-20));
        ledger.RecordLimits(limits, Now.AddMinutes(-10)); // identical, < 15 min → skipped
        limits["claude"] = limits["claude"] with { FiveHourUtilization = 0.5 };
        ledger.RecordLimits(limits, Now);

        var history = ledger.LimitHistory("claude", Now.AddDays(-1));
        Assert.Equal(2, history.Count);
        Assert.Equal(40, history[0].FiveHourPercent, 3);
        Assert.Equal(50, history[1].FiveHourPercent, 3);
        Assert.Equal(Now.AddHours(2), history[1].FiveHourReset);
        Assert.Empty(ledger.LimitHistory("codex", Now.AddDays(-1)));
    }

    [Fact]
    public void Forecast_extrapolates_the_window_trend_to_the_reset()
    {
        (DateTimeOffset, double)[] readings =
        [
            (Now.AddHours(-2), 20),
            (Now.AddHours(-1), 30),
            (Now, 40)
        ];

        var forecast = LimitForecasting.Project(readings, Now.AddHours(2), TimeSpan.FromHours(5), Now);

        Assert.NotNull(forecast);
        Assert.Equal(60, forecast.Percent, 3); // +10 pp per hour, 2 h to the reset
        Assert.Equal(Now.AddHours(2), forecast.At);
    }

    [Fact]
    public void Forecast_starts_after_a_reset_and_needs_enough_readings()
    {
        (DateTimeOffset, double)[] readings =
        [
            (Now.AddHours(-3), 90),
            (Now.AddMinutes(-5), 5), // reset happened
            (Now, 6)
        ];

        Assert.Null(LimitForecasting.Project(readings, Now.AddHours(4), TimeSpan.FromHours(5), Now)); // < 10 min after the reset
        Assert.Null(LimitForecasting.Project([(Now, 10)], Now.AddHours(1), TimeSpan.FromHours(5), Now));
        Assert.Null(LimitForecasting.Project(readings, null, TimeSpan.FromHours(5), Now));
    }
}
