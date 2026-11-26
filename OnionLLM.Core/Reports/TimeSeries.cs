using OnionLLM.Core.Models;

namespace OnionLLM.Core.Reports;

/// <summary>Punkt serii narastającej: chwila, koszt i tokeny od początku encji (sesji, bloku).</summary>
public sealed record CumulativePoint(DateTimeOffset At, double CostUsd, long Tokens);

/// <summary>
/// Rozkład „dni × godziny” (heatmapa Raportów): koszt i tokeny zsumowane po dniu tygodnia (<see cref="DayOfWeek"/>, 0 = niedziela)
/// i godzinie lokalnej 0–23 w strefie raportu.
/// </summary>
public sealed record HourlyReport
{
    public required double[,] Cost { get; init; }

    public required long[,] Tokens { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public double Max
    {
        get
        {
            var max = 0d;
            foreach (var value in Cost)
            {
                max = Math.Max(max, value);
            }

            return max;
        }
    }
}

/// <summary>Sesja w bloku (drawer bloku): udział sesji w koszcie i tokenach bloku.</summary>
public sealed record BlockSession(string Source, string SessionId, string? Project, DateTimeOffset FirstActivity, DateTimeOffset LastActivity,
    TokenCounts Totals);

/// <summary>Szczegóły bloku: koszt narastający w czasie (oś bloku) i sesje, które w nim pracowały.</summary>
public sealed record BlockDetails(IReadOnlyList<CumulativePoint> Points, IReadOnlyList<BlockSession> Sessions);

/// <summary>Budowa serii czasowych z wpisów (jedna implementacja dla UI i testów).</summary>
public static class TimeSeries
{
    /// <summary>Seria narastająca: jeden punkt na wpis, w kolejności czasu.</summary>
    public static IReadOnlyList<CumulativePoint> Cumulative(IEnumerable<UsageEntry> entries, Pricing.CostCalculator calculator)
    {
        var points = new List<CumulativePoint>();
        var cost = 0d;
        var tokens = 0L;
        foreach (var entry in entries.OrderBy(entry => entry.TimestampMs))
        {
            cost += calculator.Calculate(entry).CostUsd;
            tokens += entry.TotalTokens;
            points.Add(new CumulativePoint(DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampMs), cost, tokens));
        }

        return points;
    }

    /// <summary>
    /// Próbki serii narastającej w <paramref name="count"/> równych odstępach od <paramref name="start"/> do <paramref name="end"/>
    /// (wartość = ostatni punkt nie późniejszy niż chwila próbki; przed pierwszym punktem 0) — oś bloku, przebieg sesji.
    /// </summary>
    public static IReadOnlyList<double> Resample(IReadOnlyList<CumulativePoint> points, DateTimeOffset start, DateTimeOffset end, int count)
    {
        if (count < 2 || end <= start)
        {
            return points.Count > 0 ? [0, points[^1].CostUsd] : [];
        }

        var samples = new double[count];
        var index = -1;
        var step = (end - start).Ticks / (double)(count - 1);
        for (var i = 0; i < count; i++)
        {
            var at = start.AddTicks((long)(step * i));
            while (index + 1 < points.Count && points[index + 1].At <= at)
            {
                index++;
            }

            samples[i] = index >= 0 ? points[index].CostUsd : 0;
        }

        return samples;
    }

    public static HourlyReport Hourly(IEnumerable<UsageEntry> entries, Pricing.CostCalculator calculator, TimeZoneInfo zone, DateOnly? from, DateOnly? to)
    {
        var cost = new double[7, 24];
        var tokens = new long[7, 24];
        foreach (var entry in entries)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampMs).UtcDateTime, zone);
            var day = (int)local.DayOfWeek;
            cost[day, local.Hour] += calculator.Calculate(entry).CostUsd;
            tokens[day, local.Hour] += entry.TotalTokens;
        }

        return new HourlyReport { Cost = cost, Tokens = tokens, From = from, To = to };
    }

    public static BlockDetails Block(IEnumerable<UsageEntry> entries, Pricing.CostCalculator calculator)
    {
        var list = entries.OrderBy(entry => entry.TimestampMs).ToList();
        var sessions = list
            .GroupBy(entry => (entry.Source, entry.SessionId))
            .Select(group =>
            {
                var totals = new TokenCounts();
                foreach (var entry in group)
                {
                    totals.Add(entry, calculator.Calculate(entry).CostUsd);
                }

                return new BlockSession(group.Key.Source, group.Key.SessionId, group.First().Project,
                    DateTimeOffset.FromUnixTimeMilliseconds(group.Min(entry => entry.TimestampMs)),
                    DateTimeOffset.FromUnixTimeMilliseconds(group.Max(entry => entry.TimestampMs)), totals);
            })
            .OrderByDescending(session => session.Totals.CostUsd)
            .ToList();
        return new BlockDetails(Cumulative(list, calculator), sessions);
    }
}
