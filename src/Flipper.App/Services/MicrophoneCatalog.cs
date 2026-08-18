using Windows.Devices.Enumeration;

namespace Flipper.App.Services;

public sealed class MicrophoneOption
{
    public MicrophoneOption(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public override string ToString() => Name;
}

public static class MicrophoneCatalog
{
    public const string SystemDefaultId = "";

    public static async Task<IReadOnlyList<MicrophoneOption>> ListAsync()
    {
        var found = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        var list = new List<MicrophoneOption>(found.Count + 1)
        {
            new(SystemDefaultId, "System default")
        };

        foreach (var device in found.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(device.Name))
            {
                continue;
            }

            list.Add(new MicrophoneOption(device.Id, device.Name));
        }

        return list;
    }
}
