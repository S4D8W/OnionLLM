namespace OnionLLM.Core.Limits;

/// <summary>Zapisany odczyt limitów dostawcy (tabela <c>LimitSnapshots</c> w ledgerze); wykorzystanie w procentach 0–100+.</summary>
public sealed record LimitSnapshot(DateTimeOffset At, string Provider, double FiveHourPercent, double SevenDayPercent,
    DateTimeOffset? FiveHourReset, DateTimeOffset? SevenDayReset);

/// <summary>Prognoza wykorzystania limitu na chwilę resetu (ekran Limity, §6.2).</summary>
public sealed record LimitForecast(double Percent, DateTimeOffset At);

/// <summary>
/// Prognoza liniowa z odczytów bieżącego okna limitu (od ostatniego spadku wartości lub początku okna): nachylenie
/// metodą najmniejszych kwadratów, ekstrapolacja do resetu. Wymaga co najmniej dwóch odczytów rozłożonych na
/// ≥ 10 minut; prognoza nigdy nie jest niższa od wartości bieżącej.
/// </summary>
public static class LimitForecasting
{
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(10);

    public static LimitForecast? Project(IReadOnlyList<(DateTimeOffset At, double Percent)> readings, DateTimeOffset? reset, TimeSpan window, DateTimeOffset now)
    {
        if (reset is not { } end || end <= now)
        {
            return null;
        }

        var start = end - window;
        var points = readings.Where(reading => reading.At >= start && reading.At <= now).OrderBy(reading => reading.At).ToList();

        // Spadek wartości = reset w trakcie (np. wcześniejszy reset okna) — liczymy tylko od niego.
        for (var i = points.Count - 1; i > 0; i--)
        {
            if (points[i].Percent < points[i - 1].Percent)
            {
                points = points.Skip(i).ToList();
                break;
            }
        }

        if (points.Count < 2 || points[^1].At - points[0].At < MinimumSpan)
        {
            return null;
        }

        var origin = points[0].At;
        var xs = points.Select(point => (point.At - origin).TotalHours).ToList();
        var ys = points.Select(point => point.Percent).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        if (denominator <= 0)
        {
            return null;
        }

        var slope = xs.Zip(ys).Sum(pair => (pair.First - meanX) * (pair.Second - meanY)) / denominator;
        var current = ys[^1];
        var projected = current + Math.Max(0, slope) * (end - points[^1].At).TotalHours;
        return new LimitForecast(Math.Max(current, projected), end);
    }
}
