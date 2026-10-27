# Validation

All inputs needed for parity are included. On Linux x64, install .NET 10,
Node.js, and GCC; NuGet supplies Unicorn 2. Then run:

```sh
dotnet build AIL2.NET.slnx -c Release
dotnet test --project tests/AIL2.NET.Tests -c Release --no-build
dotnet run --project tools/AIL2.NET.Parity -c Release --no-build -- --profile all
dotnet run --project tools/AIL2.NET.Parity -c Release --no-build -- --samples
```

The build compiles `reference/A214_D3/SOUNDFX.C`. The music and sample comparisons execute the REL105 WR modules under Unicorn, and the tests also run the A214 real-mode `.ADV` drivers. Default scenarios generate their XMIDI, VOC, and instrument inputs; no game data is required.

The 1991 driver tests compare against Star Trek: 25th Anniversary's own drivers and songs. Copy them to the ignored `game_files/st25th/` as `drivers/*.ADV` and `music/*` (the `.AD`, `.MT` and `.PC` songs, `GTL.AD` and `INIT.MT`); without them those tests are skipped and the asset-free 1991 contract tests still run.

Unicorn 2.1.3 currently terminates under the Windows .NET test host because of
its [Control Flow Guard incompatibility](https://github.com/unicorn-engine/unicorn/issues/2281).
Use Linux x64 to reproduce the recorded parity results.

Parity reports are written under `artifacts/parity/`. Each report records the
input and driver paths and SHA-256 identities, selected profile, observed
event or block counts, completion state, timing assumptions, and whether the
run passed. See [oracle-boundaries.md](oracle-boundaries.md) for the exact
hardware substitutions and normalization limits.

The Linux x64 validation run on 2026-10-27, with the Star Trek: 25th Anniversary files present, produced:

| Check | Result |
|---|---|
| Release solution build | Zero warnings and errors |
| Test suite | 190 passed; zero failures or skips |
| Music parity | 36 comparisons passed across all nine profiles |
| Sample parity | Four profile comparisons passed; 22 blocks compared |
| Player publish (run on 2026-09-06, not repeated) | Completed with notices and no oracle or test assemblies |

Rendering comparisons feed both event streams through the same
NukedOPL3Sharp dependency. They test AIL scheduling, routing, and mixing; they
do not establish the synthesizer package's correctness. Avalonia lifecycle
tests use its headless backend. The NativeAOT player was compiled on Linux
x64, but native window interaction was not exercised without a display.
