# AIL2.NET

AIL2.NET provides managed AIL/32 sequencing, FM interpretation, MT-32 messages, PC speaker timer output, and PCM/VOC
playback. Besides AIL 2.14, it follows the 1991 drivers shipped with Star Trek: 25th Anniversary. The repository also
includes an Avalonia player. Open `AIL2.NET.slnx`.

[![NuGet](https://img.shields.io/nuget/v/AIL2.NET.svg)](https://www.nuget.org/packages/AIL2.NET)

```sh
dotnet add package AIL2.NET
dotnet add package AIL2.NET.Rendering
```

The projects target .NET 10 and allow any compatible SDK. `AIL2.NET.Rendering` uses the **NukedOPL3Sharp 1.3.1 NuGet
package** for OPL synthesis.

Builds use the recommended .NET analyzers and Roslynator.Analyzers 5.0.0, with warnings treated as errors. The analyzer
package is used only during development. NuGet packages include XML documentation for the public APIs.

The project and assembly names use `AIL2.NET`; the root namespace is `AIL2`. The rendering, player and harness namespaces
are `AIL2.Rendering`, `AIL2.Player` and `AIL2.Parity`.

```sh
dotnet build AIL2.NET.slnx -c Release
dotnet run --project src/AIL2.NET.Player -c Release
```

The player needs an available OpenAL audio device. Original-code tests additionally require Node.js and GCC. NuGet
supplies Unicorn 2 on supported Linux and macOS targets; Windows x64 builds download it, and the parity and test
executables run there as described in [validation](docs/validation.md#windows). The REL105 WR drivers,
the A214 `.ADV` drivers and A214 `SOUNDFX.C` are included. No game assets are required: tests and default harness
scenarios generate their own resources. Tests of the 1991 drivers also play Star Trek: 25th Anniversary's own drivers
and songs when they are copied to the ignored `game_files/st25th/` (`drivers/*.ADV`, `music/*`); otherwise they are
skipped.

## NativeAOT

```sh
dotnet publish src/AIL2.NET.Player -c Release -r linux-x64 -p:PublishProfile=NativeAot -o artifacts/native-player
./artifacts/native-player/AIL2.NET.Player
```

The `NativeAot` profile enables native compilation and treats trimming and AOT warnings as errors. It has been tested on
Linux x64 with GCC, binutils, and the zlib development files. The player requires a graphical display. Other runtimes
need their corresponding native toolchains.

The player loads OpenAL through native exports and writes MIDI JSON without reflection. Both libraries enable AOT
compatibility analysis.

## Choosing instruments and chip mode

Choose the music **device/profile**, **OPL chip mode**, and **instrument bank** independently. Select the bank intended
for your music; a two-operator `.ad` bank can be used with **Sound Blaster + OPL3**.

Bank contents define the instruments. An `.opl` file can contain two-operator definitions; its extension alone does not
imply four-operator instruments or the same sound as another bank.

| Device             | Chip mode   | Original AIL driver profile |
|--------------------|-------------|-----------------------------|
| AdLib              | OPL2        | AdLib                       |
| Sound Blaster      | OPL2        | Sound Blaster               |
| Sound Blaster      | Dual OPL2   | Sound Blaster Pro 1         |
| Sound Blaster      | OPL3        | Sound Blaster Pro 2         |
| Pro Audio Spectrum | Dual OPL2   | Pro Audio Spectrum          |
| Pro Audio Spectrum | OPL3        | PAS OPL3                    |
| AdLib Gold         | OPL3        | AdLib Gold                  |
| Roland MT-32       | No OPL chip | MT-32 messages              |
| IBM PC speaker     | No OPL chip | PC speaker timer output     |

OPL3 modes accept two-operator `.ad` instruments and four-operator instruments. OPL2 modes reject four-operator
definitions. The digital profile is a separate choice for sampled effects. The speaker uses no instruments; it plays
only MIDI channel 2 (zero-based channel 1) and reports timer divisors and speaker on/off through `ISpeakerOutput`. The player and `AILRenderer` do
not synthesize it.

The player keeps the selected bank when it is compatible with the new chip mode. Loading different bank contents resets
playback and the instrument cache, while changing tracks with the same bank keeps the current session.

Play an XMIDI file, then select and play a VOC to hear music and an effect together. WAV export starts the loaded
resources from the beginning and follows the same PCM path. Finite resources finish automatically; use **Stop export**
for looping music. The completed portion is finalized as a playable WAV. Long files use RF64 when necessary. Live
controls remain responsive during export. MT-32 messages can be inspected and exported to timestamped JSON; MT-32
synthesis and device transport belong to the host.

## Library use

An engine selects the device family and chip mode through the same public configuration as the player:

```csharp
using AIL2;

var music = new MusicConfiguration(MusicDevice.SoundBlaster, OplChipMode.Opl3);
using var session = new AILSession(
    music,
    DigitalProfile.SoundBlasterPro,
    oplOutput: hostOplOutput,
    sampleOutput: hostSampleOutput);

session.LoadTimbreBank(TimbreBank.Load(File.ReadAllBytes("instruments.ad")));
using var sequence = session.CreateSequence(XmidiFile.Load(File.ReadAllBytes("music.xmi")));
sequence.Start();
session.AdvanceTo(TimeSpan.FromSeconds(1));
```

A third argument, `DriverRevision.Ail1991`, follows the 1991 drivers instead of AIL 2.14, for AdLib and Sound Blaster
OPL2, Sound Blaster dual OPL2, MT-32 and the PC speaker. The game's `PASFM.ADV` is byte-identical to its `SBFM.ADV`, so
Sound Blaster OPL2 covers it.

For standalone PCM rendering, construct `AILRenderer` with that configuration and call `Render(Span<short>)` with an
interleaved stereo buffer. The default is 48 kHz. For sampled effects, use `CreateSample(VocFile)` or create a
`SampleBuffer` with a `SampleFormat`. The session owns the buffer data. `ReplaceBuffer` stops the current sample before
starting the replacement, and the selected profile quantizes the requested source rate.

See [host integration](docs/host-integration.md) for a complete console export example, state transitions, channel
numbering and output-consumer contracts. Public types and members carry XML summaries, parameters, return values and
relevant exceptions in the source.

All session operations and callbacks run on the thread that created the session. The core creates no threads, reads no
wall clock, and does not dispose output sinks supplied by the caller. Resource loaders copy their input, while playback
objects keep the resources they need. Treat sample-block memory as read-only, and copy MIDI spans if they must outlive
the callback. Dispose playback objects and sessions on their creating thread. Repeated disposal is safe.

`AdvanceTo` processes due sample completions, XMIDI services, then MIDI-effect services in deterministic order. XMIDI
triggers run synchronously at their original event boundary and may stop, branch or otherwise control a sequence.
Recursive advancement and backwards time are rejected. Music is serviced at the driver's interval, the source's 8,333 µs,
or 2,083 µs (480 Hz) for the 1991 PC speaker driver. The sound-effect helper runs every 10 ms.

The sample consumer declares format support. Uncompressed PCM completes automatically on the session timeline.
Compressed bytes are forwarded unchanged only to a capable consumer; that consumer advances the session to its
completion timestamp and calls `SamplePlayback.CompleteBlock()`. **No compression decoder is included.**

`LoadTimbreBank` preserves original installation/index order. As in AIL, installing an already resident key does not
replace its data. Hosts can preload a bank before starting sequences or use `PrepareTimbres` for strict demand loading.
Original sequence, note and channel capacities are retained; they are musical behavior, not player duration limits.

## Verification

```sh
dotnet build AIL2.NET.slnx -c Release
dotnet test --project tests/AIL2.NET.Tests -c Release --no-build
dotnet run --project tools/AIL2.NET.Parity -c Release --no-build -- --samples
dotnet run --project tools/AIL2.NET.Parity -c Release --no-build -- --profile all
```

The parity executable runs the original AIL instructions in Unicorn without DOSBox. It compares output order, timing,
status, and sequence position. With no input path, it creates a small set of XMIDI, VOC, and bank files under
`artifacts/generated-resources/`. Expected behavior comes from the original code rather than stored output files.
Reports under `artifacts/parity/` record input and driver hashes along with each scenario's ending. Music comparisons
run finite sequences to completion; looping sequences run twice and then stop. A scenario fails if it exhausts its
service budget.

To compare your own resources, pass file or directory paths together with the appropriate bank paths. Directories are
scanned recursively; bank names need not match track names. All relative paths resolve from the working directory.

```sh
dotnet run --project tools/AIL2.NET.Parity -c Release -- --input /path/to/music.xmi --profile SoundBlasterPro2 --bank /path/to/instruments.ad
dotnet run --project tools/AIL2.NET.Parity -c Release -- --input /path/to/music --profile all --bank-ad /path/to/fm.bank --bank-opl /path/to/opl3.bank --bank-mt /path/to/mt32.bank
dotnet run --project tools/AIL2.NET.Parity -c Release -- --samples --input /path/to/effects
dotnet run --project tools/AIL2.NET.Parity -c Release -- --adv /path/to/ADLIB.ADV --profile AdLib --revision Ail1991 --input /path/to/TITLE.AD --bank /path/to/GTL.AD
```

Use `--drivers /path/to/REL105/WR` to select an external original-driver directory, `--adv FILE` to compare against a
real-mode AIL 2.x `.ADV` music driver instead, `--revision NAME` to choose the library's `DriverRevision`, `--sequence
INDEX` to select one sequence, `--marker NUMBER` for marked VOC resources, and `--help` for other options. External music
inputs require an explicit compatible bank, except on the PC speaker. Missing inputs, empty directories, sample selections with no played blocks, unsupported
resources and oracle failures return a failing exit code. Sample corpus runs require finite VOC playback; compressed
completion is tested through an explicit external-consumer schedule, without decoding.

Custom resources and generated reports are not committed. Keep optional inputs outside the checkout or in the ignored
`private-resources/` directory. The AIL files required by the tests and parity harness are under `reference/`; see
their [license grant](reference/LICENSE.md). Player builds do not include the original drivers or test resources.

The MT-32 parity path repairs a stack-width error in a temporary copy of the original driver. A separate regression test
covers the unmodified instruction.
See [validation.md](docs/validation.md), [oracle-boundaries.md](docs/oracle-boundaries.md), [coverage.md](docs/coverage.md),
and [host-integration.md](docs/host-integration.md). Rendering comparisons feed both AIL event streams through the same
NukedOPL3Sharp package; they test AIL2.NET rather than the synthesizer.

AIL2.NET is licensed under the [MIT License](LICENSE). Original AIL reference material and dependencies retain their own
terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
