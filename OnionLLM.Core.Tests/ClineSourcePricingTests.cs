using System.Text.Json;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.5: dostawca Cline (<c>providerId</c> / <c>provider</c>) i kandydaci cennika.</summary>
public class ClineSourcePricingTests
{
    [Fact]
    public void Extension_and_cli_records_carry_their_provider()
    {
        // ccusage cline/src/parser.rs: adapter_pricing_identity
        using var extension = JsonDocument.Parse("""
            [{"ts":1790856000000,"type":"say","say":"api_req_started","modelInfo":{"modelId":"gpt-5.2","providerId":"OpenAI"},
              "text":"{\"tokensIn\":100,\"tokensOut\":20,\"cacheReads\":0,\"cacheWrites\":0}"},
             {"ts":1790856000001,"type":"say","say":"api_req_started","modelInfo":{"modelId":"gpt-5.2"},
              "text":"{\"tokensIn\":100,\"tokensOut\":20,\"cacheReads\":0,\"cacheWrites\":0}"}]
            """);
        using var cli = JsonDocument.Parse("""
            {"messages":[{"role":"assistant","ts":1790856000000,"modelInfo":{"id":"gpt-5.2","provider":"openai"},
              "metrics":{"inputTokens":100,"outputTokens":20,"cacheReadTokens":0,"cacheWriteTokens":0}}]}
            """);

        var extensionEntries = ClineSource.ParseExtension(extension.RootElement, "task:a", new ParseDiagnostics());
        var cliEntry = Assert.Single(ClineSource.ParseCli(cli.RootElement, "cli:a", new ParseDiagnostics()));

        Assert.Equal(["openai", null], extensionEntries.Select(entry => entry.Provider));
        Assert.Equal("openai", cliEntry.Provider);
    }

    [Fact]
    public void Provider_qualifies_only_models_without_a_slash()
    {
        Assert.Equal(["openai/gpt-5.2", "gpt-5.2"], ClineSource.ModelCandidates("gpt-5.2", "openai"));
        Assert.Equal(["vertex_ai/gemini-2.5-pro", "gemini-2.5-pro"], ClineSource.ModelCandidates("gemini-2.5-pro", "vertex_ai"));
        Assert.Equal(["anthropic/claude-sonnet-4.5"], ClineSource.ModelCandidates("anthropic/claude-sonnet-4.5", "openrouter"));
        Assert.Equal(["glm-4.7-flash"], ClineSource.ModelCandidates("glm-4.7-flash", null));
    }
}
