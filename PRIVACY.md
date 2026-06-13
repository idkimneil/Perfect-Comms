# Privacy

Perfect Comms is a proximity voice mod. This describes the network connections it makes and the data they
carry. It is informational, not legal advice.

## Voice and signaling

When you are in a voice room, audio and connection-setup (signaling) traffic flows to the voice server your
client is configured for (a BetterCrewLink-compatible server, or the Interstellar backend) and, for audio,
peer-to-peer over WebRTC to the other players in the room. This carries your microphone audio and the
network metadata (IP address, ICE candidates) inherent to a real-time voice connection. Audio is not stored
by the mod.

## Lobby browser (outbound HTTP)

Opening the in-game Voice Lobby browser issues a `GET` to the public lobby list:

- `https://au-eu.duikbo.at/public_api/games`

The request sends nothing user-identifying beyond the connection itself (your IP, as with any HTTP request)
and receives a list of public lobbies. If you do not open the lobby browser, this request is not made.

## Update check (outbound HTTP)

To tell you when a newer build exists, the mod requests the latest release metadata from (default, or the
configured update URL):

- `https://api.github.com/repos/artriy/Perfect-Comms/releases/latest`
- fallback: `https://perfect-comms-lobbies.edgetel.workers.dev/updates/latest`

The request includes a User-Agent containing the mod version and receives release metadata (version, notes,
download link). No account or personal data is sent.

## Third parties

These endpoints are operated by their respective providers (GitHub, Cloudflare, the lobby-list host) under
their own privacy policies. Perfect Comms is not affiliated with Innersloth, Among Us, BepInEx, MiraAPI,
Reactor, or any supported mods.
