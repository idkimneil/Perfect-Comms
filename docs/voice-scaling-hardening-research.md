# Hardening Perfect Comms Voice Playout for ~15 Players

## Executive summary

The deployed fix-pack (adaptive jitter buffer + decode guard + per-peer connection recovery) is healthy on the 2-client Epic<->Steam cross-platform test: 0 rejoin storm, 0 openChannels deficit, recovery prebuffer cleanly escalating to its 160 ms ceiling. But that same test still logged underruns, and the right question is the one the user asked: if 2 clients underrun, what breaks at 15?

The forensics resolve to a **two-cause diagnosis**, and the headline is that **raising the buffer ceiling fixes only part of cause 1 and pays for it in latency**:

- **Cause 1 - link jitter exceeding the 160 ms cushion (steady state).** On the majority of underruns the adaptive recovery prebuffer AND the measured-jitter setpoint were both pinned at the 7680-sample (160 ms) ceiling - i.e. the buffer is saturated and *wants more*. peerJitter windows reached 105-125 packets. This is genuinely the Epic<->Steam link's jitter tail exceeding 160 ms, on both ends (Steam was jitterier than Epic). A deeper buffer helps this - at a latency cost - and only for the few worst links.
- **Cause 2 - process-stall bursts (transition).** A 540 ms frame at game entry (97% external engine/scene load, only 3% voice, no GC) sits next to a 207 ms frame that is 86% voice - but that voice cost is the **HUD/overlay rebuild** (`hud=177 ms`, 24 MB allocated in one frame), not decode or peer setup. A deeper buffer **cannot ride out a half-second freeze**.

The durable scaling work is therefore **not** "buy more buffer." It is three orthogonal hardening tracks:

1. **De-spike the transition** (amortize per-peer setup, move first-time HUD init off the entry frame, throttle the O(players) meeting/HUD rebuild).
2. **Decode/mix/alloc throughput** (pool the per-frame PLC bridge allocation, keep the single WaveOut pull thread under its ~20 ms deadline, skip work for inaudible peers - with the caveats below).
3. **Stall-resilient playout** (a read-clock freeze detector so one process stall becomes one clean re-prime per ring instead of a per-ring underrun burst, and a *per-peer* link-aware ceiling so only genuinely jittery links pay latency).

**Honesty note on confidence:** the current-build evidence is a 2-client run. The 11/12-peer logs that establish the scaling slope are an **older build** with no adaptive-cushion fields (recovery hardcoded at 2880 = 60 ms), so they prove the *underrun-count slope* (~14x from 1 to 12 peers) and the *steady-state frame-time regression* but **cannot** prove the new adaptive machinery's at-scale behavior. Several individually-proposed fixes were refuted by adversarial verification against the actual code/logs and are marked below. The genuinely load-bearing, verification-survived items are a small subset.

---

## Diagnosis (confirmed)

### Cause 1 - link jitter > 160 ms cushion (DOMINANT, buffer-addressable)

**Confirmed and extended.** A key correction first: the "34 underruns" in the Epic log are **L/R double-counts** of a split-mono stereo graph. `BclStereoPlaybackProvider` reads left then right on the same pull (`BetterCrewLinkPlaybackGraph.cs:153-209`) and `AudioProviders.cs:281` logs per-route, so every logical underrun produces two rows. Epic = 34 rows = **17 logical** events; Steam = 38 rows = **19 logical**.

Evidence the cushion is the binding constraint:

- **Epic** (`voicechat_...125930`): 17 logical underruns, 7/17 with the jitter setpoint clamped at the 7680/160 ms ceiling; setpoint min/med/max = 5884/7536/7680; 20/34 rows had recovery prebuffer >=95% of ceiling. peerJitter window counts reached 125/window (`BclVoicePacket.cs:214 CurrentJitterSamples` drives the setpoint via `RecomputeSetpointLocked`, `AudioProviders.cs:302-310`).
- **Steam** (`voicechat_...125934`) mirrors Epic but is jitterier: 19 logical underruns, 30/38 rows (79%) clamped at ceiling, jitter median = 7680 (full ceiling vs Epic's 7536). Steam wanted **more** depth than Epic on the same link.
- The upstream jitter buffer never backed up (`late:0 dup:0 lost:0 plc:0 fec:0 depth:2/3` throughout). Packets arrive in **bursts then gap**, starving the downstream ring between bursts. That is exactly the pattern a deeper downstream cushion addresses - at a latency cost.

**Important nuance from verification:** on the current build, every logged underrun read shows `requested=960 actual=960` - the existing **trailing-PLC bridge** (`AudioProviders.cs:248-271`) already *concealed* each one. So these are end-of-talkspurt drains and jitter-gaps that were papered over, not raw audible holes. The cushion is saturated and the setpoint wants more, but the audible damage is currently limited.

### Cause 2 - process-stall bursts at game entry (REAL, NOT buffer-addressable, currently non-damaging)

**Confirmed real, re-attributed, and currently non-damaging.** The transition cluster is three back-to-back frames at game entry (Epic):

- `+73.331s dt=540.6ms vc=14.55ms vcPct=3 gc=n players=0 peers=0` - a 540 ms **engine/scene-load freeze**, only 3% voice. No buffer rides this out.
- `+73.766s dt=207.8ms vc=179.07ms vcPct=86 heapDeltaKB=24382 phase=Lobby players=1 peers=0` with `segs=[vc.tick=179.02 hud=177.40 ...]`. **The 179 ms / 24 MB spike is ~entirely `hud`.**
- `+73.794s dt=224.0ms` - overlay first-build.

Critical correction to the working hypothesis: the 24 MB / 179 ms is **NOT** PeerConnection/OpusDecoder peer-setup. At that frame `players=1/peers=0`, and `bcl.peer.created` fires *later* at `+73.938s` (`BetterCrewLinkVoiceBackend.cs:1499`); the first underrun is at `t=75.84`. So the freeze fired **before any peer connected and before audio flowed** - it produced ~0 underruns this run. The 179 ms is first-time, uninstrumented HUD init: `EnsureHudButtons` doing `Object.Instantiate` of HUD prefabs (`VoiceChatHudState.cs:245/265/285`), `LoadSprite` -> `new Texture2D` + `tex.LoadImage` + `Sprite.Create` (embedded-PNG decode, `:896-913`), and `CreateTooltipObject` (`:633-637`). The DTLS peer-setup cost ran on **background ThreadPool threads** (`bcl.pcpool.built` 18-33 ms each) - the pre-built pool (`BetterCrewLinkVoiceBackend.cs:1524`) already moved it off the critical path. GC is **not** dominant: gen2 fired once, heap 141->313 MB.

Steam mirrors this with one `+69.3s dt=498.1ms` external freeze and the same `vc.tick=134ms / hud=124ms` spike.

### 2-client residual split, and the 15-client projection

**Residual split at 2 clients:** ~100% link-jitter (cause 1), ~0% process-stall damage (cause 2 fired before audio flowed). Cause 2 is real but currently harmless.

**What scales toward 15, and why buffer depth cannot touch it:**

| Vector | At 2 clients | Why it gets worse at 15 | Buffer fixes it? |
|---|---|---|---|
| Independent per-link jitter tails | 17/19 logical underruns | N independent rings each drip underruns; raw count multiplies | Partially (cause 1) |
| Per-peer setup batched in one frame | 1 peer, invisible | `setClients` (`BetterCrewLinkVoiceBackend.cs:1044-1062`) runs EnsurePeer x15 in one main-thread wave, landing in the same transition window as the 540 ms freeze | No |
| First-time HUD init (24 MB/177 ms) | once, before audio | Lands in the heavier 15-player scene-load window; can overlap active talk | No |
| O(players) meeting/HUD rebuild | absent at 2 clients | `overlay.meeting`/`hud` already hit 62/55 ms at 12 peers; grows linearly | No |
| Single-threaded WaveOut pull running every peer's router chain | idle 99% of slot | At 15 peers, 15x DSP + 30 ring-lock pairs every ~20 ms; one missed deadline underruns ALL rings together | No |
| PLC-bridge `new float[960]` per decoded frame | ~0.4 MB/s | ~2.8 MB/s at 15 continuous talkers (realistically ~0.8 MB/s at 2-4 concurrent) feeding gen0/gen1 GC | No |

**At-scale evidence (OLD build, scaling slope only):** 12-peer (`voicechat_215113`) = 468 underrun rows (~234 logical) vs 17 logical at 1 peer = ~14x for 12x peers (super-linear). Steady state degraded: dtP50 8.3->12.2 ms, dtP95 8.3->21.7 ms, dtP99 22->32.9 ms; 303 slow frames; 65 gen1 GCs (vs ~3). The dominant CPU spikes are `overlay.meeting` (62 ms) and `hud/vc.tick` (55 ms) - **not decode**. Only 5 of 12 streams underrun, concentrated on the worst-jitter peers, and **max 2 distinct streams starved together in any 50 ms window** - the all-streams-starve-together mode did NOT trigger at 12, but it is the real 15-client tail risk.

---

## Ranked hardening plan

Each item lists mechanism (file:line), the 15-vs-2 scaling rationale, the code approach, effort/risk, and the **adversarial verdict** from independent verification against the actual code and logs. Items that the verifier refuted are kept in the table for honesty and marked **REFUTED** or **OVER-PROMISED** - do not ship those as written.

### P0 - ship these (survived verification or low-cost/high-leverage)

#### P0.1 Pool the per-frame PLC bridge allocation
- **Mechanism:** `PublishBridgeFrameLocked` (`BetterCrewLinkVoiceBackend.cs:3327-3343`) does `new float[960]` (3.8 KB) on every real decoded frame, per peer, by design (atomic publish of `_latestPlcFrame`). Replace the single published reference with a per-peer 2-slot ping-pong (two reusable `float[960]` + an int toggle), published via `Volatile.Write`. The reader (`GetFreshBridgeFrame`, `:3349`) already snapshots under `Volatile.Read` and copies into reused `_plcTaperScratch` (`AudioProviders.cs:259-263`) without retaining the reference.
- **15 vs 2:** ~0.4 MB/s at 2 talkers (invisible) -> up to ~2.8 MB/s at 15 continuous talkers; this is the single largest decode-path allocator. Flattens decode-path allocation to near-constant regardless of talker count.
- **Code approach:** *pool*. Per-peer `float[][] _bridgeSlots = new float[2][]`, `int _bridgeSlot`; write the faded frame into `_bridgeSlots[_bridgeSlot^=1]` then `Volatile.Write(ref _latestPlcFrame, that)`. No reader change.
- **Effort:** small. **Risk:** low.
- **Adversarial verdict - PARTIALLY REFUTED / OVER-PROMISED, ship as hygiene only.** The verifier confirms the mechanism *works* (publisher serialized under `_sync` at <=1 publish/20 ms/peer; reader copies sub-microsecond and never retains the ref) - with one caveat the proposal glossed: **two** readers (left+right route, wired `:2889-2890`) read the same `_latestPlcFrame`, so the safety guarantee weakens from "impossible by construction" to "never observed." More importantly, **the GC-freeze premise is contradicted by both logs**: 12-peer run = gc0=96/gc1=25/gc2=0 over ~50 min (heap oscillates 216-251 MB, *ends lower*); 2-client = gc0=2/gc1=2. There is no GC-pause-freeze signal to remove. **Verdict: keep it as cheap allocation hygiene (it removes real per-frame garbage and the 2-slot pattern is sound), but do NOT claim it fixes the at-scale freezes - it does not.** Use the double-buffer (not ping-pong with a single slot) to keep both-reader safety airtight.

#### P0.2 Per-peer link-aware ceiling (deepen toward ~200 ms ONLY for sustained-clamped peers)
- **Mechanism:** `ReachableCeilingLocked` (`AudioProviders.cs:293-299`) returns `min(BufferCutToSize, _maxAdaptivePrebufferSamples)`. Make the effective ceiling **per-peer**: when the *unclamped* jitter target stays at/above the current ceiling for a sustained streak (incremented in `RecomputeSetpointLocked`, `:302`), raise that peer's `_maxAdaptivePrebufferSamples` one frame-step toward 200 ms; decay it back when jitter falls (`DecayRecoveryPrebufferLocked`, `:335`). Each `BufferedSampleProvider` is created per-peer in `AudioManager.Generate(groupId)` (`AudioRouting.cs:188-228`) with its own ring/cap, so a jittery peer cannot add latency to healthy peers.
- **15 vs 2:** at 15 you typically have 1-3 truly jittery links and a dozen fine ones. A *global* bump to 200 ms adds 40 ms to all 15; the per-peer clamp confines the latency cost to the peers that earn it. Directly attacks the 7/17 (Epic) and 30/38 (Steam) ceiling-clamped underruns.
- **Effort:** medium. **Risk:** medium.
- **Adversarial verdict - KEEP, but the codeApproach as written is INEFFECTIVE (a real bug) and must be corrected.** The diagnosis is confirmed: in the current 2-client log one peer (group 22635) held `jitter==recovery==ceiling==7680` for 30 of 38 underruns across ~56 s, and the latency cost is provably per-peer (separate rings). **But there is a hard code bug:** `BufferCutToSize` is set ONCE at `Generate()` time (`AudioRouting.cs:203`) to `min(_bufMax-960, PlaybackMaxRecoveryPrebufferSamples)+960 = min(13440,7680)+960 = 8640` (180 ms), and `ReachableCeilingLocked = min(BufferCutToSize, _maxAdaptivePrebufferSamples)`. Merely bumping `_maxAdaptivePrebufferSamples` to 9600 (200 ms) yields `min(8640,9600)=8640` - **you gain one frame (20 ms), not 40 ms** - and `AddSamplesLocked` (`:136-138`) actively discards ring content above `BufferCutToSize` every write. **To actually reach 200 ms you MUST make `BufferCutToSize` AND `BufferCutSize` per-peer-mutable in lockstep with `_maxAdaptivePrebufferSamples`,** preserving `target<=reachable<BufferCutToSize<BufferCutSize<_bufMax` (`_bufMax`=300 ms = FrameSize*15, so 200 ms+headroom fits). The PLC-bridge headroom gate (`AudioProviders.cs:250`, `_ring.Count+FrameSize<=BufferCutToSize`) must move with it or the bridge stops emitting near the new cap. Decay must lower the per-peer **cap** (not just `_adaptiveRecoveryPrebufferSamples` within it) or a one-time bad spell strands the peer at 200 ms forever. **This is the only buffer-domain fix that is sound at scale - but implement the full per-peer-cut-size version, not the literal codeApproach.**

### P1 - de-spike the transition and harden the pull thread

#### P1.1 Throttle / cache the meeting+HUD speaking-indicator rebuild
- **Mechanism:** `overlay.meeting` (62 ms) and `hud/vc.tick` (55 ms) at 12 peers come from `MeetingSpeakingIndicatorPatch.cs:143-185` looping all `playerStates` every frame (transform sync + `GetPaletteColor` + sprite color math), and the per-card `GetComponentsInChildren<Renderer>(true)` interop.
- **15 vs 2:** this is O(players) every render frame and is what pushed dtP95 from 8.3 ms (2 clients) to 21.7 ms (12). A long render stall during active voice eventually starves the WaveOut feeder.
- **Code approach:** *throttle/cache*. Dirty-flag on `_speakingLevels` delta; cache the per-card renderer set keyed by `TargetPlayerId`.
- **Effort:** medium. **Risk:** low.
- **Adversarial verdict - LARGELY REFUTED as the *primary* lever; the source anchors in the proposal are wrong. Treat as speculative.** Verification found: (a) the cited lines 398/505 are in `TryGetWorldBounds`/`CountRenderers`, **not** in `ApplyBuiltInHighlight/ClearBuiltInHighlight` (those touch a single cached `state.HighlightedFX`). (b) The `GetComponentsInChildren` calls run **only** on the debug describe path (`DescribeVoteArea`, called under `if (logNow)` gated by `DebugVoiceStats.Value`, throttled to 1/s). (c) The cited 62 ms `overlay.meeting` spike is a **debug-run artifact**: the 12-peer log was captured with debug stats ON (2444 `renderers=` lines); the 2-client log has zero. (d) Correlating spikes, `overlay.meeting` dominated only ~7% of slow frames (p50=0.15 ms, p90=4.18 ms); the big values track `dt` with `gc=n` = a Stopwatch suspended *through* an external engine freeze, not CPU burned. (e) The steady-state loop already idle-skips when nobody speaks (`:135-141`) and `EnsurePlayerLookup` already caches the O(players) walk to once/frame. **Verdict: do NOT ship a renderer-array cache on the false anchor. IF a real meeting-overlay cost is confirmed with debug OFF on a real 15-player capture, the dirty-flag throttle is the right shape - but it is currently unproven and likely chasing an observer effect.** This needs a real capture before any code change.

#### P1.2 Move first-time HUD init off the game-entry frame
- **Mechanism:** the measured 24 MB / 177 ms `hud` segment at `+73.766s` is one-time HUD init: `EnsureHudButtons` (`VoiceChatHudState.cs:238-298`, `Object.Instantiate`), `LoadSprite` (`:890-915`, `new Texture2D` + `tex.LoadImage` + `Sprite.Create`), `CreateTooltipObject` (`:633-637`). Already spread one-button-per-frame (`:260`), but the first button + its sprite decode lands on the entry frame next to the engine freeze.
- **15 vs 2:** does NOT scale with peers (once per process), but it lands in the exact window where the 540 ms freeze + 15x join wave pile up. Removing it widens headroom during the worst frame for every lobby size.
- **Code approach:** *pre-warm*. Decode sprites and pre-instantiate tooltip/button GameObjects (inactive) during room construction - the same lifecycle slot as `WarmOpusCodec` (`BetterCrewLinkVoiceBackend.cs:199-215`). Keep the per-frame `EnsureHudButtons` guards as fallback.
- **Effort:** medium. **Risk:** low.
- **Adversarial verdict - PLAUSIBLE, not independently contested.** This is the one transition-spike item whose *attribution* (the 24 MB/177 ms = first-time HUD init, not peer setup) is consistent across both the forensic and transition-alloc analyses. It does not scale with peers, so it is not the 15-client cliff, but it is a real one-time blip on the worst frame and pre-warming it is low-risk. **Ship it, but scope the claim correctly: it widens transition headroom; it does not address any O(peers) cost.**

#### P1.3 Add a scale-test harness (simulate 15 decode+mix streams)
- **Mechanism:** `PeerConnection` has a reflection-constructable ctor for the test harness (`BetterCrewLinkVoiceBackend.cs:2872`), and `BclMonoPlaybackGraph.Generate(groupId)` (`BetterCrewLinkPlaybackGraph.cs:69`) is the exact per-peer wiring used in prod. Build a harness that creates left/right graphs, generates N routes, spawns N threads feeding jittered 20 ms Opus frames into `TryReceiveVoicePacket`, and one reader thread pulling `BclStereoPlaybackProvider.Read` at the WaveOut cadence - measuring per-Read wall time, lock-wait, underrun count, and GC gen0/1 rate as N goes 2->15->20.
- **15 vs 2:** today the only scale data is OLD-build in-game logs with no isolation of decode vs mix vs HUD. A harness turns "does it scale?" into a measured per-Read-ms-vs-N and GC-rate-vs-N curve, catching a deadline regression before ship.
- **Code approach:** *thread*. Extend the existing `PerfectComms.Tests` console/xunit harness (reuse `GetOpusEncoder`, `AudioHelpers.cs:83`); per-stream gaussian inter-arrival jitter.
- **Effort:** medium. **Risk:** low (test-only assembly, zero production code).
- **Adversarial verdict - KEEP, with scope/assertion corrections.** Verification confirms this is the right move and adds zero production risk, but corrects two things: (1) `PerfectComms.Tests` **already** has `TestVoiceProtocol15ClientSafetyContract` (`Program.cs:3552`), a 6-route meeting-mix test (`:3899`), and `TestAudioMixerConcurrentAccess` (`:3825`) - so the novelty is the **timed/GC measurement vs N**, not the harness itself. (2) Do NOT use an absolute `p99 Read < 18ms` gate (arbitrary, CI-flaky vs the real 60 ms WaveOut budget). Use a **relative** gate (p99 at N=15 within a bounded multiple of p99 at N=2) plus a generous absolute ceiling tied to the 60 ms buffer, and **assert bridge-frame alloc-rate is flat across N** (the real, CI-robust signal). **Scope it to steady-state decode+mix throughput only - it does NOT validate the game-entry allocation spike** (per-peer construction + HUD rebuild are not exercised by a steady-state harness).

### P2 - lower-confidence / situational

#### P2.1 Pre-guard the per-packet Concentus decode THROW
- **Mechanism:** logs show 74 (Epic) / 139 (Steam) `error during decoding: Specified argument...` - each a thrown+caught exception on the receive thread (`DecodeAndAddSamples` catch, `BetterCrewLinkVoiceBackend.cs:3275-3286`). The `MinLegacyOpusBytes` pre-guard (`:3180`) only covers legacy <3-byte frames; the PV2 path can still feed an out-of-range frame to Concentus and throw.
- **15 vs 2:** on a shared public room, throw volume scales with foreign/incompatible-stream count and lands on the receive threads that also do real decode, stealing time from healthy streams. The `_decodeSuppressed` breaker (`:3200`) only kicks in after 8-10 sustained throws.
- **Code approach:** *conceal*. Validate frame_size/packet bounds (cheap arithmetic on the Opus TOC byte) before `Decoder.Decode`; `RouteSilence` on a clearly-invalid packet. Keep the suppression breaker as backstop.
- **Effort:** small. **Risk:** low.
- **Adversarial verdict - not independently contested; low-cost defensive hygiene.** Reduces steady-state throw cost per incompatible stream toward zero. Reasonable to include.

### Refuted / do-not-ship (kept for the record)

These were proposed across the four analysis angles and **refuted by verification against the actual code/logs**. They are listed so the plan does not silently re-introduce them.

| Proposed fix | Verdict | Why refuted |
|---|---|---|
| **Gate `AudioMixer.Read` to skip Count==0 AND gain==0 inputs** | **REFUTED** | Muted peers already do NOT run their DSP: `VolumeRouter.Property.Read` early-outs at `_volume<=0` (`AudioRouting.cs:282`) without pulling upstream, and `MuteAll()` zeroes the terminal volume routers. The chain is already O(active); reverbs/distortion are global. The "all-streams-starve cliff" never appeared (12-peer concurrency max = 3, mostly 1). The frame spikes are on the Unity main thread (`vc.tick`/`hud`/`overlay.meeting`), which the mixer cannot touch. Re-implements an existing optimization with a strictly narrower gate, and risks skipping a peer whose gain was just set >0 while its ring is still filling at talkspurt onset. |
| **Skip decode+ring-write for proximity-inaudible peers (RemoveInput/AddInput on transition)** | **REFUTED** | `Apply()` is deliberately lock-free/decode-free (`:976`); calling RemoveInput/AddInput from it takes `_inputsLock` + allocates a fresh snapshot (`_inputs.ToArray()`) on the render thread, racing the WaveOut reader - re-introducing the exact coupling a prior fix removed. Decode is already per-peer-parallel on receive threads (15 threads, not one serialized path), so skipping saves diffuse CPU, not a stall. Proximity-boundary thrash at 15 players truncates reverb tails, forces re-prebuffer, cold-starts the decoder/jitter EWMA, and starves the PLC bridge. Measured audible count at 12 peers is avg 4.68/p90 8 (not "2-5"), so the WaveOut win is ~0.5x at bad moments, not 0.13-0.33x. |
| **Amortize `setClients` join wave (K peers/frame), defer EnsurePeer/StartOffer** | **REFUTED** | The expensive step (DTLS cert ~300-500 ms in IL2CPP) is **already** off-thread via the warm `_pcPool` (`:1645-1672`); OpusDecoder is pre-warmed (`WarmOpusCodec`). Logs: `backend.mainactions` max = 0.00 ms, `backend.join` max <= 0.03 ms - the segment this amortizes is already negligible. The mixer reader is lock-free via a volatile snapshot, so wiring a peer does not block the WaveOut thread - the "one frame starves all 15 rings" claim is mechanically false. Adds connect-latency at the exact moment everyone starts talking, plus a roster-consistency hazard (MapClient eager but EnsurePeer deferred opens a mapped-but-no-peer window). |
| **Pre-build per-peer playback routes in a background pool** | **REFUTED** | `Generate().Build()` interleaves node allocation with groupId-stamped wiring into the **shared** global mixers (`AddInput` republishes `_inputSnapshot` read live by the WaveOut thread) and the shared `AudioBufferRegistry` (`_buffers.Add`). The clone IS the groupId stamping - there is no separable heavy clone to pool. The RTCPeerConnection-pool precedent does not transfer (isolated object vs shared-graph splice). The targeted ring float array is allocated **lazily on the decode thread** (`_ring ??= new CircularFloatBuffer`, `:108`), not inline on the main thread. Worst voice frame at 12 peers had `vc=63ms` with gc0/1/2=0 - a stall, not allocation pressure. |
| **Fix `AudioBufferRegistry.Add` O(n^2) ToArray rebuild** | **REFUTED** | Only 2 non-global routers (`_imager`, `_clientVolume`) emit a buffer per Generate, so the entire "quadratic burst" at 15 peers is ~7.4 KB of reference copies, one-time, spread across the whole join wave - not 24 MB/179 ms. It runs in `backend.mainactions` (0.00 ms in logs), not on the real-time path. The proposed "lazy-on-read snapshot" would move a `ToArray` + lock onto the real-time WaveOut pull thread (`EndpointWrapper.Read`, `:74-78`), which is currently lock-free/zero-alloc - a regression for a ~7 KB saving. |
| **Freeze-aware conceal + re-prebuffer in `ReadLocked` (read-gap detector)** | **REFUTED as specified** | There is no `_lastReadUtc` and no freeze detector in the codebase today, so this is net-new real-time-thread work, not "one bool." The WaveOut callback thread is **independent of the Unity main thread** - an engine freeze does not stall it, so `(now-_lastReadUtc)` stays ~20 ms and the detector **never fires for the freeze it targets**. Conversely, stamping only on `num>0` misclassifies every normal inter-talkspurt silence (1-15 s) as a freeze, forcing re-prebuffer onset latency on ordinary speech. Empirically, per-peer max consecutive-underrun burst is **exactly 2** in every at-scale case (the L/R double-count), not the claimed O(frozen_ms/20ms) burst - so there is no per-ring burst to collapse to O(1). The trailing-PLC bridge already shows `actual=960` (already concealed), and the bridge frame self-expires at 200 ms (`PlcFrameMaxAgeTicks`, `:2868/3353`) so it physically cannot paper over a 500 ms tail regardless of frame cap. |
| **Extend trailing-PLC bridge to ~160 ms on a "freeze path"** | **REFUTED** | Depends on the non-existent freeze detector; the only landable form bumps the cap for ALL recentlyActive underruns, smearing up to 160 ms of one held 20 ms slice (the buzz the 3-frame cap exists to avoid). The bridge self-expires at 200 ms and is only refreshed by a real decode, so during a true freeze no new frame is published and it stops by ~200 ms anyway. |
| **Suppress cushion-escalation during a "detected freeze"** | **REFUTED** | Same non-existent detector. The real ratchet is the jitter **setpoint** floor: every underrun shows `jitter==recovery` exactly, so `DecayRecoveryPrebufferLocked` (floored at `_jitterSetpointSamples`, `:338`) can never decay below an at-ceiling setpoint. Suppressing the *grow* path leaves recovery pinned at 160 ms. The real lever is the jitter EWMA recovery in `BclVoiceJitterBuffer.Enqueue` and capping `JitterGain*jitter`, plus letting decay age a stale setpoint - **not** a freeze flag. |
| **Shrink WaveOut quantum / NumberOfBuffers 3->4 / defer idle-drain** | **REFUTED** | `NumberOfBuffers 3->4` adds 20 ms output latency for every listener and cannot bridge 180-540 ms stalls. Every underrun is `bufferedBefore=0/buffered=0` (empty-ring starvation) - a reader cannot underrun a ring that holds samples, so reader-side lock scope is irrelevant. No correlated global underrun (>=3 groups together) ever occurs in any log. Moving the idle-drain to the writer side breaks idle-detection semantics (the drain exists *because* the talker went idle, which only the reader observes). The active-drain already fires at most once per idle->reset transition and is O(1) - not in the hot path. |

---

## What buffer-ceiling tuning does and does NOT fix

**Be explicit and honest.** Raising `PlaybackMaxRecoveryPrebufferSamples` (and the per-peer cap, P0.2) is **necessary but not sufficient.**

**It DOES fix (cause 1, the dominant 2-client residual):**
- The steady-state link-jitter tail where the setpoint is genuinely clamped at the ceiling and *wants more* (7/17 Epic, 30/38 Steam underruns). A deeper *per-peer* cushion rides out the burst-then-gap arrival pattern for the few worst links.
- At 15 clients, the multiplied independent per-link jitter tails - but only for the 1-3 links that actually peg the ceiling, if the cap is per-peer (P0.2). A global bump would tax all 15 with latency.

**It does NOT fix (everything that genuinely scales toward 15):**
- **The 540 ms transition freeze.** No buffer depth (160 ms, 200 ms, even 300 ms) rides out a half-second process stall. Only de-spiking the source (P1.2 HUD pre-warm; the engine scene-load is outside our control) helps.
- **The O(players) main-thread HUD/meeting rebuild** that grows the steady-state frame time (dtP95 8.3->21.7 ms at 12). Buffer depth is on the audio thread; this is render-thread cost.
- **The single-threaded WaveOut pull traversing every peer's router chain** against a ~20 ms deadline. If one pull misses the deadline at 15 peers, ALL rings underrun together - and a deeper buffer just delays that cliff, it does not remove it.
- **Decode-path allocation/GC growth.** Pooling (P0.1) addresses the garbage; buffer depth does nothing for it.

**The honest framing for the user:** at 2 clients ~100% of the residual is buffer-fixable jitter, so the deeper per-peer cushion is a real and cheap win. But the *new* 15-client failure modes (transition freeze overlapping active talk, the single-pull throughput cliff) are buffer-immune, and that is why "raise the ceiling" is a band-aid for cause 1, not a scaling strategy.

---

## Validation plan

### A. Offline - scale-test harness (P1.3)
Prove throughput before a human lobby exists:
1. Reflection-construct N `PeerConnection`s over real left/right `BclMonoPlaybackGraph` (`BetterCrewLinkPlaybackGraph.cs:69`); spawn N feeder threads pushing jittered 20 ms Opus frames into `TryReceiveVoicePacket`; one reader thread pulling `BclStereoPlaybackProvider.Read` at the WaveOut cadence.
2. Sweep N = 2, 8, 15, 20. Record per-Read wall time (p50/p95/p99), per-ring lock-wait, underrun count, and `GC.GetAllocatedBytesForCurrentThread` / `GC.CollectionCount(0/1/2)`.
3. **Assertions (CI-robust):** p99 Read at N=15 within a bounded multiple of p99 at N=2 (relative, not absolute); absolute ceiling tied to the 60 ms WaveOut budget; **bridge-frame alloc-rate flat across N** after P0.1.
4. **Scope caveat:** this validates steady-state decode+mix throughput ONLY. It does **not** exercise the game-entry peer-construction + HUD-rebuild spike - that needs the in-game capture below.

### B. In-game - the signals to watch on a real capture
The fields the per-frame profiler and the underrun log already emit:
- **Underrun cause split:** classify each underrun by (a) `recovery`/`jitter` AT ceiling within the read = cause-1 link-jitter, vs (b) underrun timestamp within ~1.5 s of a `dt>=150ms` frame = cause-2 stall-burst. At 2 clients this was ~100% / ~0%; watch the split shift at 15.
- **Per-peer ceiling-clamp:** count peers with `jitter==recovery==ceiling` sustained. P0.2 should keep this confined to 1-3 worst links and let the rest decay back to the 60 ms baseline between talkspurts (`IdleRecoveryResetWindow=200ms`).
- **`vc.tick` / `hud` / `overlay.meeting` at the transition** (game-entry frame): with P1.2, the 24 MB/177 ms `hud` first-init should disappear from the entry frame. **Capture with `DebugVoiceStats` OFF** - the 12-peer 62 ms `overlay.meeting` was a debug-on observer artifact (P1.1), so any meeting-overlay measurement must be debug-off to be real.
- **Heap/GC:** `heapDeltaKB` per frame and gen0/1/2 counts. With P0.1, decode-path per-frame `heapDeltaKB` should flatten; watch that gen2 does not start firing (it fired once at 2 clients).
- **Worst-frame attribution:** for any `dt>=150ms` frame, log `vcPct`/`gc` - confirm the freeze is external (`vcPct` low, `gc=n`) vs voice-caused, so we know whether the stall is ours to fix.
- **Concurrency-of-underrun:** the count of distinct groups underrunning in the same 50 ms window. At 12 peers max was 2; if 15 peers show >=3+ together, the single-pull throughput cliff has arrived and decode/mix throughput (not buffer depth) is the lever.

---

## Risks / open questions / what needs a real 15-player capture

**Confidence is split.** The 2-cause diagnosis and the per-peer ceiling clamp are HIGH confidence (grounded in the current-build 2-client logs and source). The 15-client *behavior* is a projection, and several individually-attractive fixes were refuted because the at-scale logs are an older build. Specifically:

1. **No current-build at-scale capture exists.** The 11/12-peer logs predate the adaptive cushion (recovery hardcoded at 2880, no `jitter=`/`ceiling=` fields). They prove the underrun-count slope (~14x) and the frame-time regression, but **cannot** prove how the new adaptive machinery behaves at 15. A real 15-player current-build capture is the single highest-value missing artifact.

2. **The single-pull throughput cliff is unobserved, not disproven.** At 12 peers max 2 streams starved together; the all-streams-starve-together mode is a source-level *risk* (15x DSP + 30 ring locks per ~20 ms slot), not a measured failure. The harness (P1.3) is the cheapest way to find its actual onset N before a human lobby does.

3. **The meeting/HUD cost is contaminated by the observer effect.** The headline 62 ms `overlay.meeting` was captured with debug stats ON; the per-card `GetComponentsInChildren` only runs on the debug path. **Open question:** with debug OFF, how much of the 12-peer dtP95 regression (8.3->21.7 ms) is the meeting overlay vs external engine render cost scaling with player count? P1.1 should not be coded until this is answered on a real capture.

4. **The transition freeze fired before audio flowed - on this run.** At 15 players the scene load is heavier and the join wave lands in the same window; whether the freeze overlaps active talk is the untested failure. The stall-resilient-playout family of fixes was refuted *as specified* (no read-gap the WaveOut thread can observe), so the honest position is: **we have no good in-band mitigation for a true process freeze that overlaps talk** - the only levers are reducing the freeze source (P1.2, plus engine-side scene-load work outside this codebase) and accepting that the PLC bridge caps out at ~200 ms by design.

5. **The PLC-bridge pooling (P0.1) buys allocation hygiene, not freeze immunity.** GC was not the dominant freeze cause in either log; do not let pooling create a false sense that the at-scale freezes are addressed.

**Bottom line for the user:** ship P0.2 (per-peer link-aware ceiling, with the corrected per-peer-cut-size implementation) and P0.1 (bridge pooling) and P1.2 (HUD pre-warm) and P1.3 (the measurement harness) - those are sound. Hold P1.1 and anything freeze-detector-shaped until a real 15-player current-build capture confirms the cost is real and on-thread. Raising the ceiling alone is necessary for cause 1 but is not a 15-client scaling strategy.
