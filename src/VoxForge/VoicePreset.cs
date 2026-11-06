using VoxForge.Audio;

namespace VoxForge;

/// <summary>
/// A ready-made voice: which effects are on and how their knobs are set. Name, description and
/// category are UI text (French). Utility effects (noise gate, EQ) are never touched by presets,
/// except when a preset lists them explicitly.
/// </summary>
public sealed class VoicePreset : Observable
{
    private readonly (string Effect, (string Param, double Value)[] Values)[] effects;
    private string hotkeyText = "";

    /// <summary>Shown on the voice card; empty when no hotkey is bound.</summary>
    public string HotkeyText
    {
        get => hotkeyText;
        set
        {
            hotkeyText = value;
            Notify();
        }
    }

    private VoicePreset(string name, string category, string description, params (string, (string, double)[])[] effects)
    {
        Name = name;
        Category = category;
        Description = description;
        this.effects = effects;
    }

    public string Name { get; }
    public string Category { get; }
    public string Description { get; }

    public void ApplyTo(EffectChain chain)
    {
        foreach (var effect in chain.All)
            if (!effect.IsUtility) effect.ResetAll();

        foreach (var (id, values) in effects)
        {
            var effect = chain.Find(id);
            if (effect is null) continue;
            effect.ResetAll();
            foreach (var (param, value) in values)
                if (effect.Param(param) is { } p) p.Value = value;
            effect.Enabled = true;
        }
        chain.Enabled = true;
    }

    private static (string, (string, double)[]) E(string id, params (string, double)[] values) => (id, values);

    public static IReadOnlyList<VoicePreset> All { get; } = new[]
    {
        new VoicePreset("Naturelle", "Base", "Ta voix sans effet"),

        new VoicePreset("Monstre", "Personnages", "Grave et grondant",
            E("pitch", ("semitones", -7)), E("distortion", ("drive", 3), ("mix", 0.3)), E("reverb", ("room", 0.5), ("mix", 0.2))),
        new VoicePreset("Démon", "Personnages", "Venu des profondeurs",
            E("pitch", ("semitones", -10)), E("distortion", ("drive", 6), ("mix", 0.45)), E("chorus", ("rate", 0.4), ("mix", 0.4)),
            E("reverb", ("room", 0.85), ("damping", 0.3), ("mix", 0.35))),
        new VoicePreset("Géant", "Personnages", "Énorme et lent",
            E("pitch", ("semitones", -5)), E("reverb", ("room", 0.9), ("mix", 0.4))),
        new VoicePreset("Écureuil", "Personnages", "Petite voix aiguë et rapide",
            E("pitch", ("semitones", 8))),
        new VoicePreset("Enfant", "Personnages", "Un peu plus aigu",
            E("pitch", ("semitones", 4))),
        new VoicePreset("Fantôme", "Personnages", "Souffle d'outre-tombe",
            E("pitch", ("semitones", 3), ("mix", 0.5)), E("whisper", ("mix", 0.5)), E("chorus", ("mix", 0.4)),
            E("echo", ("delay", 450), ("feedback", 0.35), ("mix", 0.3)), E("reverb", ("room", 0.95), ("damping", 0.2), ("mix", 0.55))),
        new VoicePreset("Chuchoteur", "Personnages", "Que du souffle",
            E("whisper", ("mix", 1)), E("reverb", ("room", 0.3), ("mix", 0.15))),

        new VoicePreset("Robot", "Machines", "Voix métallique classique",
            E("robot", ("frequency", 90)), E("bitcrusher", ("bits", 10), ("downsample", 2))),
        new VoicePreset("Androïde", "Machines", "Synthétique mais humain",
            E("pitch", ("semitones", -2)), E("robot", ("frequency", 50), ("mix", 0.6)), E("chorus", ("mix", 0.35)),
            E("reverb", ("room", 0.3), ("mix", 0.15))),
        new VoicePreset("Alien", "Machines", "Venu d'une autre galaxie",
            E("pitch", ("semitones", 5)), E("vibrato", ("rate", 7), ("depth", 2)), E("robot", ("frequency", 300), ("mix", 0.3)),
            E("flanger", ("mix", 0.4))),
        new VoicePreset("Rétro 8-bit", "Machines", "Console des années 80",
            E("bitcrusher", ("bits", 4), ("downsample", 8))),

        new VoicePreset("Radio", "Appareils", "Talkie-walkie qui grésille",
            E("radio", ("low", 500), ("high", 2800), ("drive", 4), ("noise", 0.08))),
        new VoicePreset("Téléphone", "Appareils", "Vieux combiné",
            E("radio", ("low", 400), ("high", 3400), ("drive", 2), ("noise", 0.01)), E("bitcrusher", ("bits", 10), ("downsample", 3))),
        new VoicePreset("Mégaphone", "Appareils", "Annonce dans la rue",
            E("radio", ("low", 700), ("high", 4000), ("drive", 12), ("noise", 0)), E("reverb", ("room", 0.6), ("mix", 0.2))),
        new VoicePreset("Sous l'eau", "Appareils", "Bulles et pression",
            E("muffle", ("cutoff", 450), ("resonance", 3)), E("vibrato", ("rate", 2), ("depth", 3))),

        new VoicePreset("Petite pièce", "Lieux", "Une chambre, léger et proche",
            E("reverb", ("room", 0.35), ("damping", 0.6), ("mix", 0.25))),
        new VoicePreset("Grande salle", "Lieux", "Comme sur une scène",
            E("reverb", ("room", 0.75), ("damping", 0.4), ("mix", 0.4))),
        new VoicePreset("Cathédrale", "Lieux", "Une queue longue et immense",
            E("reverb", ("room", 0.95), ("damping", 0.2), ("mix", 0.55))),
        new VoicePreset("Stade", "Lieux", "La sono résonne au loin",
            E("echo", ("delay", 420), ("feedback", 0.3), ("mix", 0.35)), E("reverb", ("room", 0.9), ("mix", 0.45))),
        new VoicePreset("Canyon", "Lieux", "Des répétitions qui s'éloignent",
            E("echo", ("delay", 380), ("feedback", 0.45), ("mix", 0.5)), E("reverb", ("room", 0.5), ("mix", 0.2))),
        new VoicePreset("Grotte", "Lieux", "Humide, sombre et profond",
            E("echo", ("delay", 220), ("feedback", 0.35), ("mix", 0.35)), E("reverb", ("room", 0.85), ("damping", 0.3), ("mix", 0.45))),
        new VoicePreset("Slapback", "Lieux", "Écho court façon rockabilly",
            E("echo", ("delay", 110), ("feedback", 0.15), ("mix", 0.45))),
    };
}
