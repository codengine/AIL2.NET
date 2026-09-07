# Original-code oracle boundaries

Parity uses the AIL/32 REL105 WR drivers under `reference/REL105/RELEASE.105/WR`. Pass `--drivers` to use another copy. `LxImage` follows `DLLLOAD.C` when loading objects, pages, zero pages, internal 32-bit fixups, and exported function tables. Unsupported fixups and services fail immediately. `UnicornMachine` also limits the number of instructions in each call so invalid inputs cannot run forever.

## Music

| Boundary | How the harness handles it |
|---|---|
| Guest addresses/selectors | Isolated guest allocations and flat selectors support original `LES`/`LDS`. No guest pointer crosses into the library API. |
| OPL address/data ports | The harness captures every ordered register write, preserving repetitions, bank identity and both chips on dual OPL2. `YAMAHA32.INC:update_reg` defines routing. |
| MPU readiness/reset | Deterministic ready/acknowledge replies for `MPU40132.INC` transport. Channel messages and SysEx bytes execute unchanged. |
| MT-32 retrace wait | Alternating retrace status yields one nominal 14 ms delay per original `sysex_wait` iteration. Delays are coalesced between complete messages; subsequent message timestamps honor queued waits. |
| PAS shutdown state | The original INT 2F state lookup receives a deterministic neutral state; routing restoration remains observed. |
| Timer service | The harness calls the original exported `serve_driver` every 83,330 .NET ticks. No emulated timer hardware is needed. |
| Sequence callbacks | A harness callback observes arguments; optional guest trampolines call the original stop/branch APIs. No sequencer algorithm is replaced. |
| Timbre requests | The test compares the packed patch/bank WORD and the no-request sentinel. The MT-32 source loads a DWORD from the WORD list; adjacent-list bytes above the public patch/bank fields are discarded. |

Bank preloading calls the original installation API for each packed entry in index order before registering and starting the selected sequences. The original driver parses resources and produces the expected musical output. Tests generate their inputs, while the CLI also accepts files and banks supplied by the caller.

### MT-32 stack repair

`MT3232.INC:378–381` pushes EAX and later pops AX while switching patch banks. The WR binary contains `66 58` at loaded offset `0x989`, which removes two bytes after a four-byte push and corrupts the saved registers. Depending on the guest addresses, the driver either faults or writes through the wrong pointer.

For MT-32 comparisons, the harness changes those two bytes to `58 90` (`POP EAX; NOP`) in memory. This keeps instruction offsets and musical behavior intact. A signature check prevents the patch from being applied to an unknown binary, and a separate regression test observes the two-byte imbalance in the unmodified instruction. The file on disk is never changed. MT-32 parity results therefore describe the repaired driver.

## Samples

`OriginalSampleDriver` executes the original `DMASND32.ASM` initialization, VOC parsing, marker search, repeat handling, continuation, rate selection, mixer arithmetic and public controls. The harness resolves each source procedure within its expected scope, where its signature must be unique.

| Boundary | How the harness handles it |
|---|---|
| Detection | The harness reports the device as present at the original `detect_device` entry because the scenario has already selected its capability. |
| Physical transfer | The harness intercepts `DMA_transfer` before page fragmentation and records the requested bytes and length. Guest addresses and DMA chunks normalize to one logical sample block. |
| Pause/resume transport | `halt_DMA` and `continue_DMA` return at their hardware boundaries. Original public status transitions still execute. |
| Completion interrupt | On whole-block completion, the harness calls the original `next_block` and `process_block` routines. |
| Sample rate | The harness executes the original `set_sample_rate` and interprets the emitted SB time constants, PAS divisors, or Gold frequency bits as exact host frame cadences. The original code chooses among the Gold rates. |
| Mixer | The harness captures SB Pro mixer nibbles, PAS ASCII mixer commands and Gold channel levels after the original arithmetic. Plain SB has speaker enable but no amplitude attenuation. |
| Silence | The harness captures original SB silence commands and frame counts. PAS and Gold execute their original skip branches. |
| Operating-system calls | Deterministic interrupt-vector bookkeeping; PAS state queries and mixer-device open/write/close. Unknown operations fail. |
| Compressed payloads | The harness captures encoded bytes and codec/layout metadata. No hardware ADPCM decoder or software codec is implemented. |

The default generated VOC uses unsigned 8-bit PCM in blocks 1/2. Gold's physical signed-byte preformatting is a transport representation; the logical sink uses the original resource's unsigned bytes and the renderer converts PCM representation. Tests cover raw PCM buffers, extended stereo, silence, markers, repeats and external compressed completion. Physical DMA fragmentation, device startup waits and analog filtering are outside sample-time equality.

## MIDI sound effects

`build-soundfx-oracle.mjs` compiles the A214 `SOUNDFX.C` file with GCC. Its shim replaces the DOS headers, unavailable AIL and timer calls, diagnostic printing, and DOS EOF marker. The effect logic is unchanged, and its AIL calls still pass through the original music driver. Tests also cover the source's unusual use of a one-based channel directly in the MIDI status nibble.

## Scope of bit equality

Parity covers these digital boundaries when both implementations receive the same inputs and timing. It includes repeated OPL writes, message order, voice behavior, sample restarts, completion, and rate and mixer choices. It does not cover physical port timing, analog response, application filters, MT-32 synthesis, external decoding, or the accuracy of NukedOPL3Sharp itself.
