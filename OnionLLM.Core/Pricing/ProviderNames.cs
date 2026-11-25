namespace OnionLLM.Core.Pricing;

/// <summary>Kto rozliczył żądanie (ccusage <c>BillingRoute</c>).</summary>
public enum BillingRouteKind
{
    /// <summary>Sam dostawca (np. DeepSeek, Google, xAI, Anthropic) — stosuje się jego datowane harmonogramy.</summary>
    Direct,

    /// <summary>Host stosujący datowane ceny dostawcy, gdy brak własnego wiersza (Vertex AI → Google).</summary>
    Host,

    /// <summary>Inny dostawca (OpenRouter, Bedrock, Copilot…) — harmonogramy dostawcy nie obowiązują.</summary>
    Foreign
}

/// <param name="Vendor">Dostawca harmonogramu (<c>deepseek</c>, <c>google</c>…); null dla <see cref="BillingRouteKind.Foreign"/>.</param>
public readonly record struct BillingRoute(BillingRouteKind Kind, string? Vendor);

/// <summary>
/// Nazwy dostawców zapisane przez narzędzia → identyfikatory katalogu cen (ccusage <c>adapters/common/src/billing.rs</c>).
/// Źródła ustawiają <see cref="Models.UsageEntry.Provider"/> przez <see cref="Normalize"/> i budują kandydatów cennika
/// przez <see cref="Qualified"/>.
/// </summary>
public static class ProviderNames
{
    private static readonly string[] Unknown = ["", "unknown"];

    /// <summary>Różne zapisy tego samego hosta (models.dev <c>google-vertex</c>, LiteLLM <c>vertex_ai</c>, <c>vertex</c>), ccusage #2226.</summary>
    private static readonly (string Provider, string[] Aliases)[] HostAliases =
    [
        ("vertex_ai", ["vertex", "vertex_ai", "vertexai", "google_vertex", "google_vertex_ai"])
    ];

    /// <summary>Identyfikator katalogu dla hosta chmurowego (np. <c>vertex_ai</c> dla <c>google-vertex</c>) albo null.</summary>
    public static string? HostProvider(string? logged)
    {
        if (string.IsNullOrWhiteSpace(logged))
        {
            return null;
        }

        var key = logged.Trim().ToLowerInvariant().Replace('-', '_');
        foreach (var (provider, aliases) in HostAliases)
        {
            if (aliases.Contains(key))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>Dostawca do zapisania we wpisie: małe litery, aliasy hostów złożone; null dla pustego lub <c>unknown</c>.</summary>
    public static string? Normalize(string? logged)
    {
        if (string.IsNullOrWhiteSpace(logged))
        {
            return null;
        }

        var normalized = HostProvider(logged) ?? logged.Trim().ToLowerInvariant();
        return Unknown.Contains(normalized) ? null : normalized;
    }

    /// <summary>Kandydat <c>dostawca/model</c> (bez podwajania prefiksu); null, gdy brak dostawcy lub modelu.</summary>
    public static string? Qualified(string? model, string? provider)
    {
        var normalized = Normalize(provider);
        if (normalized is null || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return model.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase) ? model : $"{normalized}/{model}";
    }

    /// <summary>
    /// Droga rozliczenia dla dostawcy zapisanego w logu (ccusage <c>common/src/billing.rs</c> <c>billing_route</c>):
    /// API dostawcy → <see cref="BillingRouteKind.Direct"/>, Vertex AI → <see cref="BillingRouteKind.Host"/> (Google), inny →
    /// <see cref="BillingRouteKind.Foreign"/>; null, gdy dostawca nieznany (wycena bez wskazówki).
    /// </summary>
    public static BillingRoute? Route(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        var normalized = (HostProvider(provider) ?? provider).Trim().ToLowerInvariant().Replace('_', '-');
        if (Unknown.Contains(normalized))
        {
            return null;
        }

        foreach (var (id, vendor) in PricingData.DirectBillingProviders)
        {
            if (id == normalized)
            {
                return new BillingRoute(BillingRouteKind.Direct, vendor);
            }
        }

        foreach (var (id, vendor) in PricingData.HostsFollowingVendorPrices)
        {
            if (id == normalized)
            {
                return new BillingRoute(BillingRouteKind.Host, vendor);
            }
        }

        return new BillingRoute(BillingRouteKind.Foreign, null);
    }
}
