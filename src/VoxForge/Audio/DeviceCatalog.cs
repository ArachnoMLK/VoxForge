using NAudio.CoreAudioApi;

namespace VoxForge.Audio;

/// <summary>An active Windows audio endpoint.</summary>
public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;

    /// <summary>True when the device looks like the VB-Audio virtual cable playback end.</summary>
    public bool IsVirtualCableInput => Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Lists microphones and playback devices through WASAPI.</summary>
public static class DeviceCatalog
{
    public static List<AudioDevice> Inputs() => List(DataFlow.Capture);

    public static List<AudioDevice> Outputs() => List(DataFlow.Render);

    public static string? DefaultInputId() => DefaultId(DataFlow.Capture);

    public static string? DefaultOutputId() => DefaultId(DataFlow.Render);

    private static List<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            list.Add(new AudioDevice(device.ID, device.FriendlyName));
            device.Dispose();
        }
        return list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? DefaultId(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }
}
