using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.ViewModels;

namespace OnionLLM.Design;

/// <summary>
/// Display currency (DESIGN.md §5.1): applies <c>currency.*</c> to <see cref="Fmt.Currency"/> and refreshes the NBP rate
/// once a day (automatic mode, not offline). The new rate is saved to the config, so every screen re-renders.
/// </summary>
public static class CurrencyService
{
    /// <summary>A rate older than this is flagged in Ustawienia and the status bar.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public static void Apply(CurrencySettings currency) =>
        Fmt.Currency = currency.IsConverted ? new DisplayCurrency(currency.Display, currency.Rate!.Value, currency.ParsedRateDate) : DisplayCurrency.Usd;

    /// <summary>Rate date older than <see cref="StaleAfter"/> (or missing) while converting.</summary>
    public static bool IsStale(CurrencySettings currency) =>
        currency.Display != "USD" && (currency.ParsedRateDate is not { } date || DateOnly.FromDateTime(DateTime.Today).DayNumber - date.DayNumber > StaleAfter.TotalDays);

    /// <summary>Fetches today's NBP rate when needed (at most once an hour) and saves it.</summary>
    public static async Task RefreshAsync(bool force = false)
    {
        var config = AppState.Config;
        var currency = config.Currency;
        if (currency.Display == "USD" || currency.RateSource != "nbp" || config.Offline ||
            (!force && (currency.ParsedRateDate == DateOnly.FromDateTime(DateTime.Today) || DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromHours(1))))
        {
            return;
        }

        _lastAttempt = DateTimeOffset.UtcNow;
        if (await ExchangeRates.FetchNbpAsync(config) is not { } rate)
        {
            return;
        }

        var date = rate.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (currency.Rate is { } existing && Math.Abs(existing - rate.Rate) < 1e-9 && currency.RateDate == date)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var updated = AppState.Config.Clone();
                updated.Currency.Rate = rate.Rate;
                updated.Currency.RateDate = date;
                AppState.SaveConfig(updated);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigException)
            {
                global::OnionLLM.Diagnostics.Trace("currency rate not saved: " + exception.Message);
            }
        });
    }
}
