# Invites & vanity URLs - frontend integration guide

Audience: web/desktop/mobile client engineers.

Everything about a join link: minting one, listing them, previewing one before you are a member,
redeeming it, revoking it, and the `venta.gg/the-flat` shortcut a paid guild can claim.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call a
microservice directly. Guild endpoints are reached under the `/api/v1/guild/` prefix; the gateway
strips the `guild` segment before forwarding, which is why the paths read
`/api/v1/guild/invites/{code}`. That doubled-looking segment is correct.

**Status:** everything in this document is the target contract for the invite round. **Six things
break against what you have shipped** - they are collected in §11 and each is flagged **BREAKING**
where it appears. Read §11 first if you are maintaining a live client.

Enums are on the wire as **strings**, not numbers (`"Active"`, `"Permanent"`, `"VoiceChannel"`), and
all field names are camelCase.

---

## 1. The model in one screen

| Thing | What it is |
|---|---|
| **Invite** | A row with a short `code`. Redeeming it makes you a member. |
| **Code** | 8 characters from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` - no `0/O`, no `1/I/L`. Case-sensitive, always uppercase. |
| **Vanity URL** | A guild-level slug that resolves to one designated invite. Paid feature. |
| **Target** | What the invite is an invite *to*, beyond the guild - today, a voice channel. |
| **Temporary** | The membership ends when the member goes offline, unless they got a role. |

An invite has a lifecycle: `Active` -> `Expired` (ran out on its own) or `Revoked` (taken away).

---

## 2. The invite object

```ts
interface Invite {
  id: string;                 // "chiv_..."
  guildId: string;
  createdAt: string;          // ISO-8601
  updatedAt: string;

  code: string;               // 8 chars, uppercase
  type: 'OneTime' | 'Permanent';
  state: 'Active' | 'Expired' | 'Revoked';   // ← see §3, the semantics changed

  expiresAt: string | null;   // null = never
  maxUses: number | null;     // null = unlimited
  useCount: number;

  channelId: string | null;   // landing channel; advisory unless targetType says otherwise
  inviterId: string | null;   // NEW - who created it
  temporary: boolean;         // NEW
  targetType: 'None' | 'VoiceChannel';   // NEW
  targetUserId: string | null;           // NEW
  revokedAt: string | null;   // NEW

  guild: Guild | null;        // present on the preview routes
  channel: Channel | null;
  welcomeScreen: WelcomeScreen | null;   // preview routes only, see onboarding guide
}
```

`inviterId` is a **user id**, not a profile. Guild does not own usernames or avatars - hydrate it
through your existing profile cache the same way you do for message authors. It is `null` for every
invite minted before this round and for anything created by a system path.

---

## 3. `state` is now server-derived - delete your local expiry check

**BREAKING (behavioural).** Until now `state` was whatever was last written to the row, and nothing
wrote `Expired` when the clock passed. An invite that expired months ago came back as `Active`, and
both clients grew their own `expiresAt < now` re-derivation to paper over it.

Every read path now computes it:

- `Revoked` if it was revoked. Terminal.
- `Expired` if it was a consumed one-time invite, **or** `expiresAt` has passed, **or**
  `useCount >= maxUses`.
- `Active` otherwise.

**Remove your local derivation.** Keeping it is not harmful but it is now a second source of truth
that can disagree - notably on the one-time case, where there is no `maxUses` to compare against and
only the server knows. Render `state` as given.

**One exception, and it is not an invite endpoint.** The `invite` object nested inside a *member*
payload (`GET /guilds/{id}/members`, `GET /guilds/{id}/me`) is a flattened historical record of the
link that member arrived on, and its `state` is the raw stored column. Nobody is about to redeem it,
so it is not derived there. Do not use it to decide whether an invite still works - ask the invite
endpoints.

There is deliberately **no background sweeper**, so the stored row still says `Active` for an
expired invite. That is invisible to you - no API returns the stored value - but it means you must
not treat `state` as something that changes on a timer you can subscribe to. If you render a
countdown against `expiresAt`, re-derive locally for the *countdown only* and keep using `state` for
the badge.

---

## 4. Reading a guild's invites

```
GET /api/v1/guild/guilds/{guildId}/invites
GET /api/v1/guild/guilds/{guildId}/invites?includeRevoked=true
```

Returns `Invite[]`.

**BREAKING (permission).** This required `ManageChannel`. It now requires **`ManageGuild`**, matching
Discord's `MANAGE_GUILD` gate on the invite list. The list is the guild's entire set of live join
credentials, and `ManageChannel` is both the wrong scope (channel, not guild) and the wrong level of
trust. A channel moderator who could open your invites screen yesterday gets `403` today - **hide the
screen behind `ManageGuild`, or they will hit a wall you did not warn them about.**

Revoked invites are **excluded by default**. Pass `includeRevoked=true` for an audit view. The
default is what keeps this list byte-for-byte the same shape it was before revocation existed.

---

## 5. Creating an invite

```
POST /api/v1/guild/guilds/{guildId}/invite
```

Requires `CreateInvite`. Returns the `Invite`.

```jsonc
{
  "type": "Permanent",            // or "OneTime"
  "expiresAt": "2026-09-01T00:00:00Z",   // optional, null = never
  "maxUses": 25,                  // optional, null = unlimited (accepted already; see below)
  "channelId": "chan_...",        // optional landing channel
  "temporary": false,             // NEW
  "targetType": "None",           // NEW - or "VoiceChannel"
  "targetUserId": "user_..."      // NEW - optional, advisory
}
```

Every field is optional. `{}` gives you exactly today's behaviour.

**`maxUses` is the one to actually build a control for.** It was already accepted on this request and
already returned on the invite - what was missing was any client sending it, and any validation of
what arrived. Build the "max number of uses" dropdown Discord has (`1, 5, 10, 25, 50, 100, No
limit`). Send `null` or omit for no limit; **`0` is now rejected with `400`** where it used to be
accepted, because an invite that is exhausted the moment
it exists is a link somebody is about to share.

### Errors

| Response | Cause |
|---|---|
| `400 "maxUses must be at least 1, or omitted for unlimited."` | `maxUses: 0` or negative |
| `400 "A VoiceChannel invite must name the channel in channelId."` | target with no channel |
| `400 "The target channel does not belong to this guild."` | wrong guild |
| `400 "A VoiceChannel invite must target a voice channel."` | target is a text/forum/etc channel |
| `403` | caller lacks `CreateInvite` |
| `404` | guild does not exist |

Targets are validated **at creation**, never at redemption. The person who can still fix a bad target
is the one filling in your form.

---

## 6. Previewing an invite (unauthenticated)

Three routes, all public, all returning the same `Invite` with `guild` and `welcomeScreen` filled in:

```
GET /api/v1/guild/invites/{idOrCodeOrSlug}   // tries id, then code, then vanity slug
GET /api/v1/guild/invites/code/{code}        // code only
GET /api/v1/guild/invites/vanity/{slug}      // vanity slug only   (NEW)
```

Prefer the specific route when you know what you are holding. The catch-all costs up to three lookups
and spends the same rate-limit token either way.

A **revoked** invite answers `404`, exactly as a deleted one used to. An **expired** invite still
answers `200` with `state: "Expired"` - that is deliberate, so you can render "this invite has
expired" with the guild's name on it rather than a bare not-found.

### Rate limiting - **NEW, plan for `429`**

These routes are the only unauthenticated surface that will tell you whether a code exists, so they
carry their own budget on top of the gateway's: **30 requests per minute per caller, burst 60**,
partitioned by account when you are signed in and by client address when you are not.

```jsonc
// 429
{ "error": "rate_limited", "message": "Too many invite lookups; try again shortly." }
```

Practical consequences:

- **Do not poll a preview.** Fetch once per link the user opens.
- **Do not prefetch** previews for every invite code you happen to have in a message body. Resolve
  on hover or on click.
- One retry with a few seconds of backoff is fine. A retry loop is not.

Every request spends a token, **including a miss** - a miss is the request worth pricing, because it
is the one that probes the code space.

---

## 7. Redeeming

```
POST /api/v1/guild/invites/{idOrCodeOrVanitySlug}/redeem
```

Requires authentication. Still answers **`202 Accepted`** - but now with a body.

```ts
interface RedeemResult {
  guildId: string;
  channelId: string | null;
  targetType: 'None' | 'VoiceChannel';
  targetUserId: string | null;
  joinVoice: boolean;            // connect to channelId as voice
  onboardingRequired: boolean;   // show the rules gate, see the onboarding guide
  temporaryMembership: boolean;
}
```

Additive: a client that ignores the body behaves exactly as before.

**Use `joinVoice`, not `targetType`.** `joinVoice` is false when the target channel has been deleted
or is no longer a voice channel since the link was made. The join still succeeds in that case - only
the landing is dropped. Deriving "should I connect" from `targetType` yourself will make you try to
join a room that is not there.

**`temporaryMembership: true` deserves a line of UI.** A member who is not told will simply find
themselves gone. Say so on the join confirmation: *"You will leave this server when you go offline,
unless you are given a role."*

### Errors

| Response | Cause |
|---|---|
| `404` | unknown code, **or the invite was revoked** |
| `400 "Invite has expired"` | past `expiresAt`, or a consumed one-time invite |
| `400 "Invite has reached its maximum number of uses"` | exhausted |
| `403` | banned from the guild |
| `403 { "error": "verification_level_not_met", "requiredLevel": "..." }` | see the verification-levels guide |
| `409 "User is already a member of this guild."` | already joined |

Revocation answers `404` rather than a explanatory `400` on purpose: to whoever is holding the code
it has to be indistinguishable from a code that was never real.

---

## 8. Revoking (still `DELETE`)

```
DELETE /api/v1/guild/invites/{inviteId}
```

Still `DELETE`, still answers `200` with the `Invite` body. **The row is no longer deleted** - it
moves to `state: "Revoked"` with a `revokedAt`.

**BREAKING (permission).** Was `ManageChannel` guild-wide. Now: **`ManageGuild`** anywhere in the
guild, **or** `ManageChannel` on the specific channel this invite lands on. The second is how a
channel moderator revokes a link into their own channel without being handed the guild. An invite
with no `channelId` can therefore only be revoked with `ManageGuild`.

The route is **idempotent**: revoking an already-revoked invite answers `200` with the same body and
writes no second audit entry.

Why the row survives: `guildMember.inviteId` points at it. That FK was once cascading, so revoking an
invite deleted every member who had joined through it. The fix nulled the FK and snapshotted the code
onto the member - which preserved *a string* and lost the inviter, the channel, the expiry and the
audit target. Keeping the row keeps the whole chain, which is what makes "who brought this member in"
answerable at all.

For your UI: a revoked invite disappears from the default list (§4). If you show an audit view with
`includeRevoked=true`, render it greyed with its `revokedAt`.

---

## 9. Vanity URLs

A guild on a plan that includes `guild.vanity_url` can claim a slug. `venta.gg/the-flat` then resolves
to a real invite for that guild.

### 9.1 Reading

```
GET /api/v1/guild/guilds/{guildId}/vanity-url
```

Requires `ManageGuild`.

```ts
interface VanityUrl {
  guildId: string;
  vanityUrl: string | null;   // the claimed slug, lowercase
  entitled: boolean;          // does the plan cover it right now
  active: boolean;            // vanityUrl !== null && entitled
  setAt: string | null;
}
```

**`vanityUrl` and `active` are separate on purpose.** A guild that downgrades keeps the name and
loses the resolution. Collapsing them - returning `null` once the plan lapses - would be
indistinguishable from the name having been taken away, which is precisely what our downgrade terms
promise does not happen. Render the held name with an "inactive - upgrade to re-enable" state, never
as an empty field.

### 9.2 Setting and clearing

```
PUT /api/v1/guild/guilds/{guildId}/vanity-url
```

Requires `ManageGuild`, plus the entitlement **to claim** (not to clear).

```jsonc
{ "vanityUrl": "the-flat" }   // claim
{ "vanityUrl": null }         // give it up
{ "vanityUrl": "" }           // also gives it up
```

Returns the `VanityUrl` object.

### 9.3 The rules

| Rule | Detail |
|---|---|
| Length | 3-32 characters |
| Alphabet | `a-z`, `0-9`, and single hyphens **between** segments |
| No leading or trailing hyphen | `-flat` and `flat-` are rejected |
| No doubled hyphens | `the--flat` is rejected |
| Case | Normalized to lowercase. `The-FLAT` and `the-flat` are the same name. |
| Whitespace | Trimmed |
| Uniqueness | Instance-wide, case-insensitive |
| Reserved | ~90 words are refused - `support`, `billing`, `venta`, `invite`, `admin`, `login`, … |

**Validate client-side with the same grammar** so the user is not told "invalid" after a round trip,
but expect the server to be the authority on reserved words and uniqueness - neither is derivable
locally.

### 9.4 Errors

| Response | Cause |
|---|---|
| `400 <explanation>` | Grammar or reserved. The body is a plain-text sentence written for a human; show it verbatim. |
| `409 "That vanity URL is already taken."` | another guild holds it |
| `403 { "error": "vanity_url_not_entitled", "message": "..." }` | plan does not cover it, **or** billing could not be reached |
| `403` (bare) | caller lacks `ManageGuild` |

The two `403` shapes are distinguishable by the body. The JSON one is an upgrade prompt; the bare one
is a permission problem.

Note the deliberate asymmetry: **claiming fails closed and resolving fails open.** If billing is
unreachable, nobody may claim a new name (a claim is permanent, and one bad minute must not hand out
a free one) but every existing vanity link keeps working (an outage must not take every guild's
landing page down). You will see this as: `PUT` can `403` transiently, `GET /invites/vanity/...`
will not.

### 9.5 What happens on downgrade

- The link **stops resolving**. `/invites/vanity/{slug}` answers `404`.
- The name is **kept**. `GET .../vanity-url` still returns it, with `active: false`.
- **Ordinary invite links are completely unaffected**, on every tier.
- Restoring the plan restores the link, same name, automatically. Nothing to reconfigure.
- The owner can still clear it while downgraded.

Our downgrade terms say a name is held **for 90 days** and may then be released to somebody else.
**Nothing releases one today** - a held name is held indefinitely. That is more generous than the
promise, never less, so there is nothing for you to build against it: do not render a countdown, and
do not tell the user their name expires. If a release policy ever lands, it will arrive as
`vanityUrl` becoming `null`, which your existing empty state already handles.

### 9.6 The backing invite

Claiming a slug the first time mints an ordinary permanent, unlimited invite for it to point at. It
appears in the invite list (§4) like any other, is revocable like any other, and shows up in
`guild.InviteCreated`.

Two consequences worth knowing:

- **Renaming reuses it**, so a rename does not invalidate the join the old name resolved to.
- **Clearing the vanity URL does not revoke it.** People may be holding that link; dropping a name is
  not a decision to break it. If the owner wants it gone, that is a normal `DELETE` on the invite.

---

## 10. Realtime events

Two new SignalR events on the existing hub, following the house `guild.*` convention.

```jsonc
// guild.InviteCreated
{
  "guildId": "gild_...",
  "inviteId": "chiv_...",
  "code": "K7MPQ2XR",
  "channelId": "chan_..." | null,
  "inviterId": "user_..." | null,
  "expiresAt": "..." | null,
  "maxUses": 25 | null,
  "uses": 0,
  "temporary": false,
  "targetType": "None" | "VoiceChannel",
  "targetUserId": "user_..." | null
}
```

```jsonc
// guild.InviteDeleted
{
  "guildId": "gild_...",
  "inviteId": "chiv_...",
  "code": "K7MPQ2XR",
  "channelId": "chan_..." | null
}
```

**These are not sent to everyone in the guild.** Unlike every other `guild.*` broadcast, the payload
carries the code - which is the credential - so the audience is the online members who hold
`ManageGuild`. If your client is not one of them it will simply never see these, which is correct and
not a bug to work around.

`guild.InviteDeleted` fires on revocation. Treat it as "remove this from the invite list" - the same
handling a delete would have got.

Use them to keep an open invites screen live instead of refetching. A creation you performed yourself
also arrives (you are in the audience), so dedupe on `inviteId`.

---

## 11. BREAKING changes, collected

Both clients are live against the current shape. These six need a code change; everything else in
this document is additive.

| # | Change | What breaks | What to do |
|---|---|---|---|
| 1 | `GET /guilds/{id}/invites` now needs **`ManageGuild`** (was `ManageChannel`) | A `ManageChannel`-only moderator gets `403` on a screen they could open | Gate the invites screen on `ManageGuild` |
| 2 | `DELETE /invites/{id}` now needs **`ManageGuild`**, or `ManageChannel` **on that invite's channel** | Same population; a moderator with guild-wide `ManageChannel` can no longer revoke an unchannelled invite | Gate the revoke button the same way, and don't render it on invites with no `channelId` unless the user has `ManageGuild` |
| 3 | `state` is **server-derived** | Long-expired invites now come back `Expired` instead of `Active` | Delete your local `expiresAt < now` derivation; render `state` |
| 4 | `DELETE /invites/{id}` **revokes** rather than deletes | An invite you deleted is still in the database. Default list output is unchanged, but `includeRevoked=true` will show it, and `state` can now be `"Revoked"` | Add `'Revoked'` to your state union. It is a **new enum value on an existing field** - a strict deserializer will throw |
| 5 | Preview routes are **rate limited** (`429`) | A client that prefetches or polls previews will start failing | Fetch once per link the user opens; handle `429` |
| 6 | Redeem answers `202` **with a body** | Nothing, unless your deserializer rejects an unexpected body on a 202 | Read `joinVoice` / `onboardingRequired` / `temporaryMembership` |

**#4 is the one that bites silently.** `state: "Revoked"` is a value your existing union type does not
have. A TypeScript client with a loose type is fine; a Dart client with a `switch` on an enum, or any
strict deserializer, will throw on an audit view. Add the case before you ship anything else here.

Not breaking, listed so you do not go looking: `code`, `id`, `type`, `expiresAt`, `maxUses`,
`useCount`, `channelId` and the nested `guild`/`channel`/`welcomeScreen` are all unchanged in shape
and meaning.

---

## 12. Permissions, in one table

| Operation | Permission |
|---|---|
| List a guild's invites | `ManageGuild` |
| Create an invite | `CreateInvite` |
| Preview an invite (id / code / vanity) | none - unauthenticated, rate limited |
| Redeem an invite | authenticated; no guild permission |
| Revoke an invite | `ManageGuild`, **or** `ManageChannel` on the invite's `channelId` |
| Read the vanity URL | `ManageGuild` |
| Set the vanity URL | `ManageGuild` + `guild.vanity_url` entitlement |
| Clear the vanity URL | `ManageGuild` (entitlement **not** required) |
| Receive `guild.InviteCreated` / `guild.InviteDeleted` | `ManageGuild` |

---

## 13. Temporary membership

An invite created with `temporary: true` grants a membership that ends when the account goes offline.

- **A role beyond `@everyone` makes it permanent.** Granting one at any point - before or during the
  grace window - converts the membership and it is never reconsidered.
- **A disconnect does not remove anybody immediately.** There is a **five-minute grace window**, so a
  network change, a backgrounded app, a client update or a gateway rollout costs nothing. Reconnecting
  inside it cancels the removal outright.
- Removal, when it happens, arrives as the ordinary **`guild.MemberLeft`** event. There is no separate
  event, because from every consumer's point of view that is what happened.
- The guild owner and anybody who joined by another route are unaffected.

Client work: surface it at redeem time (§7), and render the member normally otherwise - a temporary
member is an ordinary member until they are not.
