# Original AIL reference material

This directory contains the AIL 2.14 and AIL/32 1.05 files used to implement
and verify AIL2.NET. John Miles released the Miles-owned source and compiled
object code for unrestricted use on May 26, 2000; see [LICENSE.md](LICENSE.md).

The parity harness executes the original REL105 Watcom/Rational driver modules
under Unicorn and compares their observable output with the managed code. It
does not modify the reference files on disk. The checked-in managed FM tables
come from `YAMAHA32.INC`, and `tools/build-soundfx-oracle.mjs` builds the
original `SOUNDFX.C` helper for its contract tests.

The original distribution also contained third-party DIGPAK evaluation
drivers, Microsoft MAKE, and DOS/4GW. Those files are omitted because they are
not needed by the build, tests, managed libraries, or parity harness.
