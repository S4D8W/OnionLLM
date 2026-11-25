using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>Wspólne reguły Core: lustra między źródłami, wpisy tylko do raportu sesji, hooki źródeł.</summary>
public class SharedContractTests
{
    private static UsageEntry Mirror(string claudeSession) =>
        TestData.Entry("2026-11-24T10:00:00Z", 100, 10, source: OpenCodeSource.SourceId, session: "oc-1") with
        {
            MirrorOf = $"{ClaudeSource.SourceId}:{claudeSession}"
        };

    [Fact]
    public void Only_main_thread_claude_entries_claim_mirrored_sessions()
    {
        var sidechainOnly = TestData.Entry("2026-11-24T10:00:00Z", 100, 10, session: "side") with { Preference = 0 };
        var mainThread = TestData.Entry("2026-11-24T10:00:00Z", 100, 10, session: "main") with { Preference = 1 };

        // Sesja Claude tylko z sidechainem nie „zgłasza” tur claude-agent — wpis OpenCode zostaje.
        Assert.Contains(Deduplicator.Apply([sidechainOnly, Mirror("side")]), entry => entry.Source == OpenCodeSource.SourceId);
        Assert.DoesNotContain(Deduplicator.Apply([mainThread, Mirror("main")]), entry => entry.Source == OpenCodeSource.SourceId);
    }

    [Fact]
    public void Discarded_duplicate_copies_still_claim_their_session()
    {
        // Ta sama odpowiedź w dwóch sesjach: kopia z sesji "copy" przegrywa deduplikację, ale nadal zgłasza swoją sesję.
        var kept = TestData.Entry("2026-11-24T10:00:00Z", 100, 10, session: "orig") with { Preference = 1, DedupeKey = "k", CacheCreationTokens = 5 };
        var discarded = TestData.Entry("2026-11-24T10:00:00Z", 100, 10, session: "copy") with { Preference = 1, DedupeKey = "k" };

        var result = Deduplicator.Apply([kept, discarded, Mirror("copy")], _ => null);

        Assert.Single(result);
        Assert.Equal("orig", result[0].SessionId);
    }

    [Fact]
    public void Session_totals_are_used_only_by_unbounded_session_report()
    {
        var total = TestData.Entry("2026-11-24T10:00:00Z", 1000, 100, source: OpenCodeSource.SourceId, session: "oc-1") with { IsSessionSummary = true };
        var source = new OpenCodeSource();

        Assert.True(source.IsSessionReportOnly(total));
        Assert.False(source.IsSessionReportOnly(total with { SessionTier = 0 }));
        Assert.False(new ClaudeSource().IsSessionReportOnly(TestData.Entry("2026-11-24T10:00:00Z", 1, 1) with { IsSessionSummary = true }));
    }

    [Fact]
    public void Base_class_hooks_are_reached_through_the_interface()
    {
        // Regresja: domyślne metody interfejsu nie są nadpisywane przez składowe klas pochodnych bez `override` w bazie.
        IUsageSource codex = new CodexSource();
        var fast = TestData.Entry("2026-11-24T10:00:00Z", 1, 1, model: "gpt-5", source: CodexSource.SourceId) with { Speed = SpeedTier.Fast };

        Assert.NotNull(codex.DuplicateComparer);
        Assert.Equal(SpeedTier.Standard, codex.ApplySettings(fast, new Config.SourceSettings { Speed = "standard" }).Speed);
    }
}
