using System.Text.Json;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.5: Codebuff rozlicza przez własną usługę (trasa obca), kandydaci z <c>infer_provider</c>.</summary>
public class CodebuffSourcePricingTests
{
    [Fact]
    public void Every_entry_is_billed_by_codebuff()
    {
        using var document = JsonDocument.Parse("""
            [{"id":"m","role":"assistant","timestamp":"2026-11-17T10:00:00.000Z",
              "metadata":{"model":"deepseek-v4-flash","usage":{"inputTokens":1000,"outputTokens":1000}}}]
            """);

        var entry = Assert.Single(CodebuffSource.ParseChat(document.RootElement,
            Path.Combine("manicode", "projects", "p", "chats", "c", "chat-messages.json"), new ParseDiagnostics()));

        Assert.Equal(CodebuffSource.BillingProvider, entry.Provider);
    }

    [Fact]
    public void Candidates_add_the_inferred_vendor_after_the_logged_name()
    {
        // ccusage codebuff/src/parser.rs: adapter_pricing_identity
        Assert.Equal(["gpt-5.2", "openai/gpt-5.2"], CodebuffSource.ModelCandidates("gpt-5.2"));
        Assert.Equal(["claude-sonnet-4-5", "anthropic/claude-sonnet-4-5"], CodebuffSource.ModelCandidates("claude-sonnet-4-5"));
        Assert.Equal(["openai/gpt-5.2"], CodebuffSource.ModelCandidates("openai/gpt-5.2"));
        Assert.Equal(["grok-4", "xai/grok-4"], CodebuffSource.ModelCandidates("grok-4"));
        Assert.Equal(["deepseek-v4-flash"], CodebuffSource.ModelCandidates("deepseek-v4-flash"));
    }
}
