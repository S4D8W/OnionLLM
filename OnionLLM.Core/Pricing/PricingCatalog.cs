using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnionLLM.Core.Config;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Pricing;

public enum PricingOrigin
{
    Embedded,
    Cache,
    Online
}

/// <summary>Wynik wyceny modelu: cennik (po harmonogramie, jeśli podano czas) i kandydat nazwy, który go znalazł.</summary>
public sealed record PricingResolution(ModelPricing Pricing, string Candidate);

/// <summary>
/// Katalog cen modeli (port mechanizmu ccusage <c>ccusage-core/src/pricing/</c>). Źródła (od najważniejszego):
/// <c>pricingOverrides</c> z konfiguracji, LiteLLM (z sieci co 24 h, cache, wbudowany snapshot) uzupełniony cenami wbudowanymi
/// (<see cref="BuiltinPricing"/>), a na końcu models.dev — tylko dla modeli, których LiteLLM nie zna.
/// <para>
/// Kolejność dopasowania nazwy (ccusage <c>resolve.rs</c>): nadpisania; alias z konfiguracji; cennik hosta zapisanego w nazwie
/// (Bedrock/Azure/Vertex); tożsamość nazwy (<c>.</c>/<c>@</c> = <c>-</c>, wielkość liter, ID dostawcy bez prefiksu); sufiksy ścieżki
/// (<c>openrouter/anthropic/x</c> → <c>anthropic/x</c> → <c>x</c>) i models.dev; na końcu dopasowanie przybliżone, które dopuszcza
/// wyłącznie różnice neutralne cenowo (data, rewizja, effort, <c>thinking</c> rodziny), zob. <see cref="PricingKeys.Matches"/>.
/// </para>
/// </summary>
public sealed partial class PricingCatalog
{
    public const string LiteLlmUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    public const string ModelsDevUrl = ModelsDev.ApiUrl;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    /// <summary>Prefiksy kluczy LiteLLM wbudowanych w snapshot (oprócz reguł ccusage <see cref="Admissions"/>).</summary>
    private static readonly string[] KeyPrefixes =
    [
        "claude-", "anthropic/", "anthropic.", "us.anthropic.", "eu.anthropic.", "global.anthropic.", "gpt-", "o1", "o3", "o4-",
        "codex-", "gemini-", "gemini/", "deepseek", "xai/", "grok-", "zai/", "glm-", "qwen", "dashscope/", "moonshot/", "kimi-",
        "mistral/", "devstral", "codestral"
    ];

    /// <summary>Reguły wbudowania wierszy z ccusage <c>litellm-sources.json</c> (<c>embedded</c>).</summary>
    private static readonly (string? Key, string Prefix, string? Provider, bool Unqualified, string[]? Modes)[] Admissions =
    [
        (null, "claude-", null, false, null), (null, "anthropic.", null, false, null), (null, "anthropic/", null, false, null),
        (null, "us.anthropic.", null, false, null), (null, "eu.anthropic.", null, false, null), (null, "global.anthropic.", null, false, null),
        (null, "jp.anthropic.", null, false, null), (null, "au.anthropic.", null, false, null), (null, "gpt-", null, false, null),
        ("o3", "", null, false, null), (null, "o3-", null, false, null), ("o4-mini", "", null, false, null), (null, "o4-mini-", null, false, null),
        (null, "openai/", null, false, null), (null, "azure/", null, false, null), (null, "zai/", null, false, null), (null, "xai/", null, false, null),
        (null, "openrouter/openai/", null, false, null), (null, "", "deepseek", false, null), (null, "", "gemini", false, null),
        (null, "", "bedrock_converse", true, ["chat"]), (null, "", "bedrock", true, ["chat"]), (null, "bedrock/", "bedrock", true, ["chat"])
    ];

    private readonly Dictionary<string, ModelPricing> _models;
    private readonly Dictionary<string, ModelPricing> _modelsDev;
    private readonly Index _primary;
    private readonly Index _secondary;
    private readonly HashSet<string> _devExactOnlyIdentities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _paidHosts;
    private readonly Dictionary<string, (string Key, PricingOverride Value)> _overrides = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly ConcurrentDictionary<string, ModelPricing?> _overrideRows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Hit?> _searches = new(StringComparer.Ordinal);

    private enum Lookup
    {
        Pricing,
        Direct,
        Paid
    }

    private enum HitKind
    {
        Override,
        Identity,
        Suffix,
        ModelsDevIdentity,
        Fuzzy,
        ModelsDevFuzzy
    }

    private sealed record Hit(ModelPricing Row, int Candidate, HitKind Kind);

    private sealed class Index
    {
        public Dictionary<string, List<ModelPricing>> Identity { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ModelPricing>> Vendor { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ModelPricing>> BareHosted { get; } = new(StringComparer.Ordinal);
        public List<(ModelPricing Row, string Identity)> Keys { get; } = [];

        public static Index Build(IEnumerable<ModelPricing> rows)
        {
            var index = new Index();
            foreach (var row in rows)
            {
                var identity = PricingKeys.Identity(row.Key);
                index.Keys.Add((row, identity));
                Add(row.IsBareHosted ? index.BareHosted : index.Identity, identity, row);
                if (row.VendorId is { } vendor && !vendor.Equals(row.Key, StringComparison.OrdinalIgnoreCase))
                {
                    Add(index.Vendor, PricingKeys.Identity(vendor), row);
                }
            }

            return index;
        }

        private static void Add(Dictionary<string, List<ModelPricing>> target, string key, ModelPricing row)
        {
            if (!target.TryGetValue(key, out var list))
            {
                target[key] = list = [];
            }

            list.Add(row);
        }
    }

    private PricingCatalog(
        Dictionary<string, ModelPricing> models,
        PricingOrigin origin,
        DateTimeOffset? updatedAt,
        Dictionary<string, ModelPricing> modelsDev,
        PricingOrigin modelsDevOrigin,
        DateTimeOffset? modelsDevUpdatedAt,
        IReadOnlyDictionary<string, PricingOverride>? overrides,
        IReadOnlyDictionary<string, string>? aliases,
        IEnumerable<string>? paidHosts = null)
    {
        BuiltinPricing.Apply(models);
        foreach (var key in models.Keys.ToArray())
        {
            if (PricingSchedules.Curate(models[key]) is { } curated)
            {
                models[key] = curated;
            }
        }

        FillLongContextFromModelsDev(models, EmbeddedModelsDev.Value);
        _models = models;
        _modelsDev = modelsDev;
        _primary = Index.Build(models.Values);
        _secondary = Index.Build(modelsDev.Values);
        foreach (var row in modelsDev.Values.Where(row => row.ExactOnly))
        {
            _devExactOnlyIdentities.Add(PricingKeys.Identity(row.Key));
        }

        _paidHosts = new HashSet<string>(
            models.Values.Where(row => row.Source == PricingSource.LiteLlm && ((row.Base.Input ?? 0) > 0 || (row.Base.Output ?? 0) > 0))
                .Select(row => row.Provider).OfType<string>().Concat(paidHosts ?? EmbeddedPaidHosts.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in overrides ?? new Dictionary<string, PricingOverride>())
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                _overrides[PricingKeys.Identity(key.Trim())] = (key.Trim(), value);
            }
        }

        _aliases = aliases ?? new Dictionary<string, string>();
        Origin = origin;
        UpdatedAt = updatedAt;
        ModelsDevOrigin = modelsDevOrigin;
        ModelsDevUpdatedAt = modelsDevUpdatedAt;
    }

    /// <summary>Pochodzenie cen LiteLLM.</summary>
    public PricingOrigin Origin { get; }

    public DateTimeOffset? UpdatedAt { get; }

    /// <summary>Liczba modeli w cenniku głównym (LiteLLM + ceny wbudowane).</summary>
    public int Count => _models.Count;

    /// <summary>Pochodzenie cen models.dev (drugie źródło).</summary>
    public PricingOrigin ModelsDevOrigin { get; }

    public DateTimeOffset? ModelsDevUpdatedAt { get; }

    /// <summary>Liczba modeli models.dev używanych, gdy LiteLLM nie zna modelu.</summary>
    public int ModelsDevCount => _modelsDev.Count;

    /// <summary>Cache pełnego pliku LiteLLM (pola cen + <c>litellm_provider</c>).</summary>
    public static string CachePath => Path.Combine(FileUtil.DataDirectory, "litellm-pricing-cache.json");

    /// <summary>Cache odpowiedzi <c>models.dev/api.json</c>.</summary>
    public static string ModelsDevCachePath => Path.Combine(FileUtil.DataDirectory, "models-dev-cache.json");

    private static readonly Lazy<Dictionary<string, ModelPricing>> EmbeddedLiteLlm = new(() =>
        ParseLiteLlm(LoadResource("OnionLLM.Core.pricing-snapshot.json").RootElement, filter: false));

    private static readonly Lazy<Dictionary<string, ModelPricing>> EmbeddedModelsDev = new(() =>
        ModelsDev.Parse(LoadResource("OnionLLM.Core.models-dev-snapshot.json").RootElement));

    /// <summary>Dostawcy models.dev z dodatnią ceną — hosty pobierające opłaty (ccusage <c>paid_host_providers</c>).</summary>
    private static readonly Lazy<string[]> EmbeddedPaidHosts = new(() =>
    {
        var root = LoadResource("OnionLLM.Core.models-dev-snapshot.json").RootElement;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in root.EnumerateObject())
        {
            if (model.Value.ValueKind == JsonValueKind.Object &&
                model.Value.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String &&
                model.Value.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
                ((cost.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Number && input.GetDouble() > 0) ||
                 (cost.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Number && output.GetDouble() > 0)))
            {
                hosts.Add(provider.GetString()!);
            }
        }

        return hosts.ToArray();
    });

    private static JsonDocument LoadResource(string name)
    {
        using var stream = typeof(PricingCatalog).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Brak wbudowanego zasobu cennika: {name}.");
        return JsonDocument.Parse(stream);
    }

    /// <summary>Katalog z wbudowanych snapshotów (bez sieci i bez cache).</summary>
    public static PricingCatalog Embedded(IReadOnlyDictionary<string, PricingOverride>? overrides = null, IReadOnlyDictionary<string, string>? modelAliases = null) =>
        new(new Dictionary<string, ModelPricing>(EmbeddedLiteLlm.Value, StringComparer.OrdinalIgnoreCase), PricingOrigin.Embedded, null,
            EmbeddedModelsDev.Value, PricingOrigin.Embedded, null, overrides, modelAliases);

    /// <summary>Katalog z podanego JSON LiteLLM (i opcjonalnie models.dev) — do testów.</summary>
    internal static PricingCatalog FromJson(string liteLlmJson, string? modelsDevJson = null,
        IReadOnlyDictionary<string, PricingOverride>? overrides = null, IReadOnlyDictionary<string, string>? modelAliases = null)
    {
        using var liteLlm = JsonDocument.Parse(liteLlmJson);
        using var modelsDev = JsonDocument.Parse(modelsDevJson ?? "{}");
        return new PricingCatalog(ParseLiteLlm(liteLlm.RootElement, filter: false), PricingOrigin.Embedded, null,
            ModelsDev.Parse(modelsDev.RootElement), PricingOrigin.Embedded, null, overrides, modelAliases, []);
    }

    /// <summary>Katalog z wbudowanych snapshotów z nadpisaniami i aliasami z konfiguracji.</summary>
    public static PricingCatalog Embedded(OnionConfig config) => Embedded(config.PricingOverrides, config.ModelAliases);

    /// <summary>
    /// Ładuje katalog: snapshot + cache; w trybie online odświeża cache LiteLLM i models.dev, jeśli są starsze niż 24 h
    /// (lub <paramref name="forceRefresh"/>). Online LiteLLM jest wczytywany w całości (bez filtra prefiksów).
    /// Błędy sieci nie są zgłaszane — zostaje cache albo snapshot. Tryb offline nigdy nie łączy się z siecią.
    /// </summary>
    public static async Task<PricingCatalog> LoadAsync(OnionConfig config, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var models = new Dictionary<string, ModelPricing>(EmbeddedLiteLlm.Value, StringComparer.OrdinalIgnoreCase);
        var (liteLlm, origin, updatedAt) = await LoadSourceAsync(config, CachePath, LiteLlmUrl, forceRefresh,
            root => ParseLiteLlm(root, filter: false), online => online.Count > 50, SerializeLoaded, cancellationToken);
        if (liteLlm is not null)
        {
            foreach (var (key, value) in liteLlm)
            {
                models[key] = value;
            }
        }

        var (modelsDev, devOrigin, devUpdatedAt) = await LoadSourceAsync(config, ModelsDevCachePath, ModelsDevUrl, forceRefresh,
            ModelsDev.Parse, online => online.Count > 50, root => root.GetRawText(), cancellationToken);

        return new PricingCatalog(models, origin, updatedAt, modelsDev ?? EmbeddedModelsDev.Value, devOrigin, devUpdatedAt,
            config.PricingOverrides, config.ModelAliases);
    }

    private static async Task<(Dictionary<string, ModelPricing>? Rows, PricingOrigin Origin, DateTimeOffset? UpdatedAt)> LoadSourceAsync(
        OnionConfig config,
        string cachePath,
        string url,
        bool forceRefresh,
        Func<JsonElement, Dictionary<string, ModelPricing>> parse,
        Func<Dictionary<string, ModelPricing>, bool> valid,
        Func<JsonElement, string> serialize,
        CancellationToken cancellationToken)
    {
        var cacheInfo = new FileInfo(cachePath);
        var cacheFresh = cacheInfo.Exists && DateTime.UtcNow - cacheInfo.LastWriteTimeUtc < CacheLifetime;

        if (!config.Offline && (forceRefresh || !cacheFresh))
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionLLM/1.0");
                await using var stream = await client.GetStreamAsync(url, cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var online = parse(document.RootElement);
                if (valid(online))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, serialize(document.RootElement), cancellationToken);
                    return (online, PricingOrigin.Online, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
            {
                // brak sieci → cache / snapshot
            }
        }

        if (cacheInfo.Exists)
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath, cancellationToken));
                var cached = parse(document.RootElement);
                if (cached.Count > 0)
                {
                    return (cached, PricingOrigin.Cache, new DateTimeOffset(cacheInfo.LastWriteTimeUtc));
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return (null, PricingOrigin.Embedded, null);
    }

    /// <summary>Filtruje pełny plik LiteLLM do wierszy i pól wbudowywanych w snapshot (narzędzie <c>pricing update-snapshot</c>).</summary>
    public static string SerializeFiltered(JsonElement root) => Serialize(root, filter: true);

    /// <summary>Wszystkie wiersze LiteLLM z polami używanymi przez OnionLLM (format cache).</summary>
    private static string SerializeLoaded(JsonElement root) => Serialize(root, filter: false);

    private static string Serialize(JsonElement root, bool filter)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var model in root.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (model.Value.ValueKind != JsonValueKind.Object || !HasTokenRates(model.Value) || (filter && !IsEmbedded(model.Name, model.Value)))
                {
                    continue;
                }

                writer.WriteStartObject(model.Name);
                foreach (var field in model.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Number &&
                        (field.Name == "max_input_tokens" || (CostFieldRegex().IsMatch(field.Name) && !ExcludedFieldRegex().IsMatch(field.Name))))
                    {
                        writer.WritePropertyName(field.Name);
                        field.Value.WriteTo(writer);
                    }
                    else if (field.Name == "litellm_provider" && field.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteString(field.Name, field.Value.GetString());
                    }
                }

                if (model.Value.TryGetProperty("provider_specific_entry", out var specific) && specific.ValueKind == JsonValueKind.Object)
                {
                    var tiers = specific.EnumerateObject().Where(tier => tier.Name is "fast" or "flex" && tier.Value.ValueKind == JsonValueKind.Number).ToArray();
                    if (tiers.Length > 0)
                    {
                        writer.WriteStartObject("provider_specific_entry");
                        foreach (var tier in tiers)
                        {
                            writer.WriteNumber(tier.Name, tier.Value.GetDouble());
                        }

                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Cennik modelu z uwzględnieniem nadpisań i aliasów, bez harmonogramu dat; null = model bez ceny.</summary>
    public ModelPricing? Resolve(string? model, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return ResolveAt([model], model, source, provider: null, timestampMs: null)?.Pricing;
    }

    /// <summary>
    /// Wycenia wpis: kandydaci nazw w kolejności źródła (ccusage <c>price_entry</c>), nazwa z logu, źródło (dla nadpisań
    /// <c>[źródło] model</c>), dostawca z logu (droga rozliczenia, <see cref="ProviderNames.Route"/>) i czas wpisu (harmonogramy).
    /// </summary>
    public PricingResolution? ResolveAt(IReadOnlyList<string> candidates, string? reported, string? source, string? provider, long? timestampMs)
    {
        var names = candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)).Select(candidate => candidate.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        var route = ProviderNames.Route(provider);
        var lookup = route is { Kind: BillingRouteKind.Direct } ? Lookup.Direct : Lookup.Pricing;
        var hit = Search(names, reported, source, lookup, provider);
        if (hit is null)
        {
            return null;
        }

        var candidate = hit.Candidate >= 0 ? names[hit.Candidate] : reported ?? names[0];
        var schedule = route switch
        {
            null => hit.Row.Schedule is { } own && (own.FollowsRow || hit.Kind is HitKind.Override or HitKind.Identity) ? own : null,
            { Kind: BillingRouteKind.Foreign } => null,
            { Kind: BillingRouteKind.Host } when !hit.Row.IsDirect => null,
            { Vendor: var vendor } => RouteSchedule(hit.Row, reported, candidate, vendor)
        };

        var pricing = hit.Row with { Schedule = schedule };
        if (timestampMs is { } timestamp)
        {
            pricing = pricing.At(timestamp);
        }

        return new PricingResolution(pricing, candidate);
    }

    /// <summary>Czy dla modelu (lub <c>[źródło] model</c>) istnieje nadpisanie użytkownika (ccusage ZCode: dostawcy spoza Z.ai tylko z nadpisań).</summary>
    public bool HasOverride(string? model, string? source = null) =>
        !string.IsNullOrWhiteSpace(model) && LabelOverride(model.Trim(), source) is not null;

    /// <summary>Mnożnik trybu fast (1 = brak dopłaty), ccusage <c>fast-multiplier-overrides.json</c>.</summary>
    public static double FastMultiplier(ModelPricing pricing) => pricing.FastMultiplier ?? SpeedMultipliers.Fast(pricing.Key) ?? 1.0;

    /// <summary>Mnożnik trybu flex (1 = brak zniżki dla modeli spoza listy).</summary>
    public static double FlexMultiplier(ModelPricing pricing) => pricing.FlexMultiplier ?? SpeedMultipliers.Flex(pricing.Key) ?? 1.0;

    /// <summary>Mnożnik trybu ultrafast (Codex).</summary>
    public static double UltrafastMultiplier(ModelPricing pricing) => pricing.UltrafastMultiplier ?? SpeedMultipliers.Ultrafast(pricing.Key) ?? 1.0;

    /// <summary>Harmonogram dostawcy na drodze rozliczenia (ccusage <c>PricingHandle::on_route</c>).</summary>
    private PricingSchedule? RouteSchedule(ModelPricing row, string? reported, string candidate, string? vendor)
    {
        foreach (var name in new[] { reported, candidate }.OfType<string>())
        {
            foreach (var spelling in new[] { ModelAliases.ResolveConfigured(name, _aliases), name })
            {
                if (PricingSchedules.For(PricingKeys.LastSegment(spelling)) is { } schedule)
                {
                    return schedule.Vendor == vendor ? schedule : null;
                }
            }
        }

        return row.Schedule is { FollowsRow: true } own && own.Vendor == vendor ? own : null;
    }

    private Hit? Search(string[] candidates, string? reported, string? source, Lookup lookup, string? provider)
    {
        var cacheKey = $"{lookup}|{source}|{provider}|{reported}|{string.Join('\n', candidates)}";
        return _searches.GetOrAdd(cacheKey, _ =>
        {
            var hit = SearchRequest(candidates, reported, source, lookup);
            // NOTE(ccusage #2228): darmowy wiersz dostawcy użyty przez płatnego hosta bez własnego wiersza → szacunek z najbliższego płatnego.
            if (hit is not null && lookup == Lookup.Pricing && hit.Kind is not (HitKind.Override or HitKind.ModelsDevIdentity or HitKind.ModelsDevFuzzy) &&
                hit.Row is { IsDirect: true, IsFree: true } && NamesForeignHost(candidates, reported, provider) &&
                SearchRequest(candidates, reported, source, Lookup.Paid) is { } paid)
            {
                return paid with { Row = paid.Row with { IsEstimated = true } };
            }

            return hit;
        });
    }

    private Hit? SearchRequest(string[] candidates, string? reported, string? source, Lookup lookup)
    {
        // 1. Nadpisania użytkownika (etykieta [źródło] model, nazwa, alias, sufiksy ścieżki).
        if (_overrides.Count > 0)
        {
            var names = (reported is null ? [] : new[] { (Index: -1, Name: reported) })
                .Concat(candidates.Select((name, index) => (Index: index, Name: name)));
            foreach (var (index, name) in names)
            {
                if (LabelOverride(name, source) is { } key && ResolveOverride(key) is { } row)
                {
                    return new Hit(row, index, HitKind.Override);
                }
            }
        }

        // 2. Alias z konfiguracji dla nazwy z logu.
        var logged = reported ?? candidates[0];
        if (ConfiguredAlias(logged) is { } alias && IdentityHit(alias, lookup) is { } aliased)
        {
            return new Hit(aliased.Row, -1, aliased.Kind);
        }

        // 3. Cennik hosta zapisanego w nazwie (ccusage #2229).
        for (var index = 0; index < candidates.Length; index++)
        {
            if (SearchHost(candidates[index], lookup) is { } row)
            {
                return new Hit(Shadowed(row), index, HitKind.Identity);
            }
        }

        // 4. Tożsamość nazwy.
        for (var index = 0; index < candidates.Length; index++)
        {
            foreach (var name in Names(candidates[index]))
            {
                if (Authoritative(name, lookup, hosted: true, _ => true) is { } row)
                {
                    return new Hit(Shadowed(row), index, HitKind.Identity);
                }
            }
        }

        // 5. Sufiksy ścieżki (płatne wiersze), potem models.dev.
        for (var index = 0; index < candidates.Length; index++)
        {
            foreach (var name in Names(candidates[index]))
            {
                foreach (var suffix in PricingKeys.Suffixes(name))
                {
                    if (Authoritative(suffix, lookup, hosted: false, row => !row.IsFree) is { } row)
                    {
                        return new Hit(Shadowed(row), index, HitKind.Suffix);
                    }
                }
            }

            foreach (var name in Names(candidates[index]))
            {
                foreach (var unclaimed in Unclaimed(name, lookup))
                {
                    if (DevExact(unclaimed, lookup) is { } row)
                    {
                        return new Hit(Shadowed(row), index, HitKind.ModelsDevIdentity);
                    }
                }
            }
        }

        // 6. Darmowe wiersze dostawcy przez sufiks.
        if (lookup != Lookup.Paid)
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                foreach (var name in Names(candidates[index]))
                {
                    foreach (var suffix in PricingKeys.Suffixes(name))
                    {
                        if (Authoritative(suffix, lookup, hosted: false, row => row is { IsDirect: true, IsFree: true }) is { } row)
                        {
                            return new Hit(Shadowed(row), index, HitKind.Suffix);
                        }
                    }
                }
            }
        }

        // 7. Dopasowanie przybliżone (tylko różnice neutralne cenowo), najpierw LiteLLM, potem models.dev.
        for (var index = 0; index < candidates.Length; index++)
        {
            // Wiersz models.dev tylko do dokładnego dopasowania blokuje przybliżenie tej nazwy (ccusage S56).
            if (Names(candidates[index]).SelectMany(name => Unclaimed(name, lookup)).Any(name => _devExactOnlyIdentities.Contains(PricingKeys.Identity(name))))
            {
                continue;
            }

            foreach (var name in Names(candidates[index]))
            {
                if (Fuzzy(_primary, name, lookup, includeOverrides: true) is { } row)
                {
                    return new Hit(Shadowed(row), index, HitKind.Fuzzy);
                }

                if (Fuzzy(_secondary, name, lookup, includeOverrides: false) is { } secondary)
                {
                    return new Hit(Shadowed(secondary), index, HitKind.ModelsDevFuzzy);
                }
            }
        }

        return null;
    }

    /// <summary>Nazwa, pisownia dostawcy (<c>google/</c> → <c>gemini/</c>), wbudowany alias, potem alias z konfiguracji (ccusage <c>with_names</c>).</summary>
    private IEnumerable<string> Names(string model)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Spellings(string name)
        {
            yield return name;
            if (PricingData.VendorSpelling(name) is { } vendor)
            {
                yield return vendor;
            }

            if (PricingData.PricingAlias(name) is { } alias)
            {
                yield return alias;
            }
        }

        foreach (var name in Spellings(model))
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }

        if (ConfiguredAlias(model) is { } configured)
        {
            foreach (var name in Spellings(configured))
            {
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
        }
    }

    private string? ConfiguredAlias(string label)
    {
        foreach (var model in new[] { label, PricingKeys.Unlabeled(label) })
        {
            var alias = ModelAliases.ResolveConfigured(model, _aliases);
            if (alias != model)
            {
                return alias;
            }
        }

        return null;
    }

    private (ModelPricing Row, HitKind Kind)? IdentityHit(string model, Lookup lookup)
    {
        foreach (var name in Names(model))
        {
            if (Authoritative(name, lookup, hosted: true, _ => true) is { } row)
            {
                return (Shadowed(row), HitKind.Identity);
            }
        }

        foreach (var name in Names(model))
        {
            if (DevExact(name, lookup) is { } row)
            {
                return (Shadowed(row), HitKind.ModelsDevIdentity);
            }
        }

        return null;
    }

    private static bool Includes(ModelPricing row, Lookup lookup) => lookup switch
    {
        Lookup.Direct => !row.IsBedrock,
        Lookup.Paid => !row.IsFree,
        _ => true
    };

    /// <summary>
    /// Wiersz o tej samej tożsamości (ccusage <c>authoritative</c>): klucz dosłowny, tożsamość, ID u dostawcy bezpośredniego
    /// (tylko gdy wszystkie mają te same stawki), na końcu wiersz hosta bez prefiksu (np. <c>gemini-2.5-pro</c> z Vertex).
    /// </summary>
    private ModelPricing? Authoritative(string name, Lookup lookup, bool hosted, Func<ModelPricing, bool> select, bool hostedOnly = false)
    {
        bool Visible(ModelPricing row) => Includes(row, lookup) && select(row) && (!hostedOnly || !row.IsDirect);

        if (_models.TryGetValue(name, out var literal) && !literal.IsBareHosted && Visible(literal))
        {
            return literal;
        }

        var identity = PricingKeys.Identity(name);
        if (_primary.Identity.TryGetValue(identity, out var rows) &&
            rows.Where(Visible).OrderBy(row => row.Key, StringComparer.Ordinal).FirstOrDefault() is { } byIdentity)
        {
            return byIdentity;
        }

        if (!hostedOnly && _primary.Vendor.TryGetValue(identity, out var vendorRows))
        {
            var visible = vendorRows.Where(Visible).ToArray();
            if (visible.Length > 0 && visible.All(row => row.Base == visible[0].Base))
            {
                return visible.OrderBy(row => row.Key, StringComparer.Ordinal).First();
            }
        }

        if (hosted && _primary.BareHosted.TryGetValue(identity, out var bare) &&
            bare.Where(Visible).OrderBy(row => row.Key, StringComparer.Ordinal).FirstOrDefault() is { } bareRow)
        {
            return bareRow;
        }

        return null;
    }

    private ModelPricing? DevExact(string name, Lookup lookup)
    {
        if (_modelsDev.TryGetValue(name, out var literal) && Includes(literal, lookup))
        {
            return literal;
        }

        return _secondary.Identity.TryGetValue(PricingKeys.Identity(name), out var rows)
            ? rows.Where(row => Includes(row, lookup)).OrderBy(row => row.Key, StringComparer.Ordinal).FirstOrDefault()
            : null;
    }

    /// <summary>Nazwa i jej sufiksy ścieżki, dopóki cennik główny ich nie zna (ccusage <c>models_dev_names</c>, #2238).</summary>
    private IEnumerable<string> Unclaimed(string name, Lookup lookup)
    {
        yield return name;
        foreach (var suffix in PricingKeys.Suffixes(name))
        {
            if (Authoritative(suffix, lookup, hosted: false, _ => true) is not null)
            {
                yield break;
            }

            yield return suffix;
        }
    }

    /// <summary>Cennik hosta zapisanego w nazwie: <c>bedrock/…</c>, <c>azure/…</c>, <c>vertex_ai/…</c>, inny host w ścieżce lub ID w stylu Bedrock.</summary>
    private ModelPricing? SearchHost(string model, Lookup lookup)
    {
        var slash = model.IndexOf('/');
        if (slash > 0)
        {
            var segment = model[..slash];
            if (PricingData.DirectProviders.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return PricingData.HostListingIndex(segment) is { } host
                ? HostListingRow(model[(slash + 1)..], host, lookup)
                : Authoritative(model, lookup, hosted: false, _ => true, hostedOnly: true);
        }

        var dot = model.IndexOf('.');
        return dot > 0 && PricingData.Hosts[PricingData.BedrockHost].ModelIdWords.Contains(model[..dot].ToLowerInvariant())
            ? HostListingRow(model, PricingData.BedrockHost, lookup)
            : null;
    }

    private ModelPricing? HostListingRow(string id, int host, Lookup lookup)
    {
        var identity = PricingKeys.Identity(id);
        if (_primary.BareHosted.TryGetValue(identity, out var bare) &&
            bare.Where(row => row.Host == host && Includes(row, lookup)).OrderBy(row => row.Key, StringComparer.Ordinal).FirstOrDefault() is { } bareRow)
        {
            return bareRow;
        }

        foreach (var prefix in PricingData.Hosts[host].Prefixes)
        {
            if (Authoritative($"{prefix}/{id}", lookup, hosted: false, _ => true, hostedOnly: true) is { } row)
            {
                return row;
            }
        }

        // Ścieżka regionalna spoza katalogu (np. bedrock/us-east-1/<id>) → cennik regionu domyślnego.
        var slash = id.IndexOf('/');
        return slash > 0 && slash < id.Length - 1 ? HostListingRow(id[(slash + 1)..], host, lookup) : null;
    }

    /// <summary>
    /// Najdłuższy pasujący klucz (ccusage <c>fuzzy</c>); jeśli któryś ma ten sam ostatni segment co nazwa, wygrywa najdłuższy
    /// klucz nie dłuższy od nazwy. Darmowe wiersze i wiersze <c>exactOnly</c> (gdy nazwa nie zawiera klucza) są pomijane.
    /// </summary>
    private ModelPricing? Fuzzy(Index index, string name, Lookup lookup, bool includeOverrides)
    {
        var identity = PricingKeys.Identity(name);
        var segment = PricingKeys.LastSegment(identity);
        var pool = index.Keys
            .Where(entry => (identity.Length >= entry.Identity.Length
                                ? identity.Contains(entry.Identity, StringComparison.Ordinal)
                                : entry.Identity.EndsWith(identity, StringComparison.Ordinal)) &&
                            Includes(entry.Row, lookup) && !entry.Row.IsFree &&
                            PricingKeys.Matches(entry.Row.Key, name) &&
                            (!entry.Row.ExactOnly || PricingKeys.NamesKey(entry.Row.Key, name)))
            .Select(entry => (entry.Row.Key, Row: entry.Row));

        if (includeOverrides && _overrides.Count > 0)
        {
            var overrideRows = _overrides.Values
                .Where(entry => !ShadowsCatalog(entry.Key) && PricingKeys.Matches(PricingKeys.Unlabeled(entry.Key), name) && !entry.Key.StartsWith('['))
                .Select(entry => ResolveOverride(entry.Key))
                .OfType<ModelPricing>()
                .Select(row => (row.Key, Row: row));
            pool = pool.Concat(overrideRows);
        }

        (string Key, ModelPricing Row)? best = null;
        (string Key, ModelPricing Row)? bestNotLonger = null;
        var equalSegment = false;
        static bool Better((string Key, ModelPricing Row) candidate, (string Key, ModelPricing Row)? current) =>
            current is null || candidate.Key.Length > current.Value.Key.Length ||
            (candidate.Key.Length == current.Value.Key.Length && string.CompareOrdinal(candidate.Key, current.Value.Key) < 0);

        foreach (var entry in pool)
        {
            if (Better(entry, best))
            {
                best = entry;
            }

            var rowSegment = PricingKeys.LastSegment(entry.Key);
            if (rowSegment.Length <= segment.Length)
            {
                if (Better(entry, bestNotLonger))
                {
                    bestNotLonger = entry;
                }

                equalSegment |= PricingKeys.Identity(rowSegment) == segment;
            }
        }

        return (equalSegment ? bestNotLonger : best)?.Row;
    }

    private bool NamesForeignHost(string[] candidates, string? reported, string? provider)
    {
        IEnumerable<string> Segments()
        {
            foreach (var name in candidates.Append(reported).OfType<string>())
            {
                var model = PricingKeys.Unlabeled(name);
                var slash = model.IndexOf('/');
                if (slash > 0)
                {
                    yield return model[..slash];
                }
            }

            if (ProviderNames.Normalize(provider) is { } logged)
            {
                yield return logged;
            }
        }

        return Segments().Any(segment => segment.Length > 0 && !ModelsDev.IsVendorSegment(segment) && IsPaidHost(segment));
    }

    /// <summary>Host znany z pobierania opłat: host chmurowy albo dostawca z dodatnią ceną w LiteLLM/models.dev (ccusage <c>is_paid_host</c>).</summary>
    private bool IsPaidHost(string segment) =>
        PricingData.HostListingIndex(segment) is not null || _paidHosts.Any(host => PricingData.SameHostSpelling(host, segment));

    // ---- nadpisania ----

    /// <summary>Klucz nadpisania dla nazwy: <c>[źródło] nazwa</c>, nazwa, alias, sufiksy ścieżki, nazwa bez etykiety (ccusage <c>label_id</c>).</summary>
    private string? LabelOverride(string name, string? source)
    {
        if (_overrides.Count == 0)
        {
            return null;
        }

        string? Find(string candidate) =>
            _overrides.TryGetValue(PricingKeys.Identity(candidate), out var entry) ? entry.Key : null;

        if (source is not null && Find($"[{source}] {PricingKeys.Unlabeled(name)}") is { } scoped)
        {
            return scoped;
        }

        return Find(name)
               ?? (ConfiguredAlias(name) is { } alias ? Find(alias) : null)
               ?? PricingKeys.Suffixes(name).Select(Find).FirstOrDefault(key => key is not null)
               ?? (PricingKeys.Unlabeled(name) is var unlabeled && unlabeled != name ? LabelOverride(unlabeled, null) : null);
    }

    /// <summary>Nadpisanie przesłaniające wiersz katalogu o tej samej tożsamości lub ID u dostawcy (ccusage <c>shadow</c>).</summary>
    private ModelPricing Shadowed(ModelPricing row)
    {
        if (_overrides.Count == 0 || row.Source == PricingSource.Override)
        {
            return row;
        }

        if (_overrides.TryGetValue(PricingKeys.Identity(row.Key), out var entry) ||
            (row.VendorId is { } vendor && _overrides.TryGetValue(PricingKeys.Identity(vendor), out entry)))
        {
            return ResolveOverride(entry.Key) ?? row;
        }

        return row;
    }

    private bool ShadowsCatalog(string key) => _primary.Identity.ContainsKey(PricingKeys.Identity(key)) ||
                                               _primary.Vendor.ContainsKey(PricingKeys.Identity(key));

    /// <summary>
    /// Wiersz nadpisania (ccusage <c>resolve_override</c>): baza z katalogu dla klucza (lub modelu bez etykiety), na niej pola
    /// podane jawnie. Jawna stawka bazowa usuwa próg z cennika; pola <c>*Above200kTokens</c> tworzą własny próg.
    /// </summary>
    private ModelPricing? ResolveOverride(string key) => _overrideRows.GetOrAdd(key, _ =>
    {
        var value = _overrides[PricingKeys.Identity(key)].Value;
        var model = PricingKeys.Unlabeled(key);
        ModelPricing? FindBase(string name) =>
            Names(name).Select(spelling => Authoritative(spelling, Lookup.Pricing, hosted: false, _ => true)).FirstOrDefault(row => row is not null)
            ?? Names(name).Select(spelling => DevExact(spelling, Lookup.Pricing)).FirstOrDefault(row => row is not null);

        var basePricing = FindBase(key) ?? (model != key ? FindBase(model) ?? SearchRequest([model], model, null, Lookup.Pricing)?.Row : null);
        var explicitRates = new RateSet
        {
            Input = value.InputCostPerToken,
            Output = value.OutputCostPerToken,
            CacheWrite = value.CacheCreationInputTokenCost,
            CacheWrite1h = value.CacheCreationInputTokenCostAbove1h,
            CacheRead = value.CacheReadInputTokenCost
        };
        var explicitLong = new RateSet
        {
            Input = value.InputCostPerTokenAbove200kTokens,
            Output = value.OutputCostPerTokenAbove200kTokens,
            CacheWrite = value.CacheCreationInputTokenCostAbove200kTokens,
            CacheRead = value.CacheReadInputTokenCostAbove200kTokens
        };

        var published = basePricing?.Tiers.FirstOrDefault();
        PricingTier? explicitTier = null;
        if (!explicitLong.IsEmpty)
        {
            var wholeRequest = published is { Marginal: false } || PricingData.IsWholeRequestOverrideModel(model) ||
                               (basePricing is not null && PricingData.WholeRequestThreshold(basePricing.Key) is not null);
            var threshold = published?.AboveTokens ?? PricingData.WholeRequestThreshold(model) ?? 200_000;
            var inherited = explicitRates.IsEmpty && published is not null ? published.Rates : new RateSet();
            explicitTier = new PricingTier(threshold, explicitLong.Over(inherited)) { Marginal = !wholeRequest };
        }

        IReadOnlyList<PricingTier> tiers = !explicitRates.IsEmpty
            ? explicitTier is null ? [] : [explicitTier]
            : explicitTier is not null ? [explicitTier] : basePricing?.Tiers ?? [];

        var row = (basePricing ?? new ModelPricing { Key = model }) with
        {
            Key = model,
            Base = explicitRates.Over(basePricing?.Base ?? new RateSet()),
            Tiers = tiers,
            MaxInputTokens = value.MaxInputTokens ?? basePricing?.MaxInputTokens,
            FastMultiplier = value.FastMultiplier ?? basePricing?.FastMultiplier ?? SpeedMultipliers.Fast(basePricing?.Key ?? model),
            FlexMultiplier = value.FlexMultiplier ?? basePricing?.FlexMultiplier ?? SpeedMultipliers.Flex(basePricing?.Key ?? model),
            UltrafastMultiplier = basePricing?.UltrafastMultiplier ?? SpeedMultipliers.Ultrafast(basePricing?.Key ?? model),
            Provider = basePricing?.Provider,
            Source = PricingSource.Override,
            IsDirect = true,
            ExactOnly = false,
            Schedule = basePricing?.Schedule,
            Explicit = explicitRates,
            ExplicitTier = explicitTier,
            Host = null
        };
        return row.IsPriced || !explicitRates.IsEmpty ? row with { Base = row.Base with { Input = row.Base.Input ?? 0, Output = row.Base.Output ?? 0 } } : null;
    });

    // ---- ładowanie ----

    private static Dictionary<string, ModelPricing> ParseLiteLlm(JsonElement root, bool filter)
    {
        var result = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object || !HasTokenRates(property.Value) ||
                (filter && !IsEmbedded(property.Name, property.Value)))
            {
                continue;
            }

            var pricing = ModelPricing.FromLiteLlm(property.Name, property.Value);
            if (!pricing.IsPriced)
            {
                continue;
            }

            // ccusage litellm.rs: darmowy wiersz hosta bez prefiksu i wiersze 0/0 Google (Gemma, Lyria) nie są ceną za token.
            if (pricing.IsFree && ((!pricing.IsDirect && !property.Name.Contains('/')) ||
                                   (pricing.Provider is { } provider && PricingData.ZeroTokenRateUnpricedProviders.Contains(provider))))
            {
                continue;
            }

            result[property.Name] = pricing;
        }

        return result;
    }

    private static bool HasTokenRates(JsonElement value) =>
        value.TryGetProperty("input_cost_per_token", out var input) && input.ValueKind == JsonValueKind.Number &&
        value.TryGetProperty("output_cost_per_token", out var output) && output.ValueKind == JsonValueKind.Number;

    private static bool IsEmbedded(string key, JsonElement value)
    {
        var provider = value.TryGetProperty("litellm_provider", out var providerElement) && providerElement.ValueKind == JsonValueKind.String
            ? providerElement.GetString()
            : null;
        var mode = value.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String ? modeElement.GetString() : null;

        if (KeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) && (mode is null or "chat" or "responses"))
        {
            return true;
        }

        return Admissions.Any(rule =>
            (rule.Key is null || rule.Key == key) &&
            key.StartsWith(rule.Prefix, StringComparison.Ordinal) &&
            (rule.Provider is null || rule.Provider == provider) &&
            (!rule.Unqualified || !key[rule.Prefix.Length..].Contains('/')) &&
            (rule.Modes is null || (mode is not null && rule.Modes.Contains(mode))));
    }

    /// <summary>Uzupełnia brakujące progi długiego kontekstu z models.dev (ccusage <c>fill_long_context_rates_from_models_dev</c>).</summary>
    private static void FillLongContextFromModelsDev(Dictionary<string, ModelPricing> models, Dictionary<string, ModelPricing> modelsDev)
    {
        Dictionary<string, ModelPricing>? exactOnly = null;
        foreach (var key in models.Keys.ToArray())
        {
            var row = models[key];
            if (row.Tiers.Count > 0 || PricingSchedules.Curate(row) is not null)
            {
                continue;
            }

            var withoutDate = PricingKeys.WithoutDateSuffix(key);
            var resolved = PricingData.PricingAlias(withoutDate) ?? withoutDate;
            foreach (var name in new[] { resolved, withoutDate }.Distinct(StringComparer.Ordinal))
            {
                if (!modelsDev.TryGetValue(name, out var source))
                {
                    exactOnly ??= modelsDev.Values.Where(dev => dev.ExactOnly).GroupBy(dev => PricingKeys.Identity(dev.Key))
                        .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                    exactOnly.TryGetValue(PricingKeys.Identity(name), out source);
                }

                if (source is null)
                {
                    continue;
                }

                if (source.Tiers.FirstOrDefault() is { } tier)
                {
                    models[key] = row with { Tiers = [tier] };
                }

                break;
            }
        }
    }

    [GeneratedRegex("(cost_per_token|token_cost)")]
    private static partial Regex CostFieldRegex();

    [GeneratedRegex("(batches|audio|image|decisions|ultrafast|12hr|per_query|per_second|per_character|per_pixel|per_request|_priority|_flex)")]
    private static partial Regex ExcludedFieldRegex();
}
