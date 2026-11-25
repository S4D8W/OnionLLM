using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Reports;

public enum BurnRateLevel
{
    Normal,
    Moderate,
    High
}

public enum LimitStatus
{
    Ok,
    Warning,
    Exceeds
}

public sealed record BurnRate(double TokensPerMinute, double NonCacheTokensPerMinute, double CostPerHour)
{
    /// <summary>Poziom wg ccusage: &lt; 2 000 / 2 000–5 000 / &gt; 5 000 tokenów (bez cache) na minutę.</summary>
    public BurnRateLevel Level => NonCacheTokensPerMinute switch
    {
        > 5000 => BurnRateLevel.High,
        > 2000 => BurnRateLevel.Moderate,
        _ => BurnRateLevel.Normal
    };
}

public sealed record BlockProjection(long TotalTokens, double TotalCostUsd, int RemainingMinutes);

/// <summary>Okno rozliczeniowe (domyślnie 5 h) lub przerwa między oknami.</summary>
public sealed record SessionBlock
{
    public required string Id { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public DateTimeOffset? FirstActivity { get; init; }
    public bool IsActive { get; init; }
    public bool IsGap { get; init; }
    public int EntryCount { get; init; }
    public TokenCounts Totals { get; init; } = new();
    public IReadOnlyList<string> Models { get; init; } = [];
    public BurnRate? BurnRate { get; init; }
    public BlockProjection? Projection { get; init; }

    public TimeSpan Elapsed(DateTimeOffset now) => (IsActive ? now : LastActivity ?? End) - Start;

    public TimeSpan Remaining(DateTimeOffset now) => IsActive && End > now ? End - now : TimeSpan.Zero;
}

public sealed record BlocksReport
{
    public IReadOnlyList<SessionBlock> Blocks { get; init; } = [];

    /// <summary>Limit tokenów (jawny albo największy zakończony blok); null = brak danych.</summary>
    public long? TokenLimit { get; init; }

    public bool TokenLimitIsMax { get; init; }

    public SessionBlock? Active => Blocks.LastOrDefault(block => block.IsActive);

    public static LimitStatus Status(long tokens, long? limit) => limit is not { } value || value <= 0
        ? LimitStatus.Ok
        : tokens > value
            ? LimitStatus.Exceeds
            : tokens > value * 0.8
                ? LimitStatus.Warning
                : LimitStatus.Ok;
}

/// <summary>Odtwarza okna rozliczeniowe Claude z historii (algorytm z ccusage <c>claude/src/blocks.rs</c>).</summary>
public static class BlockBuilder
{
    public static BlocksReport Build(
        IEnumerable<UsageEntry> entries,
        CostCalculator calculator,
        DateTimeOffset now,
        double sessionLengthHours = 5,
        long? tokenLimit = null,
        DateTimeOffset? activeBlockResetsAt = null)
    {
        var sorted = entries.OrderBy(entry => entry.TimestampMs).ToList();
        var durationMs = (long)(sessionLengthHours * 3_600_000);
        var nowMs = now.ToUnixTimeMilliseconds();
        var blocks = new List<SessionBlock>();

        long? start = null;
        var current = new List<UsageEntry>();
        foreach (var entry in sorted)
        {
            if (start is { } blockStart)
            {
                var last = current.Count > 0 ? current[^1].TimestampMs : blockStart;
                var startsFresh = entry.TimestampMs - last > durationMs;
                if (entry.TimestampMs - blockStart > durationMs || startsFresh)
                {
                    blocks.Add(CreateBlock(blockStart, current, nowMs, durationMs, calculator, null));
                    current = [];
                    if (startsFresh)
                    {
                        blocks.Add(CreateGap(last, entry.TimestampMs, durationMs));
                    }

                    start = FloorToHour(entry.TimestampMs);
                }
            }
            else
            {
                start = FloorToHour(entry.TimestampMs);
            }

            current.Add(entry);
        }

        if (start is { } finalStart && current.Count > 0)
        {
            blocks.Add(CreateBlock(finalStart, current, nowMs, durationMs, calculator, activeBlockResetsAt));
        }

        long? limit = tokenLimit;
        var isMax = false;
        if (limit is null)
        {
            var completed = blocks.Where(block => !block.IsGap && !block.IsActive).Select(block => block.Totals.TotalTokens).ToArray();
            limit = completed.Length > 0 ? completed.Max() : null;
            isMax = true;
        }

        return new BlocksReport { Blocks = blocks, TokenLimit = limit, TokenLimitIsMax = isMax };
    }

    private static long FloorToHour(long timestampMs) => timestampMs - (timestampMs % 3_600_000);

    private static SessionBlock CreateBlock(long start, List<UsageEntry> entries, long nowMs, long durationMs, CostCalculator calculator, DateTimeOffset? resetsAt)
    {
        var end = start + durationMs;
        var last = entries[^1].TimestampMs;
        var isActive = nowMs - last < durationMs && nowMs < end;

        // Dokładny koniec aktywnego okna z API limitów (jeśli pasuje do szacunku ±1 h).
        if (isActive && resetsAt is { } reset)
        {
            var resetMs = reset.ToUnixTimeMilliseconds();
            if (Math.Abs(resetMs - end) <= 3_600_000 && resetMs > nowMs)
            {
                end = resetMs;
            }
        }

        var totals = new TokenCounts();
        var models = new List<string>();
        foreach (var entry in entries)
        {
            totals.Add(entry, calculator.Calculate(entry));
            if (entry.Model is { } model && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(model);
            }
        }

        var burnRate = CalculateBurnRate(entries, totals);
        BlockProjection? projection = null;
        if (isActive && burnRate is not null)
        {
            var remainingMinutes = (int)Math.Round((end - nowMs) / 60_000.0);
            projection = new BlockProjection(
                totals.TotalTokens + (long)Math.Round(burnRate.TokensPerMinute * remainingMinutes),
                totals.CostUsd + burnRate.CostPerHour / 60 * remainingMinutes,
                remainingMinutes);
        }

        return new SessionBlock
        {
            Id = DateTimeOffset.FromUnixTimeMilliseconds(start).ToString("O"),
            Start = DateTimeOffset.FromUnixTimeMilliseconds(start),
            End = DateTimeOffset.FromUnixTimeMilliseconds(end),
            FirstActivity = DateTimeOffset.FromUnixTimeMilliseconds(entries[0].TimestampMs),
            LastActivity = DateTimeOffset.FromUnixTimeMilliseconds(last),
            IsActive = isActive,
            EntryCount = entries.Count,
            Totals = totals,
            Models = models,
            BurnRate = burnRate,
            Projection = projection
        };
    }

    private static BurnRate? CalculateBurnRate(List<UsageEntry> entries, TokenCounts totals)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var minutes = (entries[^1].TimestampMs - entries[0].TimestampMs) / 60_000.0;
        if (minutes <= 0)
        {
            return null;
        }

        return new BurnRate(
            totals.TotalTokens / minutes,
            (totals.InputTokens + totals.OutputTokens) / minutes,
            totals.CostUsd / minutes * 60);
    }

    private static SessionBlock CreateGap(long lastActivity, long nextActivity, long durationMs)
    {
        var start = lastActivity + durationMs;
        return new SessionBlock
        {
            Id = "gap-" + DateTimeOffset.FromUnixTimeMilliseconds(start).ToString("O"),
            Start = DateTimeOffset.FromUnixTimeMilliseconds(start),
            End = DateTimeOffset.FromUnixTimeMilliseconds(nextActivity),
            IsGap = true
        };
    }
}
