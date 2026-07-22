# Isle Proximity Voice — Frontend Changes (2026-07-22)

Self-contained handoff. Two changes: **(A)** position events now carry velocity + a timestamp
for smooth motion, **(B)** a dropped socket no longer wipes your voice state, so reconnect is
instant. Nothing else in the wire flow changed — your existing `SubscribeMutual` / `PeerLeft` /
WebRTC / spatial-audio code all still applies.

---

## A. Position events gained `vx, vy, vz, timestampMs`

Telemetry now arrives at **~1 Hz**. Placing peers at the raw points looks/sounds like a ~1 s
stutter, so each position event now includes a velocity vector to extrapolate between updates.

### New payload shapes

```ts
// isle.PlayerPosition  (was: { userId, x, y, z, yaw })
{ userId: string; x: number; y: number; z: number; yaw: number;
  vx: number; vy: number; vz: number; timestampMs: number }

// isle.SelfPosition    (was: { x, y, z, yaw })
{ x: number; y: number; z: number; yaw: number;
  vx: number; vy: number; vz: number; timestampMs: number }
```

- `vx/vy/vz` — velocity in **UE units/second**, same axes as position (UE: +X fwd, +Y right, +Z up).
- `timestampMs` — server unix-ms the sample was taken. **Do NOT use it as a wall clock** (server↔client
  skew). Stamp each update with your own `performance.now()` on receipt and extrapolate from that.
  Use `timestampMs` only if you want to order/dedupe updates.
- Fields are **appended** — old field order is unchanged. If you ignore the new fields you just get
  today's choppy behaviour; if a server/plugin build sends velocity `0`, extrapolation degrades
  gracefully to "hold at last position."

### Handlers (store, don't reposition on each event)

```ts
let myState = null;                     // { x,y,z,yaw, vx,vy,vz, recvAt }
let myYaw = 0;
const peers = new Map();                // userId -> { source, panner, state }
const pending = new Map();              // userId -> state (arrived before ontrack)

hub.on("isle.SelfPosition", (p) => {
  myState = { ...p, recvAt: performance.now() };
  myYaw = p.yaw;
  setListenerOrientation(p.yaw);        // your existing yaw→listener code, unchanged
});

hub.on("isle.PlayerPosition", (p) => {
  const state = { ...p, recvAt: performance.now() };
  const peer = peers.get(p.userId);
  if (!peer) { pending.set(p.userId, state); return; }   // may arrive before ontrack
  peer.state = state;
});
```

### Extrapolation + one render loop

```ts
const MAX_EXTRAP_MS = 1500;   // coast at most ~1.5 s past the last sample, then hold

function extrapolate(s) {      // s = { x,y,z, vx,vy,vz, recvAt }
  const dt = Math.min(performance.now() - s.recvAt, MAX_EXTRAP_MS) / 1000; // seconds, clamped
  return { x: s.x + s.vx * dt, y: s.y + s.vy * dt, z: s.z + s.vz * dt };
}

// UE(+X fwd, +Y right, +Z up) -> WebAudio(x right, y up, z back) — unchanged from before
function ueToAudio(dxFwd, dyRight, dzUp) { return { x: dyRight, y: dzUp, z: -dxFwd }; }

function reposition(userId) {
  const peer = peers.get(userId);
  if (!peer?.state || !myState) return;
  const me   = extrapolate(myState);
  const them = extrapolate(peer.state);
  const a = ueToAudio(them.x - me.x, them.y - me.y, them.z - me.z);
  peer.panner.positionX.value = a.x;
  peer.panner.positionY.value = a.y;
  peer.panner.positionZ.value = a.z;
}

function tick() {
  if (myState) { setListenerOrientation(myYaw); for (const id of peers.keys()) reposition(id); }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
```

> Keep the listener at the origin (`ctx.listener.positionX/Y/Z = 0`). Use
> `panner.positionX.value = …` inside the loop (instant), or
> `positionX.setTargetAtTime(v, ctx.currentTime, 0.05)` for extra smoothing. Don't also reposition
> on each event — the loop covers it.

When `ontrack` fires and you attach a peer, seed its state from `pending`:

```ts
peers.set(userId, { source, panner, state: pending.get(userId) ?? null });
pending.delete(userId);
```

---

## B. Reconnect is now instant — no movement / rejoin needed

**Before:** any hub disconnect (tab close, app restart, brief network blip under
`withAutomaticReconnect`) tore down your whole voice state server-side. On reconnect the grid was
empty, so you saw **0 nearby players until you physically moved and rejoined**.

**Now:** grid presence is tied to your **in-game character**, not the voice socket. A socket drop
only invalidates your live media (your Cloudflare track). It's cleared server-side only by an
in-game **leave**, an explicit `POST /voice/leave`, or a 2 h inactivity TTL.

### What you do on reconnect

Just re-run the publish flow you already have (new CF session → republish mic `audio` track). The
moment you republish, the backend re-seeds you **immediately**, with no movement required:

- `isle.SubscribeMutual` for every peer currently in your 3×3 block,
- an `isle.PlayerPosition` (with velocity) for each of those peers,
- your own `isle.SelfPosition`.

### While you're disconnected

Peers receive an `isle.PeerLeft` for you (your mic track died) and tear down your node — the same
per-peer teardown you already handle. When you republish they re-subscribe automatically.

> Only a **server restart** truly forgets voice state, and even then the same republish re-drives
> you as soon as the next telemetry places you back in the grid.

---

## Migration checklist

- [ ] Add `vx, vy, vz, timestampMs` to your `isle.PlayerPosition` / `isle.SelfPosition` types.
- [ ] Stamp each update with `performance.now()` on receipt; extrapolate in a `requestAnimationFrame` loop.
- [ ] Remove any logic that required the player to move/rejoin to recover peers after a reconnect —
      just re-run publish and expect a full immediate re-seed.
- [ ] (No change needed) `SubscribeMutual`, `PeerLeft`, renegotiation, and spatial attenuation are unchanged.
