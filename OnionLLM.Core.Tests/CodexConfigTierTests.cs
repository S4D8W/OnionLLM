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
