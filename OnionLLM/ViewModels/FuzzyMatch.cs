using System.Globalization;
using System.Text;

namespace OnionLLM.ViewModels;

/// <summary>Result of a fuzzy match: higher <see cref="Score"/> is better; <see cref="Positions"/> = matched characters.</summary>
public readonly record struct FuzzyResult(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Fuzzy matching for the command palette (DESIGN.md §4.6.7): case- and diacritic-insensitive („zrodla” → „Źródła”),
/// contiguous matches rank above scattered ones, word starts and prefixes rank higher. Pure logic — linked into tests.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Lower-case, diacritics removed, one output character per input character (positions stay valid).</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(FoldChar(c));
        }

        return builder.ToString();
    }

    public static FuzzyResult? Match(string query, string text)
    {
        var needle = new string(Fold(query).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (needle.Length == 0)
        {
            return new FuzzyResult(0, []);
        }

        var haystack = Fold(text);

        // 1. Contiguous substring (best at a word start, then the earliest).
        var best = -1;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal))
        {
            if (best < 0 || (IsWordStart(haystack, index) && !IsWordStart(haystack, best)))
            {
                best = index;
            }
        }

        if (best >= 0)
        {
            var score = 1000 - best + (best == 0 ? 200 : IsWordStart(haystack, best) ? 100 : 0) - haystack.Length / 4;
            return new FuzzyResult(score, Enumerable.Range(best, needle.Length).ToArray());
        }

        // 2. Subsequence: greedy, preferring word starts; scattered matches score lower.
        var positions = new List<int>(needle.Length);
        var from = 0;
        foreach (var c in needle)
        {
            var at = NextWordStart(haystack, c, from);
            if (at < 0)
            {
                at = haystack.IndexOf(c, from);
            }

            if (at < 0)
            {
                return null;
            }

            positions.Add(at);
            from = at + 1;
        }

        var subsequence = 500;
        for (var i = 0; i < positions.Count; i++)
        {
            if (IsWordStart(haystack, positions[i]))
            {
                subsequence += 15;
            }

            if (i > 0)
            {
                subsequence += positions[i] == positions[i - 1] + 1 ? 10 : -Math.Min(10, positions[i] - positions[i - 1]);
            }
        }

        return new FuzzyResult(subsequence - positions[0] - haystack.Length / 4, positions);
    }

    /// <summary>Next occurrence of <paramref name="c"/> at a word start, only if no plain occurrence comes before the next word.</summary>
    private static int NextWordStart(string haystack, char c, int from)
    {
        for (var i = from; i < haystack.Length; i++)
        {
            if (haystack[i] == c && IsWordStart(haystack, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]) || (char.IsDigit(text[index]) != char.IsDigit(text[index - 1]));

    private static char FoldChar(char c)
    {
        if (c is 'ł' or 'Ł')
        {
            return 'l';
        }

        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (var part in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
            {
                return char.ToLowerInvariant(part);
            }
        }

        return char.ToLowerInvariant(c);
    }
}
