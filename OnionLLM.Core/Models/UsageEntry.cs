namespace OnionLLM.Core.Models;

/// <summary>Tryb szybkości / poziom usługi, wpływa na cenę.</summary>
public enum SpeedTier
{
    Unknown = 0,
    Standard = 1,
    Fast = 2,
    Flex = 3,

    /// <summary>Codex „ultrafast” (ccusage <c>Speed::Ultrafast</c>, mnożnik z <c>ultrafast-multiplier-overrides.json</c>).</summary>
    Ultrafast = 4
}

/// <summary>
/// Pojedyncze, znormalizowane wywołanie modelu odczytane z lokalnych logów narzędzia.
/// Wszystkie liczniki tokenów są rozłączne: <see cref="InputTokens"/> nie zawiera tokenów cache.
/// </summary>
public sealed record UsageEntry
{
    public required string Source { get; init; }

    /// <summary>Znacznik czasu UTC w milisekundach od epoki Unix.</summary>
    public required long TimestampMs { get; init; }

    public string SessionId { get; init; } = "";

    /// <summary>Stabilny klucz projektu (np. nazwa katalogu w <c>~/.claude/projects</c>).</summary>
    public string? Project { get; init; }

    /// <summary>Ścieżka robocza (cwd), jeśli narzędzie ją zapisuje.</summary>
    public string? ProjectPath { get; init; }

    public string? Model { get; init; }

    /// <summary>True, gdy model nie był zapisany w logu i został przyjęty domyślnie.</summary>
    public bool IsFallbackModel { get; init; }

    public long InputTokens { get; init; }

    /// <summary>Wyjście; u dostawców OpenAI zawiera tokeny reasoning.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Zapis do cache (łącznie 5 min i 1 h).</summary>
    public long CacheCreationTokens { get; init; }

    /// <summary>Część <see cref="CacheCreationTokens"/> z TTL 1 h (droższa stawka).</summary>
    public long CacheCreation1hTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>Tokeny reasoning — wyłącznie informacyjnie, już zawarte w <see cref="OutputTokens"/>.</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Koszt zapisany przez samo narzędzie (np. <c>costUSD</c> w Claude Code).</summary>
    public double? RecordedCostUsd { get; init; }

    public SpeedTier Speed { get; init; }

    /// <summary>Kredyty / AIU (Copilot). Informacyjnie.</summary>
    public double? Credits { get; init; }

    /// <summary>Klucz deduplikacji w obrębie źródła; null = wpis nie podlega deduplikacji.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>
    /// Poziom szczegółowości wpisu w obrębie sesji: 0 = per wywołanie (najdokładniejszy),
    /// 1 = drugorzędne źródło per wywołanie (np. telemetria OTel), 2 = zbiorczy wpis sesji.
    /// Dla każdej sesji zostają tylko wpisy z najniższym dostępnym poziomem.
    /// </summary>
    public int SessionTier { get; init; }

    /// <summary>Wpis zbiorczy sesji (np. <c>session.shutdown</c> Copilota) — skrót dla <see cref="SessionTier"/> = 2.</summary>
    public bool IsSessionSummary
    {
        get => SessionTier >= 2;
        init => SessionTier = value ? 2 : SessionTier;
    }

    /// <summary>
    /// <c>źródło:sesja</c> innego źródła, które liczy te same wywołania (np. OpenCode <c>claude-agent</c> → sesja Claude Code).
    /// Wpis jest pomijany, jeśli wskazana sesja ma wpisy w wynikach.
    /// </summary>
    public string? MirrorOf { get; init; }

    /// <summary>
    /// Wpis sumuje kilka żądań do modelu (np. tura Grok Build), choć nie jest wpisem zbiorczym sesji.
    /// Zapisywany w ledgerze; wpisy zbiorcze sesji są obejmowane przez <see cref="SpansMultipleRequests"/> automatycznie.
    /// </summary>
    public bool IsAggregate { get; init; }

    /// <summary>
    /// Dostawca zapisany w logu (np. <c>anthropic</c>, <c>openrouter</c>, <c>vertex</c>), małymi literami; null = nieznany.
    /// Służy jako wskazówka cennika (<see cref="Sources.IUsageSource.PricingCandidates"/>), ccusage <c>billing.rs</c>.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Pierwszeństwo przy duplikatach (wyższe wygrywa, przed innymi kryteriami), np. Claude: kopia spoza sidechain,
    /// OpenClaw: wiersz z SQLite. Domyślnie 0.
    /// </summary>
    public int Preference { get; init; }

    /// <summary>
    /// True, gdy wpis łączy wiele żądań bez podziału na pojedyncze wywołania. Próg długiego kontekstu dostawca nalicza
    /// per żądanie, więc taki wpis wyceniamy stawką bazową (ccusage <c>UsageGranularity::Aggregate</c>, #2365).
    /// </summary>
    public bool SpansMultipleRequests => IsAggregate || IsSessionSummary;

    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}
