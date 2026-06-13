# Rebuilding speexdsp.x64.dll / speexdsp.x86.dll

These embedded native libraries provide the acoustic echo canceller (`SpeexEchoCanceller.cs`). They are a
minimal build of the speexdsp MDF echo canceller + preprocessor, compiled with the static CRT (`/MT`) so the
shipped DLL depends only on `KERNEL32.dll` (no VC++ redistributable required on the player's machine).

## Source

- Upstream: https://gitlab.xiph.org/xiph/speexdsp
- Pinned commit: `ed26e53f0a272c649af0642fdc815d3042a9430b`
- License: BSD 3-Clause (Xiph.org Foundation / Jean-Marc Valin). See `speexdsp.COPYING`.

## Build inputs

Place these two files in the repo root and one generated header before building.

`config.h`:

```c
#define FLOATING_POINT
#define USE_SMALLFT
#define EXPORT
```

`include/speex/speexdsp_config_types.h`:

```c
#ifndef __SPEEX_TYPES_H__
#define __SPEEX_TYPES_H__
#include <stdint.h>
typedef int16_t spx_int16_t;
typedef uint16_t spx_uint16_t;
typedef int32_t spx_int32_t;
typedef uint32_t spx_uint32_t;
#endif
```

`speexdsp.def` (only the symbols the wrapper P/Invokes are exported):

```
EXPORTS
speex_echo_state_init
speex_echo_state_reset
speex_echo_state_destroy
speex_echo_cancellation
speex_echo_ctl
speex_preprocess_state_init
speex_preprocess_state_destroy
speex_preprocess_run
speex_preprocess_ctl
```

## Compile (MSVC, from the speexdsp checkout root)

x64 (run inside `vcvars64.bat`):

```
cl /nologo /LD /MT /O2 /DHAVE_CONFIG_H /I . /I include /I libspeexdsp ^
   libspeexdsp\mdf.c libspeexdsp\preprocess.c libspeexdsp\fftwrap.c libspeexdsp\smallft.c libspeexdsp\filterbank.c ^
   /Fe:speexdsp.x64.dll /link /DEF:speexdsp.def
```

x86 (run inside `vcvars32.bat`): same command, `/Fe:speexdsp.x86.dll`.

Copy the two DLLs into `PerfectComms/Libs/`. They are embedded via `<EmbeddedResource>` in `PerfectComms.csproj`
with logical names `Lib.speexdsp.x64.dll` / `Lib.speexdsp.x86.dll`, matching `SpeexEchoCanceller.ResourceName`.
