using System.Text.Json;

namespace Flipper.Core.Library;

public sealed class ScoreFacts
{
    public string? Title { get; set; }
    public string? Composer { get; set; }
}

public static class ScoreCatalog
{
    public const string FileName = ".flipper-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyDictionary<string, ScoreFacts> Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, ScoreFacts>>(json, JsonOptions)
                ?? new Dictionary<string, ScoreFacts>();
            var map = new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in raw)
            {
                map[pair.Key.Replace('/', '\\')] = pair.Value;
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, ScoreFacts>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string Key(string relativeFolder, string fileName)
    {
        if (string.IsNullOrEmpty(relativeFolder) || relativeFolder == ".")
        {
            return fileName;
        }

        return relativeFolder.Replace('/', '\\').TrimEnd('\\') + "\\" + fileName;
    }
}
