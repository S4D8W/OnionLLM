using System.Text.Json;
using OnionLLM.Cli;
using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Tests;

public class SettingsEditorsTests
{
    [Fact]
    public void Map_text_parses_lines_comments_and_splits_on_last_equals()
    {
        var map = ConfigTextFormats.ParseMap("""

            # komentarz
            private-alpha = gpt-5.5
            arn:aws:bedrock:us-east-1:123:application-inference-profile/abc=claude-opus-4-6
            """, "Aliasy");

        Assert.Equal(2, map.Count);
        Assert.Equal("gpt-5.5", map["private-alpha"]);
        Assert.Equal("claude-opus-4-6", map["arn:aws:bedrock:us-east-1:123:application-inference-profile/abc"]);
        Assert.Empty(ConfigTextFormats.ParseMap("  \r\n ", "Aliasy"));
        Assert.Empty(ConfigTextFormats.ParseMap(null, "Aliasy"));
    }

    [Theory]
    [InlineData("bez-separatora")]
    [InlineData("=gpt-5")]
    [InlineData("alias=")]
    [InlineData("a=gpt-5\na=gpt-5.5")]
    public void Map_text_rejects_invalid_lines(string text)
    {
        var exception = Assert.Throws<FormatException>(() => ConfigTextFormats.ParseMap(text, "Aliasy modeli"));
        Assert.StartsWith("Aliasy modeli", exception.Message);
    }

    [Fact]
    public void Map_text_round_trips_sorted_and_case_sensitive()
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zeta"] = "gpt-5", ["Alpha"] = "claude-sonnet-4-5", ["alpha"] = "glm-4.6"
        };

        var text = ConfigTextFormats.FormatMap(original);

        Assert.Equal(["Alpha=claude-sonnet-4-5", "alpha=glm-4.6", "zeta=gpt-5"], text.Split(Environment.NewLine));
        Assert.Equal(original, ConfigTextFormats.ParseMap(text, "x"));
        Assert.Equal("", ConfigTextFormats.FormatMap(new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData(3e-6, "3")]
    [InlineData(3.75e-6, "3.75")]
    [InlineData(1.5e-7, "0.15")]
    [InlineData(0.0, "0")]
    [InlineData(1.875e-5, "18.75")]
    public void Per_token_rate_is_shown_per_million(double perToken, string expected)
    {
        Assert.Equal(expected, ConfigTextFormats.FormatPerMillion(perToken));
        Assert.Equal(perToken, ConfigTextFormats.ParsePerMillion(expected, null, "x"));
    }

    [Fact]
    public void Per_million_parsing_accepts_comma_and_rejects_bad_values()
    {
        Assert.Equal(2.5e-6, ConfigTextFormats.ParsePerMillion(" 2,5 ", null, "x"));
        Assert.Null(ConfigTextFormats.ParsePerMillion("  ", 3e-6, "x"));
        Assert.Equal("", ConfigTextFormats.FormatPerMillion(null));
        Assert.Throws<FormatException>(() => ConfigTextFormats.ParsePerMillion("abc", null, "Wejście"));
        Assert.Contains("ujemna", Assert.Throws<FormatException>(() => ConfigTextFormats.ParsePerMillion("-1", null, "Wejście")).Message);
    }

    [Fact]
    public void Unchanged_per_million_text_keeps_exact_config_value()
    {
        const double awkward = 1.0 / 3 * 1e-6;
        var text = ConfigTextFormats.FormatPerMillion(awkward);

        Assert.Equal(awkward, ConfigTextFormats.ParsePerMillion(text, awkward, "x"));
    }

    [Fact]
    public void Numbers_and_limits_are_validated()
    {
        Assert.Equal(0.5, ConfigTextFormats.ParseNumber("0,5", "flex"));
        Assert.Null(ConfigTextFormats.ParseNumber("", "flex"));
        Assert.Throws<FormatException>(() => ConfigTextFormats.ParseNumber("-2", "fast"));
        Assert.Throws<FormatException>(() => ConfigTextFormats.ParseNumber("dwa", "fast"));
        Assert.Equal(1_000_000, ConfigTextFormats.ParsePositiveLong("1 000 000", "max"));
        Assert.Equal(200_000, ConfigTextFormats.ParsePositiveLong("200_000", "max"));
        Assert.Throws<FormatException>(() => ConfigTextFormats.ParsePositiveLong("0", "max"));
        Assert.Throws<FormatException>(() => ConfigTextFormats.ParsePositiveLong("1.5", "max"));
    }

    [Fact]
    public void Override_form_round_trips_every_field_without_loss()
    {
        var original = new PricingOverride
        {
            InputCostPerToken = 3e-6,
            OutputCostPerToken = 1.0 / 7 * 1e-5,
            CacheCreationInputTokenCost = 3.75e-6,
            CacheCreationInputTokenCostAbove1h = 6e-6,
            CacheReadInputTokenCost = 3e-7,
            InputCostPerTokenAbove200kTokens = 6e-6,
            OutputCostPerTokenAbove200kTokens = 2.25e-5,
            CacheCreationInputTokenCostAbove200kTokens = 7.5e-6,
            CacheReadInputTokenCostAbove200kTokens = 6e-7,
            MaxInputTokens = 1_000_000,
            FastMultiplier = 6,
            FlexMultiplier = 0.5
        };

        var form = PricingOverrideForm.From("my-model", original);
        var rebuilt = ConfigTextFormats.BuildPricingOverrides([form])["my-model"];

        Assert.True(form.HasAdvanced);
        Assert.Equal("3", form.Input);
        Assert.Equal("6", form.CacheWrite1h);
        Assert.Equal("1000000", form.MaxInputTokens);
        Assert.Equal(Serialize(original), Serialize(rebuilt));
    }

    [Fact]
    public void Edited_override_fields_are_converted_to_per_token()
    {
        var form = PricingOverrideForm.From("my-model", new PricingOverride { InputCostPerToken = 3e-6, FlexMultiplier = 0.5 });
        form.Input = "5";
        form.Output = "25";
        form.CacheWrite1h = "10";
        form.FlexMultiplier = "";

        var value = form.ToOverride();

        Assert.Equal(5e-6, value.InputCostPerToken);
        Assert.Equal(2.5e-5, value.OutputCostPerToken);
        Assert.Equal(1e-5, value.CacheCreationInputTokenCostAbove1h);
        Assert.Null(value.FlexMultiplier);
        Assert.Null(value.CacheReadInputTokenCost);
        Assert.False(new PricingOverrideForm { Key = "x", Input = "1" }.HasAdvanced);
    }

    [Fact]
    public void Legacy_one_hour_field_is_saved_under_ccusage_name()
    {
        var config = JsonSerializer.Deserialize<OnionConfig>(
            """{"pricingOverrides":{"[claude] claude-opus-4-6":{"cacheCreationInputTokenCostAbove1h":0.00001,"fastMultiplier":6}}}""",
            ConfigLoader.JsonOptions)!;

        config.PricingOverrides = ConfigTextFormats.BuildPricingOverrides(ConfigTextFormats.ToForms(config.PricingOverrides), ["claude", "codex"]);
        var json = JsonSerializer.Serialize(config, ConfigLoader.JsonOptions);

        Assert.Contains("\"cacheCreationInputTokenCostAbove1hr\": 1E-05", json);
        Assert.DoesNotContain("\"cacheCreationInputTokenCostAbove1h\"", json);
        Assert.Contains("\"fastMultiplier\": 6", json);
    }

    [Fact]
    public void Override_keys_are_normalized_and_validated()
    {
        var overrides = ConfigTextFormats.BuildPricingOverrides(
        [
            new PricingOverrideForm { Key = "  [codex]   gpt-5.5 ", Input = "1" },
            new PricingOverrideForm(), // pusty wiersz po „Dodaj” — pomijany
            new PricingOverrideForm { Key = "anthropic/claude-sonnet-4-5", Output = "10" }
        ], ["claude", "codex"]);

        Assert.Equal(["[codex] gpt-5.5", "anthropic/claude-sonnet-4-5"], overrides.Keys);
        Assert.Contains("pusty klucz", Assert.Throws<FormatException>(() =>
            ConfigTextFormats.BuildPricingOverrides([new PricingOverrideForm { Input = "1" }])).Message);
        Assert.Contains("powtarza", Assert.Throws<FormatException>(() => ConfigTextFormats.BuildPricingOverrides(
            [new PricingOverrideForm { Key = "claude-sonnet-4.5" }, new PricingOverrideForm { Key = "Claude-Sonnet-4-5" }])).Message);
        Assert.Contains("nieznane źródło", Assert.Throws<FormatException>(() => ConfigTextFormats.BuildPricingOverrides(
            [new PricingOverrideForm { Key = "[clade] x", Input = "1" }], ["claude"])).Message);
        Assert.Throws<FormatException>(() => ConfigTextFormats.NormalizeOverrideKey("[claude]"));
        Assert.Contains("Nadpisanie 'm'", Assert.Throws<FormatException>(() => ConfigTextFormats.BuildPricingOverrides(
            [new PricingOverrideForm { Key = "m", CacheRead = "x" }])).Message);
    }

    [Fact]
    public void Edited_overrides_and_aliases_take_effect_in_catalog()
    {
        var overrides = ConfigTextFormats.BuildPricingOverrides([new PricingOverrideForm { Key = "private-model", Input = "2", Output = "8" }]);
        var aliases = ConfigTextFormats.ParseMap("private-alpha=claude-sonnet-4-5", "Aliasy");
        var catalog = PricingCatalog.Embedded(overrides, aliases);

        var priced = catalog.Resolve("private-model")!;
        Assert.Equal(2e-6, priced.Base.Input!.Value, 12);
        Assert.Equal(8e-6, priced.Base.Output!.Value, 12);
        Assert.Equal(catalog.Resolve("claude-sonnet-4-5")!.Base.Input, catalog.Resolve("private-alpha")!.Base.Input);
    }

    [Fact]
    public void Pricing_status_describes_origin_time_and_count()
    {
        var updated = new DateTimeOffset(2026, 11, 25, 10, 12, 0, TimeSpan.Zero);

        Assert.Equal("online · aktualizacja 2026-11-25 10:12 · modeli: 12", PricingStatus.Describe(PricingOrigin.Online, updated, 12, TimeZoneInfo.Utc));
        Assert.Equal("cache · aktualizacja 2026-11-25 10:12 · modeli: 3", PricingStatus.Describe(PricingOrigin.Cache, updated, 3, TimeZoneInfo.Utc));
        Assert.Equal("wbudowany · modeli: 5", PricingStatus.Describe(PricingOrigin.Embedded, null, 5));

        var catalog = PricingCatalog.Embedded();
        Assert.StartsWith("wbudowany", PricingStatus.DescribeModelsDev(catalog));
        Assert.EndsWith($"modeli: {catalog.ModelsDevCount}", PricingStatus.DescribeModelsDev(catalog));
        Assert.EndsWith($"modeli: {catalog.Count}", PricingStatus.DescribeLiteLlm(catalog));
    }

    private static string Serialize(PricingOverride value) => JsonSerializer.Serialize(value, ConfigLoader.JsonOptions);
}

[Collection("Console")]
public sealed class PricingInfoCliTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string? _previousDataDir = Environment.GetEnvironmentVariable("ONIONLLM_DATA_DIR");

    public PricingInfoCliTests() =>
        Environment.SetEnvironmentVariable("ONIONLLM_DATA_DIR", Path.Combine(_directory.Path, "data"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ONIONLLM_DATA_DIR", _previousDataDir);
        _directory.Dispose();
    }

    [Fact]
    public async Task Pricing_info_prints_both_sources_offline()
    {
        var config = _directory.Write("config.json",
            """{"offline":true,"modelAliases":{"private-alpha":"gpt-5.5"},"pricingOverrides":{"private-model":{"inputCostPerToken":0.000002}}}""");
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        int code;
        try
        {
            code = await CliApp.RunAsync(["pricing", "info", "--config", config]);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = writer.ToString();
        Assert.Equal(0, code);
        Assert.Contains("Cennik LiteLLM: wbudowany · modeli: ", output);
        Assert.Contains("Cennik models.dev: wbudowany · modeli: ", output);
        Assert.Contains("Nadpisania cen: 1 · aliasy modeli: 1", output);
    }
}
