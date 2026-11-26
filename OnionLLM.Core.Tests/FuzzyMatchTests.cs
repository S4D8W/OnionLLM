using OnionLLM.ViewModels;

namespace OnionLLM.Core.Tests;

/// <summary>Command palette matching (DESIGN.md §4.6.7); FuzzyMatch.cs is linked into this project.</summary>
public class FuzzyMatchTests
{
    [Fact]
    public void Ignores_case_and_polish_diacritics()
    {
        Assert.NotNull(FuzzyMatch.Match("zrodla", "Źródła"));
        Assert.NotNull(FuzzyMatch.Match("DZIS", "Dziś"));
        Assert.NotNull(FuzzyMatch.Match("ostatnia", "Ostatnia"));
        Assert.Equal("zrodla", FuzzyMatch.Fold("Źródła"));
    }

    [Fact]
    public void Fold_keeps_one_character_per_input_character()
    {
        const string text = "Ustawienia → Cennik żółć";
        Assert.Equal(text.Length, FuzzyMatch.Fold(text).Length);
    }

    [Fact]
    public void Returns_matched_positions_for_highlighting()
    {
        var result = FuzzyMatch.Match("cen", "Ustawienia → Cennik");
        Assert.NotNull(result);
        Assert.Equal([13, 14, 15], result.Value.Positions);
    }

    [Fact]
    public void Subsequence_skips_spaces_in_query()
    {
        var result = FuzzyMatch.Match("ust cen", "Ustawienia → Cennik");
        Assert.NotNull(result);
        Assert.Equal(6, result.Value.Positions.Count);
    }

    [Fact]
    public void Missing_characters_do_not_match()
    {
        Assert.Null(FuzzyMatch.Match("xyz", "Raporty"));
        Assert.Null(FuzzyMatch.Match("raportyy", "Raporty"));
    }

    [Fact]
    public void Prefix_ranks_above_inner_substring_and_substring_above_subsequence()
    {
        var prefix = FuzzyMatch.Match("ses", "Sesje")!.Value.Score;
        var inner = FuzzyMatch.Match("ses", "Przebuduj sesje bazy")!.Value.Score;
        var scattered = FuzzyMatch.Match("ses", "Sprawdź rozbieżności kosztu")!.Value.Score;
        Assert.True(prefix > inner);
        Assert.True(inner > scattered);
    }

    [Fact]
    public void Word_start_subsequence_prefers_initials()
    {
        var result = FuzzyMatch.Match("ow", "Odśwież dane");
        Assert.NotNull(result);
        Assert.Equal(0, result.Value.Positions[0]);
    }

    [Fact]
    public void Empty_query_matches_everything_without_positions()
    {
        var result = FuzzyMatch.Match("  ", "Modele");
        Assert.NotNull(result);
        Assert.Empty(result.Value.Positions);
    }
}
