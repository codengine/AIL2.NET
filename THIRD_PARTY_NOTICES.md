# Third-party notices

## Original AIL reference material

AIL2.NET derives behavior and numeric tables from AIL 2.14 and AIL/32 1.05 by Miles Design, Inc. John Miles released the
Miles-owned source and compiled object code for unrestricted use on May 26, 2000. The grant and disclaimer are
reproduced in [`reference/LICENSE.md`](reference/LICENSE.md).

The repository excludes the separately owned DIGPAK evaluation drivers, Microsoft MAKE, and DOS/4GW from the original
distribution. The original AIL files remain under their own unrestricted grant and are not relicensed as MIT.

## NukedOPL3Sharp

`AIL2.NET.Rendering` references NukedOPL3Sharp 1.3.1 as an unmodified NuGet dependency. NukedOPL3Sharp is licensed under
LGPL-2.1-only. Its license and upstream notices are retained in `licenses/NukedOPL3Sharp-LICENSE.txt` and
`licenses/NukedOPL3Sharp-NOTICES.md` and are copied into player distributions.

The player OpenAL adapter draws on the NukedOPL3Sharp demo. Its BSD 3-Clause notice is retained in
`licenses/NukedOPL3Sharp-Demo-LICENSE.txt` and copied into player distributions.

## Other dependencies

Avalonia, Silk.NET/OpenAL, Tmds.DBus.Protocol, UnicornEngine.Unicorn, and Roslynator.Analyzers retain the terms supplied
by their respective projects and packages. Roslynator is a private build dependency. Unicorn is used only by the parity
harness; on Windows the build downloads its library from the same release's PyPI wheel, which the repository does not
redistribute. None is relicensed by AIL2.NET's MIT license.
