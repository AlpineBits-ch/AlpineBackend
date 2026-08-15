# Entitlements and degradation - frontend integration guide

Everything a venta client needs to say "this worked, but you got less than you asked for, and here is
who can change that" instead of failing silently or refusing outright.

Applies to Alpine (desktop and web), venta-mobile and the bots portal. Design documents:
[monetization.md](./monetization.md) sections 3.3 and 4, execution plan
[monetization-implementation-plan.md](./monetization-implementation-plan.md) WP-09. The client-side
survey this contract was written from is `docs/contracts/entitlements-client-requirements.md` in the
Alpine repository.

## URLs in this document

**Every URL is a public gateway URL (`https://api.venta.gg`) and is written out in full.** The
entitlement endpoints are authenticated with the ordinary bearer token, on the ordinary gateway
origin, through the ordinary interceptor stack.

## Status of this contract

The contract is settled; not all of it is implemented on the server yet. Build against all of it, and
treat the "planned" rows as absent-until-present rather than as a reason to design differently.

| Section | Server state |
|---|---|
| §2 value shapes, §3 degradation object, §4 denial object | Shipped as types every service links |
| §5 `GET /entitlements/me`, `GET /entitlements/guilds/{id}` | Shipped |
| §7 `entitlements.Changed` | Shape and routing shipped; nothing changes entitlements until Billing exists, so it does not fire yet |
| `version` on the snapshot | Always `0` until Billing owns a per-subject counter. Compare it anyway |
| §6 usage endpoints | Planned. Owned by the services that do the counting |
| §8 voice `limits` block | Shipped. It rides every voice snapshot, and `degradations[]` rides the join reply in both room kinds |
| §8.1 publish enforcement and the `video` field | Shipped. Both room kinds. `degradations[]` on the negotiate reply, `403` for the two refusals |
| §8.2 `video` on the renegotiation | Shipped. Both room kinds. Never refuses and never changes the response body; omitting it leaves your recorded ceiling untouched |
| §10 guild feature resolution | Shipped, on `GET /guilds/{guildId}` as `featureResolution` and on `GET /guilds/{guildId}/features`. **Not** on the guild list, and not on a nested guild - see §10 |
| §5.5 `plan` on the snapshot | Shipped, with `currentVersion` beside `version`. Absent on `selfhost`, and on an instance with no plans or no configured default for that kind of subject. On a hosted instance with Billing deployed the plans come from Billing's table, so it is present for everybody |
| §5.6 `stripePublishableKey` | Shipped. Absent unless the instance was configured with one |

---

## 1. The one thing that will surprise you

**A degradation is a `200`.** It carries the normal response body, unchanged and complete, plus a
`degradations` array.

```jsonc
{
  // ... exactly the body you already parse, byte for byte ...
  "degradations": [ /* §3 */ ]
}
```

The rule this comes from is [monetization.md](./monetization.md) §3.3: *degrade, do not deny*. The
11th member of a full free voice room gets an audio-only seat, not "room full". The publisher who
asked for 1080p60 in a 720p30 guild publishes 720p30. The action **succeeded**, and the array is how
you learn it succeeded smaller.

Three consequences for the client:

1. **Do not roll back.** An error status would make every existing call path treat this as a failed
   join or a failed publish, which is a denial with extra steps and defeats the whole design.
2. **`degradations` is absent when nothing was reduced.** Absent and empty mean the same thing. A
   response with nothing reduced is byte-identical to what a v1 client already receives, which is why
   this is safe to add to responses that are years old.
3. **Render it at the call site that caused it.** It rides the response the caller already holds
   precisely so there is a component with the context to say "you are sharing at 720p because this
   server is on the free plan".

Hard refusals are the small reserved set that cannot degrade (the 51st emoji, the oversized upload,
the 6th bot). They are a `403` and they use the same field names and the same codes: §4.

---

## 2. Values on the wire

Every entitlement value, everywhere in this document, is one of three objects. Switch on `kind`
before reading anything else.

```jsonc
{ "kind": "numeric", "value": 26214400, "unlimited": false }
{ "kind": "numeric", "value": null,     "unlimited": true  }
{ "kind": "flag",    "granted": true }
{ "kind": "ladder",  "rung": "720p30", "rank": 2, "ladder": "video_quality" }
```

**Read `unlimited` before `value`.** `value` is `null` exactly when `unlimited` is `true`, and the
null is written explicitly so that "no ceiling" and "the server forgot the field" are different
bytes.

**The server will never send a sentinel.** Internally "no ceiling" is `long.MaxValue`
(9223372036854775807), which is larger than JavaScript's `Number.MAX_SAFE_INTEGER`
(9007199254740991) and would be silently corrupted by every JS client. It is converted at the edge
and the serializer throws rather than write it. `-1` is not used either. If you ever see a number
above `Number.MAX_SAFE_INTEGER` in one of these payloads, that is a server bug worth reporting rather
than something to work around.

`rank` is the position on `ladder`, ascending, and is comparable only against another rank of the
same ladder. `rung` is the name. Both are sent because you need the name for copy and the rank for
comparisons.

---

## 3. The degradation object

```jsonc
{
  "key": "voice.video_ceiling",
  "requested": { "kind": "ladder", "rung": "1080p60", "rank": 4, "ladder": "video_quality" },
  "granted":   { "kind": "ladder", "rung": "720p30",  "rank": 2, "ladder": "video_quality" },
  "reason": "guild_plan_limit",
  "boundBy": "guild",
  "remedy": "upgrade_guild",
  "actorCanRemedy": false,
  "subject": { "kind": "guild", "id": "guild-1" }
}
```

`requested` and `granted` are always the same shape as each other and as the key's declared kind.

### 3.1 Four fields, four different questions

Do not derive any of these from any other. The mapping between them is not fixed, and three of the
four need knowledge the client does not have.

| Field | Question | Who can answer |
|---|---|---|
| `reason` | Which side bound | Server |
| `boundBy` | Which side of a pair it was | Server. The client cannot see the other side's plan |
| `remedy` | What would fix it | Server. It knows whether this instance sells anything at all |
| `actorCanRemedy` | Can **this** caller do that | Server. It resolves ManageGuild per request |

`subject` is the party the remedy applies to, so a call to action can be linked at the right guild or
account. For a paired ceiling it is the side named by `boundBy`.

### 3.2 The reason vocabulary

**Closed, versioned, snake_case.** Every code is a translation key in three clients and four locale
files, so adding one is a coordinated release and renaming one is a data migration. The current
vocabulary version is **1** and is echoed on every entitlement snapshot as `vocabularyVersion`.

| `reason` | Means | Typical `remedy` |
|---|---|---|
| `guild_plan_limit` | The guild's plan is the binding constraint | `upgrade_guild` |
| `user_plan_limit` | The member's own plan is the binding constraint | `upgrade_user` |
| `paired_ceiling` | Both sides carry a ceiling and the lower won | Depends on `boundBy` |
| `operator_ceiling` | The instance operator's own cap. Not a commercial limit | `none`, always |

`boundBy` is `"guild"` or `"user"`. It is **always present for `paired_ceiling`**, is present for the
two single-sided reasons as well, and is **absent for `operator_ceiling`**, which is not a party
anybody can upgrade.

`paired_ceiling` splits into two sentences in your string table, driven by `boundBy`. That is the
whole reason the field exists: without it you would eventually tell a paying Venta Plus member that
their own plan limited them, which is the exact error the paired rule was built to prevent.

### 3.3 The remedy vocabulary

| `remedy` | Draw |
|---|---|
| `upgrade_guild` | "Upgrade this server" when `actorCanRemedy`, otherwise a sentence naming that an admin can |
| `upgrade_user` | "Upgrade your account" |
| `boost_guild` | Reserved. Nothing emits it yet |
| `none` | A sentence and no button, ever |

`remedy: "none"` always comes with `actorCanRemedy: false`. It is what you get for an operator ceiling
and for **every** limit on an instance that sells nothing, which includes every self-hosted instance
(§5.3).

### 3.4 The unknown-code rule, which you must implement

A client built today will eventually receive a code added tomorrow. When `reason` is not in the table
above:

- Render the generic sentence `ENTITLEMENT.REASON.UNKNOWN`.
- **Suppress the button entirely**, whatever `remedy` says. A remedy you do not understand is a
  remedy you should not offer.

The same applies to an unrecognised `remedy` and to an unrecognised `boundBy`: fall back to the
generic sentence with no call to action. Never render a raw code, and never render the key of a
missing translation.

Hold your reason-to-key mapping as an exhaustive lookup table of **literal** translation keys, and
add a spec asserting every entry resolves. A computed key like `'ENTITLEMENT.REASON.' + code` escapes
Alpine's `i18n-keys.spec.ts` guard entirely, so it renders raw to a user with every test green.

---

## 4. Hard denials

The three refusals that cannot degrade answer **`403`** with:

```jsonc
{
  "code": "guild_plan_limit",         // same vocabulary as `reason`, and the same value
  "key": "guild.emoji_slots",
  "requested": { "kind": "numeric", "value": 51, "unlimited": false },
  "granted":   { "kind": "numeric", "value": 50, "unlimited": false },
  "reason": "guild_plan_limit",
  "boundBy": "guild",
  "remedy": "upgrade_guild",
  "actorCanRemedy": true,
  "subject": { "kind": "guild", "id": "guild-1" },
  "retryable": false
}
```

- **The code field is called `code`.** There is not a second name for it, and this does not add one.
- `code` and `reason` are always equal. Branch on either; one lookup table serves refusals and
  degradations both.
- `requested` and `granted` are **absent** when what was refused has no countable ceiling, which today
  is only an out-of-plan module (§10). Those carry `"feature": "Forums"` instead.
- `retryable` is always `false` here. Retrying an entitlement refusal turns one refusal into three.

### 4.1 Two status codes that must never be used, and why

- **Never `429`.** Alpine's global rate-limit interceptor retries any `429` three times with backoff
  and hands on a generic error with the body long gone. An entitlement rejection sent this way is
  invisible and up to 30 seconds late.
- **Never `401`.** The logout interceptor signs the user out on any `401`.

`403` was chosen over `402 Payment Required` because it matches the machine-readable refusal
precedent the clients already implement, and because `402` would be a lie for `operator_ceiling`,
which is not about payment and is the code a self-hosted instance emits.

### 4.2 There is no server-written copy

There is no `message` field on a degradation and none on a denial. The codes are translation keys and
the client owns every sentence. This is deliberately the opposite of the status page, where the
server does write the sentence: an outage notice must not be machine-translated, whereas "this server
is on the free plan" must be.

---

## 5. Reading your own entitlements

### 5.1 The two endpoints

```http
GET https://api.venta.gg/api/v1/entitlements/me
GET https://api.venta.gg/api/v1/entitlements/guilds/{guildId}
```

Both return the same shape. Both are authenticated. The guild one is **members only** and answers
`404` for a guild you are not in, the same as for a guild that does not exist.

```jsonc
{
  "licenseMode": "hosted",              // or "selfhost"
  "upgradesAvailable": true,            // §5.3
  "vocabularyVersion": 1,
  "subject": { "kind": "user", "id": "user-1" },
  "resolvedAt": "2026-08-14T10:00:00Z",
  "version": 7,
  "ttlSeconds": 60,

  // §5.5, may be absent. version < currentVersion means grandfathered.
  "plan": { "name": "plus", "displayName": "Venta Plus", "version": 2, "currentVersion": 3 },
  "stripePublishableKey": "pk_live_...",                                   // §5.6, may be absent

  "entitlements": {
    "user.upload_max_bytes": { "kind": "numeric", "value": 26214400, "unlimited": false },
    "user.max_devices":      { "kind": "numeric", "value": 5,        "unlimited": false },
    "voice.video_ceiling":   { "kind": "ladder",  "rung": "1080p30", "rank": 3, "ladder": "video_quality" }
  },

  "ladders": {
    "video_quality": [
      { "rung": "none",    "rank": 0, "maxHeight": 0,    "maxFramerate": 0  },
      { "rung": "480p30",  "rank": 1, "maxHeight": 480,  "maxFramerate": 30 },
      { "rung": "720p30",  "rank": 2, "maxHeight": 720,  "maxFramerate": 30 },
      { "rung": "1080p30", "rank": 3, "maxHeight": 1080, "maxFramerate": 30 },
      { "rung": "1080p60", "rank": 4, "maxHeight": 1080, "maxFramerate": 60 }
    ]
  },

  "remedy": "upgrade_user",
  "actorCanRemedy": true
}
```

A snapshot carries only the keys its subject can hold: user-scoped keys plus the paired ones on
`/me`, guild-scoped keys plus the paired ones on a guild. `remedy` and `actorCanRemedy` are the same
fields as on a degradation (§3.1) so the upgrade button on a settings screen and the one in a
degradation banner are driven by the same two values.

### 5.2 The key catalogue

| Key | Kind | Scope | What it caps |
|---|---|---|---|
| `voice.max_participants` | numeric | guild | People in one voice room |
| `voice.video_ceiling` | ladder | **paired** | Camera and screenshare publish quality |
| `voice.max_publishers` | numeric | guild | Concurrent video publishers in a room |
| `storage.upload_max_bytes` | numeric | **paired** | Largest single upload into a guild |
| `storage.guild_quota_bytes` | numeric | guild | Total bytes a guild may hold |
| `guild.emoji_slots` | numeric | guild | Custom emoji |
| `guild.bots_installed` | numeric | guild | Installed bots |
| `guild.vanity_url` | flag | guild | Vanity invite. See §11.5 |
| `guild.audit_log_days` | numeric | guild | Audit log window. See §11.3 |
| `user.upload_max_bytes` | numeric | user | Largest upload where no guild is involved |
| `user.max_devices` | numeric | user | Registered devices |

**Paired means the effective value is the lower of the two sides.** A guild snapshot's
`voice.video_ceiling` is what the guild will distribute; your own is what you are allowed to publish;
what you actually get is `min` of the two, computed by the server at the moment you publish. Do not
compute it yourself for anything load-bearing: read what happened from the `degradations` array on
the publish response.

### 5.3 `licenseMode` and `upgradesAvailable`

Read these **before you draw a settings nav**. The clients point at arbitrary instances at runtime,
and a self-hoster shown upgrade buttons has hit a paywall on a product nobody is charging them for.

- `licenseMode` is `"selfhost"` or `"hosted"`. On `selfhost`, every entitlement resolves to maximum
  and there is no billing service deployed at all.
- `upgradesAvailable` is the one to branch on. It is false on `selfhost`, and **also** false on a
  hosted instance whose billing is not configured yet, which is a supported state during rollout.

When `upgradesAvailable` is false: **omit** the billing nav entries and every upgrade call to action,
rather than disabling them. Absence needs no explanation here. Limits still apply and degradations
still arrive; they simply have `remedy: "none"`.

### 5.4 Caching, and the two ways to get it wrong

Cache key is `(baseUrl, accountId, subjectKind, subjectId)`. Alpine switches instance and account at
runtime, so anything less shows one account's limits to another.

- **Honour `ttlSeconds`.** It is `60` today, and it will never be longer than the server's own cache
  backstop. Caching longer than the server does defeats the self-healing that backstop exists to
  provide: an event that got dropped would be repaired on the server and stay broken in your cache.
- **Check `subject` on arrival.** Discard a response whose subject is not the one you are currently
  looking at. Without this a late response is filed against whatever the user has since switched to.
- **Compare `version`** and discard anything older than what you hold. It is `0` on every instance
  until Billing ships a counter; comparing is still correct, and it starts working the day it moves.
- **Never persist an entitlement set to disk.** A stale plan read at cold start would show wrong
  limits before the first fetch lands, and "your server was downgraded" is not a sentence to render by
  accident.

Refetch on: the `entitlements.Changed` push, hub reconnect, account or instance switch, app resume
past the TTL, and when a guild settings screen opens.

### 5.5 `plan`: which plan these numbers came from

```jsonc
{ "name": "plus", "displayName": "Venta Plus", "version": 2, "currentVersion": 3 }
```

This is the only thing on the payload that answers "what am I on", and it is the sentence a settings
screen leads with. Everything else here is a ceiling.

- **`name` is the key, `displayName` is the copy.** Branch on `name`; render `displayName`. The
  display name is never null - a plan an operator gave no display name shows its own name - so you
  never have to pick between two fields.
- **`version` is the version this subject is actually on, not necessarily the current one.** Plans
  are grandfathered: a subject who joined on version 1 keeps version 1's numbers when the plan's
  numbers move. Render it only where a version is meaningful (a plan-change screen, a support form);
  it is not a thing to put next to the plan name on a settings row.
- **`version < currentVersion` means this subject is grandfathered**, and that comparison is the only
  honest way to tell. An unassigned subject is resolved through whatever is current and reports that
  number, so `version` on its own cannot distinguish "held on older terms" from "on the newest
  terms" - which is exactly the sentence a grandfathered subscriber is owed.
- **Both are absent together on an instance whose plans are configuration rather than rows**, which
  has never heard of a plan version at all. Absent is not "version 0". On a hosted instance with
  Billing deployed both are present, including for a subject with no assignment of their own.
- **The whole object is absent when there is no plan**, and that is a real state rather than a gap:
  an instance with no plans configured, or one that configured plans but named no default for that
  kind of subject, resolves every key to its catalogue default, and a `selfhost` instance resolves
  everything to maximum with no billing service deployed at all. **Do not substitute a "Free"**.
  Nobody configured one, and inventing it puts a tier boundary on the screen of a self-hoster who is
  not being charged for anything. Render the limits, and no plan row.
- **An unassigned subject is on the instance's default plan, not on no plan.** Almost nobody holds an
  explicit assignment - the free tier is the state a subject is in rather than one somebody put them
  in - so on an instance that configured a default, the plan object is present for everybody and
  names it.

This is not provenance (§15). Which *source* won a particular key - a subscription id, a grant, the
staff member who issued it - remains staff-facing and will never appear here. The plan a subject is
on is the one commercial fact they own.

### 5.6 `stripePublishableKey`

The publishable half of the instance's Stripe pair, when it has one.

Prefer it over anything bundled into your build, with the bundled value as the fallback. The clients
point at arbitrary instances at runtime, so a key compiled into a release aims every self-hoster's
checkout at whoever produced the release - which is the same class of mistake as showing them an
upgrade button (§5.3), with money attached.

**Absent is the normal case and is not an error.** It is absent on `selfhost`, absent on a hosted
instance whose billing is not configured, and absent on any instance whose operator has not set one.
Where `upgradesAvailable` is false there is no checkout to point anywhere, so nothing needs a key.

---

## 6. Consumption is a separate payload

An entitlement set says a guild has 50 emoji slots. It never says 47 are used. The two have opposite
caching properties, and folding `used` into §5 would make that response uncacheable and turn the
resolver into a counting service.

```http
GET https://api.venta.gg/api/v1/entitlements/guilds/{guildId}/usage   (planned)
GET https://api.venta.gg/api/v1/entitlements/me/usage                 (planned)
```

```jsonc
{
  "subject": { "kind": "guild", "id": "guild-1" },
  "resolvedAt": "2026-08-14T10:00:00Z",
  "used": {
    "guild.emoji_slots": 47,
    "guild.bots_installed": 3,
    "storage.guild_quota_bytes": 3221225472
  }
}
```

These sit under the same client-facing prefix as §5 but are answered by fanning in to the services
that do the counting, so a client has one namespace to learn rather than four. That is why they are
planned rather than shipped: nothing counts emoji, bots or stored bytes against a ceiling yet.

Usage is **free to be stale**. A count a few seconds behind renders "47 of 50" where it should have
said 48, which is cosmetic; a ceiling a few seconds behind is a wrong answer to "may I". Cache them
separately and refresh usage on the screen that shows it.

Every "X of Y" meter needs one call from §5 and one from here. Neither alone is renderable.

---

## 7. Realtime

One event, on the existing hub connection (`/api/v1/ws/hub`).

```
entitlements.Changed
```

```jsonc
{
  "subjectKind": "guild",
  "subjectId": "guild-1",
  "version": 8,
  "changedKeys": ["voice.max_participants"]
}
```

**It is an envelope, not the values.** A guild plan change fans out to every online member, and
delivery is unordered, so a pushed value can arrive stale and overwrite a newer one. A version plus a
refetch is monotonic; a pushed value is not.

- `changedKeys` is **advisory**. Use it to skip the refetch when nothing relevant is open. Handle it
  being empty.
- Routing: a guild change goes to that guild's members; a user change goes to that user's own devices
  and nowhere else. You will never receive another member's user-scoped change.
- On receipt, refetch what you actually have open. Do not fan the event out into per-screen state.

Everything else is refresh-on-navigate, which is correct almost everywhere: upload ceilings, emoji
slots, bot slots, audit window and storage quota are all read when a screen opens. There are exactly
two exceptions, and one of them is not this event.

---

## 8. Voice limits ride the voice snapshot

A room's limits can change **during** a call: a boost lapses, a grant expires, a plan downgrades at
period end. The room is the one surface a user is looking at while it changes.

**There is no entitlement event for this and there must not be one.** The voice client already has
snapshot versioning, gap detection and a resync path
([voice-frontend-guide.md](./voice-frontend-guide.md) §4.2). A second unordered channel into the same
room would race the snapshot stream with no way to order the two, which is exactly the bug the
version mechanism exists to prevent.

So the limits arrive on the voice snapshot, version-gated like everything else on it, and a change to
them advances `version`:

```jsonc
{
  "roomId": "channel-123",
  "instanceId": "...",
  "version": 43,
  "participants": [ /* ... */ ],
  "limits": {
    "maxParticipants": { "kind": "numeric", "value": 10, "unlimited": false },
    "videoCeiling":    { "kind": "ladder",  "rung": "720p30", "rank": 2, "ladder": "video_quality" },
    "maxPublishers":   { "kind": "numeric", "value": 2, "unlimited": false },
    "publisherCount":  2
  }
}
```

`publisherCount` is room state, not entitlement state. It is here for the same reason a slot count is
anywhere: "2 of 2 people are sharing" is a sentence, "you cannot share" is a mystery.

`limits` is absent on a room whose limits have never been computed, and an absent `limits` means "no
limit information", not "no limits".

The join and publish responses carry `degradations[]` (§1) when the limit actually bit. Render the
banner from those; use `limits` to pre-empt, to draw denominators, and to disable a share button that
would only be refused.

**Both room kinds carry it, and the two joins are different requests.** For a guild channel it is
`POST /api/v1/guilds/{guildId}/channels/{channelId}/voice/join`, whose body is the room snapshot. For
a direct call it is `POST /api/v1/voice/calls/{callId}/session`, whose body is
`{ mediaSessionId, backend }`. In both cases the array is a sibling of the body you already parse and
is absent whenever nothing was reduced, which is every join on an instance that sells nothing.

A call has no guild plan behind it, so the only thing that can reduce a call is an operator ceiling -
a self-hoster who has capped what their own box will carry. Those carry no `remedy`, because no
amount of money moves one. Render the sentence, not a button.

### 8.1 Publishing video: one optional field, and two answers

The negotiate body gains one optional field. Both room kinds, same name, same meaning:

```http
POST https://api.venta.gg/api/v1/guilds/{guildId}/channels/{channelId}/voice/tracks
POST https://api.venta.gg/api/v1/voice/calls/{callId}/tracks
```

```jsonc
{
  "mediaSessionId": "...",
  "sessionDescription": { "type": "offer", "sdp": "..." },
  "tracks": [ /* unchanged */ ],
  "video": { "height": 1080, "framerate": 60 }   // new, optional
}
```

- **Send it whenever the body publishes a camera or a screen share**, with the size you are actually
  about to encode. It is ignored for an audio-only publish and for a body that only subscribes.
- **Absent means "whatever the room allows".** A client that never sends it keeps working and is
  resolved to the ceiling rather than to nothing, so this is safe to adopt at your own pace. It is
  worth adopting, though: see the last bullet of this section.
- A non-positive number on either axis reads as unstated for that axis. There is no way to ask for
  "no video" here; not publishing a video track is how that is said.

Two answers, and they are §1 and §4 exactly as everywhere else:

| Situation | Status | Body |
|---|---|---|
| Above the granted rung, and a publisher slot is free | `200` | The negotiate response you already parse, plus `degradations[]` carrying `voice.video_ceiling` |
| Granted rung is `none`, or no publisher slot is free | `403` | The §4 denial, keyed `voice.video_ceiling` or `voice.max_publishers` |

- **The granted rung is on the degradation**, as `granted.rung` (§2, a ladder value). Re-encode to
  that and to `ladders.video_quality`'s entry for it; there is nothing to fetch.
- **A refusal takes the whole request, audio included.** An SDP offer is answered symmetrically, so
  the server cannot accept the microphone and drop the camera out of one negotiation. If you offered
  both and got a `403`, re-offer audio on its own - that request cannot be refused by any of this.
- **A publish that declared a size above its rung is also capped server-side**, at the simulcast
  layer the SFU is asked for, until a later negotiation declares a size inside the rung. So ignoring
  the clamp does not get you the quality you asked for; it gets you a lower layer distributed to
  everyone. A client that never sends `video` is not capped this way, which is the one thing sending
  it truthfully buys you.

### 8.2 Changing resolution mid-stream

Send `video` on the **renegotiation** too, whenever the renegotiation changes what your video is:

```http
PUT https://api.venta.gg/api/v1/guilds/{guildId}/channels/{channelId}/voice/negotiate
PUT https://api.venta.gg/api/v1/voice/calls/{callId}/negotiate
```

```jsonc
{
  "mediaSessionId": "...",
  "sessionDescription": { "type": "offer", "sdp": "..." },
  "video": { "height": 1080, "framerate": 60 }   // new, optional
}
```

The server-side cap is computed from your declaration, so a declaration made once at publish time
stops describing you the moment you change encodings. The rules are the same ones as §8.1, plus:

- **Absent changes nothing.** Whatever the last declaration recorded stays in force - in either
  direction. Omitting the field does not lift a cap, and it does not apply one either.
- **It moves both ways.** Renegotiating down into your rung lifts the cap on the same call that would
  have applied one, so re-encoding to comply is never punished by having to republish.
- **There is no new failure.** A renegotiation is how you repair your own connection, so none of this
  can refuse one: no `403`, no `degradations[]`, no change to the response body. If your declaration
  is above your rung the effect is on what leaves the room, not on this request.
- **A renegotiation that does not touch video needs nothing.** An ICE restart, a track close, a
  reconnect - send the body you always sent.

---

## 9. Video quality: the rungs are the server's

The server's ladder is `none / 480p30 / 720p30 / 1080p30 / 1080p60`. The clients' pickers offer
resolutions and framerates that do not line up with it, including 1440p, "source", and every 15 fps
option.

**Do not invent the mapping.** Deciding that 1440p30 clamps to 1080p30 is a pricing decision, and a
pricing decision in a `.ts` file is one nobody can change later. The server publishes what each rung
permits (§5.1, `ladders`), and the rules are:

1. Clamp your picker to the granted rung's `maxHeight` and `maxFramerate`. A 720p30 rung means
   nothing above 720 lines and nothing above 30 fps.
2. **A lower framerate is always allowed.** Every 15 fps option is legal on every rung above `none`.
3. `none` is a real rung, not an absence. It means audio-only, and it is how "this guild is over its
   video budget" is expressed without refusing the call.
4. Options above the top rung are not errors. Offer them, and expect the server to clamp and to say
   so with a `voice.video_ceiling` degradation.
5. Never hardcode the rung list. It is on the wire so that the day a rung is added your picker gains
   it without a release.

---

## 10. Guild features and the plan

A module can now be unusable for **three** different reasons, and the client currently knows two: the
owner turned it off, or you lack the permission. The third is that the plan does not include it, and
it is neither of the others: the owner wants it on and has every permission.

Guild feature state therefore travels as four lists, not one:

```jsonc
{
  "chosen":         ["Forums", "Events", "Wiki"],   // what the owner turned on
  "includedByPlan": ["Events", "Wiki", "..."],      // what the plan covers
  "withheldByPlan": ["Forums"],                     // chosen and not covered
  "effective":      ["Events", "Wiki"]              // what is actually on
}
```

### Where it arrives

Two places, and neither of them is the entitlement snapshot (§5) - a feature bitmask is not a
numeric, flag or ladder value, so it is Guild's shape and travels on Guild's payloads.

```http
GET https://api.venta.gg/api/v1/guilds/{guildId}            -> `featureResolution` on the guild body
GET https://api.venta.gg/api/v1/guilds/{guildId}/features   -> the object on its own
```

Both are members-only, the same bar as the rest of the guild's structure.

- **On the single-guild read it is a field called `featureResolution`**, alongside the existing
  `features` string. That string is unchanged, still comma-separated, and still says what is
  *effective* - so nothing a v1 client parses has moved.
- **The dedicated route is what you refetch**, on an `entitlements.Changed` push or when a module
  screen opens. Re-pulling a guild's entire channel, category and role tree to learn that Forums went
  out of plan is a payload proportional to the guild's structure for four short lists.
- **It is absent from `GET /guilds`** and from every guild nested inside another payload. A member of
  two hundred guilds would pay two hundred plan lookups to draw a sidebar. Treat absent as "not
  loaded", never as "no modules".

- **`withheldByPlan` is the upgrade prompt.** It is exactly "the owner asked for this and is not
  getting it", and it is the only list that distinguishes an out-of-plan module from an owner-disabled
  one. Deriving it from `effective` is what this contract exists to stop you doing.
- **These are arrays of feature names, never numbers.** The underlying representation is a bitmask,
  and an unconstrained plan is all bits set, which would cross the wire as a nonsense-looking
  `18446744073709551615`. Same class of bug as `long.MaxValue` in §2, and the same rule: names on the
  wire, never the raw mask.
- When no plan constrains the guild, `includedByPlan` lists every known feature and `withheldByPlan`
  is empty. That is what every guild looks like today.
- Unknown names must survive a round trip. Keep them; do not filter to the ones you recognise.

### 10.1 The three empty states

Module screens already render two mutually exclusive empty states. Add a third rather than folding it
into either:

| Condition | Copy family |
|---|---|
| Not in `effective`, not in `withheldByPlan` | `MODULE_OFF_TITLE` / `MODULE_OFF_BODY` |
| In `withheldByPlan` | `NOT_IN_PLAN_TITLE` / `NOT_IN_PLAN_BODY`, plus the upgrade call to action |
| Refused for permissions | `FORBIDDEN_TITLE` / `FORBIDDEN_BODY` |

An API call refused because the plan does not include the module returns the §4 denial body with
`key: "guild.features"` and `feature: "<name>"`, so you can tell it from a permission refusal at the
call site rather than by inferring it. A permission refusal carries its own codes and never one of
the entitlement reason codes.

### 10.2 Existing content in a module the plan dropped

Today, a guild that loses a module loses **access to that module's existing content**, not only the
ability to create more: forum channels in a plan without Forums answer as refused rather than as
read-only. Render `NOT_IN_PLAN_TITLE` / `NOT_IN_PLAN_BODY` with the upgrade call to action there, and
**do not** write copy that says the content is gone. Nothing is deleted, ever, on a downgrade.

This behaviour predates the monetization work and sits badly against §3.3 and against the drafted
downgrade terms; the intended end state is read-only access to what already exists. Build the empty
state so that a later change to read-only is a rendering change and not a redesign.

---

## 11. Per-surface notes

### 11.1 Uploads

Two keys, and which one applies depends on where the file is going: `storage.upload_max_bytes`
(paired) inside a guild, `user.upload_max_bytes` outside one. Validate against the effective ceiling
before you start the transfer, and still handle the `403` (§4), because the ceiling can move between
your read and your upload, and because a client-side check is a courtesy rather than the enforcement.

An oversized file in a batch is refused on its own. Do not roll back the whole batch.

### 11.2 Guild storage

`storage.guild_quota_bytes` with §6's usage gives the "3.2 GB of 5 GB" meter. **Over quota freezes new
uploads and never deletes anything.** Copy must not imply otherwise.

### 11.3 Audit log

`guild.audit_log_days` is a window, and the response must tell you where the window ends as distinct
from where the data ends. Without that distinction, paging into the end of the window renders as "your
server has no history", which is both wrong and alarming. Expect a `windowEndsAt` alongside the
entries and render "this is as far back as your plan goes" with an upgrade call to action rather than
an empty state.

### 11.4 Message history

**Retention is unlimited on every tier and always will be.** This is a deliberate decision in the
spec, not an oversight and not a placeholder. Do not build a retention warning, a "messages older
than" notice or an archive prompt. The audit log window (§11.3) is a different thing and is the only
time-bounded surface here.

### 11.5 Vanity URL

**The capability shipped on 2026-08-15, so the flag now means what it says.** Render the locked
upsell. `false` is "not entitled", and the vanity settings surface is a real one - see
`Guild.Application/docs/invites-frontend-guide.md` §9 for the routes, the slug grammar and the error
shapes.

Two things about it are specific to this key and worth reading before you build the screen:

- **A guild that loses the entitlement keeps its name and loses the link.** The settings endpoint
  returns the held slug with `active: false`. Rendering that as an empty field would be
  indistinguishable from the name having been taken away, which is exactly what
  docs/legal/downgrade-2026-08-14.md 9.2 promises does not happen. Show the name, greyed, with the
  upgrade prompt next to it.
- **Claiming a name is refused when billing is unreachable, and resolving one is not.** So a `PUT`
  can answer `403 vanity_url_not_entitled` transiently on a guild that really is entitled. Treat it
  as retryable rather than as a settled answer about the plan.

### 11.6 Devices, emoji, bots

`user.max_devices`, `guild.emoji_slots` and `guild.bots_installed` are all "X of Y" surfaces needing
§5 plus §6. Emoji and bot creation past the ceiling is a hard denial (§4), not a degradation.

---

## 12. Translation keys

Reason and remedy, one per code plus the mandatory fallbacks:

```
ENTITLEMENT.REASON.GUILD_PLAN_LIMIT
ENTITLEMENT.REASON.USER_PLAN_LIMIT
ENTITLEMENT.REASON.PAIRED_CEILING_GUILD
ENTITLEMENT.REASON.PAIRED_CEILING_USER
ENTITLEMENT.REASON.OPERATOR_CEILING
ENTITLEMENT.REASON.UNKNOWN
ENTITLEMENT.CTA.UPGRADE_SERVER
ENTITLEMENT.CTA.UPGRADE_ACCOUNT
ENTITLEMENT.CTA.ASK_OWNER
ENTITLEMENT.CTA.LEARN_MORE
```

One display name per key in the catalogue, so a degradation can be named in a sentence without every
component switching on eleven strings:

```
ENTITLEMENT.KEY.VOICE_MAX_PARTICIPANTS      ENTITLEMENT.KEY.GUILD_BOTS_INSTALLED
ENTITLEMENT.KEY.VOICE_VIDEO_CEILING         ENTITLEMENT.KEY.GUILD_VANITY_URL
ENTITLEMENT.KEY.VOICE_MAX_PUBLISHERS        ENTITLEMENT.KEY.GUILD_AUDIT_LOG_DAYS
ENTITLEMENT.KEY.STORAGE_UPLOAD_MAX_BYTES    ENTITLEMENT.KEY.USER_UPLOAD_MAX_BYTES
ENTITLEMENT.KEY.STORAGE_GUILD_QUOTA_BYTES   ENTITLEMENT.KEY.USER_MAX_DEVICES
ENTITLEMENT.KEY.GUILD_EMOJI_SLOTS
```

Surface copy where a generic sentence would be worse:

```
VOICE.DEGRADED.AUDIO_ONLY            GUILD_SETTINGS.EMOJIS.SLOTS         "{{count}} of {{max}}"
VOICE.DEGRADED.QUALITY_CAPPED        GUILD_SETTINGS.EMOJIS.SLOTS_FULL
VOICE.DEGRADED.PUBLISHERS_FULL       GUILD_SETTINGS.AUDIT_LOG.WINDOW_END
VOICE.DEGRADED.ROOM_AT_LIMIT         GUILD_SETTINGS.MODULES.NOT_IN_PLAN
COMPOSER.UPLOAD_TOO_LARGE            GUILD_SETTINGS.STORAGE.USED
BOT_INSTALL.SLOTS_FULL               <MODULE>.NOT_IN_PLAN_TITLE / _BODY
```

`PAIRED_CEILING` is two keys chosen by `boundBy`. That is §3.1's requirement showing up as a
translation key, which is the cheapest proof the field is needed.

---

## 13. Reference

### Endpoints

| Method | Path | Auth | Returns |
|---|---|---|---|
| `GET` | `/api/v1/entitlements/me` | Bearer | §5 snapshot, user subject |
| `GET` | `/api/v1/entitlements/guilds/{guildId}` | Bearer, members only | §5 snapshot, guild subject |
| `GET` | `/api/v1/entitlements/me/usage` | Bearer | §6, planned |
| `GET` | `/api/v1/entitlements/guilds/{guildId}/usage` | Bearer, members only | §6, planned |
| `GET` | `/api/v1/guilds/{guildId}` | Bearer, members only | The guild, with §10's `featureResolution` |
| `GET` | `/api/v1/guilds/{guildId}/features` | Bearer, members only | §10 on its own |

### Events

| Event | Payload | Routed to |
|---|---|---|
| `entitlements.Changed` | `{ subjectKind, subjectId, version, changedKeys }` | The guild's members, or the one user's devices |

Voice limits are not an event. They are a field on the voice snapshot (§8).

### Statuses

| Situation | Status | Body |
|---|---|---|
| Reduced, but it worked | `200` / `201` | Normal body plus `degradations[]` |
| Refused, cannot degrade | `403` | §4 denial |
| Not a member of that guild | `404` | `{ code: "not_found" }` |
| Anything entitlement-related | never `401`, never `429` | |

---

## 14. Rules that will bite you if you skip them

1. **Fix the joined-state bug before you ship any of this.** Alpine's `voice-channel.service.ts` sets
   `joinedChannelId` and `joinedGuildId` before it issues the join request and only logs in the
   `catch`, so a failed join leaves the sidebar, status bar and mute controls rendering as joined
   against no media. That is the exact path an entitlement rejection takes, and every degradation is
   invisible underneath it.
2. **Do not roll back on a degradation.** It is a `200` and the operation succeeded.
3. **Never compute `actorCanRemedy`.** Re-implementing ManageGuild in the client is how you get a buy
   button that 403s.
4. **Never treat a missing `degradations` array as an error path.** It is the normal case.
5. **Never render a raw code.** Unknown code means generic sentence and no button (§3.4).
6. **Never cache an entitlement set longer than `ttlSeconds`, and never to disk.**
7. **Never show billing UI when `upgradesAvailable` is false.** Omit it, do not disable it.
8. **Never derive an out-of-plan module from `effective`.** Read `withheldByPlan` (§10).
9. **Do not read `value` without reading `unlimited`.**

---

## 15. What you will not get

- **Provenance.** Which source granted a value (a Stripe subscription id, a grant reason, the staff
  member who issued it) is staff-facing and lives in the moderation console. It will never appear on
  a member-facing payload.
- **Server-written copy.** See §4.2.
- **A per-member effective set on a guild screen.** A guild snapshot is the guild's ceiling. What one
  member effectively gets is `min` of the two sides and is computed per operation, which is what the
  `degradations` array reports.
- **Prices, or a plan catalogue.** The plan a subject is *on* is §5.5; what the other plans are, what
  they cost and what they include belongs to the billing surface and arrives with it. One plan is an
  answer about you; the catalogue is a shop.
- **A push carrying values.** §7 is an envelope by design.
