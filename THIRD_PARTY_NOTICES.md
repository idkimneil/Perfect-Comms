# Third-Party Notices

Perfect Comms embeds the following third-party native libraries as assembly resources and extracts them at
runtime. Their licenses are reproduced or referenced below.

## speexdsp (acoustic echo cancellation + preprocessor)

- Files: `Libs/speexdsp.x64.dll`, `Libs/speexdsp.x86.dll`
- Upstream: https://gitlab.xiph.org/xiph/speexdsp (commit `ed26e53`)
- License: BSD 3-Clause. Copyright 2002-2008 Xiph.org Foundation and Jean-Marc Valin. Full text in
  `Libs/speexdsp.COPYING`.
- Build recipe: `Libs/speexdsp-build.md`.

## RNNoise (noise suppression)

- Files: `Libs/rnnoise.x64.dll`, `Libs/rnnoise.x86.dll`
- Upstream: https://github.com/xiph/rnnoise
- License: BSD 3-Clause (Xiph.org Foundation / Gregor Richards / Mozilla). Full text in `Libs/rnnoise.COPYING`.

## Other bundled managed dependencies

NAudio, Concentus, SIPSorcery, SocketIOClient, and related packages are redistributed under their respective
MIT/BSD licenses; see each package's repository for the full license text.
