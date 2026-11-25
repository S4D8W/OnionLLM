using System.Globalization;
using System.Text.RegularExpressions;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Config;

/// <summary>
/// Tekstowe formaty edytorów w Ustawieniach: mapy <c>nazwa=wartość</c> (aliasy modeli, etykiety statusline)
/// oraz stawki cennika w USD za 1M tokenów (w configu: USD za token).
/// </summary>
public static partial class ConfigTextFormats
{
    private const decimal Million = 1_000_000m;

    /// <summary>Mapa jako wiersze <c>nazwa=wartość</c>, posortowane po nazwie.</summary>
    public static string FormatMap(IReadOnlyDictionary<string, string>? map) =>
        map is null || map.Count == 0
            ? ""
            : string.Join(Environment.NewLine, map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>
    /// Parsuje wiersze <c>nazwa=wartość</c> (puste wiersze i wiersze od <c>#</c> są pomijane). Nazwa jest dzielona po ostatnim
    /// <c>=</c> (nazwy modeli i ARN-y Bedrock go nie zawierają, etykieta też nie powinna). Nazwy porównywane dokładnie.
    /// </summary>
    /// <exception cref="FormatException">Wiersz bez <c>=</c>, pusta nazwa lub wartość, powtórzona nazwa.</exception>
    public static Dictionary<string, string> ParseMap(string? text, string label)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.LastIndexOf('=');
            var key = separator > 0 ? line[..separator].Trim() : "";
            var value = separator >= 0 ? line[(separator + 1)..].Trim() : "";
            if (key.Length == 0 || value.Length == 0)
            {
                throw new FormatException($"{label}, wiersz {lineNumber} ('{line}'): oczekiwano nazwa=wartość.");
            }

            if (!result.TryAdd(key, value))
            {
                throw new FormatException($"{label}, wiersz {lineNumber}: nazwa '{key}' występuje więcej niż raz.");
            }
        }

        return result;
    }

    /// <summary>Stawka USD/token jako USD za 1M tokenów (do 15 cyfr znaczących, bez zbędnych zer); null → pusty tekst.</summary>
    public static string FormatPerMillion(double? perToken)
    {
        if (perToken is not { } value)
        {
            return "";
        }

        if (!double.IsFinite(value) || Math.Abs(value) > 1e12)
        {
            return (value * 1e6).ToString("R", CultureInfo.InvariantCulture);
        }

        // 15 cyfr znaczących (jak w JSON-ie wpisanym ręcznie), a dopiero potem mnożenie w decimal — bez szumu binarnego (3e-6 → „3”).
        var rounded = decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return (rounded * Million).ToString("0.############################", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stawka w USD za 1M tokenów → USD/token. Tekst niezmieniony względem <paramref name="original"/> zwraca dokładnie
    /// <paramref name="original"/> (bez strat zaokrągleń). Pusty tekst = brak nadpisania (null).
    /// </summary>
    /// <exception cref="FormatException">Niepoprawna lub ujemna liczba.</exception>
    public static double? ParsePerMillion(string? text, double? original, string field)
    {
        var trimmed = (text ?? "").Trim();
        if (original is not null && trimmed == FormatPerMillion(original))
        {
            return original;
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var perMillion))
        {
            throw new FormatException($"{field}: niepoprawna liczba '{trimmed}'.");
        }

        return perMillion < 0
            ? throw new FormatException($"{field}: stawka nie może być ujemna.")
            : (double)(perMillion / Million);
    }

    /// <summary>Liczba (mnożnik) w zapisie dokładnym; null → pusty tekst.</summary>
    public static string FormatNumber(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Nieujemna liczba (mnożnik); pusty tekst = null.</summary>
    /// <exception cref="FormatException">Niepoprawna lub ujemna liczba.</exception>
    public static double? ParseNumber(string? text, string field)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new FormatException($"{field}: niepoprawna liczba '{trimmed}'.");
        }

        return value < 0 ? throw new FormatException($"{field}: wartość nie może być ujemna.") : value;
    }

    /// <summary>Dodatnia liczba całkowita (np. limit tokenów wejścia); pusty tekst = null. Dopuszcza separatory tysięcy (spacja, <c>_</c>).</summary>
    /// <exception cref="FormatException">Niepoprawna liczba lub mniejsza od 1.</exception>
    public static long? ParsePositiveLong(string? text, string field)
    {
        var trimmed = (text ?? "").Trim().Replace(" ", "").Replace(" ", "").Replace("_", "");
        if (trimmed.Length == 0)
        {
            return null;
        }

        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1
            ? value
            : throw new FormatException($"{field}: oczekiwano dodatniej liczby całkowitej, jest '{text?.Trim()}'.");
    }

    /// <summary>
    /// Normalizuje klucz nadpisania: model, <c>dostawca/model</c> albo <c>[źródło] model</c> (jedna spacja po nawiasie,
    /// jak przy dopasowaniu w <see cref="PricingCatalog"/>). Gdy podano <paramref name="sourceIds"/>, źródło musi być znane.
    /// </summary>
    /// <exception cref="FormatException">Pusty klucz lub niepoprawny zapis <c>[źródło] model</c>.</exception>
    public static string NormalizeOverrideKey(string? key, IReadOnlyCollection<string>? sourceIds = null)
    {
        var trimmed = (key ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new FormatException("Nadpisanie cennika: pusty klucz (nazwa modelu lub [źródło] model).");
        }

        if (!trimmed.StartsWith('['))
        {
            return trimmed;
        }

        var match = ScopedKey().Match(trimmed);
        if (!match.Success)
        {
            throw new FormatException($"Nadpisanie cennika '{trimmed}': oczekiwano [źródło] model.");
        }

        var source = match.Groups[1].Value;
        if (sourceIds is not null && !sourceIds.Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            throw new FormatException($"Nadpisanie cennika '{trimmed}': nieznane źródło '{source}' (dostępne: {string.Join(", ", sourceIds)}).");
        }

        return $"[{source}] {match.Groups[2].Value.Trim()}";
    }

    /// <summary>
    /// Buduje <c>pricingOverrides</c> z formularzy. Pomija całkiem puste wiersze; klucze muszą być unikalne
    /// (porównanie jak w cenniku: wielkość liter, <c>.</c>/<c>@</c> = <c>-</c>).
    /// </summary>
    /// <exception cref="FormatException">Błąd klucza, duplikat lub niepoprawna wartość pola.</exception>
    public static Dictionary<string, PricingOverride> BuildPricingOverrides(IEnumerable<PricingOverrideForm> forms, IReadOnlyCollection<string>? sourceIds = null)
    {
        var result = new Dictionary<string, PricingOverride>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var form in forms)
        {
            if (form.IsBlank)
            {
                continue;
            }

            var key = NormalizeOverrideKey(form.Key, sourceIds);
            if (identities.TryGetValue(PricingKeys.Identity(key), out var existing))
            {
                throw new FormatException($"Nadpisanie cennika '{key}' powtarza klucz '{existing}'.");
            }

            identities[PricingKeys.Identity(key)] = key;
            result[key] = form.ToOverride(key);
        }

        return result;
    }

    /// <summary>Formularze dla nadpisań z configu (posortowane po kluczu).</summary>
    public static List<PricingOverrideForm> ToForms(IReadOnlyDictionary<string, PricingOverride>? overrides) =>
        (overrides ?? new Dictionary<string, PricingOverride>())
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .Select(pair => PricingOverrideForm.From(pair.Key, pair.Value))
        .ToList();

    [GeneratedRegex(@"^\[([^\]\s]+)\]\s*(\S.*)$")]
    private static partial Regex ScopedKey();
}

/// <summary>
/// Tekstowy formularz jednego nadpisania cennika. Stawki w USD za 1M tokenów; mnożniki i limit jako liczby.
/// Pola niezmienione od wczytania zwracają dokładnie wartości z configu.
/// </summary>
public sealed class PricingOverrideForm
{
    private readonly PricingOverride _original;

    public PricingOverrideForm() : this(new PricingOverride())
    {
    }

    private PricingOverrideForm(PricingOverride original) => _original = original;

    public string Key { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string CacheWrite { get; set; } = "";
    public string CacheWrite1h { get; set; } = "";
    public string CacheRead { get; set; } = "";
    public string InputAbove200k { get; set; } = "";
    public string OutputAbove200k { get; set; } = "";
    public string CacheWriteAbove200k { get; set; } = "";
    public string CacheReadAbove200k { get; set; } = "";
    public string MaxInputTokens { get; set; } = "";
    public string FastMultiplier { get; set; } = "";
    public string FlexMultiplier { get; set; } = "";

    /// <summary>Czy wypełniono któreś z pól zaawansowanych (próg 200k, limit wejścia, mnożniki).</summary>
    public bool HasAdvanced =>
        new[] { InputAbove200k, OutputAbove200k, CacheWriteAbove200k, CacheReadAbove200k, MaxInputTokens, FastMultiplier, FlexMultiplier }
            .Any(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Wiersz bez klucza i bez żadnej wartości (np. dodany i niewypełniony).</summary>
    public bool IsBlank =>
        string.IsNullOrWhiteSpace(Key) && !HasAdvanced &&
        new[] { Input, Output, CacheWrite, CacheWrite1h, CacheRead }.All(string.IsNullOrWhiteSpace);

    public static PricingOverrideForm From(string key, PricingOverride value) => new(value)
    {
        Key = key,
        Input = ConfigTextFormats.FormatPerMillion(value.InputCostPerToken),
        Output = ConfigTextFormats.FormatPerMillion(value.OutputCostPerToken),
        CacheWrite = ConfigTextFormats.FormatPerMillion(value.CacheCreationInputTokenCost),
        CacheWrite1h = ConfigTextFormats.FormatPerMillion(value.CacheCreationInputTokenCostAbove1h),
        CacheRead = ConfigTextFormats.FormatPerMillion(value.CacheReadInputTokenCost),
        InputAbove200k = ConfigTextFormats.FormatPerMillion(value.InputCostPerTokenAbove200kTokens),
        OutputAbove200k = ConfigTextFormats.FormatPerMillion(value.OutputCostPerTokenAbove200kTokens),
        CacheWriteAbove200k = ConfigTextFormats.FormatPerMillion(value.CacheCreationInputTokenCostAbove200kTokens),
        CacheReadAbove200k = ConfigTextFormats.FormatPerMillion(value.CacheReadInputTokenCostAbove200kTokens),
        MaxInputTokens = value.MaxInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
        FastMultiplier = ConfigTextFormats.FormatNumber(value.FastMultiplier),
        FlexMultiplier = ConfigTextFormats.FormatNumber(value.FlexMultiplier)
    };

    /// <summary>Nadpisanie z pól formularza (stawki przeliczone na USD/token).</summary>
    /// <exception cref="FormatException">Niepoprawna wartość pola (komunikat zawiera klucz i nazwę pola).</exception>
    public PricingOverride ToOverride(string? key = null)
    {
        var name = $"Nadpisanie '{(key ?? Key).Trim()}'";
        return new PricingOverride
        {
            InputCostPerToken = ConfigTextFormats.ParsePerMillion(Input, _original.InputCostPerToken, $"{name} — wejście"),
            OutputCostPerToken = ConfigTextFormats.ParsePerMillion(Output, _original.OutputCostPerToken, $"{name} — wyjście"),
            CacheCreationInputTokenCost = ConfigTextFormats.ParsePerMillion(CacheWrite, _original.CacheCreationInputTokenCost, $"{name} — zapis cache"),
            CacheCreationInputTokenCostAbove1h = ConfigTextFormats.ParsePerMillion(CacheWrite1h, _original.CacheCreationInputTokenCostAbove1h, $"{name} — zapis cache 1 h"),
            CacheReadInputTokenCost = ConfigTextFormats.ParsePerMillion(CacheRead, _original.CacheReadInputTokenCost, $"{name} — odczyt cache"),
            InputCostPerTokenAbove200kTokens = ConfigTextFormats.ParsePerMillion(InputAbove200k, _original.InputCostPerTokenAbove200kTokens, $"{name} — wejście >200k"),
            OutputCostPerTokenAbove200kTokens = ConfigTextFormats.ParsePerMillion(OutputAbove200k, _original.OutputCostPerTokenAbove200kTokens, $"{name} — wyjście >200k"),
            CacheCreationInputTokenCostAbove200kTokens = ConfigTextFormats.ParsePerMillion(CacheWriteAbove200k, _original.CacheCreationInputTokenCostAbove200kTokens, $"{name} — zapis cache >200k"),
            CacheReadInputTokenCostAbove200kTokens = ConfigTextFormats.ParsePerMillion(CacheReadAbove200k, _original.CacheReadInputTokenCostAbove200kTokens, $"{name} — odczyt cache >200k"),
            MaxInputTokens = ConfigTextFormats.ParsePositiveLong(MaxInputTokens, $"{name} — maks. tokenów wejścia"),
            FastMultiplier = ConfigTextFormats.ParseNumber(FastMultiplier, $"{name} — mnożnik fast"),
            FlexMultiplier = ConfigTextFormats.ParseNumber(FlexMultiplier, $"{name} — mnożnik flex")
        };
    }
}
