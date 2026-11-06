using static VoxForge.Audio.BuiltInSounds;

namespace VoxForge.Audio;

/// <summary>
/// Ready-made soundboards of well-known kinds of sound effects (air horn, sad trombone, drum sting, retro game blips…).
/// Every sound is synthesised here from scratch: no recordings from other apps, games or shows are shipped.
/// </summary>
public static class SoundPacks
{
    public sealed record Pack(string Name, IReadOnlyList<SoundClip> Clips);

    public static IReadOnlyList<Pack> All() => new[]
    {
        new Pack("Classiques", BuiltInSounds.All().ToList()),
        new Pack("Mèmes", new[]
        {
            SoundClip.Synth("Klaxon de stade", AirHorn),
            SoundClip.Synth("Ba-dum-tss", Rimshot),
            SoundClip.Synth("Trombone triste", SadTrombone),
            SoundClip.Synth("Mauvaise réponse", Buzzer),
            SoundClip.Synth("Bonne réponse", Correct),
            SoundClip.Synth("Bip de censure", Censor),
            SoundClip.Synth("Boum dramatique", DramaticBoom),
            SoundClip.Synth("Suspense", Suspense),
            SoundClip.Synth("Criquets", Crickets),
            SoundClip.Synth("Disque rayé", RecordScratch),
            SoundClip.Synth("Roulement de tambour", DrumRoll),
            SoundClip.Synth("Applaudissements", Applause),
        }),
        new Pack("Rétro jeux", new[]
        {
            SoundClip.Synth("Pièce", Coin),
            SoundClip.Synth("Saut", Jump),
            SoundClip.Synth("Bonus", PowerUp),
            SoundClip.Synth("Dégâts", Hurt),
            SoundClip.Synth("Explosion 8-bit", ChipExplosion),
            SoundClip.Synth("Victoire", Victory),
            SoundClip.Synth("Game over", GameOver),
            SoundClip.Synth("Alarme", Siren),
            SoundClip.Synth("Menu", MenuTick),
        }),
    };

    // ---------- Oscillators ----------

    private static double Saw(double phase) => 2 * (phase - Math.Floor(phase + 0.5));
    private static double Square(double phase, double duty = 0.5) => phase - Math.Floor(phase) < duty ? 1 : -1;

    /// <summary>Note from a list of (start, length, frequency); 0 between notes.</summary>
    private static (double Frequency, double Local, double Length) NoteAt(double t, (double Start, double Length, double Frequency)[] notes)
    {
        foreach (var n in notes)
            if (t >= n.Start && t < n.Start + n.Length) return (n.Frequency, t - n.Start, n.Length);
        return (0, 0, 0);
    }

    private static double Gate(double local, double length, double attack = 0.005, double release = 0.03) =>
        local < attack ? local / attack : local > length - release ? Math.Max(0, (length - local) / release) : 1;

    // ---------- Memes ----------

    private static float[] AirHorn(int rate)
    {
        var bursts = new[] { (0.0, 0.16), (0.22, 0.16), (0.44, 0.75) };
        double[] voices = { 349, 352, 440, 446, 523 };
        double lp = 0;
        return Render(rate, 1.25, t =>
        {
            double env = 0;
            foreach (var (start, length) in bursts)
                if (t >= start && t < start + length) env = Gate(t - start, length, 0.01, 0.05);
            double x = 0;
            foreach (var f in voices) x += Saw(f * t * (1 + 0.004 * Math.Sin(2 * Math.PI * 5 * t)));
            x = Math.Tanh(x * 0.6);
            lp += (x - lp) * 0.35;
            return lp * env * 0.9;
        });
    }

    private static float[] Rimshot(int rate)
    {
        var noise = new Random(11);
        double hp = 0, last = 0;
        return Render(rate, 1.6, t =>
        {
            double tom1 = t < 0.25 ? Tone(190 - 30 * t, t) * Math.Exp(-t * 18) : 0;
            double t2 = t - 0.18;
            double tom2 = t2 is > 0 and < 0.3 ? Tone(140 - 30 * t2, t2) * Math.Exp(-t2 * 16) : 0;
            double t3 = t - 0.4;
            double cymbal = 0;
            if (t3 > 0)
            {
                double n = noise.NextDouble() * 2 - 1;
                hp = 0.92 * (hp + n - last); // high-pass: shimmering noise
                last = n;
                cymbal = hp * Math.Exp(-t3 * 3.2) * 0.55 * Math.Min(1, t3 / 0.002);
            }
            return tom1 * 0.9 + tom2 * 0.9 + cymbal;
        });
    }

    private static float[] SadTrombone(int rate)
    {
        var notes = new[] { (0.0, 0.42, 293.66), (0.45, 0.42, 277.18), (0.9, 0.42, 261.63), (1.35, 1.25, 246.94) };
        double phase = 0, lp = 0, step = 1.0 / rate;
        return Render(rate, 2.65, t =>
        {
            var (f, local, length) = NoteAt(t, notes);
            if (f == 0) return lp *= 0.95;
            double vibrato = length > 1 ? 1 + 0.012 * Math.Sin(2 * Math.PI * 5.5 * local) * Math.Min(1, local / 0.3) : 1;
            phase += f * vibrato * step;
            double x = Saw(phase) * 0.7 + Square(phase) * 0.2;
            lp += (x - lp) * (0.08 + 0.06 * Math.Min(1, local / 0.08)); // "wah" opening
            return lp * Gate(local, length, 0.03, 0.12) * 1.4;
        });
    }

    private static float[] Buzzer(int rate) => Render(rate, 0.85, t =>
        (Square(110 * t) * 0.5 + Square(117 * t) * 0.5) * Gate(t, 0.85, 0.005, 0.06) * 0.55);

    private static float[] Correct(int rate)
    {
        var notes = new[] { (0.0, 0.16, 880.0), (0.14, 0.7, 1318.5) };
        return Render(rate, 0.85, t =>
        {
            double sum = 0;
            foreach (var (start, length, f) in notes)
            {
                double local = t - start;
                if (local is > 0 and < 0.85) sum += (Tone(f, local) + 0.3 * Tone(f * 2, local)) * Math.Exp(-local * (length < 0.3 ? 9 : 3.5));
            }
            return sum * Math.Min(1, t / 0.003) * 0.7;
        });
    }

    private static float[] Censor(int rate) => Render(rate, 0.75, t => Tone(1000, t) * Gate(t, 0.75, 0.004, 0.01) * 0.8);

    private static float[] DramaticBoom(int rate)
    {
        var noise = new Random(3);
        double phase = 0, step = 1.0 / rate;
        return Render(rate, 1.8, t =>
        {
            double f = 42 + 110 * Math.Exp(-t * 9);
            phase += f * step;
            double body = Math.Tanh(Math.Sin(2 * Math.PI * phase) * 3.2) * Math.Exp(-t * 2.2);
            double click = (noise.NextDouble() * 2 - 1) * Math.Exp(-t * 60) * 0.6;
            return (body + click) * Math.Min(1, t / 0.002) * 0.95;
        });
    }

    private static float[] Suspense(int rate)
    {
        var notes = new[] { (0.0, 0.28, 146.83), (0.36, 0.28, 155.56), (0.75, 1.8, 130.81) };
        double lp = 0;
        return Render(rate, 2.6, t =>
        {
            var (f, local, length) = NoteAt(t, notes);
            double x = f == 0 ? 0 : (Saw(f * local) + Saw(f * 1.005 * local) + 0.6 * Saw(f / 2 * local)) / 2.6;
            lp += (x - lp) * 0.12;
            double tremolo = length > 1 ? 1 - 0.25 * (1 - Math.Cos(2 * Math.PI * 6 * local)) / 2 : 1;
            return lp * Gate(local, length, 0.01, 0.25) * tremolo * 1.6;
        });
    }

    private static float[] Crickets(int rate) => Render(rate, 3.0, t =>
    {
        // Chirps of four quick pulses, twice a second, with a second cricket offset in time and pitch.
        static double Cricket(double t, double offset, double pitch)
        {
            double local = (t + offset) % 0.55;
            if (local > 0.12) return 0;
            double pulse = Math.Max(0, Math.Sin(2 * Math.PI * 33 * local));
            return Math.Sin(2 * Math.PI * pitch * t) * pulse * pulse;
        }
        return (Cricket(t, 0, 4400) * 0.35 + Cricket(t, 0.27, 4950) * 0.22) * Math.Min(1, t / 0.2) * Math.Min(1, (3 - t) / 0.3);
    });

    private static float[] RecordScratch(int rate)
    {
        var noise = new Random(5);
        double phase = 0, step = 1.0 / rate, lp = 0;
        return Render(rate, 0.6, t =>
        {
            double speed = Math.Sin(Math.PI * t / 0.6 * 2.5); // forward, back, forward
            phase += (180 + 520 * Math.Abs(speed)) * step;
            double grain = (noise.NextDouble() * 2 - 1) * Math.Abs(speed);
            lp += (grain - lp) * 0.25;
            return (Saw(phase) * 0.35 + lp * 0.9) * Math.Abs(speed) * Gate(t, 0.6, 0.005, 0.05);
        });
    }

    private static float[] DrumRoll(int rate)
    {
        var noise = new Random(9);
        double hp = 0, last = 0;
        return Render(rate, 2.8, t =>
        {
            double n = noise.NextDouble() * 2 - 1;
            if (t < 2.0)
            {
                double local = t % 0.045;
                double crescendo = 0.25 + 0.75 * (t / 2.0);
                return (n * 0.6 + Tone(200, local) * 0.4) * Math.Exp(-local * 70) * crescendo;
            }
            double crash = t - 2.0;
            hp = 0.9 * (hp + n - last);
            last = n;
            return (hp * 0.8 + Tone(110, crash) * Math.Exp(-crash * 12)) * Math.Exp(-crash * 3.5) * Math.Min(1, crash / 0.002);
        });
    }

    private static float[] Applause(int rate)
    {
        var random = new Random(21);
        double seconds = 3.2;
        var samples = new float[(int)(rate * seconds)];
        // Hundreds of short band-limited claps scattered in time.
        for (int clap = 0; clap < 520; clap++)
        {
            double start = random.NextDouble() * (seconds - 0.1);
            double density = Math.Min(1, start / 0.4) * Math.Min(1, (seconds - start) / 1.0);
            if (random.NextDouble() > density) continue;
            int at = (int)(start * rate);
            int length = (int)(rate * (0.012 + random.NextDouble() * 0.01));
            double gain = 0.12 + random.NextDouble() * 0.12, lp = 0, tone = 0.3 + random.NextDouble() * 0.4;
            for (int i = 0; i < length && at + i < samples.Length; i++)
            {
                lp += ((random.NextDouble() * 2 - 1) - lp) * tone;
                samples[at + i] += (float)(lp * gain * Math.Exp(-i / (length * 0.3)));
            }
        }
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)Math.Tanh(samples[i] * 1.6) * 0.7f;
        return samples;
    }

    // ---------- Retro game ----------

    private static float[] Coin(int rate)
    {
        var notes = new[] { (0.0, 0.07, 1046.5), (0.07, 0.38, 1567.98) };
        return Render(rate, 0.45, t =>
        {
            var (f, local, length) = NoteAt(t, notes);
            return f == 0 ? 0 : Square(f * local, 0.25) * 0.35 * (length < 0.1 ? 1 : Math.Exp(-local * 6));
        });
    }

    private static float[] Jump(int rate)
    {
        double phase = 0, step = 1.0 / rate;
        return Render(rate, 0.24, t =>
        {
            phase += (280 + 900 * t / 0.24) * step;
            return Square(phase, 0.3) * 0.32 * Gate(t, 0.24, 0.002, 0.04);
        });
    }

    private static float[] PowerUp(int rate)
    {
        double[] steps = { 523.25, 659.25, 783.99, 1046.5, 659.25, 783.99, 1046.5, 1318.5, 783.99, 1046.5, 1318.5, 1568 };
        return Render(rate, 0.72, t =>
        {
            int i = Math.Min((int)(t / 0.06), steps.Length - 1);
            return Square(steps[i] * t, 0.5) * 0.3 * Gate(t, 0.72, 0.002, 0.08);
        });
    }

    private static float[] Hurt(int rate)
    {
        var noise = new Random(13);
        double phase = 0, step = 1.0 / rate;
        return Render(rate, 0.3, t =>
        {
            phase += (420 * Math.Exp(-t * 6)) * step;
            double n = noise.NextDouble() * 2 - 1;
            return (Square(phase) * 0.3 + n * 0.25 * Math.Exp(-t * 14)) * Gate(t, 0.3, 0.002, 0.05);
        });
    }

    private static float[] ChipExplosion(int rate)
    {
        var noise = new Random(17);
        double held = 0;
        int counter = 0;
        return Render(rate, 1.1, t =>
        {
            int hold = 2 + (int)(t * 60); // coarser noise as it fades, like old sound chips
            if (counter++ % hold == 0) held = noise.NextDouble() * 2 - 1;
            return Math.Round(held * 4) / 4 * Math.Exp(-t * 3.4) * 0.6;
        });
    }

    private static float[] Victory(int rate)
    {
        var notes = new[] { (0.0, 0.12, 523.25), (0.13, 0.12, 659.25), (0.26, 0.12, 783.99), (0.39, 0.22, 1046.5), (0.64, 0.12, 783.99), (0.77, 0.75, 1046.5) };
        return Render(rate, 1.55, t =>
        {
            var (f, local, length) = NoteAt(t, notes);
            if (f == 0) return 0;
            double voice = Square(f * local, 0.5) * 0.25 + Square(f / 2 * local, 0.25) * 0.12;
            return voice * Gate(local, length, 0.003, 0.04) * (length > 0.5 ? Math.Exp(-local * 1.5) : 1);
        });
    }

    private static float[] GameOver(int rate)
    {
        var notes = new[] { (0.0, 0.3, 392.0), (0.32, 0.3, 349.23), (0.64, 0.3, 311.13), (0.96, 0.9, 261.63) };
        return Render(rate, 1.9, t =>
        {
            var (f, local, length) = NoteAt(t, notes);
            if (f == 0) return 0;
            double bend = length > 0.5 ? 1 - 0.03 * local : 1;
            return Square(f * bend * local, 0.5) * 0.28 * Gate(local, length, 0.004, 0.08);
        });
    }

    private static float[] Siren(int rate)
    {
        double phase = 0, step = 1.0 / rate;
        return Render(rate, 2.4, t =>
        {
            double f = 750 + 450 * Math.Sin(2 * Math.PI * t / 1.2 - Math.PI / 2);
            phase += f * step;
            return (Math.Sin(2 * Math.PI * phase) * 0.6 + Square(phase) * 0.12) * Gate(t, 2.4, 0.02, 0.1);
        });
    }

    private static float[] MenuTick(int rate) => Render(rate, 0.06, t => Square(1250 * t, 0.5) * 0.3 * Gate(t, 0.06, 0.001, 0.02));
}
