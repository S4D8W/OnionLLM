using OnionLLM.Core.Config;
using OnionLLM.Core.Limits;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core;

/// <summary>Podsumowanie jednego źródła na ekran „Dziś”.</summary>
public sealed record SourceSummary
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Vendor { get; init; }
    public required string AccentColor { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsInstalled { get; init; }
    public TokenCounts Today { get; init; } = new();
    public TokenCounts Last7Days { get; init; } = new();
    public TokenCounts Last30Days { get; init; } = new();
    public int ActiveSessions { get; init; }
    public int CompletedSessions { get; init; }
    public IReadOnlyList<string> TopModels { get; init; } = [];
    public bool HasUnpricedUsage { get; init; }
    public bool HasEstimatedModel { get; init; }
    public ProviderLimits? Limits { get; init; }

    public bool HasDataToday => Today.TotalTokens > 0;
}

public sealed record DashboardSnapshot
{
    public IReadOnlyList<SourceSummary> Sources { get; init; } = [];
    public TokenCounts Today { get; init; } = new();
    public TokenCounts Last7Days { get; init; } = new();
    public TokenCounts Last30Days { get; init; } = new();
    public SessionBlock? ActiveBlock { get; init; }
    public long? BlockTokenLimit { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Koszt dzienny z 30 dni (od najstarszego; ostatni = dziś) — sparkline KPI i porównania na „Dziś”.</summary>
    public IReadOnlyList<double> DailyCost { get; init; } = [];

    /// <summary>Koszt wczoraj (TrendDelta „vs wczoraj”); null bez serii.</summary>
    public double? YesterdayCost => DailyCost.Count >= 2 ? DailyCost[^2] : null;

    /// <summary>Średni koszt dzienny 7 dni przed dziś (TrendDelta „vs średnia 7 dni”); null bez serii.</summary>
    public double? Average7DaysCost => DailyCost.Count >= 8 ? DailyCost.Skip(DailyCost.Count - 8).Take(7).Average() : null;
    public PricingOrigin PricingOrigin { get; init; }
    public SyncReport? Sync { get; init; }
}

/// <summary>
/// Fasada logiki OnionLLM dla GUI i CLI: synchronizacja ledgera, cennik, raporty, bloki.
/// Metody są bezpieczne do wywołania z wątku w tle.
/// </summary>
public sealed class UsageEngine : IDisposable
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ProviderLimitService _limitService = new();
    private PricingCatalog? _pricing;
    private DateTimeOffset _lastSyncAt = DateTimeOffset.MinValue;
    private DateTimeOffset _pricingLoadedAt = DateTimeOffset.MinValue;

    public UsageEngine(OnionConfig? config = null, UsageLedger? ledger = null, IReadOnlyList<IUsageSource>? sources = null)
    {
        Config = config ?? new OnionConfig();
        Ledger = ledger ?? new UsageLedger();
        Sources = sources ?? SourceRegistry.CreateAll();
    }

    public OnionConfig Config { get; private set; }

    public UsageLedger Ledger { get; }

    public IReadOnlyList<IUsageSource> Sources { get; }

    public SyncReport? LastSync { get; private set; }

    public PricingCatalog Pricing => _pricing ??= PricingCatalog.Embedded(Config);

    public CostCalculator Calculator => new(Pricing, Config.CostMode, FindSource);

    public ReportBuilder Reports => new(Config, Calculator);

    public void UpdateConfig(OnionConfig config)
    {
        Config = config;
        _pricing = null;
        _pricingLoadedAt = DateTimeOffset.MinValue;
        _lastSyncAt = DateTimeOffset.MinValue;
    }

    public IUsageSource? FindSource(string id) =>
        Sources.FirstOrDefault(source => source.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ładuje cennik (online co 24 h, chyba że tryb offline).</summary>
    public async Task<PricingCatalog> LoadPricingAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (_pricing is not null && !forceRefresh && DateTimeOffset.UtcNow - _pricingLoadedAt < TimeSpan.FromHours(6))
        {
            return _pricing;
        }

        _pricing = await PricingCatalog.LoadAsync(Config, forceRefresh, cancellationToken);
        _pricingLoadedAt = DateTimeOffset.UtcNow;
        return _pricing;
    }

    /// <summary>Synchronizuje ledger z plikami źródeł (nie częściej niż co <paramref name="minInterval"/>, chyba że <paramref name="force"/>).</summary>
    public async Task<SyncReport?> SyncAsync(bool force = false, TimeSpan? minInterval = null, CancellationToken cancellationToken = default,
        IProgress<SyncProgress>? progress = null)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var interval = minInterval ?? TimeSpan.FromSeconds(5);
            if (!force && LastSync is not null && DateTimeOffset.UtcNow - _lastSyncAt < interval)
            {
                return LastSync;
            }

            LastSync = await Task.Run(() => Ledger.Sync(Sources, Config, cancellationToken, progress), cancellationToken);
            _lastSyncAt = DateTimeOffset.UtcNow;
            return LastSync;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Zdeduplikowane wpisy z zakresu UTC [fromMs, toMs).</summary>
    /// <param name="includeSessionReportOnly">Uwzględnij wpisy tylko do raportu sesji bez filtra dat (<see cref="IUsageSource.IsSessionReportOnly"/>).</param>
    public List<UsageEntry> LoadEntries(long? fromMs = null, long? toMs = null, IReadOnlyCollection<string>? sources = null,
        bool includeSessionReportOnly = false)
    {
        var enabled = sources is { Count: > 0 }
            ? sources
            : Sources.Where(source => Config.IsSourceEnabled(source.Id)).Select(source => source.Id).ToArray();
        if (enabled.Count == 0)
        {
            return [];
        }

        var requested = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var bounded = fromMs is not null || toMs is not null;

        // Źródła, których uzgadnianie zależy od całej historii (np. Copilot: OTel vs shutdown), czytamy bez zakresu dat;
        // zakres nakładamy dopiero po deduplikacji, żeby raport ograniczony datami liczył tak samo jak pełny.
        var fullHistory = bounded
            ? enabled.Where(id => FindSource(id)?.ReconcileNeedsFullHistory == true).ToArray()
            : [];
        var ranged = enabled.Except(fullHistory, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = ranged.Length > 0 ? WithSettings(Ledger.Query(fromMs, toMs, ranged)) : [];

        // Pozostałe źródła mogą wskazać wpisy spoza zakresu, z którymi uzgadniają wpisy z zakresu (ReconcileKeyPrefixes);
        // dociągamy tylko je, a zakres nakładamy po deduplikacji — wynik jak przy pełnej historii, bez jej wczytywania.
        var related = bounded ? LoadReconcileRelated(entries, fromMs, toMs) : [];
        if (related.Count > 0)
        {
            // Stabilnie po czasie: dociągnięte wpisy leżą poza zakresem, więc kolejność (ts, plik) jest jak w pełnej historii.
            entries = entries.Concat(related).OrderBy(entry => entry.TimestampMs).ToList();
        }

        if (fullHistory.Length > 0)
        {
            entries.AddRange(WithSettings(Ledger.Query(null, null, fullHistory)));
        }

        // Wpisy-lustra (np. OpenCode claude-agent → sesja Claude Code) wymagają wpisów źródła docelowego, także gdy widok
        // go nie obejmuje. Okno źródła docelowego jest dłuższe o dzień, jak w ccusage (adapter-all/src/loader.rs:161-176).
        var targets = entries.Where(entry => entry.MirrorOf is not null)
            .Select(entry => entry.MirrorOf!.Split(':')[0])
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length > 0)
        {
            var extra = WithSettings(Ledger.Query(fromMs, toMs + MirrorWindowMs, targets))
                .Where(entry => !requested.Contains(entry.Source) || entry.TimestampMs >= toMs);
            entries.AddRange(extra);
        }

        if (!includeSessionReportOnly)
        {
            entries.RemoveAll(entry => FindSource(entry.Source)?.IsSessionReportOnly(entry) == true);
        }

        var result = Deduplicator.Apply(entries, FindSource);
        if (targets.Length == 0 && fullHistory.Length == 0 && related.Count == 0)
        {
            return result;
        }

        return result
            .Where(entry => requested.Contains(entry.Source) &&
                            (fromMs is null || entry.TimestampMs >= fromMs) &&
                            (toMs is null || entry.TimestampMs < toMs))
            .ToList();
    }

    private const long MirrorWindowMs = 24 * 60 * 60 * 1000;

    /// <summary>
    /// Wpisy spoza zakresu [fromMs, toMs), które <see cref="IUsageSource.Reconcile"/> może połączyć z wpisami z zakresu
    /// (<see cref="IUsageSource.ReconcileKeyPrefixes"/>, np. oryginał kopii Claude sprzed <c>--since</c>).
    /// </summary>
    private List<UsageEntry> LoadReconcileRelated(List<UsageEntry> entries, long? fromMs, long? toMs)
    {
        var related = new List<UsageEntry>();
        foreach (var group in entries.GroupBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase))
        {
            if (FindSource(group.Key)?.ReconcileKeyPrefixes(group.ToList()) is { Count: > 0 } prefixes)
            {
                related.AddRange(WithSettings(Ledger.QueryKeyPrefixesOutside(group.Key, prefixes, fromMs, toMs)));
            }
        }

        return related;
    }

    /// <summary>Ustawienia źródeł stosowane przy odczycie (<see cref="IUsageSource.ApplySettings"/>).</summary>
    private List<UsageEntry> WithSettings(List<UsageEntry> entries)
    {
        var bySource = new Dictionary<string, (IUsageSource? Source, SourceSettings Settings)>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!bySource.TryGetValue(entry.Source, out var rule))
            {
                bySource[entry.Source] = rule = (FindSource(entry.Source), Config.GetSource(entry.Source));
            }

            if (rule.Source is not null)
            {
                entries[index] = rule.Source.ApplySettings(entry, rule.Settings);
            }
        }

        return entries;
    }

    public UsageReport BuildReport(ReportPeriod period, ReportQuery query, DateTimeOffset? now = null)
    {
        var reports = Reports;
        var moment = now ?? DateTimeOffset.Now;
        var (from, to) = reports.ResolveRange(query, period, moment);
        var (fromMs, toMs) = reports.ToUtcRange(from?.AddDays(-1), to?.AddDays(1));
        return reports.Build(LoadEntries(fromMs, toMs, query.Sources), period, query, moment);
    }

    /// <summary>Heatmapa „dni × godziny” (Raporty): rozkład kosztu w zakresie zapytania.</summary>
    public HourlyReport BuildHourly(ReportQuery query, DateTimeOffset? now = null)
    {
        var reports = Reports;
        var moment = now ?? DateTimeOffset.Now;
        var (from, to) = reports.ResolveRange(query, ReportPeriod.Daily, moment);
        var (fromMs, toMs) = reports.ToUtcRange(from?.AddDays(-1), to?.AddDays(1));
        var entries = reports.Filter(LoadEntries(fromMs, toMs, query.Sources), query, from, to);
        return TimeSeries.Hourly(entries, reports.Calculator, reports.TimeZone, from, to);
    }

    /// <summary>Przebieg sesji (drawer sesji): koszt i tokeny narastająco, punkt na wpis.</summary>
    public IReadOnlyList<CumulativePoint> BuildSessionTimeline(string source, string sessionId, DateTimeOffset firstActivity, DateTimeOffset lastActivity)
    {
        var entries = LoadEntries(firstActivity.ToUnixTimeMilliseconds(), lastActivity.ToUnixTimeMilliseconds() + 1, [source])
            .Where(entry => entry.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
        return TimeSeries.Cumulative(entries, Reports.Calculator);
    }

    /// <summary>Szczegóły bloku Claude (drawer bloku, oś bloku): koszt narastający i sesje w bloku.</summary>
    public BlockDetails BuildBlockDetails(SessionBlock block)
    {
        var entries = LoadEntries(block.Start.ToUnixTimeMilliseconds(), block.End.ToUnixTimeMilliseconds(), [ClaudeSource.SourceId]);
        return TimeSeries.Block(entries, Reports.Calculator);
    }

    /// <summary>Szczegóły wielu bloków jednym odczytem ledgera (tabela Bloków: mini-osie). Klucz = <see cref="SessionBlock.Id"/>.</summary>
    public IReadOnlyDictionary<string, BlockDetails> BuildBlocksDetails(IReadOnlyCollection<SessionBlock> blocks)
    {
        var real = blocks.Where(block => !block.IsGap).ToList();
        if (real.Count == 0)
        {
            return new Dictionary<string, BlockDetails>();
        }

        var entries = LoadEntries(real.Min(block => block.Start).ToUnixTimeMilliseconds(), real.Max(block => block.End).ToUnixTimeMilliseconds(),
            [ClaudeSource.SourceId]);
        var calculator = Reports.Calculator;
        return real.ToDictionary(block => block.Id, block =>
        {
            var from = block.Start.ToUnixTimeMilliseconds();
            var to = block.End.ToUnixTimeMilliseconds();
            return TimeSeries.Block(entries.Where(entry => entry.TimestampMs >= from && entry.TimestampMs < to), calculator);
        });
    }

    public SessionReport BuildSessions(ReportQuery query, string? sessionId = null, DateTimeOffset? now = null)
    {
        var reports = Reports;
        var moment = now ?? DateTimeOffset.Now;
        var (from, to) = reports.ResolveRange(query, ReportPeriod.Daily, moment);
        var (fromMs, toMs) = reports.ToUtcRange(from?.AddDays(-1), to?.AddDays(1));
        var unbounded = query.Since is null && query.Until is null && query.Last is null;
        return reports.BuildSessions(LoadEntries(fromMs, toMs, query.Sources, includeSessionReportOnly: unbounded), query, moment, sessionId);
    }

    /// <summary>Bloki rozliczeniowe Claude.</summary>
    public BlocksReport BuildBlocks(DateOnly? since = null, DateOnly? until = null, DateTimeOffset? now = null,
        long? tokenLimitOverride = null, double? sessionLengthHours = null, DateTimeOffset? activeResetsAt = null, string? project = null)
    {
        var reports = Reports;
        var moment = now ?? DateTimeOffset.Now;
        var (fromMs, toMs) = reports.ToUtcRange(since, until);
        var entries = LoadEntries(fromMs, toMs, [ClaudeSource.SourceId]);
        if (project is not null)
        {
            entries = entries.Where(entry => reports.MatchesProject(entry, project)).ToList();
        }

        return BlockBuilder.Build(
            entries,
            Calculator,
            moment,
            sessionLengthHours ?? Config.SessionLengthHours,
            tokenLimitOverride ?? Config.ParseTokenLimit(),
            activeResetsAt,
            noLimit: tokenLimitOverride is null && Config.IsTokenLimitDisabled);
    }

    public Task<IReadOnlyDictionary<string, ProviderLimits>> ReadLimitsAsync(CancellationToken cancellationToken = default) =>
        _limitService.ReadAsync(cancellationToken);

    /// <summary>Dane ekranu „Dziś”: per źródło dziś / 7 dni / 30 dni, sesje, modele, aktywny blok.</summary>
    public DashboardSnapshot BuildDashboard(IReadOnlyDictionary<string, ProviderLimits>? limits = null, DateTimeOffset? now = null)
    {
        var moment = now ?? DateTimeOffset.Now;
        var reports = Reports;
        var calculator = reports.Calculator;
        var today = reports.Today(moment);
        var from30 = today.AddDays(-29);
        var from7 = today.AddDays(-6);
        var entries = LoadEntries(reports.StartOfDayMs(from30), reports.StartOfDayMs(today.AddDays(1)));
        var activeSinceMs = moment.AddMinutes(-Config.ActiveSessionMinutes).ToUnixTimeMilliseconds();

        var totalsToday = new TokenCounts();
        var totals7 = new TokenCounts();
        var totals30 = new TokenCounts();
        var daily = new double[30];
        var perSource = new Dictionary<string, (TokenCounts Today, TokenCounts Week, TokenCounts Month, Dictionary<string, long> Models,
            Dictionary<string, long> SessionLast, bool Unpriced, bool Estimated)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!perSource.TryGetValue(entry.Source, out var data))
            {
                data = (new TokenCounts(), new TokenCounts(), new TokenCounts(), new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, long>(), false, false);
            }

            var cost = calculator.Calculate(entry);
            var date = reports.LocalDate(entry.TimestampMs);
            var dayIndex = date.DayNumber - from30.DayNumber;
            if (dayIndex is >= 0 and < 30)
            {
                daily[dayIndex] += cost.CostUsd;
            }

            data.Month.Add(entry, cost);
            totals30.Add(entry, cost);
            if (date >= from7)
            {
                data.Week.Add(entry, cost);
                totals7.Add(entry, cost);
            }

            if (date == today)
            {
                data.Today.Add(entry, cost);
                totalsToday.Add(entry, cost);
                var model = entry.Model ?? "(nieznany)";
                data.Models[model] = data.Models.GetValueOrDefault(model) + entry.TotalTokens;
                data.SessionLast[entry.SessionId] = Math.Max(data.SessionLast.GetValueOrDefault(entry.SessionId), entry.TimestampMs);
                data.Unpriced |= cost.IsUnpriced;
                data.Estimated |= cost.IsEstimated;
            }

            perSource[entry.Source] = data;
        }

        var summaries = Sources.Select(source =>
        {
            perSource.TryGetValue(source.Id, out var data);
            ProviderLimits? sourceLimits = null;
            limits?.TryGetValue(source.Id, out sourceLimits);
            return new SourceSummary
            {
                Id = source.Id,
                DisplayName = source.DisplayName,
                Vendor = source.Vendor,
                AccentColor = source.AccentColor,
                IsEnabled = Config.IsSourceEnabled(source.Id),
                IsInstalled = data.Month is not null || source.IsInstalled(Config),
                Today = data.Today ?? new TokenCounts(),
                Last7Days = data.Week ?? new TokenCounts(),
                Last30Days = data.Month ?? new TokenCounts(),
                ActiveSessions = data.SessionLast?.Count(pair => pair.Value >= activeSinceMs) ?? 0,
                CompletedSessions = data.SessionLast?.Count(pair => pair.Value < activeSinceMs) ?? 0,
                TopModels = data.Models?.OrderByDescending(pair => pair.Value).Take(3).Select(pair => pair.Key).ToArray() ?? [],
                HasUnpricedUsage = data.Unpriced,
                HasEstimatedModel = data.Estimated,
                Limits = sourceLimits
            };
        }).ToArray();

        DateTimeOffset? resetsAt = null;
        if (limits is not null && limits.TryGetValue(ClaudeSource.SourceId, out var claudeLimits) && claudeLimits.IsAvailable)
        {
            resetsAt = claudeLimits.FiveHourReset;
        }

        var blocks = BlockBuilder.Build(
            entries.Where(entry => entry.Source == ClaudeSource.SourceId && entry.TimestampMs >= moment.AddDays(-3).ToUnixTimeMilliseconds()),
            calculator, moment, Config.SessionLengthHours, Config.ParseTokenLimit(), resetsAt, noLimit: Config.IsTokenLimitDisabled);

        // Limit „max” liczony z dłuższej historii niż 3 dni.
        long? blockLimit = blocks.TokenLimit;
        if (Config.ParseTokenLimit() is null && !Config.IsTokenLimitDisabled)
        {
            var history = BlockBuilder.Build(entries.Where(entry => entry.Source == ClaudeSource.SourceId), calculator, moment, Config.SessionLengthHours);
            blockLimit = history.TokenLimit;
        }

        return new DashboardSnapshot
        {
            Sources = summaries,
            Today = totalsToday,
            Last7Days = totals7,
            Last30Days = totals30,
            ActiveBlock = blocks.Active,
            BlockTokenLimit = blockLimit,
            GeneratedAt = moment,
            DailyCost = daily,
            PricingOrigin = Pricing.Origin,
            Sync = LastSync
        };
    }

    public void Dispose()
    {
        _limitService.Dispose();
        _syncLock.Dispose();
    }
}
