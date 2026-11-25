using System.Globalization;

namespace OnionLLM.Core.Pricing;

/// <summary>Opis stanu źródeł cennika (LiteLLM, models.dev) do Ustawień i <c>onionllm pricing info</c>.</summary>
public static class PricingStatus
{
    public const string LiteLlmName = "LiteLLM";
    public const string ModelsDevName = "models.dev";

    /// <summary>Polska nazwa pochodzenia cen.</summary>
    public static string OriginName(PricingOrigin origin) => origin switch
    {
        PricingOrigin.Online => "online",
        PricingOrigin.Cache => "cache",
        _ => "wbudowany"
    };

    /// <summary>
    /// Np. <c>online · aktualizacja 2026-11-25 10:12 · modeli: 2345</c> (czas lokalny lub <paramref name="zone"/>);
    /// bez daty (snapshot wbudowany): <c>wbudowany · modeli: 2345</c>.
    /// </summary>
    public static string Describe(PricingOrigin origin, DateTimeOffset? updatedAt, int count, TimeZoneInfo? zone = null)
    {
        var updated = updatedAt is { } value
            ? " · aktualizacja " + TimeZoneInfo.ConvertTime(value, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
        return $"{OriginName(origin)}{updated} · modeli: {count}";
    }

    public static string DescribeLiteLlm(PricingCatalog catalog, TimeZoneInfo? zone = null) =>
        Describe(catalog.Origin, catalog.UpdatedAt, catalog.Count, zone);

    public static string DescribeModelsDev(PricingCatalog catalog, TimeZoneInfo? zone = null) =>
        Describe(catalog.ModelsDevOrigin, catalog.ModelsDevUpdatedAt, catalog.ModelsDevCount, zone);
}
