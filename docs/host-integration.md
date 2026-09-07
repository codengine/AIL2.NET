# Engine/host composition

AIL2.NET does not depend on a particular game engine or application. The integrating application chooses the music configuration and supplies the available output sinks. Reference `AIL2.NET.csproj` for sequencing and output contracts, or `AIL2.NET.Rendering.csproj` to add NukedOPL3Sharp synthesis and PCM output. Namespaces start with `AIL2`, not the assembly name.

An engine can choose either Sound Blaster chip mode:

```csharp
using AIL2;

var selectedMusic = new MusicConfiguration(
    MusicDevice.SoundBlaster,
    OplChipMode.Opl3); // or Opl2 / DualOpl2

using var session = new AILSession(
    selectedMusic,
    digitalProfile: DigitalProfile.SoundBlasterPro,
    oplOutput: hostOplSink,
    sampleOutput: hostSampleSink);
```

An OPL3 choice resolves to the source's Sound Blaster Pro 2 musical behavior; OPL2 resolves to SBFM; dual OPL2 resolves to the Pro 1 driver. This selection controls available operator topology and source stereo interpretation. The bank independently selects instrument definitions. An OPL3 session can use a two-operator `.ad` bank.

| AIL contract | Host responsibility |
|---|---|
| `IOplOutput.WriteRegister` | Route ordered writes to the selected chip instance and preserve their timestamps. Dual OPL2 needs two independent chip spaces. |
| `SetOplLevels` / `SetSampleLevels` | Apply the AIL mixer settings once. Add any application-specific filters afterward. |
| `IMidiOutput` | Copy complete MIDI/SysEx messages and schedule delivery/delays; select an external MT-32 transport or synthesizer. |
| `ISampleOutput` | Accept read-only blocks owned by the session, along with their PCM layout, rate, continuation state, and controls. Advertise only supported encodings. |
| Compressed completion | Decode externally, advance the session to logical completion, then call `CompleteBlock` on the owning thread. |
| `AdvanceTo` | Supply monotonically increasing logical time from the host's audio/timing service. |
| Player commands | Queue to the session's owning worker; retrieve detached snapshots for the UI. |

A host may have multiple music interpreters using the same synthesizer. Select the interpreter independently from its output hardware. A Miles adapter should expose its own audio path, resolve device/chip choices into `MusicConfiguration`, and request sampled and MIDI capabilities independently.

Report unsupported capabilities to the caller: one OPL chip cannot provide dual OPL2 routing, a PCM-only consumer cannot accept encoded ADPCM, and an MT-32 message sink does not synthesize audio. DOS transport code is not needed for any of these cases.

## Complete console export example

Create a .NET 10 console project referencing `src/AIL2.NET.Rendering/AIL2.NET.Rendering.csproj`. The following `Program.cs` accepts three arguments: XMIDI path, instrument-bank path and output WAV path. It uses Sound Blaster + OPL3 and installs only the sequence's required instruments. Ctrl+C finalizes a partial file for looping music.

```csharp
using AIL2;
using AIL2.Rendering;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Export music.xmi instruments.ad output.wav");
    return;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var music = new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl3);
using var renderer = new AILRenderer(music, digitalProfile: null);
renderer.Session.Timbres = TimbreBank.Load(File.ReadAllBytes(args[1]));
using var sequence = renderer.Session.CreateSequence(XmidiFile.Load(File.ReadAllBytes(args[0])));
sequence.PrepareTimbres();
sequence.Start();
using var output = File.Create(args[2]);
var frames = WaveExporter.WriteToEnd(output, renderer,
    () => sequence.Status == SequenceStatus.Playing, stop.Token);
Console.WriteLine($"Wrote {frames} stereo frames.");
```

`WriteToEnd` tests the predicate before each frame. This example ends when sequence events finish; a host wanting an instrument release tail can continue rendering according to its own policy. It supports RIFF and RF64 and finalizes on cancellation. The fixed-duration `Write` overload emits classic RIFF and throws on cancellation; an interrupted fixed-duration export can be incomplete.

For live playback, call `renderer.Render(buffer)` with an even-length `Span<short>` and send its signed 16-bit interleaved left/right samples to the audio device at `renderer.SampleRate`. Rendering advances the session, so do not advance it separately. `MasterVolume` is a finite linear gain from zero through one; AIL mixer levels have already been applied.

## Playback state and ownership

Construct, control, query and dispose a session and its handles on one thread. The core starts no worker. `PlaybackWorker` is a demo adapter which queues commands from other threads and returns detached snapshots; an integrating host may supply its own queue.

`CreateSequence` selects a zero-based resource index and allocates one of the eight original handles. `Start` rewinds and restores source defaults. `Pause` and `Stop` release active notes while preserving a resumable position; `Resume` restores logged controls without replaying those notes. End-of-track produces `Done`; use `Start` to replay it. Tempo and relative volume accept nonnegative percentages, including values above 100. Output volume follows source saturation arithmetic. Ramp durations are nonnegative.

MIDI channels in the managed sequence API are zero-based, from 0 through 15. `LockChannel` returns a reserved channel or -1; `ReleaseChannel` restores source logged state. The MIDI-effect helper preserves its original one-based message-channel quirk, while `MidiEffect.Channel` reports the zero-based reservation.

`Timbres` changes the source for missing definitions; it does not invalidate resident instruments. `PrepareTimbres` follows the track's TIMB request order. `LoadTimbreBank` preloads in index order with normal eviction. Installations under a resident key retain that definition. A fresh session is needed to replace instruments under the same keys; the player does this when bank content changes.

Loaders copy their input. Sequences keep their tracks, samples keep their resources, and disposing a session disposes its handles. The session does not dispose sinks supplied by the caller. MIDI spans are valid only during the callback; copy them if they must be retained. Sample block memory may be kept but must remain read-only. A directly constructed `VocBlock` keeps the caller's memory, so the caller must not modify it.

At a shared logical deadline, sample completion precedes sequence service in slot order, followed by driver service and MIDI-effect service. Trigger callbacks occur synchronously during event processing and can change sequence controls. Recursive `AdvanceTo`, backward time and access from another thread are rejected. Sink and trigger exceptions propagate; do not assume a failed advance rolls back already emitted output.

## Samples and external MIDI

`CreateSample(VocFile)` preserves VOC control blocks and profile behavior. `CreateSample(SampleBuffer)` takes ownership of the raw payload and accepts a rational frame rate. Playback then resolves the actual rate for the selected profile. Starting a sample stops any other active or paused sample in that session. Pausing keeps the remaining block duration; after stopping, call `Play` to start again. Marker playback begins after the selected marker and ends at the next marker or terminator. `ReplaceBuffer` stops the current buffer before starting its replacement.

The session completes PCM and silence automatically. Encoded blocks are forwarded only when `ISampleOutput.Supports` accepts the resolved format. An external consumer advances session time to the logical completion timestamp and calls `CompleteBlock` once per compressed block. That call can synchronously begin the next block; a continuation must preserve decoder state. This library includes no compression decoder.

MT-32 output consists of complete channel and SysEx messages with timing requests; it does not include synthesis. Supply `IMidiOutput` when constructing the session to receive initialization messages. A renderer event handler added later cannot recover messages already sent. MT-32 timestamps may run ahead of session time as delays accumulate, so schedule transport from the provided timestamps and copy message spans that must outlive the callback.
