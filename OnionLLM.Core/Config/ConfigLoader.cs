using System.Text.Json;
using System.Text.Json.Serialization;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Config;

/// <summary>
/// Ładuje konfigurację. Priorytet (od najwyższego): jawny plik (<c>--config</c>),
/// <c>./.onionllm.json</c>, plik użytkownika <c>%APPDATA%\OnionLLM\config.json</c>, wartości domyślne.
/// Pliki są nakładane warstwowo — późniejszy (ważniejszy) nadpisuje tylko podane pola.
/// </summary>
public static class ConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Plik użytkownika; w trybie przenośnym (<c>ONIONLLM_DATA_DIR</c>) leży w katalogu danych.</summary>
    public static string UserConfigPath => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ONIONLLM_DATA_DIR"))
        ? Path.Combine(FileUtil.RoamingAppData, "OnionLLM", "config.json")
        : Path.Combine(FileUtil.DataDirectory, "config.json");

    public static string LocalConfigPath => Path.Combine(Environment.CurrentDirectory, ".onionllm.json");

    public static OnionConfig Load(string? explicitPath = null)
    {
        var layers = new List<string>();
        if (File.Exists(UserConfigPath))
        {
            layers.Add(UserConfigPath);
        }

        if (File.Exists(LocalConfigPath) &&
            !string.Equals(FileUtil.NormalizePath(LocalConfigPath), FileUtil.NormalizePath(UserConfigPath), StringComparison.OrdinalIgnoreCase))
        {
            layers.Add(LocalConfigPath);
        }

        if (explicitPath is not null)
        {
            if (!File.Exists(explicitPath))
            {
                throw new ConfigException($"Nie znaleziono pliku konfiguracji: {explicitPath}");
            }

            layers.Add(explicitPath);
        }

        var merged = new System.Text.Json.Nodes.JsonObject();
        foreach (var layer in layers)
        {
            System.Text.Json.Nodes.JsonNode? node;
            try
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(
                    File.ReadAllText(layer),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException exception)
            {
                throw new ConfigException($"Niepoprawny JSON w {layer}: {exception.Message}");
            }

            if (node is System.Text.Json.Nodes.JsonObject layerObject)
            {
                Merge(merged, layerObject);
            }
        }

        OnionConfig config;
        try
        {
            config = merged.Deserialize<OnionConfig>(JsonOptions) ?? new OnionConfig();
        }
        catch (JsonException exception)
        {
            throw new ConfigException($"Niepoprawna wartość w konfiguracji: {exception.Message}");
        }

        Normalize(config);
        Validate(config);
        return config;
    }

    /// <summary>Wczytuje konfigurację, a przy błędzie zwraca domyślną i komunikat.</summary>
    public static OnionConfig LoadOrDefault(out string? error, string? explicitPath = null)
    {
        try
        {
            error = null;
            return Load(explicitPath);
        }
        catch (ConfigException exception)
        {
            error = exception.Message;
            return new OnionConfig();
        }
    }

    public static void SaveUser(OnionConfig config)
    {
        Validate(config);
        Directory.CreateDirectory(Path.GetDirectoryName(UserConfigPath)!);
        config.Schema ??= "./onionllm.schema.json";
        var temporary = UserConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, UserConfigPath, overwrite: true);
        WriteSchema(Path.GetDirectoryName(UserConfigPath)!);
    }

    public static void Validate(OnionConfig config)
    {
        config.ResolveTimeZone();
        config.ParseTokenLimit();
        if (config.SessionLengthHours is <= 0 or > 168)
        {
            throw new ConfigException("sessionLengthHours musi być w zakresie (0, 168].");
        }

        if (config.RefreshIntervalSeconds < 5)
        {
            throw new ConfigException("refreshIntervalSeconds musi wynosić co najmniej 5.");
        }
    }

    public static void WriteSchema(string directory)
    {
        using var stream = typeof(ConfigLoader).Assembly.GetManifestResourceStream("OnionLLM.Core.onionllm.schema.json");
        if (stream is null)
        {
            return;
        }

        using var file = File.Create(Path.Combine(directory, "onionllm.schema.json"));
        stream.CopyTo(file);
    }

    private static void Normalize(OnionConfig config)
    {
        config.Appearance ??= new AppearanceSettings();
        config.Window ??= new WindowSettings();
        config.Sources = new Dictionary<string, SourceSettings>(config.Sources ?? [], StringComparer.OrdinalIgnoreCase);
        config.PricingOverrides = new Dictionary<string, PricingOverride>(config.PricingOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        config.ModelAliases = new Dictionary<string, string>(
            (config.ModelAliases ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)),
            StringComparer.Ordinal);
    }

    private static void Merge(System.Text.Json.Nodes.JsonObject target, System.Text.Json.Nodes.JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is System.Text.Json.Nodes.JsonObject sourceObject &&
                target[key] is System.Text.Json.Nodes.JsonObject targetObject)
            {
                Merge(targetObject, sourceObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
