# Roleplay realtime events - frontend integration guide

Every lifecycle moment on the roleplay surface - characters, adoption, the approval queue, character
pages, scenes, dice and autoproxy - arrives on the existing guild hub, so a client that already
handles `guild.MessageCreated` needs no new connection and no new subscription.

Payloads are camel-cased on the wire; enums serialize as their names. Ids keep their prefixes:
`pers_` a character, `pgnt_` a grant, `chan_` a channel, `wkpg_` a page, `roll_` a roll.

## Who receives what

Three audiences, and which one an event gets is a privacy decision rather than a performance one.

| Audience | Which events | Why |
|---|---|---|
| Every present guild member | character, adoption, page and scene events | the same data the cast route already serves to any member |
| The character's players plus the module's permission holders | `guild.PersonaReviewRequested`, `guild.PersonaReviewCompleted`, `guild.PersonaGrant*` | a reviewer's reason and a grant say who plays whom, and their REST routes are gated |
| The caller alone | `guild.AutoproxyChanged` | it is that user's own composer state on their other devices |

Nothing on this surface ever carries the account behind a character to a third party. Where a payload
has a user id it is either the recipient's own, a reviewer acting in a moderation capacity, or a
grantee named to the people who administer grants.

---

## Characters

### `guild.PersonaCreated`

A guild-owned character goes to the whole guild. A personal one has no guild yet, so it goes to its
owner alone and `guildId` is null.

```jsonc
{
  "guildId": "gild_01J…",      // null for a personal character not yet adopted anywhere
  "personaId": "pers_01J…",
  "scope": "User",              // User | Guild
  "name": "Mayor Cogsgrove",
  "avatarUrl": null,
  "pronouns": "he/him",
  "color": "#4F8A6B",
  "shortBio": null,
  "isRetired": false,
  "updatedAt": "2026-08-18T09:14:22.115Z"
}
```

### `guild.PersonaUpdated`

Same shape as above. Fires once per guild the character is adopted into, plus once to its owner with
`guildId: null`.

It now carries `pronouns`, `color`, `shortBio` and `isRetired` alongside `name` and `avatarUrl`, so a
colour change invalidates a colour rather than the whole persona cache.

### `guild.PersonaDeleted`

```jsonc
{
  "guildId": "gild_01J…",      // null on the copy sent to the owner
  "personaId": "pers_01J…",
  "retired": true               // true when it had spoken, so the row stayed behind
}
```

`retired: true` means historic messages keep rendering under it. Drop it from switchers and cast
lists, not from message history.

---

## Adoption and the approval queue

### `guild.PersonaAdopted`

A character joined this guild's cast, either by being adopted or by being created guild-owned.

```jsonc
{
  "guildId": "gild_01J…",
  "personaId": "pers_01J…",
  "name": "Mayor Cogsgrove",    // the per-guild display name, falling back to the character's own
  "avatarUrl": null,
  "color": "#4F8A6B",
  "tag": "[Blackwater]",
  "wikiPageId": null,
  "approvalState": "Draft",     // Draft | Submitted | Approved | ChangesRequested
  "canSpeak": false
}
```

### `guild.PersonaProfileChanged`

Fires on every profile write - adoption, an override edit, a submission, an approval, a rejection.

```jsonc
{ "guildId": "gild_01J…", "personaId": "pers_01J…", "approvalState": "Submitted", "canSpeak": false }
```

`canSpeak` is the profile's own state - not retired, approval satisfied - and not a per-recipient
answer. Per-user grant resolution stays on `GET /guilds/{guildId}/personas`.

### `guild.PersonaUnadopted`

```jsonc
{ "guildId": "gild_01J…", "personaId": "pers_01J…" }
```

### `guild.PersonaReviewRequested`

To whoever holds `ApprovePersonas` here, plus whoever answers for the character.

```jsonc
{
  "guildId": "gild_01J…",
  "personaId": "pers_01J…",
  "name": "Mayor Cogsgrove",
  "wikiPageId": "wkpg_01J…",    // null when the character has no page yet
  "approvalState": "Submitted",
  "isResubmission": false,       // true when it had previously been sent back
  "submittedAt": "2026-08-18T09:14:22.115Z"
}
```

### `guild.PersonaReviewCompleted`

Same audience. This is the only event that carries the reviewer's reason; the guild-wide
`guild.PersonaProfileChanged` deliberately does not.

```jsonc
{
  "guildId": "gild_01J…",
  "personaId": "pers_01J…",
  "name": "Mayor Cogsgrove",
  "approvalState": "ChangesRequested",
  "approved": false,
  "reviewedByUserId": "user_01J…",
  "reviewedAt": "2026-08-18T09:20:11.004Z",
  "reason": "Give him a reason to be in town.",   // null on an approval
  "canSpeak": false
}
```

### `guild.PersonaGrantCreated` / `guild.PersonaGrantDeleted`

To whoever holds `ManageAnyPersona` here, plus the user the grant names.

```jsonc
{
  "guildId": "gild_01J…",
  "personaId": "pers_01J…",
  "grantId": "pgnt_01J…",
  "roleId": "role_01J…",        // exactly one of roleId / userId is set
  "userId": null
}
```

---

## Character pages

### `guild.PersonaPageCreated`

Arrives alongside `guild.WikiPageCreated`, which says a page appeared but not which character it is
for.

```jsonc
{
  "guildId": "gild_01J…",
  "personaId": "pers_01J…",
  "pageId": "wkpg_01J…",
  "title": "Mayor Cogsgrove",
  "categoryId": "wkca_01J…"
}
```

### `guild.PersonaPagePulled`

A page was merged from its reference copy. A `Preview` writes nothing and announces nothing, so this
never fires for one.

```jsonc
{
  "guildId": "gild_01J…",
  "personaId": "pers_01J…",
  "pageId": "wkpg_01J…",
  "strategy": "KeepLocal",      // KeepLocal | TakeUpstream (a Preview never fires this)
  "upstreamState": "current",   // current | behind | diverged
  "upstreamRevisionNumber": 12,
  "referenceRevisionNumber": 12,
  "conflictCount": 0
}
```

---

## Scenes

### `guild.SceneCreated`

The two `guild.ThreadCreated` events that go out alongside say two threads appeared; this is the one
that says a game started.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "parentChannelId": "chan_01J…",
  "name": "The Siege of Blackwater",
  "oocThreadId": "chan_01J…",
  "status": "Open",              // Open | Active | Paused | Concluded
  "participantPersonaIds": ["pers_01J…"],
  "turnOrder": ["pers_01J…"],
  "currentTurnPersonaId": null,
  "turnStartedAt": null,
  "turnDeadlineAt": null,
  "turnNumber": 0,
  "turnLengthHours": 48
}
```

### `guild.SceneTurnChanged`

Fires wherever the turn moves, the automatic advance on a post included - which is the case that
matters. A client that advances its own rail locally on seeing a post will drift; follow this
instead.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "previousPersonaId": "pers_01J…",
  "currentTurnPersonaId": "pers_01J…",
  "turnStartedAt": "2026-08-18T09:14:22.115Z",
  "turnDeadlineAt": "2026-08-20T09:14:22.115Z",
  "turnNumber": 47,
  "status": "Active"
}
```

### `guild.SceneUpdated`

Fires when the cast, the order, the status or the clock changes.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "status": "Active",
  "participantPersonaIds": ["pers_01J…"],
  "turnOrder": ["pers_01J…"],
  "currentTurnPersonaId": "pers_01J…",
  "turnStartedAt": "2026-08-18T09:14:22.115Z",
  "turnDeadlineAt": "2026-08-20T09:14:22.115Z",
  "turnNumber": 47,
  "postCount": 312,
  "conclusionNote": null,
  "oocThreadId": "chan_01J…"
}
```

### `guild.SceneConcluded`

Fires once, on the transition. A later edit to an already concluded scene's note is an edit to a
chronicle and arrives as `guild.SceneUpdated` only.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "status": "Concluded",
  "conclusionNote": "The siege broke at dawn.",
  "turnNumber": 47,
  "postCount": 312,
  "concludedAt": "2026-08-18T09:14:22.115Z"
}
```

### `guild.SceneTurnNudge`

To whoever answers for the character on the clock, plus - on the second miss - whoever holds
`ManageScenes`. `escalated` says which copy this is, and the two read differently enough to need it.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "sceneName": "The Siege of Blackwater",
  "personaId": "pers_01J…",
  "turnStartedAt": "2026-08-18T09:14:22.115Z",
  "turnDeadlineAt": "2026-08-20T09:14:22.115Z",
  "turnNumber": 47,
  "nudgeCount": 2,
  "escalated": true
}
```

---

## Dice

### `guild.DiceRolled`

To whoever can see the channel the roll landed in. The faces travel with it, so a client can animate
the roll rather than parse the message embed back out.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "rollId": "roll_01J…",
  "messageId": "mesg_01J…",
  "rollerUserId": null,          // null when the roll went out in character
  "personaId": "pers_01J…",
  "expression": "2d6 + 3",
  "total": 12,
  "breakdown": "2d6 (4, 5) + 3 = 12",
  "reason": "Perception",
  "visibility": "Public",
  "createdAt": "2026-08-18T09:14:22.115Z"
}
```

`rollerUserId` is withheld for a roll made in character. Naming the account behind a character is the
one thing this whole surface is built not to do.

---

## Autoproxy

### `guild.AutoproxyChanged`

To the caller alone, on every device. Fires when they set the state, and - under `Sticky` - when the
send path moves the latched character on its own, which nobody asked for and which a second device
otherwise never hears about.

```jsonc
{
  "guildId": "gild_01J…",
  "channelId": "chan_01J…",
  "mode": "Sticky",              // Off | Pinned | Sticky
  "personaId": "pers_01J…"       // null under Off
}
```

---

## The inbox

Three of these events change what `GET /api/v1/guild/inbox/tasks` returns, and therefore the header
badge. There is no separate inbox event for them - refetch `/inbox/tasks` and `/inbox/summary` on:

| Event | What it adds or removes |
|---|---|
| `guild.SceneTurnChanged` | a `SceneTurn` row, for whoever the turn moved to or away from |
| `guild.SceneTurnNudge` | nothing new, but the row is now overdue |
| `guild.PersonaReviewRequested` | a `PersonaReview` row for the reviewers |
| `guild.PersonaReviewCompleted` | clears that row, and may add a `PersonaChangesRequested` row for the character's players |

See [inbox-frontend-guide.md](./inbox-frontend-guide.md).
