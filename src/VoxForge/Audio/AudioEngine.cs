using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoxForge.Audio;

/// <summary>
/// Microphone -> effects -> soundboard mix -> virtual cable (and optional headphone monitor).
/// All processing happens on the WASAPI capture thread, block by block, to keep latency low.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int CaptureBufferMs = 10;
    private const int OutputLatencyMs = 30;
    /// <summary>If an output queue grows past this (clock drift between devices), it is flushed.</summary>
    private const double MaxQueuedMs = 120;

    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private MMDeviceEnumerator? enumerator;
    private WasapiCapture? capture;
    private WasapiOut? mainOut;
    private WasapiOut? monitorOut;
    private BufferedWaveProvider? mainQueue;
    private BufferedWaveProvider? monitorQueue;
    private WaveFormat? inputFormat;
    private SampleKind inputKind;

    private float[] voice = Array.Empty<float>();
    private float[] soundMix = Array.Empty<float>();
    private byte[] outputBytes = Array.Empty<byte>();
    private float inputPeak;
    private float outputPeak;

    public EffectChain Effects { get; } = new();
    public Soundboard Soundboard { get; } = new();
    public bool IsRunning { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public float InputGain { get; set; } = 1f;
    public bool MicMuted { get; set; }

    /// <summary>Raised (on an audio thread) when a device fails or disappears while running.</summary>
    public event Action<string>? Faulted;

    private enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    public void Start(string micId, string? outputId, string? monitorId)
    {
        Stop();
        try
        {
            enumerator = new MMDeviceEnumerator();
            capture = new WasapiCapture(enumerator.GetDevice(micId), true, CaptureBufferMs);
            inputFormat = capture.WaveFormat;
            inputKind = Classify(inputFormat)
                ?? throw new NotSupportedException($"Unsupported microphone format: {inputFormat}");

            SampleRate = inputFormat.SampleRate;
            Effects.Prepare(SampleRate);
            Soundboard.SampleRate = SampleRate;

            var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
            if (!string.IsNullOrEmpty(outputId)) (mainOut, mainQueue) = OpenOutput(outputId, outputFormat);
            if (!string.IsNullOrEmpty(monitorId)) (monitorOut, monitorQueue) = OpenOutput(monitorId, outputFormat);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            mainOut?.Play();
            monitorOut?.Play();
            capture.StartRecording();
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        IsRunning = false;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { /* device may already be gone */ }
            capture.Dispose();
            capture = null;
        }
        DisposeOutput(ref mainOut);
        DisposeOutput(ref monitorOut);
        mainQueue = null;
        monitorQueue = null;
        Soundboard.StopAll();
        enumerator?.Dispose();
        enumerator = null;
    }

    /// <summary>Peak level since the last call, 0..1.</summary>
    public float TakeInputPeak() => Interlocked.Exchange(ref inputPeak, 0f);

    /// <summary>Peak level since the last call, 0..1.</summary>
    public float TakeOutputPeak() => Interlocked.Exchange(ref outputPeak, 0f);

    public void Dispose() => Stop();

    private (WasapiOut, BufferedWaveProvider) OpenOutput(string deviceId, WaveFormat format)
    {
        var queue = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        var output = new WasapiOut(enumerator!.GetDevice(deviceId), AudioClientShareMode.Shared, true, OutputLatencyMs);
        try
        {
            output.Init(queue);
        }
        catch
        {
            output.Dispose();
            throw;
        }
        return (output, queue);
    }

    private static void DisposeOutput(ref WasapiOut? output)
    {
        if (output is null) return;
        try { output.Stop(); } catch { /* ignore */ }
        output.Dispose();
        output = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = inputFormat;
        if (format is null || e.BytesRecorded == 0) return;

        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = e.BytesRecorded / format.BlockAlign;
        EnsureCapacity(frames);

        float gain = MicMuted ? 0f : InputGain;
        float inPeak = 0f;
        var source = e.Buffer;
        for (int i = 0; i < frames; i++)
        {
            int offset = i * format.BlockAlign;
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += ReadSample(source, offset + c * bytesPerSample);
            float sample = sum / channels * gain;
            voice[i] = sample;
            float magnitude = Math.Abs(sample);
            if (magnitude > inPeak) inPeak = magnitude;
        }

        Effects.Process(voice, frames);
        Array.Clear(soundMix, 0, frames);
        Soundboard.MixInto(soundMix, frames);

        var output = MemoryMarshal.Cast<byte, float>(outputBytes.AsSpan(0, frames * 8));
        float outPeak = 0f;
        for (int i = 0; i < frames; i++)
        {
            float sample = SoftLimit(voice[i] + soundMix[i]);
            output[2 * i] = sample;
            output[2 * i + 1] = sample;
            float magnitude = Math.Abs(sample);
            if (magnitude > outPeak) outPeak = magnitude;
        }

        Enqueue(mainQueue, frames * 8);
        Enqueue(monitorQueue, frames * 8);

        if (inPeak > inputPeak) inputPeak = inPeak;
        if (outPeak > outputPeak) outputPeak = outPeak;
    }

    /// <summary>Transparent below 0.8, then bends smoothly towards 1 instead of hard clipping.</summary>
    private static float SoftLimit(float x)
    {
        const float knee = 0.8f;
        float magnitude = Math.Abs(x);
        if (magnitude <= knee) return x;
        float bent = knee + (1f - knee) * MathF.Tanh((magnitude - knee) / (1f - knee));
        return MathF.CopySign(bent, x);
    }

    private void Enqueue(BufferedWaveProvider? queue, int byteCount)
    {
        if (queue is null) return;
        if (queue.BufferedDuration.TotalMilliseconds > MaxQueuedMs) queue.ClearBuffer();
        queue.AddSamples(outputBytes, 0, byteCount);
    }

    private void EnsureCapacity(int frames)
    {
        if (voice.Length >= frames) return;
        voice = new float[frames];
        soundMix = new float[frames];
        outputBytes = new byte[frames * 8];
    }

    private float ReadSample(byte[] buffer, int offset) => inputKind switch
    {
        SampleKind.Float32 => BitConverter.ToSingle(buffer, offset),
        SampleKind.Pcm16 => BitConverter.ToInt16(buffer, offset) / 32768f,
        SampleKind.Pcm24 => ((buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16))) / 8388608f,
        _ => BitConverter.ToInt32(buffer, offset) / 2147483648f,
    };

    private static SampleKind? Classify(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubtype);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
            || (format is WaveFormatExtensible ext2 && ext2.SubFormat == PcmSubtype);

        if (isFloat && format.BitsPerSample == 32) return SampleKind.Float32;
        if (!isPcm) return null;
        return format.BitsPerSample switch
        {
            16 => SampleKind.Pcm16,
            24 => SampleKind.Pcm24,
            32 => SampleKind.Pcm32,
            _ => null,
        };
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Faulted?.Invoke(e.Exception.Message);
    }
}
