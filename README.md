# VoxForge

Real-time voice changer and soundboard for Windows (WPF, .NET 8, NAudio/WASAPI). The UI is in French.

```
microphone (WASAPI shared, 10 ms blocks)
  -> input gain / mute
  -> effect chain (gate, pitch, whisper, robot, distortion, 8-bit, radio, muffle, EQ,
                   chorus, flanger, vibrato, tremolo, echo, reverb)
  -> + soundboard mix
  -> soft limiter
  -> virtual cable (e.g. "CABLE Input" from VB-Cable)  -> Discord / games / OBS
  -> optional headphone monitor
```

## Build and run

```
cd src/VoxForge
dotnet build -c Release
bin/Release/net8.0-windows/VoxForge.exe
```

## Sending the voice to other apps

VoxForge plays into a virtual audio cable. Install VB-Cable (free, vb-audio.com/Cable), restart,
pick "CABLE Input" as the VoxForge output, then pick "CABLE Output" as the microphone in Discord/OBS/games.
Without a cable, enable "M'écouter dans le casque" to hear the result.

## Layout

- `Audio/AudioEngine.cs` capture, processing and output on the WASAPI capture thread.
- `Audio/Effects.cs` effect framework (`VoiceEffect`, bindable `EffectParam`) and all effects.
- `Audio/Soundboard.cs` pad playback mixer, file decoding, built-in synthesised sounds.
- `VoicePreset.cs` ready-made voices (combinations of effects).
- `SoundBoards.cs` user soundboards: one folder per board under `%AppData%\VoxForge\Boards`.
- `AppSettings.cs` settings in `%AppData%\VoxForge\settings.json`.
- `Hotkeys.cs` system-wide hotkeys (`RegisterHotKey`) for app actions, voices and pads, plus the capture dialog.
- `TrayIcon.cs` notification-area icon; closing the window hides VoxForge there (the voice keeps running).
- `App.xaml.cs` single instance: launching again re-shows the running window.
