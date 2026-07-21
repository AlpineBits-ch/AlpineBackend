# Isle Proximity Voice — Frontend Integration Guide

Positional ("proximity") voice chat for The Isle. Players who are physically close
**in-game** hear each other; volume/direction follow their in-game positions. Media is
carried by **Cloudflare Calls (SFU)**; the Echo backend only relays signalling and tells
each client whom to hear.

> **Audience:** frontend / client developer. You implement the WebRTC peer connection,
> the SignalR event handlers, and the spatial audio graph. The backend gives you a
> Cloudflare session relay + a stream of "who to subscribe to" and "where they are" events.

---

## 1. Concepts & identities

There are **three** IDs. Do not mix them up.

| ID | Origin | Where you see it |
|----|--------|------------------|
| `userId` | Echo identity (JWT `sub` / NameIdentifier) | Authenticates the WebSocket **and** is the SignalR "user". **Every voice payload identifies peers by `userId`.** This is your key for rendering/mixing peers. |
| `steamId` | Steam / game server | Internal; the game telemetry is keyed by this. You normally don't touch it. |
| `playerId` | Isle `Player` aggregate id (e.g. `player_ab12…`) | Internal only — **not used on the voice wire.** |

The backend keys the whole voice subsystem by `userId` and maps `userId ↔ steamId` for you.
A player must be **fully linked** (has both `userId` and a non-empty `steamId`) to use voice.

**Audibility model — grid cells, not a radius.** The world is divided into square voice
cells of **3000 Unreal units = 30 m** (`VoiceGridConfig.CellSize`). You hear everyone in
**your current cell**. This is a grid, not a smooth radius: two players 1 m apart but on
opposite sides of a cell boundary will **not** hear each other, and everyone in the same
30 m cell is audible regardless of exact distance. Use position data for volume/panning
*within* that set; membership itself is cell-based.

---

## 2. Coordinate system

- Units: **centimetres** (1 Unreal unit = 1 cm). 30 m cell = 3000 units.
- Unreal Engine is **left-handed, Z-up**: `+X` forward, `+Y` right, `+Z` up.
- Positions arrive as absolute world coordinates `(x, y, z)`.
- **`yaw`** is in-game facing in **degrees** (Unreal convention, `0` = +X / north).
- A position event is broadcast when the player moves **> 25 units (0.25 m)**
  (`MovementEpsilon`) **or** turns **> 4°** (`YawEpsilon`) — so both movement and pure
  rotation update spatial audio, but events are throttled, not continuous.
- `yaw` is only meaningful if the game plugin reports rotation in its stats stream; when it
  doesn't, `yaw` is `0` (distance attenuation still works; directional panning won't).

---

## 3. Connection & auth

Everything goes through the **Echo gateway** (single origin). REST is reverse-proxied to
the Isle service; the WebSocket hub is terminated at the gateway.

- **REST base:** `https://<gateway-host>` (all endpoints below are gateway paths).
- **Auth:** Bearer JWT. For REST use the `Authorization: Bearer <jwt>` header.
- **WebSocket hub:** SignalR at `/api/v1/ws/hub`. Browsers can't set headers on a WS
  handshake, so pass the token as a query param: `?access_token=<jwt>`.

```ts
import { HubConnectionBuilder } from "@microsoft/signalr";

const hub = new HubConnectionBuilder()
  .withUrl(`${GATEWAY}/api/v1/ws/hub`, { accessTokenFactory: () => jwt })
  .withAutomaticReconnect()
  .build();
await hub.start();
```

The hub is a **single shared connection** for all realtime features (chat, guild, voice).
Don't open a second one for voice — just add handlers.

---

## 4. REST API

All endpoints require `Authorization: Bearer <jwt>`. Base path `/api/v1/isle/voice`.

### Membership

| Method | Path | Body | Response |
|--------|------|------|----------|
| `POST` | `/join` | — | `204 No Content` |
| `POST` | `/leave` | — | `204 No Content` |
| `GET`  | `/status` | — | `{ isGameConnected, isVoiceConnected }` |

- **`join`** opts you into proximity voice (registers your `steamId↔userId`). Call this
  **first**, before opening a Cloudflare session. `400` if your player isn't fully linked.
  You are not placed in any cell until the game reports your position (see §7).
- **`leave`** removes you from voice and the spatial grid.
- **`status`** — `isGameConnected`: your character is online in-game; `isVoiceConnected`:
  you're registered for voice. Poll this for UI state.

### Cloudflare signalling relay

Base path `/api/v1/isle/voice/cf`. These proxy to Cloudflare Calls and (for your mic)
register your track so peers can pull it.

| Method | Path | Body | Response |
|--------|------|------|----------|
| `POST` | `/session` | — | `{ cfSessionId }` |
| `POST` | `/tracks/new` | `TracksNewBody` | `TracksNewResponse` |
| `PUT`  | `/renegotiate` | `RenegotiateBody` | `{ sessionDescription }` |
| `PUT`  | `/tracks/close` | `CloseTracksBody` | `204 No Content` |

JSON is **camelCase**. Shapes:

```ts
// SDP wrapper (matches RTCSessionDescription)
type SessionDescription = { type: "offer" | "answer"; sdp: string };

// A track in a tracks/new request
type TrackNew = {
  location: "local" | "remote";
  mid?: string;        // local: the transceiver mid from your offer
  trackName?: string;  // local: "audio"; remote: the peer's track name
  sessionId?: string;  // remote ONLY: the peer's cfSessionId
};

// POST /cf/tracks/new
type TracksNewBody = {
  cfSessionId: string;                 // YOUR session
  sessionDescription: SessionDescription;
  tracks: TrackNew[];
};

type TracksNewResponse = {
  sessionDescription: SessionDescription;      // answer (publish) or offer (subscribe)
  tracks: { mid: string; trackName: string; sessionId?: string; error?: string }[];
  requiresImmediateRenegotiation: boolean;     // if true -> do the renegotiate step
};

// PUT /cf/renegotiate
type RenegotiateBody = { cfSessionId: string; sessionDescription: SessionDescription };

// PUT /cf/tracks/close
type CloseTracksBody = { cfSessionId: string; trackNames: string[] };
```

> The server registers your published mic under the track name **`"audio"`**. Publishing an
> `audio` track is what makes you audible and triggers subscription of your current
> roommates. (Non-audio local tracks are relayed but not part of proximity voice.)

---

## 5. WebSocket events (server → client)

Register these on the shared hub. **You do not call any hub methods for proximity voice** —
positions come from the game server, so the flow is entirely server-push + REST. Payloads
are camelCase.

| Event | Payload | Meaning |
|-------|---------|---------|
| `isle.SubscribeMutual` | `{ targetUserId, cfSessionId, trackName }` | Pull this peer's audio. `cfSessionId`+`trackName` locate their remote track. Fires when you enter a shared cell (and they're already publishing) and when someone new starts publishing in your cell. |
| `isle.SelfPosition` | `{ x, y, z, yaw }` | **Your own** position + facing. This is your listener origin — store it and re-place all peers whenever it changes. |
| `isle.PlayerPosition` | `{ userId, x, y, z, yaw }` | A peer in your cell moved/turned. Update that peer's spatial position. |
| `isle.UnsubscribeAll` | `{ cellId, trackIds }` | **You** left a cell — tear down remote pulls for that cell. (`trackIds` is currently empty; see §8.) |

```ts
hub.on("isle.SubscribeMutual", (p: { targetUserId: string; cfSessionId: string; trackName: string }) =>
  subscribeToPeer(p.targetUserId, p.cfSessionId, p.trackName));

hub.on("isle.SelfPosition", (p: { x: number; y: number; z: number; yaw: number }) =>
  updateSelfPosition(p.x, p.y, p.z, p.yaw));

hub.on("isle.PlayerPosition", (p: { userId: string; x: number; y: number; z: number; yaw: number }) =>
  updatePeerPosition(p.userId, p.x, p.y, p.z /* p.yaw available if you model peer facing */));

hub.on("isle.UnsubscribeAll", (_p: { cellId: string; trackIds: string[] }) =>
  tearDownAllRemotePeers());
```

> Peers are keyed by **`userId`** throughout. `isle.SelfPosition` and `isle.PlayerPosition`
> both piggyback on the same throttled movement/turn events (§2), so your own position and
> peers' positions arrive on the same cadence.

---

## 6. WebRTC flow (Cloudflare Calls)

You maintain **one** `RTCPeerConnection` for the whole voice session. Cloudflare is the SFU;
you push your mic once and pull each peer as instructed.

### 6.1 Setup + publish your mic

```ts
const pc = new RTCPeerConnection({
  iceServers: [{ urls: "stun:stun.cloudflare.com:3478" }], // add TURN if you need it
  bundlePolicy: "max-bundle",
});

// 1) get a session
const { cfSessionId } = await api.post("/api/v1/isle/voice/cf/session");

// 2) add mic, offer
const mic = await navigator.mediaDevices.getUserMedia({ audio: true });
const tx = pc.addTransceiver(mic.getAudioTracks()[0], { direction: "sendonly" });
await pc.setLocalDescription(await pc.createOffer());

// 3) push local track "audio"
const res = await api.post("/api/v1/isle/voice/cf/tracks/new", {
  cfSessionId,
  sessionDescription: { type: "offer", sdp: pc.localDescription!.sdp },
  tracks: [{ location: "local", mid: tx.mid!, trackName: "audio" }],
});

// 4) apply Cloudflare's answer
await pc.setRemoteDescription(res.sessionDescription); // {type:"answer", sdp}
```

After step 3 you are audible; the backend will push `isle.SubscribeMutual` to the peers
already in your cell.

### 6.2 Subscribe to a peer (on `isle.SubscribeMutual`)

```ts
async function subscribeToPeer(peerUserId, peerSessionId, peerTrackName) {
  const res = await api.post("/api/v1/isle/voice/cf/tracks/new", {
    cfSessionId,                                  // YOUR session
    sessionDescription: { type: "offer", sdp: (await pc.createOffer()).sdp }, // see note
    tracks: [{ location: "remote", sessionId: peerSessionId, trackName: peerTrackName }],
  });

  // Pulling a remote track requires renegotiation: Cloudflare returns an OFFER.
  if (res.requiresImmediateRenegotiation) {
    await pc.setRemoteDescription(res.sessionDescription); // offer
    await pc.setLocalDescription(await pc.createAnswer());
    await api.put("/api/v1/isle/voice/cf/renegotiate", {
      cfSessionId,
      sessionDescription: { type: "answer", sdp: pc.localDescription!.sdp },
    });
  }

  // map the resolved mid -> peerUserId so ontrack can be attributed
  for (const t of res.tracks) if (!t.error) midToUser.set(t.mid, peerUserId);
}
```

> **Attributing incoming audio:** `pc.ontrack` fires with `event.transceiver.mid`. Look up
> `midToUser.get(mid)` to know which `userId` the stream belongs to, then route it into
> that peer's spatial node (§7). Keep a `Map<userId, { audioEl, panner, source }>`.

```ts
pc.ontrack = (e) => {
  const userId = midToUser.get(e.transceiver.mid!);
  if (userId) attachPeerStream(userId, new MediaStream([e.track]));
};
```

### 6.3 Teardown

- On `isle.UnsubscribeAll` or leaving voice, stop rendering peers and
  `PUT /cf/tracks/close { cfSessionId, trackNames: ["audio"] }`, then `POST /voice/leave`.
- On reconnect (incl. after a **server restart** — the backend forgets all voice state and
  will re-drive you), re-run §6.1; the backend rebuilds your proximity list and re-emits
  `isle.SubscribeMutual` for your current cell.

---

## 7. Spatial audio

Use the **Web Audio API**. Each remote peer gets its own `PannerNode`; the single
`AudioContext.listener` is *you*.

```
MediaStream (peer) → MediaStreamAudioSourceNode → PannerNode → AudioContext.destination
```

```ts
const ctx = new AudioContext();
let myPos: { x: number; y: number; z: number } | null = null;
let myYaw = 0;

function attachPeerStream(userId, stream) {
  // NOTE: some browsers need the stream also attached to a muted <audio> element to pump.
  const source = ctx.createMediaStreamSource(stream);
  const panner = new PannerNode(ctx, {
    panningModel: "HRTF",
    distanceModel: "inverse",
    refDistance: 300,      // 3 m: full volume within this
    maxDistance: 3000,     // 30 m: cell size — inaudible beyond
    rolloffFactor: 1,
  });
  source.connect(panner).connect(ctx.destination);
  peers.set(userId, { source, panner, pos: pending.get(userId) ?? null });
  pending.delete(userId);
  reposition(userId);
}
```

### 7.1 Placing a peer

The listener (you) stays at the origin; peers are placed **relative to you**. Feed your own
position/facing from `isle.SelfPosition` and each peer's from `isle.PlayerPosition`.
Web Audio is **right-handed, Y-up, −Z forward** — different from Unreal, so map the axes.

```ts
// UE(+X fwd, +Y right, +Z up)  ->  WebAudio(x right, y up, z back)
function ueToAudio(dxFwd, dyRight, dzUp) {
  return { x: dyRight, y: dzUp, z: -dxFwd };
}

// from isle.SelfPosition
function updateSelfPosition(x, y, z, yaw) {
  myPos = { x, y, z };
  myYaw = yaw;
  setListenerOrientation(yaw);
  for (const id of peers.keys()) reposition(id);   // your move re-places everyone
}

// from isle.PlayerPosition
function updatePeerPosition(userId, x, y, z) {
  const peer = peers.get(userId);
  if (!peer) { pending.set(userId, { x, y, z }); return; } // may arrive before ontrack
  peer.pos = { x, y, z };
  reposition(userId);
}

function reposition(userId) {
  const peer = peers.get(userId);
  if (!peer?.pos || !myPos) return;
  // relative vector in UE space
  const dFwd = peer.pos.x - myPos.x;
  const dRight = peer.pos.y - myPos.y;
  const dUp = peer.pos.z - myPos.z;
  const a = ueToAudio(dFwd, dRight, dUp);
  peer.panner.positionX.value = a.x;
  peer.panner.positionY.value = a.y;
  peer.panner.positionZ.value = a.z;
}
```

Keep the listener at the origin (`ctx.listener.positionX/Y/Z = 0`).

### 7.2 Orientation (directional panning)

`isle.SelfPosition.yaw` gives your facing in **degrees**. Convert to radians and set the
listener forward vector:

```ts
function setListenerOrientation(yawDeg) {
  const yaw = (yawDeg * Math.PI) / 180;          // degrees -> radians
  ctx.listener.forwardX.value = Math.sin(yaw);   // = +Y (right) component
  ctx.listener.forwardZ.value = -Math.cos(yaw);  // = -X (fwd)  component
  ctx.listener.upX.value = 0; ctx.listener.upY.value = 1; ctx.listener.upZ.value = 0;
}
```

If the game isn't reporting rotation, `yaw` stays `0`: distance attenuation still works, but
panning won't track which way you're looking (see §8).

### 7.3 Simpler fallback (distance-only)

If HRTF/panning is more than you need, drive a `GainNode` from distance instead:

```ts
const dist = Math.hypot(dFwd, dRight, dUp);       // cm
const gain = Math.max(0, 1 - dist / 3000);        // linear fade to 0 at 30 m
gainNode.gain.value = gain;
```

---

## 8. Known limitations

The signalling, identity, self-position and yaw paths are all wired. Two things to be aware of:

1. **Yaw depends on the game plugin.** The server forwards `yaw` from the game's stats
   stream (`StatsSnapshot.Rot.Yaw`). If a given server/plugin build doesn't emit rotation in
   its stats, `yaw` arrives as `0` — distance attenuation is unaffected, but directional
   panning (§7.2) will be inert until the plugin reports rotation. Build for `yaw === 0`
   gracefully (fixed forward).
2. **No explicit "peer left my cell" event.** Only the *leaver* gets `isle.UnsubscribeAll`;
   the people they walked away from are not notified. Mitigate client-side: drop/fade a peer
   whose `isle.PlayerPosition` has gone stale (no update for a few seconds) or on hub
   disconnect. (When you yourself change cell you still get `isle.UnsubscribeAll` for the old
   cell followed by fresh `isle.SubscribeMutual` for the new one.)

> **Resolved since the first draft:** you now receive your own position (`isle.SelfPosition`),
> `yaw` is delivered on both position events, peers are keyed by `userId`, and server→client
> events address the correct connection (`Clients.User(userId)`).

---

## 9. Happy-path checklist

1. `hub.start()` on the shared `/api/v1/ws/hub` connection; register the four `isle.*` handlers.
2. `POST /voice/join`.
3. `POST /voice/cf/session` → `POST /voice/cf/tracks/new` (local `audio`) → apply answer.
4. Player spawns / moves in-game → backend clusters you → you receive `isle.SubscribeMutual`
   for each roommate → pull each (renegotiate) → `ontrack` → attach to a spatial node.
5. Receive `isle.SelfPosition` (your origin + facing) and `isle.PlayerPosition` (peers) →
   reposition on every update.
6. On cell change you get `isle.UnsubscribeAll` (old cell) then fresh `isle.SubscribeMutual` (new cell).
7. Leaving: `PUT /voice/cf/tracks/close` → `POST /voice/leave` → close the peer connection.
```
