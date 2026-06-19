Perfect Comms v3.2.0 rebuilds the voice processing chain around the same battle-tested WebRTC audio engine that powers Chrome and Google Meet: real AEC3 echo cancellation and AGC2 automatic gain, with DeepFilterNet still handling noise suppression. Text-to-speech and other line-in audio now come through continuously instead of cutting out, and the whole mod is lighter on memory and more crash-resistant.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v3.2.0/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **Real echo cancellation (WebRTC AEC3).**
  > <sub>The Echo Cancellation toggle now runs WebRTC's AEC3, the same canceller used by Chrome and Google Meet, replacing the older Speex one. It properly removes speaker bleed for players who don't use headphones, including the harsh echo from cheap laptop speakers that the old canceller couldn't touch.</sub>

- **Smarter automatic gain (WebRTC AGC2).**
  > <sub>Auto Mic Gain now uses WebRTC's AGC2, which raises your level only while you're actually talking, so quiet mics are boosted cleanly without pumping up background noise during pauses.</sub>

- **Text-to-speech and line-in no longer cut out.**
  > <sub>Synthetic or already-clean audio (TTS, virtual cables, line-in) used to get chopped at the silent gaps between words. The mic now transmits continuously, so that audio comes through clean and uninterrupted, with no setting to flip.</sub>

- **Cleaner noise suppression.**
  > <sub>DeepFilterNet noise suppression is now capped so it removes noise without ever fully erasing quiet or steady speech, fixing voices that could sound thin or garbled in some conditions.</sub>

- **Lighter and smoother.**
  > <sub>Cut the mod's per-frame memory allocations dramatically, reducing the tiny stutters garbage collection can cause, and fixed a memory leak that slowly grew across a play session.</sub>

- **More stable.**
  > <sub>Closed several rare crash paths (malformed network messages, audio-thread and timer errors, and exceptions inside game patches) so they now log and recover instead of taking down the game, plus a few thread-safety fixes that matter most on 32-bit clients.</sub>

- **Fails gracefully.**
  > <sub>If a native audio component can't load, voice now degrades cleanly instead of breaking, and a couple of game-version-sensitive lookups warn once and keep going instead of silently breaking on an Among Us update.</sub>
