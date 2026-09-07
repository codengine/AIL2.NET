# Source surface and implementation coverage

The AIL 2.14 and AIL/32 1.05 sources define the behavior covered below. The parity harness runs the original WR drivers against generated inputs. Hardware and operating-system behavior outside the listed boundaries is not implemented by the managed library.

| Original surface | AIL2.NET behavior | Evidence |
|---|---|---|
| `AIL32.ASM`, public lifecycle/sequence calls | `AILSession`, disposable playback objects, caller-provided time and output sinks | Music parity, thread ownership and disposal tests |
| `XMIDI32.ASM` | `XmidiFile`, `XmidiSequence`: notes/durations, channels, loops, branches, callbacks, ramps, beat/measure state | Generated single/multiple-sequence resources across profiles; finite/nested-loop/controller/callback tests |
| `YAMAHA32.INC` | `FmDriver`, `FmTables`: timbres, cache, allocation, stereo, pitch and operator commands | Native register comparisons, exhausted voices/cache, all four four-operator algorithms and mixed stealing |
| `MT3232.INC` | `Mt32Driver`: patch mapping, cache, SysEx uploads/checksums, controller queues, display and delays | Native MIDI and delay comparisons, including the stack repair and unmodified-instruction regression |
| `DMASND32.ASM` | `VocFile`, `SampleBuffer`, `SamplePlayback`: source sequencing, formats/rates, repeat/marker/silence behavior, controls and replacement | Generated VOC/profile comparisons to completion; extended stereo, boundary mixer levels, raw replacement, external compressed completion |
| `SOUNDFX.C` | `MidiEffect` with a 10 ms host service | The original C helper calls native AIL while the parity test compares ordering, status, and release behavior |
| `.AD`, `.OPL`, `.MT` library records | `TimbreBank`, original packed indexes and length prefixes | Cache, request-order, and malformed-index tests |
| `DLLLOAD.C`, extender and physical-device primitives | `AIL2.NET.Parity` only | LX loader and Unicorn hooks that reject unexpected interactions; see [oracle boundaries](oracle-boundaries.md) |
| OPL synthesis | `NukedOPL3Sharp` NuGet package | Both comparison paths use the same synthesizer, so these tests cover AIL scheduling and routing rather than chip emulation |
| Host audio | `AILRenderer`, `WaveExporter`, `PlaybackWorker`, `OpenAlDevice` | Mixed output/block-size equality; WAV finalization/cancellation; worker and UI lifecycle tests |
| TVFX/ALE, compression codecs, speaker/Tandy, DIGPAK, authoring/conversion utilities | Not included | Source review and product tests |

## Design boundaries

- The core has no guest memory, hardware detection, DMA, interrupts, timer hardware or OS driver loading. OPL register commands are the synthesizer interface.
- Replacing a public sample buffer stops the old buffer before starting the new one. DOS pointer bookkeeping and physical fragmentation stay outside the managed API.
- Empty VOC voice blocks, malformed lengths, invalid branches, zero-time cycles, and unavailable output capabilities fail with managed exceptions instead of reproducing undefined reads or infinite loops.
- The included profiles define FM SysEx/display operations and the unused periodic TVFX synthesizer service as no-ops.
- `MusicConfiguration` selects the music device and chip independently from the digital sample profile.
- Changing banks in the player resets playback and the instrument cache. At the lower level, `InstallTimbre` keeps an instrument that is already resident under the same key, matching AIL.
- Four-operator coverage uses explicit 25-byte synthetic timbres and all four algorithms. Other synthetic FM banks use 14-byte definitions regardless of filename.

Export uses a dedicated session and thread so long or looping exports do not block live commands. Bank contents remain independent of device and chip selection. Parity never modifies reference files on disk, and compressed formats are passed to the output sink without decoding.
