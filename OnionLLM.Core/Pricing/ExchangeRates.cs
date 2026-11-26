using System.Globalization;
using System.Text.Json;
using OnionLLM.Core.Config;

namespace OnionLLM.Core.Pricing;

/// <summary>Kurs przeliczenia USD na walutę wyświetlania (1 USD = <see cref="Rate"/> jednostek) z datą notowania.</summary>
public sealed record ExchangeRate(string Currency, double Rate, DateOnly Date);

/// <summary>
/// Kursy walut z tabeli A NBP (DESIGN.md §5.1 „Waluta wyświetlania”: „Automatyczny (NBP, dziennie)”). Kursy NBP są
/// w PLN: dla PLN to kurs USD, dla EUR — kurs USD / kurs EUR. Eksport i CLI zawsze pozostają w USD.
/// </summary>
public static class ExchangeRates
{
    public static readonly string[] Supported = ["USD", "PLN", "EUR"];

    private const string NbpUrl = "https://api.nbp.pl/api/exchangerates/rates/a/{0}/?format=json";

    /// <summary>Kurs średni i data z odpowiedzi NBP (<c>{"rates":[{"effectiveDate":"2026-11-26","mid":3.98}]}</c>).</summary>
    public static (double Mid, DateOnly Date) ParseNbp(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rate = document.RootElement.GetProperty("rates")[0];
        return (rate.GetProperty("mid").GetDouble(),
            DateOnly.ParseExact(rate.GetProperty("effectiveDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>Łączy kursy NBP w przelicznik USD → <paramref name="currency"/>.</summary>
    public static ExchangeRate Combine(string currency, (double Mid, DateOnly Date) usd, (double Mid, DateOnly Date)? eur) => currency switch
    {
        "PLN" => new ExchangeRate("PLN", usd.Mid, usd.Date),
        "EUR" when eur is { } euro => new ExchangeRate("EUR", usd.Mid / euro.Mid, usd.Date < euro.Date ? usd.Date : euro.Date),
        _ => new ExchangeRate("USD", 1, usd.Date)
    };

    /// <summary>Pobiera kurs z NBP; null przy braku sieci, trybie offline lub walucie USD.</summary>
    public static async Task<ExchangeRate?> FetchNbpAsync(OnionConfig config, CancellationToken cancellationToken = default)
    {
        var currency = config.Currency.Display;
        if (config.Offline || currency is not ("PLN" or "EUR"))
        {
            return null;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionLLM/1.0");
            var usd = ParseNbp(await client.GetStringAsync(string.Format(CultureInfo.InvariantCulture, NbpUrl, "usd"), cancellationToken));
            (double, DateOnly)? eur = currency == "EUR"
                ? ParseNbp(await client.GetStringAsync(string.Format(CultureInfo.InvariantCulture, NbpUrl, "eur"), cancellationToken))
                : null;
            return Combine(currency, usd, eur);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or FormatException
                                              or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
