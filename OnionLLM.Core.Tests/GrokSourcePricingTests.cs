using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.1 i 11.6.10: kandydaci cennika Grok Build i <c>GROK_HOME</c> jako jedna ścieżka.</summary>
public class GrokSourcePricingTests
{
    private static readonly CostCalculator Calculator = new(PricingCatalog.Embedded(), CostMode.Calculate);

    [Fact]
    public void Pricing_candidates_strip_build_and_add_xai_like_ccusage()
    {
        // ccusage grok/src/parser.rs: pricing_candidates_strip_build_and_add_xai
        Assert.Equal(
            ["grok-4.5-build", "xai/grok-4.5-build", "x-ai/grok-4.5-build", "grok-4.5", "xai/grok-4.5", "x-ai/grok-4.5"],
            GrokSource.ModelCandidates("grok-4.5-build"));
        Assert.Equal(["grok-4.5", "xai/grok-4.5", "x-ai/grok-4.5"], GrokSource.ModelCandidates("grok-4.5"));
        Assert.Empty(GrokSource.ModelCandidates("   "));
    }

    [Fact]
    public void Build_model_is_priced_through_the_xai_row()
    {
        var entry = new UsageEntry
        {
            Source = GrokSource.SourceId,
            TimestampMs = DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            Model = "grok-4.5-build",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            IsAggregate = true
        };

        var result = Calculator.Calculate(entry);

        // xai/grok-4.5: $2 / $6 za 1M.
        Assert.False(result.IsUnpriced);
        Assert.Equal(8.0, result.CostUsd, 9);
    }

    [Fact]
    public void Grok_home_is_a_single_path_and_blank_falls_back_to_default()
    {
        const string variable = "GROK_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, @"C:\grok,home");
            Assert.Equal([Util.FileUtil.NormalizePath(@"C:\grok,home")], new GrokSource().ResolveRoots(new OnionConfig()));

            Environment.SetEnvironmentVariable(variable, "  ");
            Assert.Equal([Util.FileUtil.NormalizePath(Path.Combine(Util.FileUtil.Home, ".grok"))],
                new GrokSource().ResolveRoots(new OnionConfig()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
