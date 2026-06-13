# Voice Falloff Softness — Design Spec

**Date:** 2026-05-31
**Target release:** v2.0.9
**Type:** Feature (local, per-player setting)

## Problem

When players are near the edge of the audible range — most noticeably when the
host has **Only Hear In Sight** enabled, which squeezes the falloff curve into the
player's in-game vision radius — their voices become very quiet. A teammate who is
clearly *visible* on screen but near the edge of vision can be almost inaudible.

Players want voices to sound near-normal as long as someone is within vision, with
a smooth fade only as that person *approaches the edge* of vision/range — i.e. a
less aggressive fade, perceptibly louder across most of the range, without simply
boosting master volume.

## Goal

Add an opt-in, **per-player (local)** control that reshapes the *interior* of the
proximity fade so voices stay near-normal across most of the audible range and the
fade compresses into the final stretch near the edge. It must **layer on top of any
host configuration** (any falloff curve, occlusion mode, wall blocking, max chat
distance, Only-Hear-In-Sight) and must **never change what a player is allowed to
hear** — only how loud an already-audible player sounds.

## Non-goals

- Not a host/room setting. It does not sync over the network and does not affect
  other players' audio.
- Not a master-volume boost. It is a distance-curve reshape, not a flat gain.
- Does not alter occlusion, role mutes, vent rules, team radio, sight gating, or any
  audibility *gate* — only the proximity volume magnitude within the allowed range.

## Design

### Core transformation

Every audible proximity voice today is computed as:

```
t      = clamp(distance / maxDistance, 0, 1)
volume = ApplyFalloff(t, hostFalloffMode)      // VoiceAudioOcclusion.ApplyFalloff
```

`maxDistance` is already whatever the host allows for this listener: it equals the
player's vision/light radius when Only-Hear-In-Sight is on, otherwise the host's Max
Chat Distance. So reshaping relative to `t` is automatically relative to the correct
envelope.

We insert a single local remap of `t` **before** the host curve runs:

```
softness01 ∈ [0, 1]                  // the slider, 0% .. 100%
γ          = 1 + softness01 * 3      // exponent, γ ∈ [1, 4]   (max-strength 3 is tunable)
t'         = pow(t, γ)
volume     = ApplyFalloff(t', hostFalloffMode)
```

Properties that make this correct and pleasant:

- **Endpoints are pinned.** `t=0 → t'=0` (on top of someone = full volume) and
  `t=1 → t'=1` (the edge still maps to the host curve's silent end). The remap can
  **never** make `volume` non-zero past `maxDistance`, so it cannot extend hearing
  range.
- **Interior pulled toward "loud."** For `γ > 1`, `t' < t` on `(0,1)`, so the host
  curve is evaluated nearer its loud end across most of the range. The host curve's
  own shape still governs the fade — it is just compressed into the last stretch.
- **Smooth edge, no pop.** Because the voice is already near-silent as `t → 1`, the
  host's hard sight cutoff at the boundary (`!inSight || dist > maxDistance ⇒ 0`)
  catches an already-quiet voice, so crossing out of vision stays smooth.
- **Curve-agnostic.** Works with all three host falloff modes (Linear, Smooth,
  Voice Focused); each keeps its characteristic edge shape.

Worked example, host curve = Smooth, softness = 100% (`γ = 4`): full volume out to
~70% of range, smooth fade over the final ~30%, silent exactly at the edge. At
softness = 0% (`γ = 1`) the output is byte-for-byte identical to today.

### Components touched

1. **`Comms/VoiceAudioOcclusion.cs`**
   - New static field `public static float ProximitySoftness01` (default `0f`,
     clamped `[0,1]` on assignment).
   - New helper `public static float SoftenDistance(float t)` returning
     `ProximitySoftness01 <= 0 ? t : MathF.Pow(t, 1f + ProximitySoftness01 * 3f)`.
   - `ApplyFalloff` applies `t = SoftenDistance(t)` immediately after computing
     `t = clamp(distance/maxDistance,0,1)`. This single change covers all six
     `ApplyFalloff` call sites (proximity, impostor-hears-ghost, lobby, medium
     spatial route, camera proxy, virtual-speaker cache) — all of which are
     local-listener-hearing-a-target paths.

2. **`Comms/VoiceProximityCalculator.cs`**
   - `ApplyGhostFalloff` (the one duplicated copy of the falloff math, used for
     dead-player hearing) calls `VoiceAudioOcclusion.SoftenDistance(t)` after
     computing its own `t`, so ghost hearing matches living hearing.

3. **`Comms/VoiceChatLocalSettings.cs`**
   - New `ConfigEntry<float> VoiceFalloffSoftness`, bound in the `Audio` category
     with default `0.30f`, range `[0,1]`, annotated
     `[LocalSliderSetting("Voice Falloff Softness", min:0f, max:1f, displayValue:true, formatString:"0%")]`.
   - In the constructor, after binding, push the persisted value into
     `VoiceAudioOcclusion.ProximitySoftness01` so it is correct at startup.
   - In `OnOptionChanged`, when `configEntry == VoiceFalloffSoftness`, update
     `VoiceAudioOcclusion.ProximitySoftness01` (live-apply, mirroring how Mic
     Volume / Overlay Scale apply immediately).

No changes to `VoiceRoomSettingsSnapshot`, `VoiceRoomControlCodec`,
`VoiceRoomSettingsRpc`, `VoiceChatGameOptions`, or any wire format — the setting is
local only.

### Why this is safe with all host settings

The remap reshapes only the *magnitude* of an already-permitted proximity volume
within `[0, maxDistance]`. Every mechanism that *gates* audibility runs exactly as
before and is untouched: out-of-sight hard cut, wall occlusion / hard block, role
mutes, vent privacy, team radio routing, comms-sabotage disable, and the
`LowVolumeFloor` snap-to-zero. Therefore the feature can never make a
rule-inaudible player audible; it only changes how loud an already-hearable player
sounds.

### Default value

Slider defaults to **30%** for v2.0.9 — a gentle, pleasant softening that is felt
out of the box. Existing players will hear visible teammates a bit louder/flatter
after updating, and can dial the slider down to 0% to restore the exact previous
behavior.

## Testing

`PerfectComms.Tests` (the existing test project) gains unit coverage:

- **Off = identity:** with `ProximitySoftness01 = 0`, `ApplyFalloff` output matches
  the current implementation across sampled distances for all three host modes.
- **Endpoints pinned:** for any softness in `[0,1]` and any host mode, `t=0` yields
  full volume and `t=1` yields `0`.
- **Monotonic louder:** at a fixed mid-range `t` (e.g. 0.6), output volume is
  non-decreasing as softness increases from 0 → 1.
- **Ghost parity:** `ApplyGhostFalloff` with softness produces the same softening
  as `ApplyFalloff` for equivalent inputs.

## Release note

Add one line to `docs/release-notes-v2.0.9.md`:

> **New — Voice Falloff Softness:** a personal slider that keeps teammates clear
> across your vision, with the fade only near the edge. Off (0%) matches the old
> behavior.
