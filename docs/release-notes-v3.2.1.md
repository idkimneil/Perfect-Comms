Perfect Comms v3.2.1 adds a Unity Audio compatibility mode for players whose normal audio path does not work, and fixes deafening.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v3.2.1/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **Unity Audio compatibility mode (new).**
  > <sub>A new "Use Unity Audio" toggle in Audio settings routes microphone capture and playback through Unity's own audio engine instead of the BASS path, for players whose voice does not work otherwise (some Wine/CrossOver setups and unusual audio devices). It is a fallback: it has a little more delay and runs without noise suppression, echo cancellation, or auto mic gain, since those need the BASS path. Enabling it switches those three off automatically and turns them back on when you disable it.</sub>

- **Deafening is fixed.**
  > <sub>Deafening had no effect: you would still hear everyone after deafening. It now properly silences all incoming voice.</sub>
