using System.Globalization;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Reports;

/// <summary>Buduje raporty okresowe i sesyjne z (zdeduplikowanych) wpisów.</summary>
public sealed class ReportBuilder(OnionConfig config, CostCalculator calculator)
{
    private readonly TimeZoneInfo _zone = config.ResolveTimeZone();
    private readonly IReadOnlyDictionary<string, string> _aliases = config.ParseProjectAliases();

    public TimeZoneInfo TimeZone => _zone;

    public CostCalculator Calculator => calculator;

    public DateOnly LocalDate(long timestampMs) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime, _zone));

    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);

    /// <summary>Początek dnia lokalnego w UTC ms.</summary>
    public long StartOfDayMs(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _zone)).ToUnixTimeMilliseconds();
    }

    public DateOnly PeriodStart(DateOnly date, ReportPeriod period) => period switch
    {
        ReportPeriod.Daily => date,
        ReportPeriod.Weekly => date.AddDays(-(((int)date.DayOfWeek - (int)config.StartOfWeek + 7) % 7)),
        ReportPeriod.Monthly => new DateOnly(date.Year, date.Month, 1),
        _ => date
    };

    /// <summary>Zakres dat [from, to] wynikający z zapytania (null = bez ograniczenia).</summary>
    public (DateOnly? From, DateOnly? To) ResolveRange(ReportQuery query, ReportPeriod period, DateTimeOffset now)
    {
        query.Validate();
        var from = query.Since;
        var to = query.Until;
        if (query.Last is { } last)
        {
            var currentStart = PeriodStart(Today(now), period);
            var lastFrom = period switch
            {
                ReportPeriod.Daily => currentStart.AddDays(-(last - 1)),
                ReportPeriod.Weekly => currentStart.AddDays(-7 * (last - 1)),
                ReportPeriod.Monthly => currentStart.AddMonths(-(last - 1)),
                _ => currentStart
            };
            from = from is null || lastFrom > from ? lastFrom : from;
        }

        return (from, to);
    }

    /// <summary>Zakres UTC ms dla zapytania do ledgera (z marginesem na strefę).</summary>
    public (long? FromMs, long? ToMs) ToUtcRange(DateOnly? from, DateOnly? to) =>
        (from is null ? null : StartOfDayMs(from.Value), to is null ? null : StartOfDayMs(to.Value.AddDays(1)));

    public string DisplayProject(UsageEntry entry) => DisplayProject(entry.Project, entry.ProjectPath);

    public string DisplayProject(string? project, string? projectPath)
    {
        if (project is not null && _aliases.TryGetValue(project, out var alias))
        {
            return alias;
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var name = Path.GetFileName(projectPath.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(name))
            {
                return _aliases.TryGetValue(name, out var pathAlias) ? pathAlias : name;
            }
        }

        return project ?? "(bez projektu)";
    }

    public bool MatchesProject(UsageEntry entry, string project) =>
        string.Equals(entry.Project, project, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DisplayProject(entry), project, StringComparison.OrdinalIgnoreCase) ||
        (entry.ProjectPath is not null && string.Equals(entry.ProjectPath.TrimEnd('/', '\\'), project.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<UsageEntry> Filter(IEnumerable<UsageEntry> entries, ReportQuery query, DateOnly? from, DateOnly? to)
    {
        foreach (var entry in entries)
        {
            if (query.Sources.Count > 0 && !query.Sources.Contains(entry.Source, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Project is not null && !MatchesProject(entry, query.Project))
            {
                continue;
            }

            if (from is not null || to is not null)
            {
                var date = LocalDate(entry.TimestampMs);
                if ((from is not null && date < from) || (to is not null && date > to))
                {
                    continue;
                }
            }

            yield return entry;
        }
    }

    public UsageReport Build(IEnumerable<UsageEntry> entries, ReportPeriod period, ReportQuery query, DateTimeOffset now)
    {
        var (from, to) = ResolveRange(query, period, now);
        var groups = new Dictionary<(DateOnly Start, string? Project), Accumulator>();
        var totals = new Accumulator();

        foreach (var entry in Filter(entries, query, from, to))
        {
            var start = PeriodStart(LocalDate(entry.TimestampMs), period);
            var project = query.GroupByProject ? DisplayProject(entry) : null;
            if (!groups.TryGetValue((start, project), out var accumulator))
            {
                groups[(start, project)] = accumulator = new Accumulator();
            }

            var cost = calculator.Calculate(entry);
            accumulator.Add(entry, cost);
            totals.Add(entry, cost);
        }

        var ordered = groups.OrderBy(group => group.Key.Start).ThenBy(group => group.Key.Project, StringComparer.OrdinalIgnoreCase);
        var rows = (query.Order == SortOrder.Desc
                ? groups.OrderByDescending(group => group.Key.Start).ThenBy(group => group.Key.Project, StringComparer.OrdinalIgnoreCase)
                : ordered)
            .Select(group => group.Value.ToRow(PeriodKey(group.Key.Start, period), group.Key.Start, group.Key.Project))
            .ToArray();

        return new UsageReport
        {
            Period = period,
            Rows = rows,
            Totals = totals.Totals,
            UnpricedModels = totals.UnpricedModels,
            EstimatedModels = totals.EstimatedModels,
            From = from,
            To = to
        };
    }

    public SessionReport BuildSessions(IEnumerable<UsageEntry> entries, ReportQuery query, DateTimeOffset now, string? sessionId = null)
    {
        var (from, to) = ResolveRange(query, ReportPeriod.Daily, now);
        var groups = new Dictionary<(string Source, string Session), SessionAccumulator>();
        var totals = new Accumulator();

        foreach (var entry in Filter(entries, query, from, to))
        {
            if (sessionId is not null && !entry.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!groups.TryGetValue((entry.Source, entry.SessionId), out var accumulator))
            {
                groups[(entry.Source, entry.SessionId)] = accumulator = new SessionAccumulator(entry);
            }

            var cost = calculator.Calculate(entry);
            accumulator.Add(entry, cost);
            totals.Add(entry, cost);
        }

        var sessions = groups.Values.Select(accumulator => accumulator.ToRow(this));
        sessions = query.Order == SortOrder.Desc
            ? sessions.OrderByDescending(row => row.LastActivity)
            : sessions.OrderBy(row => row.LastActivity);

        return new SessionReport
        {
            Sessions = sessions.ToArray(),
            Totals = totals.Totals,
            UnpricedModels = totals.UnpricedModels,
            EstimatedModels = totals.EstimatedModels
        };
    }

    public static string PeriodKey(DateOnly start, ReportPeriod period) => period switch
    {
        ReportPeriod.Monthly => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    /// <summary>Sumy z podziałem na modele (bez dalszego podziału).</summary>
    private sealed class ModelAccumulator
    {
        private readonly Dictionary<string, (TokenCounts Totals, bool Unpriced, bool Estimated)> _models = new(StringComparer.OrdinalIgnoreCase);

        public TokenCounts Totals { get; } = new();

        public void Add(UsageEntry entry, CostResult cost)
        {
            Totals.Add(entry, cost);
            var model = entry.Model ?? UnknownModel;
            if (!_models.TryGetValue(model, out var modelTotals))
            {
                modelTotals = (new TokenCounts(), false, false);
            }

            modelTotals.Totals.Add(entry, cost);
            _models[model] = (modelTotals.Totals, modelTotals.Unpriced || cost.IsUnpriced, modelTotals.Estimated || cost.IsEstimated);
        }

        public IReadOnlyList<string> UnpricedModels =>
            _models.Where(pair => pair.Value.Unpriced).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

        public IReadOnlyList<string> EstimatedModels =>
            _models.Where(pair => pair.Value.Estimated).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

        public IReadOnlyList<string> Models =>
            _models.OrderByDescending(pair => pair.Value.Totals.CostUsd).ThenByDescending(pair => pair.Value.Totals.TotalTokens)
                .Select(pair => pair.Key).ToArray();

        public IReadOnlyList<ModelBreakdown> Breakdowns =>
            _models.OrderByDescending(pair => pair.Value.Totals.CostUsd).ThenByDescending(pair => pair.Value.Totals.TotalTokens)
                .Select(pair => new ModelBreakdown(pair.Key, pair.Value.Totals, pair.Value.Unpriced, pair.Value.Estimated)).ToArray();
    }

    /// <summary>Sumy wiersza: modele i źródła (każde źródło z własnym podziałem na modele).</summary>
    private sealed class Accumulator
    {
        private readonly ModelAccumulator _all = new();
        private readonly Dictionary<string, ModelAccumulator> _sources = new(StringComparer.OrdinalIgnoreCase);

        public TokenCounts Totals => _all.Totals;

        public IReadOnlyList<string> UnpricedModels => _all.UnpricedModels;

        public IReadOnlyList<string> EstimatedModels => _all.EstimatedModels;

        public void Add(UsageEntry entry, CostResult cost)
        {
            _all.Add(entry, cost);
            if (!_sources.TryGetValue(entry.Source, out var source))
            {
                _sources[entry.Source] = source = new ModelAccumulator();
            }

            source.Add(entry, cost);
        }

        public ReportRow ToRow(string period, DateOnly start, string? project) => new()
        {
            Period = period,
            PeriodStart = start,
            Project = project,
            Totals = _all.Totals,
            Models = _all.Models,
            Sources = _sources.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            UnpricedModels = _all.UnpricedModels,
            EstimatedModels = _all.EstimatedModels,
            ModelBreakdowns = _all.Breakdowns,
            SourceBreakdowns = _sources.OrderByDescending(pair => pair.Value.Totals.CostUsd).ThenByDescending(pair => pair.Value.Totals.TotalTokens)
                .Select(pair => new SourceBreakdown(pair.Key, pair.Value.Totals)
                {
                    Models = pair.Value.Models,
                    ModelBreakdowns = pair.Value.Breakdowns
                }).ToArray()
        };
    }

    private sealed class SessionAccumulator(UsageEntry first)
    {
        private readonly Accumulator _accumulator = new();
        private long _first = first.TimestampMs;
        private long _last = first.TimestampMs;
        private string? _project = first.Project;
        private string? _projectPath = first.ProjectPath;

        public void Add(UsageEntry entry, CostResult cost)
        {
            _accumulator.Add(entry, cost);
            _first = Math.Min(_first, entry.TimestampMs);
            _last = Math.Max(_last, entry.TimestampMs);
            _project ??= entry.Project;
            _projectPath ??= entry.ProjectPath;
        }

        public SessionRow ToRow(ReportBuilder builder)
        {
            var row = _accumulator.ToRow("", default, null);
            return new SessionRow
            {
                SessionId = first.SessionId,
                Source = first.Source,
                Project = builder.DisplayProject(_project, _projectPath),
                ProjectPath = _projectPath,
                FirstActivity = DateTimeOffset.FromUnixTimeMilliseconds(_first),
                LastActivity = DateTimeOffset.FromUnixTimeMilliseconds(_last),
                Totals = row.Totals,
                Models = row.Models,
                UnpricedModels = row.UnpricedModels,
                EstimatedModels = row.EstimatedModels,
                ModelBreakdowns = row.ModelBreakdowns
            };
        }
    }

    /// <summary>Nazwa używana dla wpisów bez modelu.</summary>
    public const string UnknownModel = "(nieznany)";
}
