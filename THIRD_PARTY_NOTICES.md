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

## Bundled managed dependencies

The plugin embeds these managed assemblies as resources and resolves them at runtime. Each is redistributed
under its own open-source license; consult the upstream project for the full text. SPDX is listed where it is
unambiguous, otherwise see upstream.

| Assembly | License |
|----------|---------|
| NAudio.Core, NAudio.WinMM | MIT |
| Concentus (Opus codec) | see upstream (xiph/Opus-derived) |
| SIPSorcery, SIPSorceryMedia.Abstractions | see upstream |
| websocket-sharp | MIT |
| SocketIOClient, SocketIO.Core, SocketIO.Serializer.* | MIT |
| BouncyCastle.Cryptography | MIT |
| DnsClient | Apache-2.0 |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions | MIT (.NET Foundation) |
| System.Diagnostics.DiagnosticSource, System.Text.Encodings.Web, System.Text.Json | MIT (.NET Foundation) |
| Interstellar, Interstellar.Messages | third-party voice backend; see its distribution for license terms |
