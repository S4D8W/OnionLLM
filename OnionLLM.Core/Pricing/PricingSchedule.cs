namespace OnionLLM.Core.Pricing;

/// <summary>Stawki obowiązujące w jednym okresie harmonogramu.</summary>
public sealed record ScheduledRates(double Input, double Output, double CacheWrite, double CacheRead, PricingTier? LongContext = null);

/// <summary>
/// Harmonogram cen dostawcy zależny od daty i pory (ccusage <c>rates.rs</c> <c>PricingSchedule</c>):
/// przed <see cref="CutoffMs"/> stawki <see cref="Before"/>, potem szczyt/poza szczytem według dnia tygodnia i godziny UTC,
/// a od <see cref="Successor"/> — inny harmonogram.
/// </summary>
public sealed class PricingSchedule
{
    public required long CutoffMs { get; init; }

    /// <summary>Dni tygodnia szczytu (UTC).</summary>
    public DayOfWeek[] PeakWeekdays { get; init; } = [];

    /// <summary>Godziny szczytu UTC jako przedziały [od, do).</summary>
    public (int From, int To)[] PeakHours { get; init; } = [];

    public required ScheduledRates Before { get; init; }
    public required ScheduledRates OffPeak { get; init; }
    public required ScheduledRates Peak { get; init; }
    public (long FromMs, PricingSchedule Schedule)? Successor { get; init; }

    /// <summary>Stosowany do wiersza niezależnie od sposobu dopasowania nazwy (Anthropic 4.6).</summary>
    public bool FollowsRow { get; init; }

    /// <summary>Tryb fast nie płaci progu z harmonogramu.</summary>
    public bool FlatInFastMode { get; init; }

    /// <summary>Dostawca, którego cennik opisuje harmonogram (<c>deepseek</c>, <c>google</c>, <c>xai</c>, <c>anthropic</c>).</summary>
    public required string Vendor { get; init; }

    public ScheduledRates RatesAt(long timestampMs)
    {
        if (Successor is { } successor && timestampMs >= successor.FromMs)
        {
            return successor.Schedule.RatesAt(timestampMs);
        }

        if (timestampMs < CutoffMs)
        {
            return Before;
        }

        var moment = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var hour = moment.Hour;
        return PeakWeekdays.Contains(moment.DayOfWeek) && PeakHours.Any(window => hour >= window.From && hour < window.To) ? Peak : OffPeak;
    }

    /// <summary>
    /// Stawki wiersza w chwili <paramref name="timestampMs"/> (ccusage <c>Pricing::at</c>): pola podane jawnie w nadpisaniu zostają,
    /// pozostałe pochodzą z harmonogramu. Próg z harmonogramu zastępuje próg wiersza (jawna stawka bazowa go wyłącza).
    /// </summary>
    internal ModelPricing ApplyTo(ModelPricing pricing, long timestampMs)
    {
        var rates = RatesAt(timestampMs);
        var explicitRates = pricing.Explicit;
        var explicitBase = explicitRates.Input is not null || explicitRates.Output is not null || explicitRates.CacheWrite is not null ||
                           explicitRates.CacheRead is not null || explicitRates.CacheWrite1h is not null;
        var scheduledTier = explicitBase ? null : rates.LongContext;

        IReadOnlyList<PricingTier> tiers = scheduledTier is not null
            ? [pricing.ExplicitTier is { } own ? scheduledTier with { Rates = own.Rates.Over(scheduledTier.Rates) } : scheduledTier]
            : pricing.ExplicitTier is { } explicitTier ? [explicitTier] : [];

        return pricing with
        {
            Base = new RateSet
            {
                Input = explicitRates.Input ?? rates.Input,
                Output = explicitRates.Output ?? rates.Output,
                CacheWrite = explicitRates.CacheWrite ?? rates.CacheWrite,
                CacheRead = explicitRates.CacheRead ?? rates.CacheRead,
                CacheWrite1h = pricing.Base.CacheWrite1h
            },
            Tiers = tiers,
            FlatLongContextInFastMode = scheduledTier is not null && FlatInFastMode,
            Schedule = null
        };
    }
}

/// <summary>Harmonogramy cen z ccusage <c>pricing/data.rs</c> (<c>SCHEDULES</c>).</summary>
public static class PricingSchedules
{
    private const double M = 1e-6;
    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    private static readonly DayOfWeek[] EveryDay = Enum.GetValues<DayOfWeek>();

    // DeepSeek: okna szczytu w dni robocze 01:00–04:00 i 06:00–10:00 UTC (ccusage 2026-11-17).
    private static readonly (int, int)[] DeepSeekPeakHours = [(1, 4), (6, 10)];

    // NOTE(ccusage #1679): DeepSeek V4 zmienił stawki 2026-08-16 16:00 UTC; wcześniejsze użycie po starych stawkach.
    private const long DeepSeekV4CutoffMs = 1_786_896_000_000;

    // NOTE(ccusage #2401): nowy cennik od 2026-09-10 04:00 UTC; wycofane ID V4 Flash przechodzą na stawki deepseek-flash.
    private const long DeepSeekV41FlashMs = 1_789_012_800_000;

    // Anthropic: okno 1M wyszło z bety po standardowej cenie 2026-03-13 (ccusage #2451).
    private const long Anthropic1MGaMs = 1_773_360_000_000;
    private const long GeminiDecember2026Ms = 1_796_083_200_000;
    private const long GeminiJanuary2027Ms = 1_798_761_600_000;

    // xAI: grok-code-fast-1 jako alias grok-build-0.1 od ostatniego zrzutu ze starymi stawkami (ccusage #2227).
    private const long GrokCodeFastSwitchMs = 1_778_857_152_000;

    private static ScheduledRates Rates(double input, double output, double cacheWrite, double cacheRead, PricingTier? tier = null) =>
        new(input * M, output * M, cacheWrite * M, cacheRead * M, tier);

    private static PricingTier WholeRequest(long threshold, double input, double output, double cacheWrite, double? cacheWrite1h, double cacheRead) =>
        new(threshold, new RateSet
        {
            Input = input * M,
            Output = output * M,
            CacheWrite = cacheWrite * M,
            CacheWrite1h = cacheWrite1h * M,
            CacheRead = cacheRead * M
        });

    private static readonly PricingSchedule DeepSeekFlash = new()
    {
        CutoffMs = DeepSeekV41FlashMs,
        PeakWeekdays = Weekdays,
        PeakHours = DeepSeekPeakHours,
        Before = Rates(0.3, 1.2, 0.3, 0.006),
        OffPeak = Rates(0.15, 0.6, 0.15, 0.003),
        Peak = Rates(0.3, 1.2, 0.3, 0.006),
        Vendor = "deepseek"
    };

    private static readonly PricingSchedule DeepSeekV4Flash = new()
    {
        CutoffMs = DeepSeekV4CutoffMs,
        PeakWeekdays = Weekdays,
        PeakHours = DeepSeekPeakHours,
        Before = Rates(0.14, 0.28, 0.14, 0.0028),
        OffPeak = Rates(0.22, 0.66, 0.22, 0.007),
        Peak = Rates(0.44, 1.32, 0.44, 0.014),
        Successor = (DeepSeekV41FlashMs, DeepSeekFlash),
        Vendor = "deepseek"
    };

    // NOTE(ccusage #2167): V4 Pro zachował swoje stawki po premierze V4.1 Flash.
    private static readonly PricingSchedule DeepSeekV4Pro = new()
    {
        CutoffMs = DeepSeekV4CutoffMs,
        PeakWeekdays = Weekdays,
        PeakHours = DeepSeekPeakHours,
        Before = Rates(0.435, 0.87, 0.435, 0.003625),
        OffPeak = Rates(0.66, 1.98, 0.66, 0.022),
        Peak = Rates(1.32, 3.96, 1.32, 0.044),
        Vendor = "deepseek"
    };

    /// <summary>Dopłata za kontekst &gt; 200k dla Opus/Sonnet 4.6 do 2026-03-13; tryb fast jej nie płacił (ccusage #2451).</summary>
    private static PricingSchedule PremiumUntil1MGa(double input, double output, PricingTier tier)
    {
        var standard = Rates(input, output, input * 1.25, input * 0.1);
        return new PricingSchedule
        {
            CutoffMs = Anthropic1MGaMs,
            PeakWeekdays = Weekdays,
            Before = standard with { LongContext = tier },
            OffPeak = standard,
            Peak = standard,
            FollowsRow = true,
            FlatInFastMode = true,
            Vendor = "anthropic"
        };
    }

    private static readonly PricingSchedule Sonnet46Beta = PremiumUntil1MGa(3, 15, WholeRequest(200_000, 6, 22.5, 7.5, 12, 0.6));
    private static readonly PricingSchedule Opus46Beta = PremiumUntil1MGa(5, 25, WholeRequest(200_000, 10, 37.5, 12.5, 20, 1));

    private static PricingSchedule DateStep(long cutoffMs, ScheduledRates before, ScheduledRates after) => new()
    {
        CutoffMs = cutoffMs,
        PeakWeekdays = EveryDay,
        Before = before,
        OffPeak = after,
        Peak = after,
        Vendor = "google"
    };

    // NOTE(ccusage #2226): Gemini 4 Argon/Carbon $2/$10 do 2026-11-30, od 2026-12-01 $4/$20.
    private static readonly PricingSchedule Gemini4ProTier = DateStep(GeminiDecember2026Ms, Rates(2, 10, 2, 0.1), Rates(4, 20, 4, 0.2));

    // Gemini 3.6/3.7/3.8 Flash: $0.75/$3.75 do końca 2026, od 2027-01-01 $1.50/$7.50.
    private static readonly PricingSchedule GeminiFlash2027 = DateStep(GeminiJanuary2027Ms, Rates(0.75, 3.75, 0.75, 0.075), Rates(1.5, 7.5, 1.5, 0.15));

    // Gemini 2.5 Pro: od 2026-12-01 kierowany na 3.8 Flash (i jego stawki od 2027-01-01).
    private static readonly PricingSchedule Gemini25Pro = new()
    {
        CutoffMs = GeminiDecember2026Ms,
        PeakWeekdays = EveryDay,
        Before = Rates(1.25, 10, 1.25, 0.125, WholeRequest(200_000, 2.5, 15, 2.5, null, 0.25)),
        OffPeak = Rates(0.75, 3.75, 0.75, 0.075),
        Peak = Rates(0.75, 3.75, 0.75, 0.075),
        Successor = (GeminiJanuary2027Ms, GeminiFlash2027),
        Vendor = "google"
    };

    // Gemini 2.5 Flash: od 2026-12-01 kierowany na 3.5 Flash-Lite po tych samych stawkach.
    private static readonly PricingSchedule Gemini25Flash = DateStep(GeminiDecember2026Ms, Rates(0.3, 2.5, 0.3, 0.03), Rates(0.3, 2.5, 0.3, 0.03));
    private static readonly PricingSchedule Gemini38FlashTts = DateStep(GeminiJanuary2027Ms, Rates(0.5, 9, 0.5, 0.05), Rates(1, 18, 1, 0.1));
    private static readonly PricingSchedule Gemini38FlashLiteTts = DateStep(GeminiJanuary2027Ms, Rates(0.5, 6, 0.5, 0.05), Rates(1, 12, 1, 0.1));
    private static readonly PricingSchedule GeminiRoboticsEr2 = DateStep(GeminiJanuary2027Ms, Rates(1, 5, 1, 0.1), Rates(2, 10, 2, 0.2));

    private static readonly ScheduledRates GrokCodeFastAsGrokBuild = Rates(1, 2, 1, 0.2, new PricingTier(199_999, new RateSet
    {
        Input = 2 * M,
        Output = 4 * M,
        CacheWrite = 2 * M,
        CacheRead = 0.4 * M
    }));

    private static readonly PricingSchedule GrokCodeFast1 = new()
    {
        CutoffMs = GrokCodeFastSwitchMs,
        PeakWeekdays = Weekdays,
        Before = Rates(0.2, 1.5, 0.2, 0.02),
        OffPeak = GrokCodeFastAsGrokBuild,
        Peak = GrokCodeFastAsGrokBuild,
        Vendor = "xai"
    };

    private static readonly Dictionary<string, PricingSchedule> Schedules = new(StringComparer.Ordinal)
    {
        ["deepseek-flash"] = DeepSeekFlash,
        ["deepseek-v4-flash"] = DeepSeekV4Flash,
        ["deepseek-v4-flash-vision-exp"] = DeepSeekV4Flash,
        ["deepseek-v4-pro"] = DeepSeekV4Pro,
        ["claude-sonnet-4-6"] = Sonnet46Beta,
        ["claude-opus-4-6"] = Opus46Beta,
        ["grok-code-fast-1"] = GrokCodeFast1,
        ["grok-code-fast"] = GrokCodeFast1,
        ["grok-code-fast-1-0825"] = GrokCodeFast1,
        ["gemini-4-argon"] = Gemini4ProTier,
        ["gemini-4-carbon"] = Gemini4ProTier,
        ["gemini-3-8-flash"] = GeminiFlash2027,
        ["gemini-3-7-flash"] = GeminiFlash2027,
        ["gemini-3-6-flash"] = GeminiFlash2027,
        ["gemini-2-5-pro"] = Gemini25Pro,
        ["gemini-2-5-flash"] = Gemini25Flash,
        ["gemini-3-8-flash-tts"] = Gemini38FlashTts,
        ["gemini-3-8-flash-lite-tts"] = Gemini38FlashLiteTts,
        ["gemini-robotics-er-2-preview"] = GeminiRoboticsEr2,
        ["gemini-robotics-er-2-streaming-preview"] = GeminiRoboticsEr2
    };

    /// <summary>Harmonogram dla identyfikatora modelu u dostawcy (dokładna tożsamość, ccusage <c>schedule_for</c>).</summary>
    public static PricingSchedule? For(string? model) =>
        model is not null && Schedules.TryGetValue(PricingKeys.Identity(model), out var schedule) ? schedule : null;

    private sealed record CuratedLongContext(double Input, double Output, PricingTier? Tier, string? Schedule);

    // NOTE(ccusage #2235): Sonnet 4/4.5 powyżej 200k — całe żądanie $6 / $22.50 (+ mnożniki cache).
    private static readonly CuratedLongContext Sonnet4LongContext = new(3, 15, WholeRequest(200_000, 6, 22.5, 7.5, 12, 0.6), null);

    // Haiku 5.5: całe żądanie powyżej 100k tokenów po stawkach progu.
    private static readonly CuratedLongContext Haiku55LongContext = new(0.1, 0.5, WholeRequest(100_000, 0.5, 2.5, 0.625, 1, 0.05), null);

    /// <summary>Kuratorskie progi Anthropic (ccusage <c>ANTHROPIC_LONG_CONTEXT</c>): 4.6+ cały 1M po stawce standardowej.</summary>
    private static readonly Dictionary<string, CuratedLongContext> AnthropicLongContext = new(StringComparer.Ordinal)
    {
        ["claude-sonnet-4"] = Sonnet4LongContext,
        ["claude-sonnet-4-5"] = Sonnet4LongContext,
        ["claude-haiku-5-5"] = Haiku55LongContext,
        ["claude-sonnet-4-6"] = new(3, 15, null, "claude-sonnet-4-6"),
        ["claude-4-6-sonnet"] = new(3, 15, null, "claude-sonnet-4-6"),
        ["claude-opus-4-6"] = new(5, 25, null, "claude-opus-4-6"),
        ["claude-opus-4-7"] = new(5, 25, null, null),
        ["claude-opus-4-8"] = new(5, 25, null, null)
    };

    /// <summary>
    /// Kuratorski próg dla wiersza Claude po stawkach listowych (ccusage <c>curate_long_context</c>); host z własnymi stawkami
    /// zachowuje swoje. Zwraca null, gdy wiersz nie podlega kuracji.
    /// </summary>
    internal static ModelPricing? Curate(ModelPricing row)
    {
        var segment = PricingKeys.LastSegment(row.Key).ToLowerInvariant();
        var start = segment.IndexOf("claude-", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var model = segment[start..];
        var colon = model.IndexOf(':');
        if (colon >= 0)
        {
            model = model[..colon];
        }

        var revision = model.LastIndexOf("-v", StringComparison.Ordinal);
        if (revision >= 0 && revision + 2 < model.Length && model[(revision + 2)..].All(char.IsAsciiDigit))
        {
            model = model[..revision];
        }

        model = PricingKeys.Identity(model);
        if (model.EndsWith("-thinking", StringComparison.Ordinal))
        {
            model = model[..^"-thinking".Length];
        }

        model = PricingKeys.WithoutDateSuffix(model);
        if (!AnthropicLongContext.TryGetValue(model, out var curated) ||
            row.Base.Input is not { } input || row.Base.Output is not { } output ||
            Math.Abs(input - curated.Input * M) > 1e-15 || Math.Abs(output - curated.Output * M) > 1e-15)
        {
            return null;
        }

        return row with
        {
            Tiers = curated.Tier is null ? [] : [curated.Tier],
            Schedule = curated.Schedule is null ? row.Schedule : For(curated.Schedule)
        };
    }
}
