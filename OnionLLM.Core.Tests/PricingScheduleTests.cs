using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Tests;

/// <summary>Harmonogramy cen zależne od daty i drogi rozliczenia (ccusage <c>pricing/tests/schedule.rs</c>, <c>common/src/billing.rs</c>).</summary>
public class PricingScheduleTests
{
    private static readonly PricingCatalog Catalog = PricingCatalog.Embedded();

    private static long At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static ModelPricing Rates(string model, string instant, string? provider = null, params string[] candidates) =>
        Catalog.ResolveAt(candidates.Length > 0 ? candidates : [model], model, null, provider, At(instant))!.Pricing;

    [Theory]
    // ccusage applies_deepseek_v4_rates_by_effective_period_for_both_models (wejście $/M).
    [InlineData("2026-08-16T15:59:59Z", 0.14, 0.435)]
    [InlineData("2026-08-16T16:00:00Z", 0.22, 0.66)]
    [InlineData("2026-08-17T01:00:00Z", 0.44, 1.32)]
    [InlineData("2026-09-10T03:59:59Z", 0.44, 1.32)]
    [InlineData("2026-09-10T04:00:00Z", 0.15, 0.66)]
    [InlineData("2026-09-10T06:00:00Z", 0.3, 1.32)]
    public void Direct_deepseek_billing_follows_the_dated_schedule(string instant, double flash, double pro)
    {
        Assert.Equal(flash, Rates("deepseek-v4-flash", instant, "deepseek").Base.Input!.Value * 1e6, 9);
        Assert.Equal(pro, Rates("deepseek-v4-pro", instant, "deepseek").Base.Input!.Value * 1e6, 9);
    }

    [Theory]
    // Okna szczytu: dni robocze 01–04 i 06–10 UTC, przedziały prawostronnie otwarte.
    [InlineData("2026-08-17T00:59:59Z", 0.22)]
    [InlineData("2026-08-17T01:00:00Z", 0.44)]
    [InlineData("2026-08-17T04:00:00Z", 0.22)]
    [InlineData("2026-08-17T09:59:59Z", 0.44)]
    [InlineData("2026-08-17T10:00:00Z", 0.22)]
    [InlineData("2026-08-22T02:00:00Z", 0.22)]
    [InlineData("2026-08-17T09:00:00+08:00", 0.44)]
    public void Deepseek_peak_windows_are_utc_weekdays(string instant, double input)
    {
        Assert.Equal(input, Rates("deepseek-v4-flash", instant, "deepseek").Base.Input!.Value * 1e6, 9);
    }

    [Fact]
    public void Billing_route_decides_schedule_eligibility()
    {
        const string peak = "2026-08-17T01:00:00Z";

        // dsh/OpenClaw: deepseek-official i deepseek_account to też API DeepSeek.
        Assert.Equal(0.44, Rates("deepseek-v4-flash", peak, "deepseek-official").Base.Input!.Value * 1e6, 9);
        Assert.Equal(0.44, Rates("deepseek-v4-flash", peak, "DeepSeek_Account").Base.Input!.Value * 1e6, 9);
        // Inny dostawca rozliczył żądanie → stała stawka wiersza.
        Assert.Equal(0.3, Rates("deepseek-v4-flash", peak, "openrouter").Base.Input!.Value * 1e6, 9);
        Assert.Equal(0.3, Rates("deepseek-v4-flash", peak, "codebuff").Base.Input!.Value * 1e6, 9);
        // Bez dostawcy: dokładne trafienie w wiersz DeepSeek → harmonogram (ccusage resolver_table_schedules_only_a_vendor_identity_hit).
        Assert.Equal(0.44, Rates("deepseek-v4-flash", peak).Base.Input!.Value * 1e6, 9);
        Assert.Equal(0.44, Rates("deepseek.v4.flash", peak).Base.Input!.Value * 1e6, 9);
        // Przybliżone dopasowanie dłuższej nazwy nie ustala dostawcy → stała stawka.
        Assert.Equal(0.3, Rates("deepseek-v4-flash-20260812", peak).Base.Input!.Value * 1e6, 9);
    }

    [Fact]
    public void Cost_calculator_uses_entry_provider_and_timestamp()
    {
        var calculator = new CostCalculator(Catalog, CostMode.Calculate, _ => null);
        var entry = TestData.Entry("2026-08-17T01:00:00Z", 1_000_000, 0, model: "deepseek-v4-flash", source: "dsh") with { Provider = "deepseek" };

        Assert.Equal(0.44, calculator.Calculate(entry).CostUsd, 9);
        Assert.Equal(0.3, calculator.Calculate(entry with { Provider = "openrouter" }).CostUsd, 9);
        Assert.Equal(0.22, calculator.Calculate(entry with { TimestampMs = At("2026-08-17T05:00:00Z") }).CostUsd, 9);
    }

    [Theory]
    // ccusage google_spellings_follow_the_announced_gemini_price_changes.
    [InlineData("gemini-4-argon", "2026-11-30T23:59:59Z", 2.0, 10.0)]
    [InlineData("gemini-4-argon", "2026-12-01T00:00:00Z", 4.0, 20.0)]
    [InlineData("google/gemini-4-argon", "2026-12-01T00:00:00Z", 4.0, 20.0)]
    [InlineData("gemini-2.5-pro", "2026-11-30T23:59:59Z", 1.25, 10.0)]
    [InlineData("gemini-2.5-pro", "2026-12-02T00:00:00Z", 0.75, 3.75)]
    [InlineData("google/gemini-2.5-pro", "2027-01-02T00:00:00Z", 1.5, 7.5)]
    public void Gemini_follows_announced_price_changes(string model, string instant, double input, double output)
    {
        var rates = Rates(model, instant);

        Assert.Equal(input, rates.Base.Input!.Value * 1e6, 9);
        Assert.Equal(output, rates.Base.Output!.Value * 1e6, 9);
    }

    [Fact]
    public void Google_billing_follows_schedule_while_foreign_hosts_stay_flat()
    {
        const string december = "2026-12-01T00:00:00Z";

        Assert.Equal(4.0, Rates("gemini-4-argon", december, "google", "google/gemini-4-argon", "gemini-4-argon").Base.Input!.Value * 1e6, 9);
        Assert.Equal(4.0, Rates("gemini-4-argon", december, "gemini", "gemini/gemini-4-argon", "gemini-4-argon").Base.Input!.Value * 1e6, 9);
        Assert.Equal(2.0, Rates("gemini-4-argon", december, "openrouter", "openrouter/gemini-4-argon", "gemini-4-argon").Base.Input!.Value * 1e6, 9);
        // Vertex AI bez własnego wiersza spada na wiersz Google → harmonogram Google (ccusage HOSTS_FOLLOWING_VENDOR_PRICES).
        Assert.Equal(4.0, Rates("gemini-4-argon", december, "google-vertex", "vertex_ai/gemini-4-argon", "gemini-4-argon").Base.Input!.Value * 1e6, 9);
    }

    [Fact]
    public void Gemini_2_5_pro_keeps_its_long_context_tier_until_rebilled()
    {
        var before = Rates("gemini-2.5-pro", "2026-11-30T23:59:59Z");
        var after = Rates("gemini-2.5-pro", "2026-12-02T00:00:00Z");

        Assert.Equal(2.5, Assert.Single(before.Tiers).Rates.Input!.Value * 1e6, 9);
        Assert.Empty(after.Tiers);
    }

    [Fact]
    public void Sonnet_4_6_paid_the_1m_beta_premium_until_ga_except_in_fast_mode()
    {
        var calculator = new CostCalculator(Catalog, CostMode.Calculate, _ => null);
        var beta = TestData.Entry("2026-03-12T12:00:00Z", 300_000, 0, model: "claude-sonnet-4-6");
        var ga = TestData.Entry("2026-03-13T00:00:00Z", 300_000, 0, model: "claude-sonnet-4-6");

        // ccusage #2451: całe żądanie > 200k po $6 do 2026-03-13; potem stawka standardowa; fast nigdy nie płacił dopłaty.
        Assert.Equal(1.8, calculator.Calculate(beta).CostUsd, 9);
        Assert.Equal(0.9, calculator.Calculate(ga).CostUsd, 9);
        Assert.Equal(0.9, calculator.Calculate(beta with { Speed = SpeedTier.Fast }).CostUsd, 9);
        // Wpis zbiorczy wielu żądań — bez progu.
        Assert.Equal(0.9, calculator.Calculate(beta with { IsAggregate = true }).CostUsd, 9);
        // OpenRouter rozlicza po swoich stawkach — harmonogram Anthropic nie obowiązuje.
        Assert.Equal(0.9, calculator.Calculate(beta with { Provider = "openrouter" }).CostUsd, 9);
    }

    [Fact]
    public void Sonnet_4_5_long_context_is_curated_whole_request_tier()
    {
        var calculator = new CostCalculator(Catalog, CostMode.Calculate, _ => null);
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 150_000, 10_000, model: "claude-sonnet-4-5-20250929", cacheRead: 100_000);

        // ccusage #2235: $6 / $22.50, odczyt cache $0.60 dla całego żądania > 200k.
        Assert.Equal(0.9 + 0.225 + 0.06, calculator.Calculate(entry).CostUsd, 9);
    }

    [Theory]
    [InlineData("2026-05-15T14:59:00Z", 0.2, 1.5)]
    [InlineData("2026-05-16T00:00:00Z", 1.0, 2.0)]
    public void Grok_code_fast_1_became_an_alias_of_grok_build(string instant, double input, double output)
    {
        var rates = Rates("grok-code-fast-1", instant, "xai", "xai/grok-code-fast-1", "grok-code-fast-1");

        Assert.Equal(input, rates.Base.Input!.Value * 1e6, 9);
        Assert.Equal(output, rates.Base.Output!.Value * 1e6, 9);
    }

    [Fact]
    public void User_override_wins_over_schedule_for_its_fields_only()
    {
        var catalog = PricingCatalog.Embedded(new Dictionary<string, PricingOverride>
        {
            ["deepseek-v4-flash"] = new() { InputCostPerToken = 9e-6 }
        });
        var peak = At("2026-08-17T01:00:00Z");

        var direct = catalog.ResolveAt(["deepseek-v4-flash"], "deepseek-v4-flash", null, "deepseek", peak)!.Pricing;
        var foreign = catalog.ResolveAt(["deepseek-v4-flash"], "deepseek-v4-flash", null, "openrouter", peak)!.Pricing;

        Assert.Equal(9.0, direct.Base.Input!.Value * 1e6, 9);
        Assert.Equal(1.32, direct.Base.Output!.Value * 1e6, 9);
        Assert.Equal(9.0, foreign.Base.Input!.Value * 1e6, 9);
        Assert.Equal(1.2, foreign.Base.Output!.Value * 1e6, 9);
    }

    [Theory]
    [InlineData("deepseek", BillingRouteKind.Direct, "deepseek")]
    [InlineData("deepseek_account", BillingRouteKind.Direct, "deepseek")]
    [InlineData("Gemini", BillingRouteKind.Direct, "google")]
    [InlineData("XAI", BillingRouteKind.Direct, "xai")]
    [InlineData("anthropic", BillingRouteKind.Direct, "anthropic")]
    [InlineData("zai-coding-plan", BillingRouteKind.Direct, "zai")]
    [InlineData("google-vertex", BillingRouteKind.Host, "google")]
    [InlineData("Vertex-AI", BillingRouteKind.Host, "google")]
    [InlineData("openrouter", BillingRouteKind.Foreign, null)]
    [InlineData("open_ai", BillingRouteKind.Foreign, null)]
    [InlineData("bedrock", BillingRouteKind.Foreign, null)]
    [InlineData("codebuff", BillingRouteKind.Foreign, null)]
    public void Provider_names_map_to_billing_routes(string provider, BillingRouteKind kind, string? vendor)
    {
        Assert.Equal(new BillingRoute(kind, vendor), ProviderNames.Route(provider));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("unknown")]
    public void Unknown_provider_has_no_route(string? provider)
    {
        Assert.Null(ProviderNames.Route(provider));
    }
}
