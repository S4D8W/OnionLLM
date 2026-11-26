using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Tests;

/// <summary>Display currency: NBP parsing, USD→EUR via PLN, config validation (DESIGN.md §5.1).</summary>
public class ExchangeRateTests
{
    [Fact]
    public void Nbp_response_is_parsed_and_combined()
    {
        var usd = ExchangeRates.ParseNbp("""{"table":"A","currency":"dolar amerykański","code":"USD","rates":[{"no":"230/A/NBP/2026","effectiveDate":"2026-11-26","mid":4.0}]}""");
        var eur = ExchangeRates.ParseNbp("""{"rates":[{"effectiveDate":"2026-11-25","mid":4.3}]}""");

        Assert.Equal(new ExchangeRate("PLN", 4.0, new DateOnly(2026, 11, 26)), ExchangeRates.Combine("PLN", usd, null));
        var euro = ExchangeRates.Combine("EUR", usd, eur);
        Assert.Equal(4.0 / 4.3, euro.Rate, 6);
        Assert.Equal(new DateOnly(2026, 11, 25), euro.Date);
    }

    [Fact]
    public void Currency_settings_are_validated_and_normalized()
    {
        Assert.False(new CurrencySettings { Display = "PLN" }.IsConverted); // no rate yet
        Assert.True(new CurrencySettings { Display = "PLN", Rate = 4 }.IsConverted);
        Assert.Equal(new DateOnly(2026, 11, 26), new CurrencySettings { RateDate = "2026-11-26" }.ParsedRateDate);
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new OnionConfig { Currency = new CurrencySettings { Display = "GBP" } }));
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new OnionConfig { Currency = new CurrencySettings { Display = "PLN", Rate = 0 } }));
        ConfigLoader.Validate(new OnionConfig { Currency = new CurrencySettings { Display = "EUR", Rate = 0.93, RateSource = "manual" } });
    }

    [Fact]
    public async Task Offline_and_usd_never_fetch()
    {
        Assert.Null(await ExchangeRates.FetchNbpAsync(new OnionConfig { Offline = true, Currency = new CurrencySettings { Display = "PLN" } }));
        Assert.Null(await ExchangeRates.FetchNbpAsync(new OnionConfig()));
    }
}
