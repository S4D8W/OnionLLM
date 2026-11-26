using System.Globalization;
using OnionLLM.Core.Config;
using OnionLLM.Core.Limits;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

public enum AlertLevel
{
    Warning,
    Exceeded
}

/// <summary>
/// One alert (DESIGN.md §5.8, texts §8.4). <see cref="Key"/> identifies the threshold in its window (5 h window by reset,
/// 7 days by reset, block, day, month) — a system notification is sent once per key.
/// </summary>
public sealed record Alert(string Key, string Limit, AlertLevel Level, string Title, string Body);

/// <summary>State the alerts are evaluated from (Claude limits, active block, budgets).</summary>
public sealed record AlertInput
{
    public required DateTimeOffset Now { get; init; }

    /// <summary>Converts to the configured time zone (texts „o 16:30”).</summary>
    public required Func<DateTimeOffset, DateTime> ToLocal { get; init; }

    public ProviderLimits? Claude { get; init; }

    public SessionBlock? Block { get; init; }

    public long? BlockLimit { get; init; }

    public BudgetReport? Budgets { get; init; }

    public required NotificationSettings Settings { get; init; }
}

/// <summary>Threshold evaluation for system notifications, the tray dot and in-app warnings (§5.8).</summary>
public static class AlertEvaluator
{
    private static readonly string[] WeekdayAccusative = ["w niedzielę", "w poniedziałek", "we wtorek", "w środę", "w czwartek", "w piątek", "w sobotę"];

    /// <summary>Highest-level alert per limit; disabled limits and disabled notifications give nothing.</summary>
    public static IReadOnlyList<Alert> Evaluate(AlertInput input)
    {
        var alerts = new List<Alert>();
        var settings = input.Settings;
        if (!settings.Enabled)
        {
            return alerts;
        }

        if (input.Claude is { IsAvailable: true } claude)
        {
            Limit("fiveHour", claude.FiveHourUtilization * 100, claude.FiveHourReset, "Limit 5-godzinny");
            Limit("sevenDay", claude.SevenDayUtilization * 100, claude.SevenDayReset, "Limit 7-dniowy");
        }

        if (input.Block is { IsActive: true } block)
        {
            BlockAlerts(block);
        }

        if (input.Budgets is { } budgets)
        {
            BudgetAlerts(budgets);
        }

        return alerts;

        void Limit(string limit, double percent, DateTimeOffset? reset, string name)
        {
            var threshold = settings.For(limit);
            if (!threshold.Enabled || percent < threshold.Warn)
            {
                return;
            }

            var window = reset?.ToUniversalTime().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) ?? "?";
            var when = reset is { } at ? ResetText(input, at, limit == "sevenDay") : "";
            if (percent >= threshold.Exceed)
            {
                alerts.Add(new Alert($"{limit}:exceed:{window}", limit, AlertLevel.Exceeded, $"{name} wyczerpany",
                    reset is { } end ? $"Claude · reset {(limit == "sevenDay" ? when : "o " + Fmt.Time(input.ToLocal(end)))}." : "Claude."));
            }
            else
            {
                alerts.Add(new Alert($"{limit}:warn:{window}", limit, AlertLevel.Warning, $"{name}: {Fmt.Percent(percent)}",
                    reset is null ? "Claude." : $"Claude · reset {when}."));
            }
        }

        void BlockAlerts(SessionBlock block)
        {
            var threshold = settings.For("block");
            if (threshold.Enabled && input.BlockLimit is { } limit and > 0)
            {
                var percent = block.Totals.TotalTokens * 100.0 / limit;
                var projected = block.Projection is { } projection ? projection.TotalTokens * 100.0 / limit : percent;
                if (percent >= threshold.Exceed)
                {
                    alerts.Add(new Alert($"block:exceed:{block.Id}", "block", AlertLevel.Exceeded, "Limit tokenów bloku przekroczony",
                        $"{Fmt.Percent(percent)} limitu · koniec bloku o {Fmt.Time(input.ToLocal(block.End))}."));
                }
                else if (projected >= threshold.Exceed && block.BurnRate is { TokensPerMinute: > 0 } rate)
                {
                    var minutes = (limit - block.Totals.TotalTokens) / rate.TokensPerMinute;
                    alerts.Add(new Alert($"block:projection:{block.Id}", "block", AlertLevel.Warning, "Blok przekroczy limit tokenów",
                        $"Przy obecnym tempie ok. {Fmt.Time(input.ToLocal(input.Now.AddMinutes(minutes)))}. Tempo: {Fmt.CostPerHour(rate.CostPerHour)}."));
                }
                else if (percent >= threshold.Warn)
                {
                    alerts.Add(new Alert($"block:warn:{block.Id}", "block", AlertLevel.Warning, $"Limit tokenów bloku: {Fmt.Percent(percent)}",
                        $"Koniec bloku o {Fmt.Time(input.ToLocal(block.End))}."));
                }
            }

            if (settings.BurnHigh && block.BurnRate is { Level: BurnRateLevel.High } burn)
            {
                alerts.Add(new Alert($"burn:{block.Id}", "burn", AlertLevel.Warning, "Wysokie tempo spalania",
                    $"Aktywny blok: {Fmt.CostPerHour(burn.CostPerHour)} · {Fmt.TokensPerMinute(burn.TokensPerMinute)}."));
            }
        }

        void BudgetAlerts(BudgetReport budgets)
        {
            var day = budgets.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var month = budgets.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (budgets.Global.Daily is { } daily && settings.For("dailyBudget") is { Enabled: true } dailyThreshold && daily.Percent >= dailyThreshold.Warn)
            {
                var exceeded = daily.Percent >= dailyThreshold.Exceed;
                alerts.Add(new Alert($"dailyBudget:{(exceeded ? "exceed" : "warn")}:{day}", "dailyBudget", exceeded ? AlertLevel.Exceeded : AlertLevel.Warning,
                    exceeded ? "Budżet dzienny przekroczony" : $"Budżet dzienny: {Fmt.Percent(daily.Percent)}",
                    $"{Fmt.Cost(daily.SpentUsd)} z {Fmt.Cost(daily.LimitUsd)}."));
            }

            if (budgets.Global.Monthly is { } monthly && settings.For("monthlyBudget") is { Enabled: true } monthlyThreshold)
            {
                if (monthly.Percent >= monthlyThreshold.Exceed)
                {
                    alerts.Add(new Alert($"monthlyBudget:exceed:{month}", "monthlyBudget", AlertLevel.Exceeded, "Budżet miesięczny przekroczony",
                        $"{Fmt.Cost(monthly.SpentUsd)} z {Fmt.Cost(monthly.LimitUsd)}."));
                }
                else if (budgets.Global.MonthForecastUsd is { } forecast && forecast * 100 / monthly.LimitUsd >= monthlyThreshold.Exceed)
                {
                    alerts.Add(new Alert($"monthlyBudget:forecast:{month}", "monthlyBudget", AlertLevel.Warning, "Prognoza przekracza budżet miesiąca",
                        $"Prognoza: {Fmt.Cost(forecast)} z {Fmt.Cost(monthly.LimitUsd)} ({Fmt.Percent(forecast * 100 / monthly.LimitUsd)})."));
                }
                else if (monthly.Percent >= monthlyThreshold.Warn)
                {
                    alerts.Add(new Alert($"monthlyBudget:warn:{month}", "monthlyBudget", AlertLevel.Warning, $"Budżet miesięczny: {Fmt.Percent(monthly.Percent)}",
                        $"{Fmt.Cost(monthly.SpentUsd)} z {Fmt.Cost(monthly.LimitUsd)}."));
                }
            }
        }
    }

    /// <summary>„za 1 h 40 min (o 16:30)” for the 5 h window, „w piątek o 09:00” for 7 days.</summary>
    private static string ResetText(AlertInput input, DateTimeOffset reset, bool weekly)
    {
        var local = input.ToLocal(reset);
        return weekly
            ? $"{WeekdayAccusative[(int)local.DayOfWeek]} o {Fmt.Time(local)}"
            : $"{Fmt.Countdown(reset - input.Now)} (o {Fmt.Time(local)})";
    }
}

/// <summary>
/// „Raz na okno limitu” (§5.8): remembers sent alert keys; muting and quiet hours postpone sending without marking.
/// </summary>
public sealed class AlertLog(IEnumerable<(string Key, DateTimeOffset At)> sent)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(40);
    private readonly Dictionary<string, DateTimeOffset> _sent = sent.ToDictionary(item => item.Key, item => item.At);

    public IReadOnlyDictionary<string, DateTimeOffset> Sent => _sent;

    /// <summary>Alerts to send now (not sent before, not muted, outside quiet hours); marks them as sent.</summary>
    public IReadOnlyList<Alert> TakeNew(IReadOnlyList<Alert> alerts, DateTimeOffset now, DateTime local, NotificationSettings settings, DateTimeOffset? mutedUntil)
    {
        foreach (var old in _sent.Where(pair => now - pair.Value > Retention).Select(pair => pair.Key).ToList())
        {
            _sent.Remove(old);
        }

        if (mutedUntil is { } until && now < until || settings.QuietHours.Contains(TimeOnly.FromDateTime(local)))
        {
            return [];
        }

        var fresh = alerts.Where(alert => !_sent.ContainsKey(alert.Key)).ToList();
        foreach (var alert in fresh)
        {
            _sent[alert.Key] = now;
        }

        return fresh;
    }

    /// <summary>Serialized as „key|unixMs” lines (ViewState).</summary>
    public string Serialize() => string.Join('\n', _sent.Select(pair => $"{pair.Key}|{pair.Value.ToUnixTimeMilliseconds()}"));

    public static AlertLog Parse(string text) => new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('|'))
        .Where(parts => parts.Length == 2 && long.TryParse(parts[1], out _))
        .Select(parts => (parts[0], DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[1], CultureInfo.InvariantCulture)))));
}
