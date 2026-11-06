using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoxForge.Audio;

/// <summary>A soundboard pad: either a user file or a sound synthesised by the app.</summary>
public sealed class SoundClip : Observable
{
    private const int MaxSeconds = 30;
    private string hotkeyText = "";
    private readonly object gate = new();
    private readonly Func<int, float[]>? generator;
    private float[]? cache;
    private int cacheRate;

    private SoundClip(string name, string? filePath, Func<int, float[]>? generator)
    {
        Name = name;
        FilePath = filePath;
        this.generator = generator;
    }

    public string Name { get; }
    public string? FilePath { get; }
    public bool IsBuiltIn => FilePath is null;

    /// <summary>Shown on the pad; empty when no hotkey is bound.</summary>
    public string HotkeyText
    {
        get => hotkeyText;
        set
        {
            hotkeyText = value;
            Notify();
        }
    }

    public static SoundClip FromFile(string path) => new(Path.GetFileNameWithoutExtension(path), path, null);

    public static SoundClip Synth(string name, Func<int, float[]> generator) => new(name, null, generator);

    /// <summary>Mono samples at the given rate, decoded once and cached.</summary>
    public float[] GetSamples(int sampleRate)
    {
        lock (gate)
        {
            if (cache is null || cacheRate != sampleRate)
            {
                cache = generator is not null ? generator(sampleRate) : Decode(FilePath!, sampleRate);
                cacheRate = sampleRate;
            }
            return cache;
        }
    }

    private static float[] Decode(string path, int sampleRate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = new MonoDownmix(reader);
        if (source.WaveFormat.SampleRate != sampleRate)
            source = new WdlResamplingSampleProvider(source, sampleRate);

        var samples = new List<float>(sampleRate * 4);
        var block = new float[sampleRate / 10];
        int read;
        while ((read = source.Read(block, 0, block.Length)) > 0 && samples.Count < sampleRate * MaxSeconds)
            samples.AddRange(block.AsSpan(0, read).ToArray());
        return samples.ToArray();
    }

    /// <summary>Averages any channel count down to mono.</summary>
    private sealed class MonoDownmix : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int channels;
        private float[] scratch = Array.Empty<float>();

        public MonoDownmix(ISampleProvider source)
        {
            this.source = source;
            channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (channels == 1) return source.Read(buffer, offset, count);
            int needed = count * channels;
            if (scratch.Length < needed) scratch = new float[needed];
            int frames = source.Read(scratch, 0, needed) / channels;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += scratch[i * channels + c];
                buffer[offset + i] = sum / channels;
            }
            return frames;
        }
    }
}

/// <summary>Mixes the pads currently playing into the outgoing voice.</summary>
public sealed class Soundboard
{
    private const int MaxVoices = 8;
    private readonly object gate = new();
    private readonly List<Playback> playing = new();

    public float Volume { get; set; } = 0.8f;
    public int SampleRate { get; set; } = 48000;

    public void Play(SoundClip clip)
    {
        var samples = clip.GetSamples(SampleRate);
        if (samples.Length == 0) return;
        lock (gate)
        {
            if (playing.Count >= MaxVoices) playing.RemoveAt(0);
            playing.Add(new Playback(samples));
        }
    }

    public void StopAll()
    {
        lock (gate) playing.Clear();
    }

    public void MixInto(float[] destination, int count)
    {
        float volume = Volume;
        lock (gate)
        {
            for (int p = playing.Count - 1; p >= 0; p--)
            {
                var playback = playing[p];
                int available = Math.Min(count, playback.Samples.Length - playback.Position);
                for (int i = 0; i < available; i++)
                    destination[i] += playback.Samples[playback.Position + i] * volume;
                playback.Position += available;
                if (playback.Position >= playback.Samples.Length) playing.RemoveAt(p);
            }
        }
    }

    private sealed class Playback
    {
        public Playback(float[] samples) => Samples = samples;
        public float[] Samples { get; }
        public int Position { get; set; }
    }
}

/// <summary>Original sounds generated in code, so the app ships without third-party audio.</summary>
public static class BuiltInSounds
{
    public static IEnumerable<SoundClip> All()
    {
        yield return SoundClip.Synth("Bip", Beep);
        yield return SoundClip.Synth("Fanfare", Fanfare);
        yield return SoundClip.Synth("Laser", Laser);
        yield return SoundClip.Synth("Boum", Boom);
        yield return SoundClip.Synth("Ding", Bell);
    }

    private static float[] Beep(int rate) => Render(rate, 0.25, t => Tone(880, t) * Envelope(t, 0.005, 0.25));

    private static float[] Fanfare(int rate)
    {
        double[] notes = { 523.25, 659.25, 783.99, 1046.5 };
        return Render(rate, 1.1, t =>
        {
            int n = Math.Min((int)(t / 0.15), notes.Length - 1);
            double start = n * 0.15;
            double length = n == notes.Length - 1 ? 0.65 : 0.15;
            double local = t - start;
            double f = notes[n];
            double voice = Tone(f, t) * 0.6 + Tone(f * 2, t) * 0.25 + Tone(f * 3, t) * 0.1;
            return voice * Envelope(local, 0.01, length);
        });
    }

    private static float[] Laser(int rate)
    {
        double phase = 0;
        double step = 1.0 / rate;
        return Render(rate, 0.4, t =>
        {
            double f = 1800 * Math.Pow(200.0 / 1800.0, t / 0.4);
            phase += 2 * Math.PI * f * step;
            return (Math.Sin(phase) * 0.7 + Math.Sign(Math.Sin(phase)) * 0.15) * Envelope(t, 0.003, 0.4);
        });
    }

    private static float[] Boom(int rate)
    {
        var random = new Random(7);
        double phase = 0;
        double step = 1.0 / rate;
        return Render(rate, 0.8, t =>
        {
            double f = 40 + 80 * Math.Exp(-t * 18);
            phase += 2 * Math.PI * f * step;
            double body = Math.Sin(phase) * Math.Exp(-t * 5);
            double noise = (random.NextDouble() * 2 - 1) * Math.Exp(-t * 30) * 0.5;
            return body + noise;
        });
    }

    private static float[] Bell(int rate) => Render(rate, 1.6, t =>
        (Tone(1320, t) * 0.5 + Tone(1320 * 2.76, t) * 0.25 * Math.Exp(-t * 3) + Tone(1320 * 5.4, t) * 0.12 * Math.Exp(-t * 6))
        * Math.Exp(-t * 2.4) * Math.Min(1, t / 0.002));

    private static double Tone(double frequency, double t) => Math.Sin(2 * Math.PI * frequency * t);

    /// <summary>Linear attack then exponential-ish fade to zero at <paramref name="length"/>.</summary>
    private static double Envelope(double t, double attack, double length)
    {
        if (t < 0 || t > length) return 0;
        if (t < attack) return t / attack;
        double x = (t - attack) / (length - attack);
        return (1 - x) * (1 - x);
    }

    private static float[] Render(int rate, double seconds, Func<double, double> signal)
    {
        var samples = new float[(int)(rate * seconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(signal((double)i / rate) * 0.7);
        return samples;
    }
}
