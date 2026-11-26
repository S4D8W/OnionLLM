namespace OnionLLM.ViewModels;

/// <summary>Date range presets of the DateRangePicker (DESIGN.md §4.2.4, labels §8.6).</summary>
public enum RangePreset
{
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
    ThisMonth,
    PreviousMonth,
    Last90Days,
    ThisYear,
    AllTime,
    Custom
}

/// <summary>Filter passed between screens on drill-down (§5.5), e.g. „Pokaż sesje z tego okresu”.</summary>
public sealed record RangeFilter(DateOnly Since, DateOnly Until, string? SourceId = null, string? Project = null);

public static class DateRanges
{
    public static string Label(RangePreset preset) => preset switch
    {
        RangePreset.Today => "Dziś",
        RangePreset.Yesterday => "Wczoraj",
        RangePreset.Last7Days => "Ostatnie 7 dni",
        RangePreset.Last30Days => "Ostatnie 30 dni",
        RangePreset.ThisMonth => "Ten miesiąc",
        RangePreset.PreviousMonth => "Poprzedni miesiąc",
        RangePreset.Last90Days => "Ostatnie 90 dni",
        RangePreset.ThisYear => "Ten rok",
        RangePreset.AllTime => "Cała historia",
        _ => "Własny zakres"
    };

    /// <summary>Inclusive bounds; <c>null</c> since = from the beginning of history.</summary>
    public static (DateOnly? Since, DateOnly Until) Resolve(RangePreset preset, DateTime customSince, DateTime customUntil)
    {
        var today = DateOnly.FromDateTime(Fmt.NowLocal);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return preset switch
        {
            RangePreset.Today => (today, today),
            RangePreset.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            RangePreset.Last7Days => (today.AddDays(-6), today),
            RangePreset.Last30Days => (today.AddDays(-29), today),
            RangePreset.ThisMonth => (monthStart, today),
            RangePreset.PreviousMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            RangePreset.Last90Days => (today.AddDays(-89), today),
            RangePreset.ThisYear => (new DateOnly(today.Year, 1, 1), today),
            RangePreset.AllTime => (null, today),
            _ => Ordered(DateOnly.FromDateTime(customSince), DateOnly.FromDateTime(customUntil))
        };

        static (DateOnly?, DateOnly) Ordered(DateOnly a, DateOnly b) => a <= b ? (a, b) : (b, a);
    }

    /// <summary>„1–26 lis 2026”, „Cała historia”.</summary>
    public static string Describe(RangePreset preset, DateOnly? since, DateOnly until) => since is { } from
        ? Fmt.Range(from.ToDateTime(TimeOnly.MinValue), until.ToDateTime(TimeOnly.MinValue))
        : "Cała historia";
}
