using System.Text.Json;
using VoxForge.Audio;

namespace VoxForge;

/// <summary>Saved state of one effect.</summary>
public sealed class EffectState
{
    public bool Enabled { get; set; }
    public Dictionary<string, double> Values { get; set; } = new();
}

/// <summary>User choices, stored as JSON in %AppData%\VoxForge.</summary>
public sealed class AppSettings
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxForge");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public string? MicId { get; set; }
    public string? OutputId { get; set; }
    public string? MonitorId { get; set; }
    public bool MonitorEnabled { get; set; }
    public string Preset { get; set; } = "Grande salle";
    public bool EffectsEnabled { get; set; } = true;
    /// <summary>Null on first run: the selected preset decides.</summary>
    public Dictionary<string, EffectState>? Effects { get; set; }
    public double InputGain { get; set; } = 1.0;
    public double SoundboardVolume { get; set; } = 0.8;
    public string? Board { get; set; }
    /// <summary>Last page shown: voices, effects, sounds or settings.</summary>
    public string? Page { get; set; }

    /// <summary>Closing the window keeps VoxForge running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>Check GitHub for new versions and install them when the app quits.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Target id ("action:…", "voice:…", "pad:…") to serialised <see cref="Hotkey"/>. Null on first run: defaults apply.</summary>
    public Dictionary<string, string>? Hotkeys { get; set; }

    public void CaptureEffects(EffectChain chain)
    {
        EffectsEnabled = chain.Enabled;
        Effects = chain.All.ToDictionary(e => e.Id, e => new EffectState
        {
            Enabled = e.Enabled,
            Values = e.Params.ToDictionary(p => p.Id, p => p.Value),
        });
    }

    /// <summary>Returns false when nothing was saved yet.</summary>
    public bool RestoreEffects(EffectChain chain)
    {
        chain.Enabled = EffectsEnabled;
        if (Effects is null) return false;
        foreach (var effect in chain.All)
        {
            effect.ResetAll();
            if (!Effects.TryGetValue(effect.Id, out var state)) continue;
            foreach (var (id, value) in state.Values)
                if (effect.Param(id) is { } p) p.Value = value;
            effect.Enabled = state.Enabled;
        }
        return true;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings: start from defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Saving is best effort.
        }
    }
}
