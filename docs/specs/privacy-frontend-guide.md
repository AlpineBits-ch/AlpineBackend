# Privacy & account controls - frontend integration guide

Everything the client needs to build against the privacy workstream (`docs/specs/privacy.md`).
Backend is done and tested; this is the client-side work.

Registration changed shape too - that has its own document,
[`registration-contract-change.md`](./registration-contract-change.md). Read this one first.

## Base URL and the path rule

```
https://api.venta.gg
```

Every route below is the **public** path through the gateway. The gateway inserts the service
segment **after** `/api/v1`:

| Service declares | You call |
|---|---|
| `/api/v1/privacy-settings` (identity) | `/api/v1/identity/privacy-settings` |
| `/api/v1/relationships` (social) | `/api/v1/social/relationships` |
| `/api/v1/guilds/{id}/privacy` (guild) | `/api/v1/guild/guilds/{id}/privacy` |
| `/connect/token` (identity) | `/connect/token` - pass-through, **no** service segment |

> Some older guides under `Guild.Application/docs/` show a doubled form like
> `/api/v1/guild/api/v1/guilds/...`. **That is wrong and 404s.** The rule above is pinned by
> `Echo.E2E.Tests/Scenarios/DocsPathMappingTests.cs`.

Normal `Authorization: Bearer <token>` throughout unless stated otherwise.

**All enums below serialize as strings**, not integers - send `"Friends"`, not `1`.

---

## 0. Breaking changes - do these first

Ordered by how loudly they fail.

| # | Change | Where | What breaks |
|---|---|---|---|
| 1 | Registration returns `202` with **no `userId`** | `POST /api/v1/identity/authentication/register` | Any client reading `userId` from the response. See the separate doc. |
| 2 | DM/conversation refusals are `403` + JSON, not `400` + prose | 4 messaging routes | Clients matching on the old string |
| 3 | New `503 privacy_lookup_unavailable` | same routes | Must be retried, not shown as "denied" |
| 4 | `429` is now actually enforced | every route | Clients with no backoff |
| 5 | `retry_after` in the 429 body is **fractional** | every route | A client parsing it as an int breaks |
| 6 | `Hidden` presence now renders as `Offline` to others | guild member lists, presence events | Any branch on `Hidden` for a non-self member |
| 7 | Friend request to an unknown user is `403`, not `400` | `POST /api/v1/social/relationships` | Error handling |
| 8 | `PUT /api/v1/identity/users/self/settings` can return `413` | settings blob | Unhandled status |
| 9 | Export status can be `Partial` | data exports | A client gating download on `Ready` hides a working download |

---

## 1. Privacy settings - the core

```http
GET   /api/v1/identity/privacy-settings
PATCH /api/v1/identity/privacy-settings
```

One object, 19 fields plus a read-only `version`:

```json
{
  "allowDataCollection": false,
  "allowPersonalization": false,
  "allowVoiceRecordingInClips": false,

  "directMessagePolicy": "Friends",
  "friendRequestPolicy": "Everyone",

  "discoverableByUsername": true,
  "discoverableByEmail": false,
  "discoverableByPhone": false,

  "mutualServersVisibility": "Friends",
  "mutualFriendsVisibility": "Friends",
  "connectionsVisibility": "Friends",
  "birthdayVisibility": "Nobody",

  "shareActivity": true,
  "allowPositionalVoiceCapture": true,

  "sendReadReceipts": true,
  "sendTypingIndicators": true,
  "dmRetentionDays": null,

  "explicitContentFilter": "UnknownSenders",
  "hidePushContent": false,

  "version": 0
}
```

Enum values:

| Field | Values |
|---|---|
| `directMessagePolicy` | `Everyone` · `FriendsAndServerMembers` · `Friends` · `Nobody` |
| `friendRequestPolicy` | `Everyone` · `FriendsOfFriends` · `ServerMembers` · `Nobody` |
| `*Visibility` | `Everyone` · `Friends` · `Nobody` |
| `explicitContentFilter` | `Off` · `UnknownSenders` · `Everyone` |

### PATCH semantics

**Every field optional; omitted means "leave alone".** Never read-modify-write - send only what
changed.

- Unknown field → `400`
- `dmRetentionDays: null` **is meaningful** - it clears the window (keep forever). It is not treated
  as an omission.
- `dmRetentionDays` capped at `3650`; `0` or negative → `400`
- A PATCH whose values all equal current state is a **no-op**: no `version` bump, no event. Don't
  use `version` changing as your "save succeeded" signal - use the `200`.

`version` increments on every real write. Use it to detect a change made from another device.

### Minors - read this before building the settings screen

Accounts under the age of majority (18 by default) have **server-enforced floors**. Six fields are
restricted:

`directMessagePolicy` (can't be `Everyone`) · `allowPersonalization` · `discoverableByEmail` ·
`discoverableByPhone` · `allowVoiceRecordingInClips` · `explicitContentFilter` (can't be `Off`)

A PATCH violating a floor returns:

```json
{ "code": "minor_restriction", "field": "allowPersonalization", "message": "..." }
```

with `403`. **A mixed PATCH containing one refused field applies none of it** - it is all-or-nothing.

**The critical part:** floors are also **clamped on read**. A minor's `GET` returns the floored
values, not what is stored. So a naive `GET` → toggle one thing → `PATCH` **round-trip will 403**,
because you will send back a clamped value the server refuses. Send only the fields the user
actually changed.

Render the restricted controls as disabled with an explanation rather than letting the user try and
fail. When a user ages out, the floors lift and their own stored choices return.

---

## 2. Blocking

```http
POST   /api/v1/social/relationships/{userId}/block     → 204
DELETE /api/v1/social/relationships/{userId}/block     → 204   (idempotent)
GET    /api/v1/social/relationships/blocked?limit=&cursor=
```

`{userId}` is the **Identity user id** - the same id `/api/v1/social/profiles/by-user/{id}` takes.

```json
{
  "blocked": [
    { "relationshipId": "...", "profileId": "...", "userId": "...",
      "userName": "...", "avatarUrl": "...", "blockedAt": "2026-08-04T..." }
  ],
  "nextCursor": "..."
}
```

Keyset paging, default 50, max 100. A malformed cursor is `400`.

**Semantics you must reflect in the UI:**

- Blocking is **one-directional and invisible to the blocked party.** They see the same thing as
  "not friends". Never surface "you have been blocked" - the server deliberately never tells you.
- **Blocking an existing friend removes the friendship**, and both sides receive
  `social.FriendRemoved`. The blocked party's client must handle that as an ordinary un-friending,
  or it strands on a phantom friend.
- Blocking cancels pending requests in either direction.
- Unblocking **deletes** the record - the pair can befriend again afterwards.

---

## 3. DM and conversation refusals

Affected routes:

```
POST /api/v1/messaging/conversations
POST /api/v1/messaging/conversations/consume-tokens
POST /api/v1/messaging/conversations/{id}/members
POST /api/v1/messaging/voice/call
POST /api/v1/messaging                     ← new gate on 1:1 DMs
```

Body:

```json
{ "error": "recipient_dm_policy", "userId": "user_..." }
```

| Code | Status | Meaning | Suggested UI |
|---|---|---|---|
| `recipient_dm_policy` | 403 | Recipient's policy doesn't admit you | "You can't message this person" + offer friend request |
| `blocked` | 403 | **You** blocked them | "You blocked this user" + offer unblock |
| `explicit_content_filtered` | 403 | Attachment refused by their filter | Explain the attachment was rejected |
| `privacy_lookup_unavailable` | **503** | Couldn't reach the policy data | **Retry.** Not a permission error. |

**`blocked` is only ever returned to the blocker.** If the *recipient* blocked you, the code is
`recipient_dm_policy` - identical to a Friends-only refusal. That is deliberate; do not try to infer
the difference.

`POST /api/v1/messaging` is a **new** refusal point for 1:1 DMs. A send that previously always
succeeded can now `403`/`503`. Group sends are not re-evaluated.

Existing conversations are not retroactively closed - a policy change governs new conversations and
new one-to-one sends only.

---

## 4. Per-guild DM privacy

```http
GET /api/v1/guild/users/me/guild-privacy
PUT /api/v1/guild/guilds/{guildId}/privacy      { "allowDirectMessages": false }
```

```json
[{ "guildId": "gild_...", "allowDirectMessages": false, "isOverride": true, "updatedAt": "..." }]
```

`GET` returns **stored overrides only** - guilds absent from the list are using the global
`directMessagePolicy`. `PUT` returns `404` if you aren't a member.

This is what makes `directMessagePolicy: "FriendsAndServerMembers"` meaningful: a shared server only
admits a DM if the recipient hasn't disabled DMs *for that server*.

---

## 5. Presence - `Hidden` no longer leaks

`OnlineStatus.Hidden` (invisible) is now projected as `Offline` to **everyone except the user
themselves**, in both member lists and `guild.PresenceChanged`.

- **You** still see your own real status - your status picker keeps working.
- **Others** never see `Hidden` on the wire.

Any client branch handling `Hidden` for a non-self member is now dead code. `@here` already treated
`Hidden` as absent; unchanged.

---

## 6. Legal documents and consent

```http
GET    /api/v1/identity/legal/documents                        (anonymous)
GET    /api/v1/identity/legal/documents/{documentType}/{version}   (anonymous, text/markdown)
GET    /api/v1/identity/legal/consents
POST   /api/v1/identity/legal/consents      { "documentType": "Privacy", "version": "0.1.0" }
DELETE /api/v1/identity/legal/consents/{documentType}
```

`documentType` is `Terms` · `Privacy` · `Cookies`.

`GET /api/v1/identity/users/self` now carries:

```json
"consentRequired": [
  { "documentType": "Terms", "version": "0.2.0", "effectiveAt": "...", "url": "..." }
]
```

Always present, never null, `[]` is normal. **Non-empty means the user must be prompted** - a new
document version was published. Registration already records consent for the then-current versions,
so this only fires on a version bump.

`DELETE` always returns `409`:

```json
{ "code": "consent_not_withdrawable", "deleteAccount": "...", "optionalConsents": [...] }
```

Terms and Privacy can't be withdrawn while the account is active - the withdrawal path is account
deletion, and the response tells you where to send them. Optional consents (the data-use toggles in
§1) are withdrawn by PATCHing privacy settings instead, and take effect immediately.

> The shipped documents are **placeholders pending legal review**. Don't build copy that implies
> they're final.

---

## 7. Data export (GDPR Article 15/20)

```http
POST /api/v1/identity/data-exports                  → 202
GET  /api/v1/identity/data-exports                  → 200 [ ... ]
GET  /api/v1/identity/data-exports/{id}/download    → 302
```

```json
{
  "exportId": "...", "status": "Pending",
  "requestedAt": "...", "completedAt": null, "expiresAt": null,
  "failureReason": null, "missingServices": []
}
```

Statuses: `Pending` → `Running` → `Ready` | `Partial` | `Failed`, plus `Expired`.

| Status | Download | Show |
|---|---|---|
| `Ready` | `302` to a signed URL | Download button |
| `Partial` | **`302` - works** | Download button **plus** a warning naming `missingServices` |
| `Failed` | `409` | `failureReason` |
| `Expired` | `410` | Offer to request again |

**Gate your download button on `Ready` OR `Partial`.** A client checking `status === "Ready"` hides
a download the server would happily serve. `Partial` means some services didn't return their data -
`missingServices` lists which, and `failureReason` says the same in a sentence, so a client written
before `Partial` existed still shows something true.

Other behaviour:

- **One request per user per 24h** → `429` with `retryAfterSeconds` and a `Retry-After` header.
  `Failed` and `Partial` exports **don't** count against it.
- Artifacts expire after 7 days. `expiresAt` is populated once ready - show it, don't let the user
  discover it on a failed download.
- The `302` target is short-lived (~5 min). Follow it immediately; don't cache or share it.
- Every download is audited server-side.

Poll `GET /api/v1/identity/data-exports` for status. There is no push notification.

---

## 8. Rate limiting

Now actually enforced (it previously wasn't). Token bucket:

| Caller | Sustained | Burst |
|---|---|---|
| Authenticated | 50/s | 100 |
| Anonymous | 20/s | 40 |

One bucket per subject **across every route**.

```
429
Retry-After: 1
X-RateLimit-Limit / -Remaining / -Reset-After
```
```json
{ "message": "...", "retry_after": 0.42, "global": true }
```

- **`retry_after` is fractional.** Parse as a float. An int parser breaks.
- **`global` is `true`** - this genuinely is one bucket spanning all routes, so back off *all*
  requests, not just the one route.
- `X-RateLimit-Limit` reflects the bucket that rejected you (100 or 40).

Implement exponential backoff honouring `retry_after`. Bursts are absorbed by the reserve, but a
cold app start across many guilds is the realistic risk - batch where you can.

---

## 9. Isle positional voice

```http
POST /api/v1/isle/voice/join
```

Returns `403` when the user has `allowPositionalVoiceCapture: false`:

```json
{ "code": "positional_voice_consent" }
```

Also enforced on `/api/v1/isle/voice/connection`, which is the one route that hands out a way into
the proximity room. It replaced the four `/voice/cf/*` signalling routes when voice moved to LiveKit,
and the consent check moved with it - re-checked there rather than inferred from the registry entry,
because a registration carries a 2h TTL and survives socket drops, so a revocation landing between
join and connection would otherwise be honoured only on the next join.

The token that route mints grants the microphone source and nothing else, so consent now binds at the
SFU rather than only at the moment of asking.

**Revocation is immediate.** Turning the setting off mid-session tears the session down server-side:
the user is unregistered and their published track dropped. Handle a live session ending without a
client-initiated leave.

---

## 10. Profile fields

`GET /api/v1/social/profiles/{id}` and `/by-user/{id}` gained optional keys, each gated by the
subject's visibility setting:

```json
{ "mutualFriends": [...], "mutualServers": [...], "connections": [...], "birthday": "...", "activity": {...} }
```

**A field you may not see is absent from the payload entirely** - not null, not empty. Code for
absence; never assume a key exists.

- `mutualFriends`, `mutualServers` - live
- `connections` - linked external accounts, `{ type, externalId, displayName?, verified }`. Steam is
  the only type today; treat it as a list so more can be added.
- `birthday`, `activity` - gates are live, but **no data source is wired yet**, so these are always
  absent for now. Build for them; don't wait on them.

Friend requests (`POST /api/v1/social/relationships`) refuse with `403 { "code": "friend_request_policy" }`
for *all* of: no such user, not discoverable, they blocked you, and their policy excludes you. That
is deliberate - do not try to distinguish them, and show one neutral message.

---

## Client work summary

1. **Settings screen** for §1 - send only changed fields; handle `minor_restriction`; disable
   restricted controls for minors rather than letting them fail.
2. **Block/unblock UI** + blocked list; handle `social.FriendRemoved` on the blocked side.
3. **Refusal handling** for §3 - distinguish 403 codes from `503` (retry).
4. **Per-guild DM toggle** in server settings.
5. **Drop `Hidden` handling** for non-self members.
6. **Consent prompt** driven by `consentRequired` on `/users/self`.
7. **Data export screen** - poll, treat `Partial` as downloadable, surface `missingServices`.
8. **429 backoff** honouring fractional `retry_after` and `global`.
9. **Registration flow** - see the separate document; `userId` is gone.
10. **Optional-field absence** everywhere in §10.
