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
    public void Exact_duplicates_need_equal_key_and_source_data()
    {
        // Kopie o tej samej tożsamości, ale innych danych pomocniczych, rozstrzyga Reconcile źródła — tu (bez źródła) obie zostają.
        var first = TestData.Entry("2026-11-24T10:00:00Z", 100, 10, source: "test") with { DedupeKey = "k", SourceData = "a" };
        var sameData = first with { SessionId = "copy" };
        var otherData = first with { SourceData = "b" };

        Assert.Single(Deduplicator.Apply([first, sameData], _ => null));
        Assert.Equal(2, Deduplicator.Apply([first, otherData], _ => null).Count);
    }

    [Fact]
    public void Dedupe_keys_carry_no_helper_data_after_a_unit_separator()
    {
        // 11.9.6: dane pomocnicze (flagi, surowe nazwy modeli, identyfikatory żądań) są w SourceData, nie w kluczu.
        var claude = TestData.Entry("2026-11-24T10:00:00Z", 1, 1) with
        {
            DedupeKey = new ClaudeDedupeKey("m", "r", "s", 1_000, null, true, true).Format(),
            SourceData = new ClaudeDedupeKey("m", "r", "s", 1_000, null, true, true).Data
        };
        var parsed = ClaudeDedupeKey.Parse(claude)!;

        Assert.Equal("sc", claude.SourceData);
        Assert.True(parsed.IsSidechain);
        Assert.True(parsed.HasCacheSplit);
        Assert.Equal(new ClaudeDedupeKey("m", "r", "s", 1_000, null, false, false).Format(), claude.DedupeKey);
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
