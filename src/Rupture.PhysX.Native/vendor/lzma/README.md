# LZMA decoder

Unmodified C decoder files from Igor Pavlov's LZMA SDK / 7-Zip **26.03**:
https://github.com/ip7z/7zip/tree/26.03/C

Included: `LzmaDec.c`, `LzmaDec.h`, `7zTypes.h`, `Precomp.h`, `Compiler.h`.
The LZMA SDK is public domain: https://www.7-zip.org/sdk.html
The upstream copyright/public-domain notices remain in the source files.

Rupture compiles this decoder into its existing native DLL with the static CRT.
The separate `rptpx_lzma_decode_v1` entry point needs no PhysX scene or Unity hooks.
It accepts a Unity LZMA block (five property bytes plus raw compressed data),
checks consumed/produced sizes, and writes only into the caller-owned output buffer.
