using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>Poziom z <c>config.toml</c> i ustawienie szybkości — przeniesione z ccusage <c>codex/src/speed.rs</c> (testy).</summary>
public class CodexConfigTierTests
{
    [Theory]
    [InlineData("service_tier = \"fast\"", SpeedTier.Fast)]
    [InlineData("service_tier = 'priority' # use higher tier", SpeedTier.Fast)]
    [InlineData("service_tier = \"flex\"", SpeedTier.Flex)]
    [InlineData("service_tier = \"ultrafast\"", SpeedTier.Ultrafast)]
    [InlineData("service_tier = \"standard\"", SpeedTier.Standard)]
    [InlineData("service_tier = \"default\"", SpeedTier.Standard)]
    [InlineData("service_tier_override = \"fast\"", SpeedTier.Unknown)]
    [InlineData("service_tier = \"breakfast\"", SpeedTier.Unknown)]
    [InlineData("service_tier = \"flex", SpeedTier.Unknown)] // niepoprawny TOML
    [InlineData("service_tier = flex", SpeedTier.Unknown)]   // niepoprawny TOML
    public void Detects_explicit_service_tier_values(string content, SpeedTier expected) =>
        Assert.Equal(expected, CodexToml.ServiceTier(content));

    [Fact]
    public void Ignores_inactive_profiles_when_resolving_config_tier()
    {
        Assert.Equal(SpeedTier.Standard, CodexToml.ServiceTier("""
            service_tier = "standard"
            [profiles.flex]
            service_tier = "flex"
            """));
        Assert.Equal(SpeedTier.Unknown, CodexToml.ServiceTier("""
            [profiles.flex]
            service_tier = "flex"
            """));
    }

    [Fact]
    public void Selected_profile_overrides_top_level_tier()
    {
        Assert.Equal(SpeedTier.Flex, CodexToml.ServiceTier("""
            service_tier = "standard"
            profile = "flex.#"
            [profiles."flex.#"]
            service_tier = "flex"
            [profiles.fast]
            service_tier = "priority"
            """));
        Assert.Equal(SpeedTier.Standard, CodexToml.ServiceTier("""
            service_tier = "priority"
            profile = "standard"
            [profiles.standard]
            service_tier = "default"
            """));
    }

    [Fact]
    public void Selected_profile_without_a_tier_inherits_top_level_tier() =>
        Assert.Equal(SpeedTier.Flex, CodexToml.ServiceTier("""
            service_tier = "flex"
            profile = "work"
            [profiles.work]
            model = "gpt-6.1-sol"
            [profiles.fast]
            service_tier = "priority"
            """));

    [Fact]
    public void Ignores_tier_assignments_inside_multiline_strings() =>
        Assert.Equal(SpeedTier.Standard, CodexToml.ServiceTier("""
            service_tier = "standard"
            instructions = '''
            service_tier = "flex"
            '''
            """));

    [Fact]
    public void Reads_dotted_keys_inline_tables_and_skips_other_values()
    {
        Assert.Equal(SpeedTier.Fast, CodexToml.ServiceTier("""
            model = "gpt-5.5" # komentarz
            approval = { mode = "never", "x.y" = [1, 2, { a = "b" }] }
            notify = [
              "a", # komentarz w tablicy
              "b",
            ]
            started = 1979-05-27 07:32:00
            limit = -1_000
            enabled = true
            profile = "p"
            profiles.p.service_tier = "priority"
            [[mcp_servers]]
            service_tier = "flex"
            """));
        Assert.Equal(SpeedTier.Flex, CodexToml.ServiceTier("""profiles = { p = { service_tier = "flex" } }""" + "\nprofile = \"p\""));
    }

    [Fact]
    public void Config_file_is_used_for_unclassified_usage_and_its_change_forces_a_reparse()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/a.jsonl",
            """{"timestamp":"2026-07-22T00:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5","last_token_usage":{"input_tokens":10,"output_tokens":1}}}}""");
        var config = directory.Write("config.toml", """
            service_tier = "standard"
            [profiles.flex]
            service_tier = "flex"
            """);
        var source = new CodexSource();
        var onion = TestData.ConfigWith("codex", directory.Path);

        Assert.Equal(SpeedTier.Standard, Assert.Single(TestData.ParseAll(source, onion)).Speed);

        var before = Assert.Single(source.EnumerateFiles(onion)).ModifiedMs;
        File.WriteAllText(config, "service_tier = \"priority\"");
        File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddMinutes(5));

        Assert.True(Assert.Single(source.EnumerateFiles(onion)).ModifiedMs > before);
        Assert.Equal(SpeedTier.Fast, Assert.Single(TestData.ParseAll(source, onion)).Speed);
    }

    /// <summary>Wpisy z kilku katalogów <c>CODEX_HOME</c> po deduplikacji regułami tej samej instancji źródła.</summary>
    private static List<UsageEntry> LoadHomes(params TempDirectory[] homes)
    {
        var source = new CodexSource();
        var config = new OnionConfig { Timezone = "UTC" };
        config.Sources["codex"] = new SourceSettings { Paths = homes.Select(home => home.Path).ToList() };
        return Storage.Deduplicator.Apply(TestData.ParseAll(source, config), _ => source);
    }

    private static string Request(string model, string timestamp = "2026-07-22T00:00:01Z") =>
        """{"timestamp":"@0@","type":"event_msg","payload":{"type":"token_count","info":{"model":"@1@","last_token_usage":{"input_tokens":10,"output_tokens":1}}}}""".Fill(timestamp, model);

    [Fact]
    public void Each_codex_home_prices_its_own_unclassified_usage_with_its_own_config()
    {
        using var fast = new TempDirectory();
        using var flex = new TempDirectory();
        fast.Write("config.toml", "service_tier = \"priority\"");
        fast.Write("sessions/a.jsonl", Request("gpt-5.5", "2026-07-22T00:00:01Z"));
        flex.Write("config.toml", "service_tier = \"flex\"");
        flex.Write("sessions/b.jsonl", Request("gpt-5.5", "2026-07-22T00:00:02Z"));

        var entries = LoadHomes(fast, flex);

        Assert.Equal([("a", SpeedTier.Fast), ("b", SpeedTier.Flex)], entries.Select(entry => (entry.SessionId, entry.Speed)));
    }

    [Theory]
    [InlineData("priority", "standard", "gpt-5.5", SpeedTier.Fast)]     // fast 2,5× > standard
    [InlineData("standard", "priority", "gpt-5.5", SpeedTier.Fast)]
    [InlineData("flex", "standard", "gpt-5.5", SpeedTier.Standard)]     // flex 0,5× < standard
    [InlineData("priority", "standard", "gpt-5.1", SpeedTier.Standard)] // gpt-5.1 bez stawki fast — remis, jak ccusage Standard
    [InlineData("priority", null, "gpt-5.5", SpeedTier.Fast)]           // katalog bez config.toml nie wnosi poziomu
    public void Unclassified_copies_across_homes_use_the_highest_configured_multiplier(string first, string? second, string model, SpeedTier expected)
    {
        foreach (var reversed in new[] { false, true })
        {
            using var one = new TempDirectory();
            using var two = new TempDirectory();
            one.Write("config.toml", $"service_tier = \"{first}\"");
            if (second is not null)
            {
                two.Write("config.toml", $"service_tier = \"{second}\"");
            }

            one.Write("sessions/2026/07/22/rollout-a.jsonl", Request(model));
            two.Write("sessions/2026/07/22/rollout-a.jsonl", Request(model));

            var entry = Assert.Single(reversed ? LoadHomes(two, one) : LoadHomes(one, two));

            Assert.Equal(expected, entry.Speed);
        }
    }

    [Fact]
    public void Recorded_tier_takes_precedence_over_another_homes_config()
    {
        foreach (var reversed in new[] { false, true })
        {
            using var recorded = new TempDirectory();
            using var configured = new TempDirectory();
            recorded.Write("config.toml", "service_tier = \"priority\"");
            recorded.Write("sessions/a.jsonl", string.Join('\n',
                """{"timestamp":"2026-07-22T00:00:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"flex"}}}""",
                Request("gpt-5.5")));
            configured.Write("config.toml", "service_tier = \"priority\"");
            configured.Write("sessions/a.jsonl", Request("gpt-5.5"));

            var entry = Assert.Single(reversed ? LoadHomes(configured, recorded) : LoadHomes(recorded, configured));

            Assert.Equal(SpeedTier.Flex, entry.Speed);
        }
    }

    [Theory]
    [InlineData(null, SpeedTier.Fast)]
    [InlineData("auto", SpeedTier.Fast)]
    [InlineData("standard", SpeedTier.Standard)]
    [InlineData("flex", SpeedTier.Flex)]
    [InlineData("fast", SpeedTier.Fast)]
    [InlineData("FAST", SpeedTier.Fast)]
    public void Speed_setting_overrides_the_logged_tier_unless_auto(string? speed, SpeedTier expected)
    {
        IUsageSource source = new CodexSource();
        var entry = TestData.Entry("2026-07-22T00:00:01Z", 10, 1, "gpt-5", "codex") with { Speed = SpeedTier.Fast };

        Assert.Equal(expected, source.ApplySettings(entry, new SourceSettings { Speed = speed }).Speed);
        Assert.Equal(SpeedTier.Standard,
            source.ApplySettings(entry with { Speed = SpeedTier.Unknown }, new SourceSettings { Speed = "standard" }).Speed);
    }
}
