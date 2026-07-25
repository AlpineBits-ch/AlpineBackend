# Proximity voice range change: 30 m → 50 m — frontend guide

The backend audible range was raised from **30 m to 50 m**. Server-side this was a
one-line change: `VoiceGridConfig.CellSize` is now `5000f` (50 m in UE units / cm),
registered in `Isle.Application/Program.cs`. That only widens the *coarse membership
filter* — the backend now subscribes you to peers in a larger 3×3 block. **The actual
audible edge is still owned by your client-side distance attenuation**, so nothing
changes for players until the frontend is updated too.

## What you must change

Bump the `PannerNode` `maxDistance` from `3000` to `5000` wherever peer streams are
attached (see the reference impl in `isle-proximity-voice-frontend-guide.md` §7):

```diff
  const panner = new PannerNode(ctx, {
    panningModel: "HRTF",
    distanceModel: "inverse",
    refDistance: 300,      // 3 m: full volume within this (unchanged)
-   maxDistance: 3000,     // 30 m: audible edge
+   maxDistance: 5000,     // 50 m: audible edge. Must stay <= backend CellSize.
    rolloffFactor: 1,
  });
```

If `maxDistance` is stored in a shared constant, just change the constant.

## The load-bearing invariant (don't break it)

`maxDistance` (client) **must stay ≤ `CellSize` (backend)**. Both are now 50 m, so the
invariant holds. If you raise `maxDistance` past 5000, peers past the 3×3 block edge
won't be subscribed and you'll get a hard audio cliff at cell borders. Keep the two
coupled — if the backend range ever changes again, change `maxDistance` to match.

## Optional tuning

- `refDistance` (full-volume radius, currently 3 m) and `rolloffFactor` are unchanged;
  the falloff curve keeps its shape and just extends further out. Adjust only if you
  want a different attenuation feel at the new range.
- Update any in-UI copy / settings that mention "30 m" voice range.

## Testing

Two players between 30 m and 50 m apart should now hear each other (previously silent),
fading to silence approaching 50 m, with no volume jump when either crosses a cell
boundary.
