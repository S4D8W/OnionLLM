using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.3: zapis do cache Z.ai po stawce wejścia i cena tylko dla dostawców Z.ai.</summary>
public class ZCodeSourcePricingTests
{
    private static readonly CostCalculator Calculator = new(PricingCatalog.Embedded(), CostMode.Calculate);

    /// <summary>Wiersz jak w <c>row()</c> testów ccusage <c>zcode/src/parser.rs</c>.</summary>
    private static Models.UsageEntry ParseRow(string model, string? provider)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cli", "db", "db.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT, version TEXT);
                CREATE TABLE model_usage (id TEXT PRIMARY KEY, session_id TEXT, started_at INTEGER, model_id TEXT, status TEXT,
                    input_tokens INTEGER, output_tokens INTEGER, cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER,
                    computed_total_tokens INTEGER, provider_id TEXT);
                INSERT INTO session VALUES ('session-1', '/workspace/project', '0.16.3');
                INSERT INTO model_usage VALUES ('usage-1', 'session-1', 1786909042666, $model, 'completed', 100, 10, 15, 25, 110, $provider);
                """;
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$provider", (object?)provider ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        return Assert.Single(TestData.ParseAll(new ZCodeSource(), TestData.ConfigWith(ZCodeSource.SourceId, directory.Path)));
    }

    [Theory]
    [InlineData("GLM-5.2", "builtin:zai-coding-plan")]
    [InlineData("GLM-5.3", "builtin:zai-coding-plan")]
    [InlineData("GLM-5.3", "builtin:bigmodel-coding-plan")]
    [InlineData("GLM-5.3", "account:zai-start-plan")]
    [InlineData("GLM-5.3", null)]
    public void Zai_cache_creation_is_billed_at_the_input_rate(string model, string? provider)
    {
        // ccusage: prices_zai_cache_creation_at_standard_input_rate_for_glm_52_and_53 → 0.0001555
        var entry = ParseRow(model, provider);

        var result = Calculator.Calculate(entry);

        Assert.Equal(60, entry.InputTokens);
        Assert.Equal(15, entry.CacheCreationTokens);
        Assert.Equal(provider, entry.Provider);
        Assert.False(result.IsUnpriced);
        // (60 + 15) × 1,4e-6 + 10 × 4,4e-6 + 25 × 2,6e-7
        Assert.Equal(0.0001555, result.CostUsd, 12);
    }

    [Fact]
    public void Unknown_custom_provider_is_not_priced_from_the_catalog()
    {
        // ccusage: prices_zai_models_but_not_unknown_custom_providers
        var entry = ParseRow("deepseek-v4-flash", "847d13c9-0568-4f2f-818e-8bd498e5d920");

        var result = Calculator.Calculate(entry);

        Assert.Equal("847d13c9-0568-4f2f-818e-8bd498e5d920", entry.Provider);
        Assert.True(result.IsUnpriced);
        Assert.Equal(0, result.CostUsd);
    }

    [Fact]
    public void Unknown_custom_provider_is_priced_only_from_user_overrides()
    {
        // ccusage zcode/src/parser.rs: dostawca spoza Z.ai → wycena wyłącznie z pricingOverrides.
        var entry = ParseRow("deepseek-v4-flash", "847d13c9-0568-4f2f-818e-8bd498e5d920");
        var catalog = PricingCatalog.Embedded(new Dictionary<string, PricingOverride>
        {
            ["[zcode] deepseek-v4-flash"] = new() { InputCostPerToken = 1e-6, OutputCostPerToken = 2e-6 }
        });

        var result = new CostCalculator(catalog, CostMode.Calculate).Calculate(entry);

        Assert.False(result.IsUnpriced);
        Assert.True(result.CostUsd > 0);
    }

    [Fact]
    public void Candidates_order_zai_prefixed_names_first()
    {
        // ccusage: adapter_pricing_identity
        Assert.Equal(["zai/GLM-4.7", "zai/glm-4.7", "GLM-4.7", "glm-4.7"], ZCodeSource.ModelCandidates(" GLM-4.7 ", null));
        Assert.Equal(["zai/deepseek-v4-flash", "deepseek-v4-flash"], ZCodeSource.ModelCandidates("deepseek-v4-flash", "zai"));
        Assert.Empty(ZCodeSource.ModelCandidates("deepseek-v4-flash", "847d13c9-0568-4f2f-818e-8bd498e5d920"));
    }

    [Fact]
    public void Non_zai_rows_keep_the_catalog_cache_write_rate()
    {
        var pricing = new ModelPricing { Key = "x", Base = new RateSet { Input = 1, Output = 2, CacheWrite = 0 } };
        var entry = new Models.UsageEntry { Source = ZCodeSource.SourceId, TimestampMs = 1, Model = "kimi-k2", Provider = null };
        var zai = entry with { Model = "glm-5.3" };

        Assert.Equal(0, new ZCodeSource().AdjustPricing(entry, pricing).Base.CacheWrite);
        Assert.Equal(1, new ZCodeSource().AdjustPricing(zai, pricing).Base.CacheWrite);
    }
}
