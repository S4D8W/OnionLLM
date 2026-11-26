using OnionLLM.Core.Config;
using OnionLLM.Core.Limits;
using OnionLLM.Core.Reports;
using OnionLLM.ViewModels;

namespace OnionLLM.Core.Tests;

/// <summary>System notification thresholds, texts and „once per window” (DESIGN.md §5.8, §8.4); Alerts.cs is linked.</summary>
public class AlertTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-26T14:50:00Z");

    private static AlertInput Input(ProviderLimits? claude = null, BudgetReport? budgets = null, NotificationSettings? settings = null) => new()
    {
        Now = Now,
        ToLocal = value => value.UtcDateTime,
        Claude = claude,
        Budgets = budgets,
        Settings = settings ?? new NotificationSettings()
    };

    [Fact]
    public void Limits_warn_at_80_and_exceed_at_100_with_reset_texts()
    {
        var claude = new ProviderLimits
        {
            IsAvailable = true,
            FiveHourUtilization = 0.82,
            FiveHourReset = Now.AddMinutes(100),
            SevenDayUtilization = 1.0,
            SevenDayReset = DateTimeOffset.Parse("2026-11-27T09:00:00Z") // Friday
        };

        var alerts = AlertEvaluator.Evaluate(Input(claude));

        var five = alerts.Single(alert => alert.Limit == "fiveHour");
        Assert.Equal(AlertLevel.Warning, five.Level);
        Assert.Equal("Limit 5-godzinny: 82%", five.Title);
        Assert.Equal($"Claude · reset {Fmt.Countdown(TimeSpan.FromMinutes(100))} (o 16:30).", five.Body);
        var seven = alerts.Single(alert => alert.Limit == "sevenDay");
        Assert.Equal(AlertLevel.Exceeded, seven.Level);
        Assert.Equal("Limit 7-dniowy wyczerpany", seven.Title);
        Assert.Equal("Claude · reset w piątek o 09:00.", seven.Body);
    }

    [Fact]
    public void Disabled_notifications_thresholds_and_budget_texts()
    {
        var claude = new ProviderLimits { IsAvailable = true, FiveHourUtilization = 0.9, FiveHourReset = Now.AddHours(1) };
        var settings = new NotificationSettings { Thresholds = { ["fiveHour"] = new ThresholdSettings { Enabled = false } } };
        Assert.Empty(AlertEvaluator.Evaluate(Input(claude, settings: settings)));
        Assert.Empty(AlertEvaluator.Evaluate(Input(claude, settings: new NotificationSettings { Enabled = false })));

        var budgets = new BudgetReport(new BudgetLine(null, new BudgetUsage(15, 12.10), new BudgetUsage(300, 200), 312), [], new DateOnly(2026, 11, 26));
        var alerts = AlertEvaluator.Evaluate(Input(budgets: budgets));

        var daily = alerts.Single(alert => alert.Limit == "dailyBudget");
        Assert.Equal("Budżet dzienny: 81%", daily.Title);
        Assert.Equal("12,10 $ z 15,00 $.", daily.Body);
        var monthly = alerts.Single(alert => alert.Limit == "monthlyBudget");
        Assert.Equal("Prognoza przekracza budżet miesiąca", monthly.Title);
        Assert.Equal($"Prognoza: {Fmt.Cost(312)} z {Fmt.Cost(300)} (104%).", monthly.Body);
    }

    [Fact]
    public void Log_sends_once_per_key_and_respects_mute_and_quiet_hours()
    {
        var alert = new Alert("fiveHour:warn:202611261630", "fiveHour", AlertLevel.Warning, "t", "b");
        var log = new AlertLog([]);
        var settings = new NotificationSettings();

        Assert.Single(log.TakeNew([alert], Now, Now.UtcDateTime, settings, mutedUntil: null));
        Assert.Empty(log.TakeNew([alert], Now.AddMinutes(5), Now.UtcDateTime, settings, mutedUntil: null));

        var other = alert with { Key = "fiveHour:exceed:202611261630" };
        Assert.Empty(log.TakeNew([other], Now, Now.UtcDateTime, settings, mutedUntil: Now.AddHours(1)));
        var quiet = new NotificationSettings { QuietHours = new QuietHoursSettings { Enabled = true, From = "22:00", To = "08:00" } };
        Assert.Empty(log.TakeNew([other], Now, new DateTime(2026, 11, 26, 23, 30, 0), quiet, mutedUntil: null));
        Assert.Single(log.TakeNew([other], Now, Now.UtcDateTime, settings, mutedUntil: null)); // postponed, not lost

        var restored = AlertLog.Parse(log.Serialize());
        Assert.Equal(2, restored.Sent.Count);
    }

    [Fact]
    public void Quiet_hours_cross_midnight()
    {
        var quiet = new QuietHoursSettings { Enabled = true, From = "22:00", To = "08:00" };
        Assert.True(quiet.Contains(new TimeOnly(23, 0)));
        Assert.True(quiet.Contains(new TimeOnly(7, 59)));
        Assert.False(quiet.Contains(new TimeOnly(12, 0)));
        Assert.False(new QuietHoursSettings { From = "22:00", To = "08:00" }.Contains(new TimeOnly(23, 0)));
    }
}
