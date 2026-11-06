using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoxForge.Audio;

/// <summary>Base for UI-bindable objects.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One adjustable knob of an effect. Label is UI text (French); Id is the stable key used in settings and presets.</summary>
public sealed class EffectParam : Observable
{
    private double value;

    public EffectParam(string id, string label, double min, double max, double defaultValue, string unit, double step = 0)
    {
        Id = id;
        Label = label;
        Min = min;
        Max = max;
        Default = defaultValue;
        Unit = unit;
        Step = step;
        value = defaultValue;
    }

    public string Id { get; }
    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public string Unit { get; }
    public double Step { get; }
    public bool Snaps => Step > 0;

    public double Value
    {
        get => value;
        set
        {
            double clamped = Math.Clamp(value, Min, Max);
            if (Snaps) clamped = Math.Round(clamped / Step) * Step;
            if (clamped == this.value) return;
            this.value = clamped;
            Notify();
            Notify(nameof(Display));
        }
    }

    public float F => (float)value;

    public string Display => Unit switch
    {
        "%" => $"{value:P0}",
        "st" => value > 0 ? $"+{value:0} dt" : $"{value:0} dt",
        "ms" => value < 10 ? $"{value:0.0} ms" : $"{value:0} ms",
        "Hz" => value >= 1000 ? $"{value / 1000:0.0} kHz" : $"{value:0.#} Hz",
        "dB" => $"{value:+0;-0;0} dB",
        "x" => $"×{value:0.#}",
        "bits" => $"{value:0} bits",
        _ => $"{value:0.##}",
    };

    public void Reset() => Value = Default;
}

/// <summary>A voice effect: bindable on/off switch and knobs, plus the DSP that runs on the audio thread.</summary>
public abstract class VoiceEffect : Observable
{
    private readonly List<EffectParam> parameters = new();
    private bool enabled;

    protected VoiceEffect(string id, string name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<EffectParam> Params => parameters;

    /// <summary>Utility effects (noise gate, EQ) are left alone when a voice preset is applied.</summary>
    public virtual bool IsUtility => false;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            Notify();
        }
    }

    public EffectParam? Param(string id) => parameters.FirstOrDefault(p => p.Id == id);

    public void ResetAll()
    {
        Enabled = false;
        foreach (var p in parameters) p.Reset();
    }

    protected EffectParam Add(string id, string label, double min, double max, double defaultValue, string unit, double step = 0)
    {
        var p = new EffectParam(id, label, min, max, defaultValue, unit, step);
        parameters.Add(p);
        return p;
    }

    protected int SampleRate { get; private set; } = 48000;

    public void Prepare(int sampleRate)
    {
        SampleRate = sampleRate;
        OnPrepare();
    }

    protected abstract void OnPrepare();

    public abstract void Process(float[] buffer, int count);

    protected static float Mix(float dry, float wet, float mix) => dry + (wet - dry) * mix;
}

/// <summary>Runs every enabled effect in a fixed, musically sensible order.</summary>
public sealed class EffectChain
{
    public EffectChain()
    {
        All = new VoiceEffect[]
        {
            new NoiseGateEffect(), new PitchShiftEffect(), new WhisperEffect(), new RobotEffect(),
            new DistortionEffect(), new BitcrusherEffect(), new RadioEffect(), new MuffleEffect(),
            new EqualizerEffect(), new ChorusEffect(), new FlangerEffect(), new VibratoEffect(),
            new TremoloEffect(), new EchoEffect(), new ReverbEffect(),
        };
    }

    public IReadOnlyList<VoiceEffect> All { get; }
    public bool Enabled { get; set; } = true;

    public VoiceEffect? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    public void Prepare(int sampleRate)
    {
        foreach (var effect in All) effect.Prepare(sampleRate);
    }

    public void Process(float[] buffer, int count)
    {
        if (!Enabled) return;
        foreach (var effect in All)
            if (effect.Enabled) effect.Process(buffer, count);
    }
}

// ---------------------------------------------------------------- building blocks

/// <summary>Circular buffer read at fractional delays (linear interpolation).</summary>
internal sealed class DelayLine
{
    private readonly float[] buffer;
    private int position;

    public DelayLine(int size) => buffer = new float[Math.Max(size, 4)];

    public int Length => buffer.Length;

    public void Write(float sample)
    {
        buffer[position] = sample;
        if (++position == buffer.Length) position = 0;
    }

    /// <summary>Delay in samples; 1 is the most recently written sample.</summary>
    public float Read(float delay)
    {
        delay = Math.Clamp(delay, 1f, buffer.Length - 2);
        float read = position - delay;
        if (read < 0) read += buffer.Length;
        int i0 = (int)read;
        float frac = read - i0;
        int i1 = i0 + 1 == buffer.Length ? 0 : i0 + 1;
        return buffer[i0] + (buffer[i1] - buffer[i0]) * frac;
    }
}

/// <summary>RBJ cookbook biquad filter.</summary>
internal sealed class Biquad
{
    private float b0 = 1, b1, b2, a1, a2, z1, z2;

    public float Process(float x)
    {
        float y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return y;
    }

    public void LowPass(int rate, double freq, double q)
    {
        var (cs, alpha) = Prep(rate, freq, q);
        Set((1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + alpha, -2 * cs, 1 - alpha);
    }

    public void HighPass(int rate, double freq, double q)
    {
        var (cs, alpha) = Prep(rate, freq, q);
        Set((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + alpha, -2 * cs, 1 - alpha);
    }

    public void LowShelf(int rate, double freq, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        var (cs, alpha) = Prep(rate, freq, Math.Sqrt(0.5));
        double s = 2 * Math.Sqrt(a) * alpha;
        Set(a * ((a + 1) - (a - 1) * cs + s), 2 * a * ((a - 1) - (a + 1) * cs), a * ((a + 1) - (a - 1) * cs - s),
            (a + 1) + (a - 1) * cs + s, -2 * ((a - 1) + (a + 1) * cs), (a + 1) + (a - 1) * cs - s);
    }

    public void HighShelf(int rate, double freq, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        var (cs, alpha) = Prep(rate, freq, Math.Sqrt(0.5));
        double s = 2 * Math.Sqrt(a) * alpha;
        Set(a * ((a + 1) + (a - 1) * cs + s), -2 * a * ((a - 1) + (a + 1) * cs), a * ((a + 1) + (a - 1) * cs - s),
            (a + 1) - (a - 1) * cs + s, 2 * ((a - 1) - (a + 1) * cs), (a + 1) - (a - 1) * cs - s);
    }

    private static (double cs, double alpha) Prep(int rate, double freq, double q)
    {
        freq = Math.Clamp(freq, 10, rate * 0.45);
        double w = 2 * Math.PI * freq / rate;
        return (Math.Cos(w), Math.Sin(w) / (2 * Math.Max(q, 0.1)));
    }

    private void Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
    {
        b0 = (float)(nb0 / na0);
        b1 = (float)(nb1 / na0);
        b2 = (float)(nb2 / na0);
        a1 = (float)(na1 / na0);
        a2 = (float)(na2 / na0);
    }
}

/// <summary>Sine LFO, phase in [0, 1).</summary>
internal struct Lfo
{
    private double phase;

    public Lfo(double startPhase) => phase = startPhase;

    public float Next(double rateHz, int sampleRate)
    {
        float value = (float)Math.Sin(2 * Math.PI * phase);
        phase += rateHz / sampleRate;
        if (phase >= 1) phase -= 1;
        return value;
    }
}

// ---------------------------------------------------------------- effects

public sealed class NoiseGateEffect : VoiceEffect
{
    private readonly EffectParam threshold, release;
    private float envelope, gain;

    public NoiseGateEffect() : base("gate", "Porte de bruit", "Coupe le souffle et les bruits de fond quand tu ne parles pas")
    {
        threshold = Add("threshold", "Seuil", -80, -20, -50, "dB");
        release = Add("release", "Relâchement", 20, 500, 150, "ms");
    }

    public override bool IsUtility => true;

    protected override void OnPrepare() => envelope = gain = 0;

    public override void Process(float[] buffer, int count)
    {
        float open = (float)Math.Pow(10, threshold.Value / 20);
        float envFall = (float)Math.Exp(-1.0 / (0.01 * SampleRate));
        float attack = 1f - (float)Math.Exp(-1.0 / (0.002 * SampleRate));
        float fall = 1f - (float)Math.Exp(-1.0 / (release.Value / 1000 * SampleRate));
        for (int i = 0; i < count; i++)
        {
            float x = Math.Abs(buffer[i]);
            envelope = x > envelope ? x : envelope * envFall;
            float target = envelope > open ? 1f : 0f;
            gain += (target - gain) * (target > gain ? attack : fall);
            buffer[i] *= gain;
        }
    }
}

/// <summary>Pitch shifter with two crossfaded read heads sweeping a short delay line.</summary>
public sealed class PitchShiftEffect : VoiceEffect
{
    private readonly EffectParam semitones, window, mix;
    private DelayLine line = new(4);
    private double phase;

    public PitchShiftEffect() : base("pitch", "Hauteur", "Voix plus grave ou plus aiguë")
    {
        semitones = Add("semitones", "Demi-tons", -12, 12, -5, "st", 1);
        window = Add("window", "Fenêtre", 20, 100, 45, "ms");
        mix = Add("mix", "Dosage", 0, 1, 1, "%");
    }

    protected override void OnPrepare()
    {
        line = new DelayLine(SampleRate / 4);
        phase = 0;
    }

    public override void Process(float[] buffer, int count)
    {
        double ratio = Math.Pow(2, semitones.Value / 12);
        double width = window.Value / 1000 * SampleRate;
        double step = (1 - ratio) / width;
        float wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            line.Write(x);
            phase += step;
            if (phase >= 1) phase -= 1;
            else if (phase < 0) phase += 1;
            double phase2 = phase + 0.5;
            if (phase2 >= 1) phase2 -= 1;
            double g1 = Math.Sin(Math.PI * phase);
            double g2 = Math.Sin(Math.PI * phase2);
            float y = (float)(line.Read((float)(phase * width + 1)) * g1 * g1 + line.Read((float)(phase2 * width + 1)) * g2 * g2);
            buffer[i] = Mix(x, y, wet);
        }
    }
}

public sealed class WhisperEffect : VoiceEffect
{
    private readonly EffectParam mix;
    private readonly Biquad noiseFilter = new();
    private readonly Random random = new(11);
    private float envelope;

    public WhisperEffect() : base("whisper", "Chuchotement", "Remplace le timbre par un souffle qui suit ta voix")
    {
        mix = Add("mix", "Dosage", 0, 1, 0.85, "%");
    }

    protected override void OnPrepare() => noiseFilter.HighPass(SampleRate, 1200, 0.7);

    public override void Process(float[] buffer, int count)
    {
        float wet = mix.F;
        float fall = (float)Math.Exp(-1.0 / (0.015 * SampleRate));
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            float magnitude = Math.Abs(x);
            envelope = magnitude > envelope ? magnitude : envelope * fall;
            float noise = noiseFilter.Process((float)(random.NextDouble() * 2 - 1));
            buffer[i] = Mix(x, noise * envelope * 1.6f, wet);
        }
    }
}

/// <summary>Ring modulation: the classic metallic robot voice.</summary>
public sealed class RobotEffect : VoiceEffect
{
    private readonly EffectParam frequency, mix;
    private Lfo carrier;

    public RobotEffect() : base("robot", "Robot", "Voix métallique de machine")
    {
        frequency = Add("frequency", "Fréquence", 20, 400, 95, "Hz");
        mix = Add("mix", "Dosage", 0, 1, 1, "%");
    }

    protected override void OnPrepare() => carrier = new Lfo(0);

    public override void Process(float[] buffer, int count)
    {
        double f = frequency.Value;
        float wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            buffer[i] = Mix(x, x * carrier.Next(f, SampleRate) * 1.4f, wet);
        }
    }
}

public sealed class DistortionEffect : VoiceEffect
{
    private readonly EffectParam drive, tone, level, mix;
    private readonly Biquad toneFilter = new();

    public DistortionEffect() : base("distortion", "Distorsion", "Saturation agressive, façon ampli")
    {
        drive = Add("drive", "Gain", 1, 40, 8, "x");
        tone = Add("tone", "Tonalité", 800, 12000, 4500, "Hz");
        level = Add("level", "Volume", 0, 1, 0.5, "%");
        mix = Add("mix", "Dosage", 0, 1, 1, "%");
    }

    protected override void OnPrepare() { }

    public override void Process(float[] buffer, int count)
    {
        toneFilter.LowPass(SampleRate, tone.Value, 0.7);
        float d = drive.F, out_ = level.F, wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            float y = toneFilter.Process(MathF.Tanh(x * d)) * out_;
            buffer[i] = Mix(x, y, wet);
        }
    }
}

/// <summary>Lowers bit depth and sample rate: retro console sound.</summary>
public sealed class BitcrusherEffect : VoiceEffect
{
    private readonly EffectParam bits, downsample, mix;
    private float held;
    private int counter;

    public BitcrusherEffect() : base("bitcrusher", "8-bit", "Son rétro de vieille console")
    {
        bits = Add("bits", "Résolution", 2, 16, 6, "bits", 1);
        downsample = Add("downsample", "Réduction", 1, 30, 6, "x", 1);
        mix = Add("mix", "Dosage", 0, 1, 1, "%");
    }

    protected override void OnPrepare() => counter = 0;

    public override void Process(float[] buffer, int count)
    {
        float levels = MathF.Pow(2, bits.F - 1);
        int hold = Math.Max(1, (int)downsample.Value);
        float wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            if (++counter >= hold)
            {
                counter = 0;
                held = MathF.Round(x * levels) / levels;
            }
            buffer[i] = Mix(x, held, wet);
        }
    }
}

/// <summary>Band-limited, slightly driven voice with a little static: radio, phone, megaphone.</summary>
public sealed class RadioEffect : VoiceEffect
{
    private readonly EffectParam low, high, drive, noise;
    private readonly Biquad highPass = new(), lowPass = new(), highPass2 = new(), lowPass2 = new();
    private readonly Random random = new(3);

    public RadioEffect() : base("radio", "Radio", "Talkie-walkie, téléphone ou mégaphone")
    {
        low = Add("low", "Coupe-bas", 100, 1500, 400, "Hz");
        high = Add("high", "Coupe-haut", 1500, 8000, 3000, "Hz");
        drive = Add("drive", "Saturation", 1, 20, 3, "x");
        noise = Add("noise", "Grésillement", 0, 0.3, 0.04, "%");
    }

    protected override void OnPrepare() { }

    public override void Process(float[] buffer, int count)
    {
        highPass.HighPass(SampleRate, low.Value, 0.7);
        highPass2.HighPass(SampleRate, low.Value, 0.7);
        lowPass.LowPass(SampleRate, high.Value, 0.7);
        lowPass2.LowPass(SampleRate, high.Value, 0.7);
        float d = drive.F, n = noise.F;
        float makeup = 1f / MathF.Tanh(d * 0.3f) * 0.3f;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i] + (float)(random.NextDouble() * 2 - 1) * n * 0.05f;
            x = lowPass2.Process(lowPass.Process(highPass2.Process(highPass.Process(x))));
            buffer[i] = MathF.Tanh(x * d) * makeup * 1.6f;
        }
    }
}

/// <summary>Resonant low-pass: behind a wall or under water.</summary>
public sealed class MuffleEffect : VoiceEffect
{
    private readonly EffectParam cutoff, resonance;
    private readonly Biquad filter = new();

    public MuffleEffect() : base("muffle", "Étouffé", "Comme derrière un mur ou sous l'eau")
    {
        cutoff = Add("cutoff", "Coupure", 150, 5000, 700, "Hz");
        resonance = Add("resonance", "Résonance", 0.5, 8, 1.2, "");
    }

    protected override void OnPrepare() { }

    public override void Process(float[] buffer, int count)
    {
        filter.LowPass(SampleRate, cutoff.Value, resonance.Value);
        for (int i = 0; i < count; i++) buffer[i] = filter.Process(buffer[i]);
    }
}

public sealed class EqualizerEffect : VoiceEffect
{
    private readonly EffectParam bass, treble, gain;
    private readonly Biquad lowShelf = new(), highShelf = new();

    public EqualizerEffect() : base("eq", "Égaliseur", "Plus de graves ou d'aigus, et volume de sortie")
    {
        bass = Add("bass", "Graves", -12, 12, 4, "dB");
        treble = Add("treble", "Aigus", -12, 12, 2, "dB");
        gain = Add("gain", "Volume", -12, 12, 0, "dB");
    }

    public override bool IsUtility => true;

    protected override void OnPrepare() { }

    public override void Process(float[] buffer, int count)
    {
        lowShelf.LowShelf(SampleRate, 200, bass.Value);
        highShelf.HighShelf(SampleRate, 4000, treble.Value);
        float g = (float)Math.Pow(10, gain.Value / 20);
        for (int i = 0; i < count; i++) buffer[i] = highShelf.Process(lowShelf.Process(buffer[i])) * g;
    }
}

/// <summary>Three slightly detuned copies: a choir of you.</summary>
public sealed class ChorusEffect : VoiceEffect
{
    private readonly EffectParam rate, depth, mix;
    private DelayLine line = new(4);
    private Lfo lfo1, lfo2, lfo3;

    public ChorusEffect() : base("chorus", "Chorus", "Plusieurs voix qui chantent ensemble")
    {
        rate = Add("rate", "Vitesse", 0.1, 5, 1.2, "Hz");
        depth = Add("depth", "Profondeur", 1, 10, 4, "ms");
        mix = Add("mix", "Dosage", 0, 1, 0.5, "%");
    }

    protected override void OnPrepare()
    {
        line = new DelayLine(SampleRate / 10);
        lfo1 = new Lfo(0);
        lfo2 = new Lfo(1 / 3.0);
        lfo3 = new Lfo(2 / 3.0);
    }

    public override void Process(float[] buffer, int count)
    {
        float baseDelay = 0.02f * SampleRate;
        float swing = depth.F / 1000 * SampleRate;
        double r = rate.Value;
        float wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            line.Write(x);
            float y = (line.Read(baseDelay + swing * lfo1.Next(r, SampleRate))
                     + line.Read(baseDelay + swing * lfo2.Next(r * 1.1, SampleRate))
                     + line.Read(baseDelay + swing * lfo3.Next(r * 0.9, SampleRate))) / 3f;
            buffer[i] = x * (1 - wet * 0.5f) + y * wet;
        }
    }
}

public sealed class FlangerEffect : VoiceEffect
{
    private readonly EffectParam rate, depth, feedback, mix;
    private DelayLine line = new(4);
    private Lfo lfo;

    public FlangerEffect() : base("flanger", "Flanger", "Balayage spatial, effet avion à réaction")
    {
        rate = Add("rate", "Vitesse", 0.05, 2, 0.3, "Hz");
        depth = Add("depth", "Profondeur", 0.5, 5, 2.5, "ms");
        feedback = Add("feedback", "Réinjection", 0, 0.9, 0.6, "%");
        mix = Add("mix", "Dosage", 0, 1, 0.5, "%");
    }

    protected override void OnPrepare()
    {
        line = new DelayLine(SampleRate / 50);
        lfo = new Lfo(0);
    }

    public override void Process(float[] buffer, int count)
    {
        float baseDelay = 0.001f * SampleRate;
        float swing = depth.F / 1000 * SampleRate / 2;
        double r = rate.Value;
        float fb = feedback.F, wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[i];
            float y = line.Read(baseDelay + swing * (1 + lfo.Next(r, SampleRate)));
            line.Write(x + y * fb);
            buffer[i] = x * (1 - wet * 0.5f) + y * wet;
        }
    }
}

public sealed class VibratoEffect : VoiceEffect
{
    private readonly EffectParam rate, depth;
    private DelayLine line = new(4);
    private Lfo lfo;

    public VibratoEffect() : base("vibrato", "Vibrato", "La voix tremble en hauteur")
    {
        rate = Add("rate", "Vitesse", 1, 12, 5.5, "Hz");
        depth = Add("depth", "Profondeur", 0.2, 5, 1.5, "ms");
    }

    protected override void OnPrepare()
    {
        line = new DelayLine(SampleRate / 50);
        lfo = new Lfo(0);
    }

    public override void Process(float[] buffer, int count)
    {
        float swing = depth.F / 1000 * SampleRate / 2;
        double r = rate.Value;
        for (int i = 0; i < count; i++)
        {
            line.Write(buffer[i]);
            buffer[i] = line.Read(1 + swing * (1 + lfo.Next(r, SampleRate)));
        }
    }
}

public sealed class TremoloEffect : VoiceEffect
{
    private readonly EffectParam rate, depth;
    private Lfo lfo;

    public TremoloEffect() : base("tremolo", "Trémolo", "Le volume pulse en rythme")
    {
        rate = Add("rate", "Vitesse", 1, 20, 6, "Hz");
        depth = Add("depth", "Profondeur", 0, 1, 0.7, "%");
    }

    protected override void OnPrepare() => lfo = new Lfo(0);

    public override void Process(float[] buffer, int count)
    {
        double r = rate.Value;
        float d = depth.F;
        for (int i = 0; i < count; i++)
            buffer[i] *= 1 - d * 0.5f * (1 + lfo.Next(r, SampleRate));
    }
}

/// <summary>Feedback delay line (repeating echo).</summary>
public sealed class EchoEffect : VoiceEffect
{
    private readonly EffectParam delay, feedback, mix;
    private float[] line = new float[1];
    private int position;

    public EchoEffect() : base("echo", "Écho", "Des répétitions de ta voix")
    {
        delay = Add("delay", "Délai", 40, 1000, 300, "ms");
        feedback = Add("feedback", "Répétitions", 0, 0.85, 0.4, "%");
        mix = Add("mix", "Volume de l'écho", 0, 1, 0.4, "%");
    }

    protected override void OnPrepare()
    {
        line = new float[SampleRate * 2 + 1];
        position = 0;
    }

    public override void Process(float[] buffer, int count)
    {
        int length = line.Length;
        int d = Math.Clamp((int)(delay.Value * SampleRate / 1000), 1, length - 1);
        float fb = feedback.F, wet = mix.F;
        for (int i = 0; i < count; i++)
        {
            int read = position - d;
            if (read < 0) read += length;
            float delayed = line[read];
            float dry = buffer[i];
            line[position] = dry + delayed * fb;
            if (++position == length) position = 0;
            buffer[i] = dry + delayed * wet;
        }
    }
}

/// <summary>Mono Freeverb layout: 8 damped combs into 4 all-pass filters.</summary>
public sealed class ReverbEffect : VoiceEffect
{
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllPassTunings = { 556, 441, 341, 225 };

    private readonly EffectParam room, damping, mix;
    private Comb[] combs = Array.Empty<Comb>();
    private AllPass[] allPasses = Array.Empty<AllPass>();

    public ReverbEffect() : base("reverb", "Réverb", "L'acoustique d'une pièce, d'une salle ou d'une cathédrale")
    {
        room = Add("room", "Taille de la pièce", 0, 1, 0.7, "%");
        damping = Add("damping", "Amortissement", 0, 1, 0.5, "%");
        mix = Add("mix", "Volume de la réverb", 0, 1, 0.35, "%");
    }

    protected override void OnPrepare()
    {
        float scale = SampleRate / 44100f;
        combs = CombTunings.Select(t => new Comb((int)(t * scale))).ToArray();
        allPasses = AllPassTunings.Select(t => new AllPass((int)(t * scale))).ToArray();
    }

    public override void Process(float[] buffer, int count)
    {
        float fb = room.F * 0.28f + 0.7f;
        float damp = damping.F * 0.4f;
        float wet = mix.F;
        float dryLevel = 1f - 0.5f * wet;
        float wetLevel = 3f * wet;
        for (int i = 0; i < count; i++)
        {
            float dry = buffer[i];
            float input = dry * 0.015f;
            float y = 0f;
            foreach (var comb in combs) y += comb.Process(input, fb, damp);
            foreach (var allPass in allPasses) y = allPass.Process(y);
            buffer[i] = dry * dryLevel + y * wetLevel;
        }
    }

    private sealed class Comb
    {
        private readonly float[] buffer;
        private int index;
        private float store;

        public Comb(int size) => buffer = new float[Math.Max(size, 1)];

        public float Process(float input, float feedback, float damp)
        {
            float output = buffer[index];
            store = output * (1f - damp) + store * damp;
            buffer[index] = input + store * feedback;
            if (++index == buffer.Length) index = 0;
            return output;
        }
    }

    private sealed class AllPass
    {
        private readonly float[] buffer;
        private int index;

        public AllPass(int size) => buffer = new float[Math.Max(size, 1)];

        public float Process(float input)
        {
            float buffered = buffer[index];
            buffer[index] = input + buffered * 0.5f;
            if (++index == buffer.Length) index = 0;
            return buffered - input;
        }
    }
}
