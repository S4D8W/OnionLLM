using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Project_aliases_and_token_limit_parse()
    {
        var config = new OnionConfig { ProjectAliases = "a=Alpha, b = Beta", TokenLimit = "500000" };

        Assert.Equal("Alpha", config.ParseProjectAliases()["a"]);
        Assert.Equal("Beta", config.ParseProjectAliases()["b"]);
        Assert.Equal(500000, config.ParseTokenLimit());
        Assert.Null(new OnionConfig().ParseTokenLimit());
    }

    [Fact]
    public void Invalid_values_are_rejected()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new OnionConfig { Timezone = "Mars/Olympus" }));
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new OnionConfig { TokenLimit = "lots" }));
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new OnionConfig { SessionLengthHours = 0 }));
    }

    [Fact]
    public void Explicit_config_file_is_layered_and_parsed()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("cfg.json", """
            {
              // komentarze są dozwolone
              "timezone": "UTC",
              "costMode": "calculate",
              "startOfWeek": "sunday",
              "sources": { "codex": { "enabled": false } },
              "pricingOverrides": { "x": { "inputCostPerToken": 0.000001 } },
            }
            """);

        var config = ConfigLoader.Load(path);

        Assert.Equal(CostMode.Calculate, config.CostMode);
        Assert.Equal(DayOfWeek.Sunday, config.StartOfWeek);
        Assert.False(config.IsSourceEnabled("codex"));
        Assert.True(config.IsSourceEnabled("claude"));
        Assert.Equal(1e-6, config.PricingOverrides["x"].InputCostPerToken);
    }

    [Fact]
    public void Appearance_and_window_settings_parse_and_default()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("cfg.json", """
            {
              "appearance": { "theme": "dark", "density": "compact", "motion": "reduced" },
              "window": { "alwaysOnTop": true }
            }
            """);

        var config = ConfigLoader.Load(path);

        Assert.Equal(ThemePreference.Dark, config.Appearance.Theme);
        Assert.Equal(DensityPreference.Compact, config.Appearance.Density);
        Assert.Equal(MotionPreference.Reduced, config.Appearance.Motion);
        Assert.True(config.Window.AlwaysOnTop);

        var defaults = new OnionConfig();
        Assert.Equal(ThemePreference.System, defaults.Appearance.Theme);
        Assert.Equal(DensityPreference.Comfortable, defaults.Appearance.Density);
        Assert.Equal(MotionPreference.Full, defaults.Appearance.Motion);
        Assert.False(defaults.Window.AlwaysOnTop);

        var json = System.Text.Json.JsonSerializer.Serialize(config, ConfigLoader.JsonOptions);
        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Contains("\"alwaysOnTop\": true", json);
    }

    [Fact]
    public void Pricing_overrides_accept_ccusage_field_names_and_model_aliases()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("cfg.json", """
            {
              "pricingOverrides": {
                "ccusage-style": { "cacheCreationInputTokenCostAbove1hr": 0.000006, "inputCostPerTokenAbove200kTokens": 0.000002,
                                   "outputCostPerTokenAbove200kTokens": 0.00001, "cacheCreationInputTokenCostAbove200kTokens": 0.000003,
                                   "cacheReadInputTokenCostAbove200kTokens": 0.0000002, "fastMultiplier": 6, "flexMultiplier": 0.5 },
                "legacy-style": { "cacheCreationInputTokenCostAbove1h": 0.000007 }
              },
              "modelAliases": { "private-alpha": "gpt-5.5" }
            }
            """);

        var config = ConfigLoader.Load(path);

        var ccusage = config.PricingOverrides["ccusage-style"];
        Assert.Equal(6e-6, ccusage.CacheCreationInputTokenCostAbove1h);
        Assert.Equal(2e-6, ccusage.InputCostPerTokenAbove200kTokens);
        Assert.Equal(1e-5, ccusage.OutputCostPerTokenAbove200kTokens);
        Assert.Equal(3e-6, ccusage.CacheCreationInputTokenCostAbove200kTokens);
        Assert.Equal(2e-7, ccusage.CacheReadInputTokenCostAbove200kTokens);
        Assert.Equal(6, ccusage.FastMultiplier);
        Assert.Equal(0.5, ccusage.FlexMultiplier);
        Assert.Equal(7e-6, config.PricingOverrides["legacy-style"].CacheCreationInputTokenCostAbove1h);
        Assert.Equal("gpt-5.5", config.ModelAliases["private-alpha"]);

        // Zapis używa nazwy ccusage i nie powiela starej.
        var json = System.Text.Json.JsonSerializer.Serialize(config, ConfigLoader.JsonOptions);
        Assert.Contains("cacheCreationInputTokenCostAbove1hr", json);
        Assert.DoesNotContain("\"cacheCreationInputTokenCostAbove1h\"", json);
        Assert.Equal(7e-6, config.Clone().PricingOverrides["legacy-style"].CacheCreationInputTokenCostAbove1h);
    }

    [Fact]
    public void Null_threshold_and_budget_entries_are_dropped_not_crashing()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("onionllm.json", """
            { "notifications": { "thresholds": { "block": null, "fiveHour": { "warn": 70 } } },
              "budgets": { "perSource": { "codex": null } } }
            """);

        var config = ConfigLoader.Load(path);

        Assert.False(config.Notifications.Thresholds.ContainsKey("block"));
        Assert.Equal(70, config.Notifications.For("fiveHour").Warn);
        Assert.Equal(80, config.Notifications.For("block").Warn);
        Assert.Empty(config.Budgets.PerSource);
    }
}
