using OnionLLM.ViewModels;

namespace OnionLLM.Core.Tests;

/// <summary>UI formatting rules from DESIGN.md §5.1, §5.2, §8.3 (Fmt.Format.cs is linked into this project).</summary>
public class UiFormatTests
{
    private const string S = " ";

    [Theory]
    [InlineData(842, "842")]
    [InlineData(12_412, "12,4" + S + "k")]
    [InlineData(842_000, "842" + S + "k")]
    [InlineData(1_243_512, "1,24" + S + "M")]
    [InlineData(2_051_330_120, "2,05" + S + "B")]
    public void Tokens_use_compact_format(long value, string expected) => Assert.Equal(expected, Fmt.Tokens(value));

    [Fact]
    public void Full_numbers_use_non_breaking_thousands_separator()
    {
        Assert.Equal("12" + S + "412", Fmt.Number(12_412));
        Assert.Equal("1" + S + "243" + S + "512", Fmt.Number(1_243_512));
    }

    [Theory]
    [InlineData(0, "0,00" + S + "$")]
    [InlineData(0.0042, "0,0042" + S + "$")]
    [InlineData(0.38, "0,38" + S + "$")]
    [InlineData(12.34, "12,34" + S + "$")]
    [InlineData(1234.5, "1" + S + "234" + S + "$")]
    [InlineData(-0.12, "−0,12" + S + "$")]
    public void Cost_follows_design_rules(double value, string expected) => Assert.Equal(expected, Fmt.Cost(value));

    [Fact]
    public void Full_cost_keeps_two_decimals_above_thousand() =>
        Assert.Equal("1" + S + "234,50" + S + "$", Fmt.CostFull(1234.5));

    [Theory]
    [InlineData(62, "62%")]
    [InlineData(4.2, "4,2%")]
    [InlineData(0.05, "<" + S + "0,1%")]
    [InlineData(112, "112%")]
    public void Percent_follows_design_rules(double value, string expected) => Assert.Equal(expected, Fmt.Percent(value));

    [Theory]
    [InlineData(1, "token")]
    [InlineData(2, "tokeny")]
    [InlineData(4, "tokeny")]
    [InlineData(5, "tokenów")]
    [InlineData(12, "tokenów")]
    [InlineData(14, "tokenów")]
    [InlineData(22, "tokeny")]
    [InlineData(0, "tokenów")]
    [InlineData(112, "tokenów")]
    public void Plural_follows_polish_rules(long n, string expected) =>
        Assert.Equal(expected, Fmt.Plural(n, "token", "tokeny", "tokenów"));

    [Fact]
    public void Durations_and_dates_are_polish()
    {
        Assert.Equal("2" + S + "h 14" + S + "min", Fmt.Duration(new TimeSpan(2, 14, 0)));
        Assert.Equal("45" + S + "min", Fmt.Duration(TimeSpan.FromMinutes(45)));
        Assert.Equal("<" + S + "1" + S + "min", Fmt.Duration(TimeSpan.FromSeconds(20)));

        var today = new DateTime(2026, 11, 26, 14, 2, 0);
        Assert.Equal("26" + S + "lis", Fmt.ShortDate(today, today));
        Assert.Equal("3" + S + "gru 2025", Fmt.ShortDate(new DateTime(2025, 12, 3), today));
        Assert.Equal("wt 24" + S + "lis", Fmt.DayDate(new DateTime(2026, 11, 24)));
        Assert.Equal("czwartek, 26" + S + "listopada", Fmt.LongDate(today));
        Assert.Equal("listopad 2026", Fmt.Month(today));
        Assert.Equal("1–26" + S + "lis 2026", Fmt.Range(new DateTime(2026, 11, 1), today));
        Assert.Equal("28" + S + "paź – 3" + S + "lis 2026", Fmt.Range(new DateTime(2026, 10, 28), new DateTime(2026, 11, 3)));
        Assert.Equal("o 13:00", Fmt.Relative(new DateTime(2026, 11, 26, 13, 0, 0), today));
        Assert.Equal("5" + S + "min temu", Fmt.Relative(today.AddMinutes(-5), today));
        Assert.Equal("wczoraj, 18:40", Fmt.Relative(new DateTime(2026, 11, 25, 18, 40, 0), today));
    }

    [Fact]
    public void Spoken_variants_read_in_full()
    {
        Assert.Equal("12,34 dolara", Fmt.CostSpoken(12.34));
        Assert.Equal("5 dolarów", Fmt.CostSpoken(5));
        Assert.Equal("1,2 miliona tokenów", Fmt.TokensSpoken(1_200_000));
        Assert.Equal("5 tokenów", Fmt.TokensSpoken(5));
    }

    [Fact]
    public void Burn_colour_is_a_token_key() =>
        Assert.Equal("ColorBurnHigh", Fmt.BurnColorKey(Reports.BurnRateLevel.High));

    [Fact]
    public void Display_currency_converts_with_approximation_and_keeps_prices_in_usd()
    {
        Fmt.Currency = new DisplayCurrency("PLN", 4, new DateOnly(2026, 11, 26));
        try
        {
            Assert.Equal("≈" + S + "49,60" + S + "zł", Fmt.Cost(12.40));
            Assert.Equal("3,00" + S + "$ /" + S + "1" + S + "M", Fmt.PricePerMillion(0.000003));
            Assert.Equal("12,40" + S + "$", Fmt.CostUsd(12.40));
            Assert.Equal("około 49,60 złotego", Fmt.CostSpoken(12.40));
            Assert.Contains("1" + S + "USD = 4" + S + "zł (26.11.2026)", Fmt.CostTooltip(12.40));
        }
        finally
        {
            Fmt.Currency = DisplayCurrency.Usd;
        }

        Assert.Equal("12,40" + S + "$", Fmt.Cost(12.40));
        Assert.Equal(string.Empty, Fmt.CostTooltip(12.40));
    }
}
