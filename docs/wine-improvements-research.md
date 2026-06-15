# Wine/Proton improvement opportunities — research notes

Scope: Linux users running Among Us under Wine/Proton (e.g. SammyJB). macOS out of scope.
Grounded in the current code, not speculation. Findings ordered by value.

## Already done (shipped this cycle)
- **Relay-only ICE + TURN-over-TCP + bind-address hint** under Wine (`WineEnvironment`,
  `BuildIceConfiguration(forceRelay)`), auto-on via `WineForceRelay`. Fixes the `peers=0`
  WebRTC failure. The headline fix.
- Native arch selection is already correct: `RnNoiseSuppressor`/`SpeexEchoCanceller` pick
  x64/x86 via `Environment.Is64BitProcess`, so SammyJB's 32-bit Wine process loads the x86
  DLLs. **No work needed here.**

## Worth doing

### W-1. The near-silent mic is a real, separate Wine bug — and AGC can't save it. (HIGH)
SammyJB's log: `bcl.mic.calibration peak=0.0002 rms=0.00007`, gate `0.003`, VAD `0.004`.
His mic is ~15x below the speaking gate, so he registers as silent even when talking.

Why AutoMicGain doesn't help: `MicPreprocessor` AGC only measures `_agcRecentSpeechPeak`
**after** a frame crosses the gate, and caps at `AgcMaxGain = 16f`. A signal 15x under the
gate never crosses it, so AGC never engages — chicken-and-egg. Even if it did, 16x * 0.0002
= 0.0032, only just at the gate.

This is a Wine input-gain problem (PulseAudio/PipeWire source volume, or Wine exposing the
mic at a low level), not strictly our bug — but we can make the mod *usable* anyway:

- **W-1a (best): a pre-gate input gain / "mic sensitivity" setting.** A user-set linear gain
  applied to capture BEFORE the noise gate + VAD (distinct from AGC, which is post-detection).
  A Wine user with a quiet device sets it to e.g. 8x and now crosses the gate. One
  `ConfigEntry<float> InputGain` (default 1.0), multiply in the capture path before the gate
  comparison. Small, safe, helps everyone with a quiet mic — not just Wine.
- **W-1b: surface the problem.** When unmuted and capture RMS stays << gate for N seconds,
  show a one-time toast: "Your mic is very quiet — raise input gain in Voice Settings (or your
  system mic volume)." Turns a silent mystery into a fixable prompt. (Audit M-6/§6 noted this.)
- **W-1c (cheap): document Wine mic gain** in the README — `pavucontrol` / PipeWire input
  volume, and that Among Us under Proton needs the mic granted to the Proton prefix.

### W-2. Confirm the Wine ICE fix actually works on a real box. (HIGH — verification, not code)
The relay-only fix is logic-verified (decision table) and provably inert on native Windows,
but it has **not** run on an actual Wine box. The proof is SammyJB's next log:
`env.wine detected=true forceRelay=True`, then a peer reaching `connected` via a **relay**
candidate. Until we see that, "fixed" is "should be fixed". Get one Wine log before claiming
the release fixes Linux.

### W-3. Wine WebRTC TCP/TLS reachability is unverified. (MEDIUM)
W-1's TURN-over-TCP assumes BCL's relay accepts `?transport=tcp` on 3478 and/or TLS on 5349.
coturn usually does, but we haven't confirmed BCL's specific deployment. If TCP is refused,
relay-only on a UDP-broken Wine box still fails. Action: confirm the relay's TCP/TLS support,
or stand up our own coturn (see W-5) where we control it.

## Worth considering

### W-4. Interstellar backend has no Wine path. (MEDIUM)
The relay-only/bind-address fix is BCL-only. `InterstellarVoiceBackend` (WebSocket to a
third-party server) has zero `WineEnvironment` awareness. If a Wine user selects Interstellar
they get no Wine handling at all. Lower priority (BCL is default), but if Interstellar is
offered to Wine users it needs its own pass — at minimum a "BCL recommended on Linux" note.

### W-5. Our own coturn relay. (MEDIUM-LOW, infra not code)
The whole Wine fix leans on **BCL's public TURN relay + baked-in credentials**
(`M9DRVaByiujoXeuYAAAG`/`TpHR9HQNZ8taxjb3`). If BCL rotates creds or the relay goes down, every
Wine user breaks and it's outside our control. A self-hosted coturn (TCP+TLS) removes that
dependency and lets us guarantee TURN-over-TCP for W-3. Cost: a small VPS + bandwidth.

### W-6. Wine playback latency / buffer tuning. (LOW)
`WaveOutEvent` runs `DesiredLatency=BclPlaybackLatencyMs, NumberOfBuffers=3`;
`WaveInEvent` `BufferMilliseconds=20, NumberOfBuffers=4`. winmm under Wine adds its own
buffering, so Wine users may get extra latency or underruns. Could expose a Wine-only buffer
profile (slightly deeper) — but only chase this if Wine users actually report choppiness
*after* W-1/W-2 land. Don't pre-optimize.

## Not worth doing
- Per-arch native rebuilds: arch selection already correct.
- A Linux-native audio backend: Among Us under Proton already routes winmm → Pulse/PipeWire;
  WaveIn/WaveOut work under Wine. The mic issue is gain (W-1), not a missing backend.

## Suggested order
1. **W-2** — get one real Wine log confirming the ICE fix (no code; do before release claims).
2. **W-1a + W-1b** — input-gain setting + quiet-mic toast (fixes the *other* half of SammyJB's
   problem; helps all quiet-mic users, not just Wine).
3. **W-3** — confirm BCL relay TCP/TLS, else plan **W-5**.
4. W-4 / W-6 only if reports warrant.
