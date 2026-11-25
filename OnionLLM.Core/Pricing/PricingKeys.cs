namespace OnionLLM.Core.Pricing;

/// <summary>
/// Porównywanie nazw modeli z kluczami cennika (ccusage <c>pricing/keys.rs</c>).
/// Separatory <c>.</c> i <c>@</c> są równoważne <c>-</c>, wielkość liter nie ma znaczenia.
/// </summary>
internal static class PricingKeys
{
    /// <summary>Tożsamość klucza: małe litery, <c>.</c>/<c>@</c> → <c>-</c>.</summary>
    public static string Identity(string value)
    {
        var chars = value.ToLowerInvariant().ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is '.' or '@')
            {
                chars[index] = '-';
            }
        }

        return new string(chars);
    }

    public static string LastSegment(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }

    /// <summary>Etykieta <c>[agent] model</c> → <c>model</c>.</summary>
    public static string Unlabeled(string label)
    {
        if (label.StartsWith('['))
        {
            var end = label.IndexOf("] ", StringComparison.Ordinal);
            if (end > 0)
            {
                return label[(end + 2)..];
            }
        }

        return label;
    }

    /// <summary>Kolejne sufiksy po każdym <c>/</c>: <c>a/b/c</c> → <c>b/c</c>, <c>c</c>.</summary>
    public static IEnumerable<string> Suffixes(string name)
    {
        for (var index = name.IndexOf('/'); index >= 0 && index < name.Length - 1; index = name.IndexOf('/', index + 1))
        {
            yield return name[(index + 1)..];
        }
    }

    /// <summary>Nazwa bez sufiksu daty <c>-YYYY-MM-DD</c> lub <c>-YYYYMMDD</c> (ccusage <c>model_without_date_suffix</c>).</summary>
    public static string WithoutDateSuffix(string model)
    {
        if (model.Length > 11)
        {
            var suffix = model[^11..];
            if (suffix[0] == '-' && AllDigits(suffix.AsSpan(1, 4)) && suffix[5] == '-' && AllDigits(suffix.AsSpan(6, 2)) &&
                suffix[8] == '-' && AllDigits(suffix.AsSpan(9, 2)))
            {
                return model[..^11];
            }
        }

        if (model.Length > 9 && model[^9] == '-' && AllDigits(model.AsSpan(model.Length - 8)))
        {
            return model[..^9];
        }

        return model;
    }

    /// <summary>
    /// Czy klucz cennika opisuje ten sam model co nazwa z logu (ccusage <c>pricing_key_matches</c>, #2236).
    /// Klucz może wystąpić w nazwie po kwalifikatorze (<c>/</c>, <c>.</c>, <c>:</c>…) i z sufiksem neutralnym cenowo
    /// (data, rewizja <c>-v1:0</c>, <c>[1m]</c>, poziom effort, <c>thinking</c> rodziny); albo nazwa jest końcówką dłuższego
    /// klucza po kwalifikatorze (<c>kimi-k3</c> → <c>moonshot/kimi-k3</c>). Słowo przed kluczem (<c>chat-gpt-5.4</c>) lub inne
    /// słowo po nim (<c>gpt-5.4-codex</c>) oznacza inny model.
    /// </summary>
    public static bool Matches(string key, string model) => SameModel(Identity(model), model, key);

    /// <summary>Klucz zawarty w nazwie z sufiksem neutralnym cenowo (ccusage <c>names_pricing_key</c>) — warunek dla wierszy <c>exactOnly</c>.</summary>
    public static bool NamesKey(string key, string model) => ContainsKey(Identity(model), model, key);

    private static bool SameModel(string identity, string raw, string key)
    {
        var keyIdentity = Identity(key);
        if (identity.Length >= keyIdentity.Length)
        {
            return ContainsKey(identity, raw, key);
        }

        var start = keyIdentity.Length - identity.Length;
        return IsQualifierEnd(key[start - 1]) && keyIdentity.AsSpan(start).SequenceEqual(identity);
    }

    private static bool ContainsKey(string identity, string raw, string key)
    {
        var keyIdentity = Identity(key);
        if (keyIdentity.Length == 0)
        {
            return false;
        }

        var family = FamilyWords(key);
        for (var index = identity.IndexOf(keyIdentity, StringComparison.Ordinal);
             index >= 0;
             index = identity.IndexOf(keyIdentity, index + 1, StringComparison.Ordinal))
        {
            if ((index == 0 || IsQualifierEnd(raw[index - 1])) &&
                IsPriceNeutralSuffix(raw[(index + keyIdentity.Length)..], family))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQualifierEnd(char value) => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_';

    /// <summary>Słowa neutralne rodziny klucza; nazwa rodziny może stać po kwalifikatorze <c>.</c> (np. <c>anthropic.claude-…</c>).</summary>
    private static string[] FamilyWords(string key)
    {
        var segment = LastSegment(key).ToLowerInvariant();
        var names = new List<string> { segment };
        for (var dot = segment.IndexOf('.'); dot >= 0; dot = segment.IndexOf('.', dot + 1))
        {
            names.Add(segment[(dot + 1)..]);
        }

        foreach (var (family, words) in PricingData.FamilyPriceNeutralSuffixWords)
        {
            if (names.Any(name => NamesFamily(name, family)))
            {
                return words;
            }
        }

        return [];
    }

    /// <summary><c>.</c> w nazwie rodziny pasuje też do <c>-</c>/<c>@</c>; <c>-</c> zostaje dosłowne (ccusage <c>names_family</c>).</summary>
    private static bool NamesFamily(string name, string family)
    {
        if (name.Length < family.Length)
        {
            return false;
        }

        for (var index = 0; index < family.Length; index++)
        {
            if (name[index] != family[index] && !(family[index] == '.' && name[index] is '-' or '@'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Sufiks złożony wyłącznie z elementów neutralnych cenowo (ccusage <c>is_price_neutral_suffix</c>).</summary>
    private static bool IsPriceNeutralSuffix(string raw, string[] family)
    {
        var rest = raw.ToLowerInvariant();
        while (rest.Length > 0)
        {
            var separator = rest[0];
            if (separator == '[')
            {
                var end = rest.IndexOf(']');
                if (end < 0)
                {
                    return false;
                }

                rest = rest[(end + 1)..];
                continue;
            }

            if (separator is not ('-' or '.' or '_' or ':' or '@'))
            {
                return false;
            }

            var body = rest[1..];
            var wordLength = 0;
            while (wordLength < body.Length && char.IsAsciiLetterOrDigit(body[wordLength]))
            {
                wordLength++;
            }

            var word = body[..wordLength];
            var after = body[wordLength..];
            if (DateSuffixEnd(word, after) is { } dated)
            {
                rest = dated;
            }
            else if (RevisionEnd(word, after) is { } revised)
            {
                rest = revised;
            }
            else if (NeutralWordEnd(body, family) is { } neutral)
            {
                rest = neutral;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static string? DateSuffixEnd(string word, string after)
    {
        if (word.Length == 8 && AllDigits(word))
        {
            return after;
        }

        // -YYYY-MM-DD
        if (word.Length == 4 && AllDigits(word) && after.Length >= 6 && after[0] == '-' && AllDigits(after.AsSpan(1, 2)) &&
            after[3] == '-' && AllDigits(after.AsSpan(4, 2)) && (after.Length == 6 || !char.IsAsciiLetterOrDigit(after[6])))
        {
            return after[6..];
        }

        return null;
    }

    /// <summary>Rewizja wdrożenia Bedrock <c>-v1:0</c>.</summary>
    private static string? RevisionEnd(string word, string after)
    {
        if (word.Length < 2 || word[0] != 'v' || !AllDigits(word.AsSpan(1)) || after.Length < 2 || after[0] != ':')
        {
            return null;
        }

        var digits = 0;
        while (1 + digits < after.Length && char.IsAsciiDigit(after[1 + digits]))
        {
            digits++;
        }

        return digits > 0 ? after[(1 + digits)..] : null;
    }

    private static string? NeutralWordEnd(string body, string[] family)
    {
        foreach (var word in PricingData.PriceNeutralSuffixWords.Concat(family))
        {
            if (body.StartsWith(word, StringComparison.Ordinal) &&
                (body.Length == word.Length || !char.IsAsciiLetterOrDigit(body[word.Length])))
            {
                return body[word.Length..];
            }
        }

        return null;
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
