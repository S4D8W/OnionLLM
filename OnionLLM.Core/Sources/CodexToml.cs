using System.Globalization;
using System.Text;
using OnionLLM.Core.Models;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Minimalny czytnik TOML na potrzeby <c>config.toml</c> Codexa: zbiera wartości tekstowe pod pełnymi ścieżkami kluczy
/// (tabele <c>[a."b.c"]</c>, klucze kropkowane, tabele inline). Obsługuje cudzysłowy, apostrofy, ciągi wielowierszowe,
/// tablice i komentarze. Niepoprawny plik daje <c>null</c> — jak <c>content.parse::&lt;toml::Table&gt;()</c> w ccusage.
/// </summary>
internal sealed class CodexToml
{
    private const char Separator = '\u001f';

    private readonly string _text;
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private int _position;

    private CodexToml(string text) => _text = text;

    /// <summary>Pełna ścieżka klucza (segmenty mogą zawierać kropki).</summary>
    public static string Key(params string[] segments) => string.Join(Separator, segments);

    /// <summary>Wartości tekstowe pod pełnymi ścieżkami kluczy (<see cref="Key"/>); null = niepoprawny TOML.</summary>
    public static Dictionary<string, string>? ReadStrings(string content)
    {
        var parser = new CodexToml(content);
        return parser.ParseDocument() ? parser._strings : null;
    }

    /// <summary>
    /// Poziom usługi z <c>config.toml</c>: <c>profiles.&lt;profile&gt;.service_tier</c> wybranego profilu,
    /// a bez niego <c>service_tier</c> najwyższego poziomu (ccusage <c>codex/src/speed.rs:26-37</c>).
    /// </summary>
    public static SpeedTier ServiceTier(string content)
    {
        if (ReadStrings(content) is not { } values)
        {
            return SpeedTier.Unknown;
        }

        string? tier = null;
        if (values.TryGetValue(Key("profile"), out var profile))
        {
            values.TryGetValue(Key("profiles", profile, "service_tier"), out tier);
        }

        tier ??= values.GetValueOrDefault(Key("service_tier"));
        return CodexSource.ParseTier(tier);
    }

    private bool ParseDocument()
    {
        string[] table = [];
        while (true)
        {
            SkipBlank();
            if (AtEnd)
            {
                return true;
            }

            if (Current == '[')
            {
                var array = Peek(1) == '[';
                _position += array ? 2 : 1;
                SkipSpaces();
                if (ParseKeyPath() is not { } path)
                {
                    return false;
                }

                SkipSpaces();
                if (!Consume(']') || (array && !Consume(']')))
                {
                    return false;
                }

                // Elementy tablic tabel nie są adresowalne prostą ścieżką — odkładamy je pod osobnym segmentem.
                table = array ? [.. path, "[]"] : path;
                if (!EndOfLine())
                {
                    return false;
                }

                continue;
            }

            if (!ParseKeyValue(table) || !EndOfLine())
            {
                return false;
            }
        }
    }

    private bool ParseKeyValue(string[] table)
    {
        if (ParseKeyPath() is not { } key)
        {
            return false;
        }

        SkipSpaces();
        if (!Consume('='))
        {
            return false;
        }

        SkipSpaces();
        return ParseValue([.. table, .. key]);
    }

    private bool ParseValue(string[] path)
    {
        if (AtEnd)
        {
            return false;
        }

        string? text;
        switch (Current)
        {
            case '"' when Peek(1) == '"' && Peek(2) == '"':
                text = ParseMultiline("\"\"\"", escapes: true);
                break;
            case '\'' when Peek(1) == '\'' && Peek(2) == '\'':
                text = ParseMultiline("'''", escapes: false);
                break;
            case '"':
                text = ParseBasicString();
                break;
            case '\'':
                text = ParseLiteralString();
                break;
            case '[':
                return ParseArray(path);
            case '{':
                return ParseInlineTable(path);
            default:
                return ParseScalar();
        }

        if (text is null)
        {
            return false;
        }

        _strings[Key(path)] = text;
        return true;
    }

    private bool ParseArray(string[] path)
    {
        _position++;
        var index = 0;
        while (true)
        {
            SkipBlank();
            if (Consume(']'))
            {
                return true;
            }

            if (!ParseValue([.. path, index.ToString(CultureInfo.InvariantCulture)]))
            {
                return false;
            }

            index++;
            SkipBlank();
            if (Consume(','))
            {
                continue;
            }

            return Consume(']');
        }
    }

    private bool ParseInlineTable(string[] path)
    {
        _position++;
        SkipSpaces();
        if (Consume('}'))
        {
            return true;
        }

        while (true)
        {
            SkipSpaces();
            if (!ParseKeyValue(path))
            {
                return false;
            }

            SkipSpaces();
            if (Consume(','))
            {
                continue;
            }

            return Consume('}');
        }
    }

    /// <summary>Liczba, data, <c>true</c>/<c>false</c>, <c>inf</c>/<c>nan</c> — wartość nie jest potrzebna, tylko jej poprawność.</summary>
    private bool ParseScalar()
    {
        var start = _position;
        ReadScalarToken();

        // Data i czas oddzielone spacją: 1979-05-27 07:32:00.
        if (_position - start == 10 && _text[start + 4] == '-' && Peek(0) == ' ' && char.IsAsciiDigit(Peek(1)))
        {
            _position++;
            ReadScalarToken();
        }

        if (_position == start)
        {
            return false;
        }

        var first = _text[start];
        var token = _text[start.._position];
        return token is "true" or "false" ||
               char.IsAsciiDigit(first) ||
               ((first is '+' or '-') && token.Length > 1) ||
               token is "inf" or "nan";
    }

    private void ReadScalarToken()
    {
        while (!AtEnd && Current is not (' ' or '\t' or '\r' or '\n' or ',' or ']' or '}' or '#'))
        {
            _position++;
        }
    }

    private string[]? ParseKeyPath()
    {
        var segments = new List<string>();
        while (true)
        {
            SkipSpaces();
            if (ParseKeySegment() is not { } segment)
            {
                return null;
            }

            segments.Add(segment);
            SkipSpaces();
            if (!Consume('.'))
            {
                return [.. segments];
            }
        }
    }

    private string? ParseKeySegment()
    {
        if (AtEnd)
        {
            return null;
        }

        if (Current == '"')
        {
            return ParseBasicString();
        }

        if (Current == '\'')
        {
            return ParseLiteralString();
        }

        var start = _position;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current is '_' or '-'))
        {
            _position++;
        }

        return _position == start ? null : _text[start.._position];
    }

    private string? ParseBasicString()
    {
        _position++;
        var builder = new StringBuilder();
        while (!AtEnd)
        {
            var current = Current;
            if (current is '\n' or '\r')
            {
                return null;
            }

            _position++;
            if (current == '"')
            {
                return builder.ToString();
            }

            if (current == '\\')
            {
                if (!ReadEscape(builder))
                {
                    return null;
                }

                continue;
            }

            builder.Append(current);
        }

        return null;
    }

    private bool ReadEscape(StringBuilder builder)
    {
        if (AtEnd)
        {
            return false;
        }

        var escape = Current;
        _position++;
        switch (escape)
        {
            case 'b': builder.Append('\b'); return true;
            case 't': builder.Append('\t'); return true;
            case 'n': builder.Append('\n'); return true;
            case 'f': builder.Append('\f'); return true;
            case 'r': builder.Append('\r'); return true;
            case 'e': builder.Append('\u001b'); return true;
            case '"': builder.Append('"'); return true;
            case '\\': builder.Append('\\'); return true;
            case 'u' or 'U' or 'x':
                var length = escape switch { 'u' => 4, 'U' => 8, _ => 2 };
                if (_position + length > _text.Length ||
                    !int.TryParse(_text.AsSpan(_position, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ||
                    code is < 0 or > 0x10FFFF)
                {
                    return false;
                }

                _position += length;
                builder.Append(char.ConvertFromUtf32(code));
                return true;
            default:
                return false;
        }
    }

    private string? ParseLiteralString()
    {
        _position++;
        var start = _position;
        while (!AtEnd)
        {
            if (Current is '\n' or '\r')
            {
                return null;
            }

            if (Current == '\'')
            {
                var value = _text[start.._position];
                _position++;
                return value;
            }

            _position++;
        }

        return null;
    }

    private string? ParseMultiline(string delimiter, bool escapes)
    {
        _position += 3;
        var builder = new StringBuilder();
        while (!AtEnd)
        {
            if (string.CompareOrdinal(_text, _position, delimiter, 0, 3) == 0)
            {
                _position += 3;

                // Do dwóch cudzysłowów tuż przed zamknięciem należy do treści.
                for (var extra = 0; extra < 2 && !AtEnd && Current == delimiter[0]; extra++)
                {
                    builder.Append(Current);
                    _position++;
                }

                return builder.ToString();
            }

            var current = Current;
            _position++;
            if (escapes && current == '\\')
            {
                if (!AtEnd && Current is ' ' or '\t' or '\r' or '\n')
                {
                    // Kontynuacja wiersza: pomijamy białe znaki do następnej treści.
                    while (!AtEnd && Current is ' ' or '\t' or '\r' or '\n')
                    {
                        _position++;
                    }

                    continue;
                }

                if (!ReadEscape(builder))
                {
                    return null;
                }

                continue;
            }

            builder.Append(current);
        }

        return null;
    }

    private bool AtEnd => _position >= _text.Length;

    private char Current => _text[_position];

    private char Peek(int offset) => _position + offset < _text.Length ? _text[_position + offset] : '\0';

    private bool Consume(char expected)
    {
        if (!AtEnd && Current == expected)
        {
            _position++;
            return true;
        }

        return false;
    }

    private void SkipSpaces()
    {
        while (!AtEnd && Current is ' ' or '\t')
        {
            _position++;
        }
    }

    private void SkipComment()
    {
        if (!AtEnd && Current == '#')
        {
            while (!AtEnd && Current != '\n')
            {
                _position++;
            }
        }
    }

    /// <summary>Białe znaki, nowe wiersze i komentarze.</summary>
    private void SkipBlank()
    {
        while (!AtEnd)
        {
            if (Current is ' ' or '\t' or '\r' or '\n')
            {
                _position++;
            }
            else if (Current == '#')
            {
                SkipComment();
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Po wartości lub nagłówku: tylko spacje i komentarz do końca wiersza.</summary>
    private bool EndOfLine()
    {
        SkipSpaces();
        SkipComment();
        if (AtEnd)
        {
            return true;
        }

        if (Current == '\r' && Peek(1) == '\n')
        {
            _position += 2;
            return true;
        }

        return Consume('\n');
    }
}
