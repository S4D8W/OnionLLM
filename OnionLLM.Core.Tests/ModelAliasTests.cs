using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class ModelAliasTests
{
    [Fact]
    public void Kimi_plan_is_priced_by_date()
    {
        var before = DateTimeOffset.Parse("2026-04-20T15:28:10Z").ToUnixTimeMilliseconds();
        var after = DateTimeOffset.Parse("2026-04-20T15:28:11Z").ToUnixTimeMilliseconds();

        Assert.Equal("moonshot/kimi-k2.5", ModelAliases.ForPricing("kimi-for-coding", before));
        Assert.Equal("moonshot/kimi-k2.6", ModelAliases.ForPricing("kimi-code/kimi-for-coding", after));
        Assert.Equal("gpt-5", ModelAliases.ForPricing("gpt-5", after));
    }

    [Fact]
    public void Provider_prefixed_catalog_keys_are_found_by_bare_name()
    {
        var catalog = PricingCatalog.Embedded();

        Assert.Equal("moonshot/kimi-k3", catalog.Resolve("kimi-k3")!.Key);
        Assert.NotNull(catalog.Resolve(ModelAliases.ForPricing("kimi-for-coding", 0)));
    }

    [Fact]
    public void Configured_aliases_follow_ccusage_model_aliases()
    {
        var aliases = new Dictionary<string, string> { ["private-alpha"] = "gpt-5.5" };

        Assert.Equal("gpt-5.5", ModelAliases.ResolveConfigured("private-alpha", aliases));
        Assert.Equal("gpt-5.5-fast", ModelAliases.ResolveConfigured("private-alpha-fast", aliases));
        Assert.Equal("gpt-5", ModelAliases.ResolveConfigured("gpt-5", aliases));
    }

    [Fact]
    public void Alias_text_parses_like_ccusage_model_aliases_env()
    {
        Assert.Equal("gpt-5.5", ModelAliases.Parse(" private-alpha = gpt-5.5, other-alpha=claude-sonnet-4 ")["private-alpha"]);
        Assert.Equal("claude-sonnet-4", ModelAliases.Parse("{private-alpha=gpt-5.5, other-alpha=claude-sonnet-4}")["other-alpha"]);
        Assert.Equal("gpt-5.5", ModelAliases.Parse("""{"private-alpha":"gpt-5.5"}""")["private-alpha"]);
        Assert.Empty(ModelAliases.Parse(null));
    }
}
