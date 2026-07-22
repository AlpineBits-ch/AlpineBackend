# Isle Proximity Voice — Frontend Integration Guide

Positional ("proximity") voice chat for The Isle. Players who are physically close
**in-game** hear each other; volume/direction follow their in-game positions. Media is
carried by **Cloudflare Calls (SFU)**; the Echo backend only relays signalling and tells
each client whom to hear.

> **Audience:** frontend / client developer. You implement the WebRTC peer connection,
> the SignalR event handlers, and the spatial audio graph. The backend gives you a
> Cloudflare session relay + a stream of "who to subscribe to" and "where they are" events.

---

## 0. Migration — what changed (2026-07-22)

Two fixes this round. **One is a wire-contract change you must adopt; the other is server-side
and requires no code change but changes behaviour you should be aware of.**

### 0.a Position events now carry velocity + a timestamp (wire change)

`isle.PlayerPosition` and `isle.SelfPosition` gained four fields: `vx, vy, vz` (velocity in
**UE units/second**) and `timestampMs` (server unix-ms the sample was taken). The game telemetry
now arrives at **~1 Hz**, so placing peers at the raw points looks like a ~1 s stutter. Use the
velocity to **extrapolate (dead-reckon)** between updates — see §7.4. Existing fields are unchanged
and in the same order, so old handlers keep working (they just ignore the new fields and stay
choppy). New shapes:

```ts
// isle.PlayerPosition
{ userId: string; x: number; y: number; z: number; yaw: number;
  vx: number; vy: number; vz: number; timestampMs: number }

// isle.SelfPosition
{ x: number; y: number; z: number; yaw: number;
  vx: number; vy: number; vz: number; timestampMs: number }
```

### 0.b A dropped socket no longer evicts you from the grid

Previously **any** hub disconnect (tab close, app restart, brief network blip under
`withAutomaticReconnect`) tore down your entire voice state server-side — registry entry, cell
membership, everything. On reconnect the grid was empty, so you got **0 nearby players until you
physically moved (re-seeding your cell) and rejoined**. That's fixed: your grid presence is now
tied to your **in-game character**, not the voice socket. A socket drop only invalidates your live
media (your Cloudflare track), so peers get an `isle.PeerLeft` for you and stop pulling a dead
track — but your cell + last position stay warm.

**Result:** on reconnect, re-run the publish flow (§6.1) and the backend **immediately** re-seeds
you — `isle.SubscribeMutual` **and** an `isle.PlayerPosition` for every peer already in range, plus
your own `isle.SelfPosition` — with no need to move first. Your grid state is now cleared only by an
in-game **leave**, an explicit `POST /voice/leave`, or the 2 h inactivity TTL.

### 0.c Checklist

- [ ] Read `vx/vy/vz/timestampMs` off both position events and extrapolate between updates (§7.4).
- [ ] On reconnect, just re-run join + publish (§6.1) — expect a full immediate re-seed, no movement needed.

---

## 0. Migration — what changed (2026-07-21)

This round fixes the two issues you raised (the cell-border cliff and the missing
"peer left" event). **Two things break the wire contract — you must adapt both:**

### 0.1 `isle.UnsubscribeAll` is gone → replaced by `isle.PeerLeft`

The old teardown was cell-scoped and only ever reached the person who *moved*. It's replaced
by a **targeted, per-peer** event that reaches **both** sides of a broken pair:

```ts
// REMOVE this handler:
// hub.on("isle.UnsubscribeAll", (_p) => tearDownAllRemotePeers());

// ADD this one — tear down exactly one peer:
hub.on("isle.PeerLeft", (p: { userId: string }) => tearDownPeer(p.userId));
```

`tearDownPeer(userId)` should: stop pulling that peer's Cloudflare track, disconnect and
drop their `PannerNode`/audio element, and remove them from your `peers` map. Do **not**
tear down everyone — only the named `userId`.

**You can now delete your stale-position fade hack.** The reason you needed it (people you
walked away from kept hearing a ghost of you) is gone: whoever loses you now gets an explicit
`isle.PeerLeft` for you, and vice-versa. A peer whose position simply stopped updating is
**standing still**, not gone — keep rendering them at their last position. (Still tear down
on hub disconnect as a safety net.)

### 0.2 Audibility is now a 3×3 cell block, not a single cell

Previously you only heard people in your *exact* 30 m cell, so two players 1 m apart across a
cell boundary heard nothing. Now the backend subscribes you to your cell **plus the 8
adjacent cells** (a 3×3 block). **Your existing distance attenuation is what actually defines
the audible edge** — it already fades to zero at 30 m (`maxDistance: 3000`), and that cutoff
is now the real range limit in every direction, smoothly.

**No code change is required for this** if your panner/gain already fades out by 30 m (§7) —
you'll simply start receiving `isle.SubscribeMutual` for a few more peers, most of whom are
attenuated to silence until they get close. See §1 for the one invariant that keeps this
cliff-free.

### 0.3 You now get a peer's position immediately on subscribe

When someone becomes audible you receive one `isle.PlayerPosition` for them **right away**
(seeded from their last-known position), instead of waiting for their next movement. This
means a **stationary** peer is placed correctly the moment you subscribe. It's the same
`isle.PlayerPosition` event you already handle — just make sure your handler tolerates a
position arriving for a peer whose `ontrack` hasn't fired yet (the `pending` map in §7 already
does this).

### 0.4 Quick checklist

- [ ] Replace the `isle.UnsubscribeAll` handler with `isle.PeerLeft` (per-peer teardown).
- [ ] Delete any stale-position / idle-timeout fade-out logic used to hide ghosts.
- [ ] Confirm your attenuation reaches 0 at 30 m (`maxDistance: 3000`) — it's now the real edge.
- [ ] Confirm `isle.PlayerPosition` handling tolerates a peer arriving before their track.

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

**Audibility model — a 3×3 cell block as coarse filter, distance as the real edge.** The
world is divided into square voice cells of **3000 Unreal units = 30 m**
(`VoiceGridConfig.CellSize`). The backend subscribes you to everyone in **your cell plus the
8 adjacent cells** (a 3×3 block). That block is only a *coarse membership filter* — the
**actual** audible edge is your client-side distance attenuation, which fades to zero at
30 m (§7). So the set you receive is continuous across cell boundaries, and volume falls off
smoothly by true distance in every direction. Two players 1 m apart across a boundary now
hear each other; a peer 40 m away is subscribed but attenuated to silence.

> **Load-bearing invariant:** this stays cliff-free only because `CellSize` (30 m) **≥** the
> client attenuation radius (`maxDistance` = 3000 = 30 m). A 3×3 block guarantees every peer
> within `CellSize` of any point in your cell is included, and the block's own outer edge is
> always ≥ 30 m away — i.e. already at zero volume — so membership never changes anywhere you
> could actually hear it. **If the backend `CellSize` is ever lowered, or you raise
> `maxDistance` past it, the border cliff comes back.** Keep the two coupled.

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
- **Velocity** `(vx, vy, vz)` is in **UE units/second** (same axes as position), derived
  server-side from the delta between the last two samples. **`timestampMs`** is the server
  unix-ms the sample was taken. Telemetry lands at **~1 Hz**, so extrapolate position from
  velocity between updates for smooth motion (§7.4) rather than snapping to each point.

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
> `audio` track is what makes you audible and triggers subscription of everyone currently in
> your 3×3 block. (Non-audio local tracks are relayed but not part of proximity voice.)

---

## 5. WebSocket events (server → client)

Register these on the shared hub. **You do not call any hub methods for proximity voice** —
positions come from the game server, so the flow is entirely server-push + REST. Payloads
are camelCase.

| Event | Payload | Meaning |
|-------|---------|---------|
| `isle.SubscribeMutual` | `{ targetUserId, cfSessionId, trackName }` | Pull this peer's audio. `cfSessionId`+`trackName` locate their remote track. Fires when they come within your 3×3 block (and are already publishing) and when someone already in range starts publishing. |
| `isle.SelfPosition` | `{ x, y, z, yaw, vx, vy, vz, timestampMs }` | **Your own** position + facing + velocity. This is your listener origin — store it and re-place all peers whenever it changes. Extrapolate with `vx/vy/vz` between updates (§7.4). |
| `isle.PlayerPosition` | `{ userId, x, y, z, yaw, vx, vy, vz, timestampMs }` | A peer's position/facing + velocity. Sent on their movement, **and once immediately when they become audible or you reconnect** (seed for stationary peers — §0.3, §0.b). Update that peer's spatial position and extrapolate with `vx/vy/vz` (§7.4). |
| `isle.PeerLeft` | `{ userId }` | This **one** peer left your earshot (walked out of your 3×3 block, or their voice socket dropped / they left voice). Tear down just their track + spatial node. Reaches **both** sides of the pair. |

```ts
hub.on("isle.SubscribeMutual", (p: { targetUserId: string; cfSessionId: string; trackName: string }) =>
  subscribeToPeer(p.targetUserId, p.cfSessionId, p.trackName));

hub.on("isle.SelfPosition", (p: { x: number; y: number; z: number; yaw: number;
                                  vx: number; vy: number; vz: number; timestampMs: number }) =>
  updateSelfPosition(p));

hub.on("isle.PlayerPosition", (p: { userId: string; x: number; y: number; z: number; yaw: number;
                                    vx: number; vy: number; vz: number; timestampMs: number }) =>
  updatePeerPosition(p));

hub.on("isle.PeerLeft", (p: { userId: string }) =>
  tearDownPeer(p.userId));   // stop pulling their track, drop their panner, remove from peers map
```

> Peers are keyed by **`userId`** throughout. `isle.SelfPosition` and `isle.PlayerPosition`
> both piggyback on the same throttled movement/turn events (§2), so your own position and
> peers' positions arrive on the same cadence — except the one seed `isle.PlayerPosition` you
> get the moment a peer becomes audible.

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
already within your 3×3 block.

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

- On `isle.PeerLeft`, tear down **that one peer** (stop pulling their track, drop their
  spatial node, remove from your `peers` map). Don't touch other peers.
- When **you** leave voice: stop rendering all peers,
  `PUT /cf/tracks/close { cfSessionId, trackNames: ["audio"] }`, then `POST /voice/leave`.
  (Everyone who could hear you gets their own `isle.PeerLeft` for you automatically.)
- On reconnect, re-run §6.1 (new Cloudflare session + republish your mic). The moment you
  republish, the backend re-seeds you from the warm grid: `isle.SubscribeMutual` **and** an
  `isle.PlayerPosition` for every peer currently in your 3×3 block, plus your own
  `isle.SelfPosition` — **no movement required** (§0.b). A socket drop keeps your grid presence
  (it's tied to your in-game character); only a **server restart** actually forgets voice state,
  and even then the same republish re-drives you as soon as the next telemetry places you.

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
    maxDistance: 3000,     // 30 m: THE audible edge. Must stay <= backend CellSize (§1). Silent beyond.
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

// from isle.SelfPosition — store position + velocity, stamped with LOCAL receive time (§7.4)
function updateSelfPosition(p) {
  myState = { ...p, recvAt: performance.now() };
  myYaw = p.yaw;
  setListenerOrientation(p.yaw);
  // repositioning is driven by the render loop (§7.4), not one-shot here.
}

// from isle.PlayerPosition
function updatePeerPosition(p) {
  const peer = peers.get(p.userId);
  const state = { ...p, recvAt: performance.now() };
  if (!peer) { pending.set(p.userId, state); return; } // may arrive before ontrack
  peer.state = state;
}
```

`reposition` and the extrapolation that drives it live in §7.4. Keep the listener at the origin
(`ctx.listener.positionX/Y/Z = 0`).

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

### 7.4 Extrapolation (smooth motion between ~1 Hz updates)

Telemetry lands at **~1 Hz**, so placing peers at the raw points looks and sounds like it jumps
once a second. Each position event now carries a **velocity** `(vx, vy, vz)` in UE units/second;
extrapolate from it every animation frame so motion is continuous and stays close to real time.

The server clock and yours differ, so **don't** trust `timestampMs` as a wall clock — stamp each
update with your own `performance.now()` on receipt (done in §7.1) and extrapolate from that. Use
`timestampMs` only if you want to order/deduplicate updates. Clamp the extrapolation horizon so a
peer who stops sending (stood still, or dropped) coasts a little and then holds, rather than
flying off forever.

```ts
const MAX_EXTRAP_MS = 1500;   // coast at most ~1.5 s past the last sample, then hold

function extrapolate(s) {     // s = { x,y,z, vx,vy,vz, recvAt }
  const dt = Math.min(performance.now() - s.recvAt, MAX_EXTRAP_MS) / 1000; // seconds, clamped
  return { x: s.x + s.vx * dt, y: s.y + s.vy * dt, z: s.z + s.vz * dt };
}

function reposition(userId) {
  const peer = peers.get(userId);
  if (!peer?.state || !myState) return;
  const me   = extrapolate(myState);
  const them = extrapolate(peer.state);
  const a = ueToAudio(them.x - me.x, them.y - me.y, them.z - me.z); // relative vector, UE→WebAudio
  peer.panner.positionX.value = a.x;
  peer.panner.positionY.value = a.y;
  peer.panner.positionZ.value = a.z;
}

// one render loop re-places everyone each frame (both your motion and theirs are extrapolated)
function tick() {
  if (myState) { setListenerOrientation(myYaw); for (const id of peers.keys()) reposition(id); }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
```

> Prefer `panner.positionX.value = …` (instant) inside a per-frame loop, or
> `positionX.setTargetAtTime(…, ctx.currentTime, 0.05)` for a little extra smoothing. Don't also
> reposition on each event — the render loop already covers it. If a build omits velocity (all
> zeros), this degrades gracefully to "hold at last position," i.e. today's behaviour.

---

## 8. Known limitations

The signalling, identity, self-position, yaw, audibility-block and per-peer-teardown paths
are all wired. One thing to be aware of:

1. **Yaw depends on the game plugin.** The server forwards `yaw` from the game's stats
   stream (`StatsSnapshot.Rot.Yaw`). If a given server/plugin build doesn't emit rotation in
   its stats, `yaw` arrives as `0` — distance attenuation is unaffected, but directional
   panning (§7.2) will be inert until the plugin reports rotation. Build for `yaw === 0`
   gracefully (fixed forward).

> **Resolved since earlier drafts:** the border cliff is gone (3×3 block + distance edge, §1);
> you now get an explicit per-peer `isle.PeerLeft` on both sides instead of a mover-only
> `isle.UnsubscribeAll`, so no more ghosts and no stale-fade hack needed; a peer's position is
> seeded immediately on subscribe (§0.3); a **hub disconnect** (tab close / dropped socket) drops
> your live Cloudflare track so peers get `isle.PeerLeft` for you instead of pulling a dead track,
> **but keeps your grid presence** so reconnect re-seeds you instantly (§0.b); position events
> carry **velocity + timestamp** for extrapolation (§0.a, §7.4); you receive your own position
> (`isle.SelfPosition`); `yaw` is delivered on both position events; peers are keyed by `userId`;
> and server→client events address the correct connection (`Clients.User(userId)`).
>
> On **reconnect** just re-run the publish flow (§6.1); the backend re-drives subscriptions and
> re-seeds positions from there — no movement or rejoin needed.

---

## 9. Happy-path checklist

1. `hub.start()` on the shared `/api/v1/ws/hub` connection; register the four `isle.*`
   handlers: `SubscribeMutual`, `SelfPosition`, `PlayerPosition`, `PeerLeft`.
2. `POST /voice/join`.
3. `POST /voice/cf/session` → `POST /voice/cf/tracks/new` (local `audio`) → apply answer.
4. Player spawns / moves in-game → backend clusters you → you receive `isle.SubscribeMutual`
   for each peer in your 3×3 block → pull each (renegotiate) → `ontrack` → attach to a
   spatial node. You also get one seed `isle.PlayerPosition` per peer on subscribe.
5. Receive `isle.SelfPosition` (your origin + facing) and `isle.PlayerPosition` (peers) →
   reposition on every update. Distance attenuation (§7) is the real audible edge.
6. As people move in/out of range you get `isle.SubscribeMutual` (new peer) and
   `isle.PeerLeft` (one peer gone) — apply per-peer, never wholesale.
7. Leaving: `PUT /voice/cf/tracks/close` → `POST /voice/leave` → close the peer connection.
```
