using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flipper.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public static SettingsStore ForAppData()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flipper");
        Directory.CreateDirectory(dir);
        return new SettingsStore(Path.Combine(dir, "settings.json"));
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
