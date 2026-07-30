# Guild onboarding & welcome screen — frontend integration guide

Audience: web/desktop/mobile client engineers.

Everything a new member sees between "clicked an invite" and "fully participating" lives here:
the welcome splash, the rules gate, the question prompts that assign roles and unlock channels,
and the post-join screen where members can change those picks.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly. Guild endpoints are reached under the `/api/v1/guild/` prefix; the gateway
strips the `guild` segment before forwarding, which is why the paths read
`/api/v1/guild/guilds/{guildId}/...`. That doubled-looking segment is correct.

**Status:** sections marked **v1** are live today. Sections marked **NEW** are landing as part of
the onboarding parity work and describe the target contract. See "What changed from v1" at the
bottom if you already integrated against the original rules-only API.

---

## 1. The model in one screen

A guild's onboarding config has three independent pieces. A guild may use any combination.

| Piece | What it does | Blocks participation? |
|---|---|---|
| **Rules text** | A block of text the member must explicitly accept | Yes — until accepted |
| **Prompts** | Questions whose answers grant roles and unlock channels | Only if `required` |
| **Default channels** | Channels to highlight to a newcomer | No — advisory only |

A member who joins while onboarding is enabled is **pending**: they can read everything they'd
normally see, but cannot send messages, react, create threads, or connect to voice. Accepting
lifts that immediately. This is the same underlying mechanism as a moderator timeout, so it fails
the same way (`403`) if you try to act while pending.

**Default channels do not grant visibility.** They are a hint for your UI ("check out #welcome and
#general"). What actually changes a member's visibility is a prompt option carrying `channelIds` —
answering it grants a real per-member permission overwrite. Getting this backwards is the most
common integration mistake.

---

## 2. Admin — reading and writing the config

```
GET  https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding
PUT  https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding
```

Both require `ManageGuild`. `403` otherwise. A guild that has never configured onboarding returns
a fully-defaulted document (`enabled: false`, empty everything) rather than `404`.

```ts
interface OnboardingConfig {
  enabled: boolean;
  mode: 'Default' | 'Advanced';   // NEW — advisory, see §2.3
  rulesText?: string | null;
  defaultChannelIds: string[];
  prompts: OnboardingPrompt[];    // NEW
}

interface OnboardingPrompt {      // NEW
  id?: string;                    // omit to create; "onbp_..." to update in place
  title: string;
  type: 'MultipleChoice' | 'Dropdown';
  singleSelect: boolean;          // true = radio, false = checkboxes
  required: boolean;              // must be answered to finish onboarding
  inOnboarding: boolean;          // false = only in Channels & Roles (§4), not the join flow
  position: number;
  options: OnboardingPromptOption[];
}

interface OnboardingPromptOption {  // NEW
  id?: string;                      // omit to create; "onbo_..." to update in place
  title: string;
  description?: string | null;
  emoji?: string | null;            // unicode emoji, or a guild emoji id
  roleIds: string[];                // granted when picked
  channelIds: string[];             // made visible when picked
  position: number;
}
```

### 2.1 `PUT` replaces the whole document

Send the complete config every time — there are no per-prompt endpoints. The server reconciles:

- a prompt/option **with** an `id` that exists → updated in place;
- a prompt/option **without** an `id` → created, and the response carries the generated id;
- anything in the database but **absent** from your payload → deleted.

So the edit loop is: `GET`, mutate the object, `PUT` it back. Round-trip the ids you were given;
dropping one deletes that prompt and every member's answer to it.

**Deleting an option does not take back what it already granted.** Members who picked it keep the
role and the channel access. Revocation only happens when a member deselects the option
themselves (§4). This matches Discord and is deliberate — an admin fixing a typo shouldn't strip
roles from half the server.

### 2.2 Validation

`400` with a plain-text reason on any of these. Worth mirroring client-side so the settings screen
can flag problems inline:

| Rule | |
|---|---|
| `enabled: true` requires `rulesText` **or** at least one prompt with `inOnboarding: true` | |
| Every prompt has ≥ 1 option | |
| Every option has ≥ 1 entry across `roleIds` + `channelIds` | An option that grants nothing does nothing |
| All `channelIds` / `roleIds` / `defaultChannelIds` must exist **in this guild** | |
| The `@everyone` role may not appear in `roleIds` | Everyone already has it |
| Roles carrying moderation or management permissions may not appear in `roleIds` | See §2.4 |
| You must be able to assign each referenced role yourself (role hierarchy) | See §2.4 |

Caps — all `400` when exceeded:

| | |
|---|---|
| `rulesText` | 4000 chars |
| `defaultChannelIds` | 25 |
| Prompts per guild | 10 |
| Options per prompt | 25 |
| `roleIds` / `channelIds` per option | 10 each |
| Prompt & option `title` | 100 chars |
| Option `description` | 100 chars |

`position` is normalized server-side to `0..n-1` in the order you send. Send whatever you like;
read back what you get.

### 2.3 `mode`

`Default` vs `Advanced` mirrors Discord's flag for whether channels reachable through prompt
options count toward "what a newcomer can see". It is **advisory** — the server does not enforce a
minimum-channel requirement the way Discord's Community program does. Store it, show it if your
settings UI wants parity, otherwise leave it `Default`.

### 2.4 Why a role can be rejected

Prompt options are self-service role assignment: nobody moderates the moment a member picks one.
Two guardrails, both returning `400`:

1. **Privileged roles are refused outright** — anything carrying admin, guild/channel/permission
   management, kick/ban/timeout, audit-log, emoji, message- or thread-moderation, or wiki-editing
   permissions.
2. **Role hierarchy applies** — you can only wire up roles you could assign by hand.

These are re-checked when a member answers, not just when you save. If a role gains a privileged
permission after the prompt was created, that role is silently skipped at grant time (the rest of
the option still applies). Don't rely on config-time success meaning a role will be granted
forever.

---

## 3. Member — the join flow

### 3.1 What to show

```
GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding/me
```

Any member of the guild. `404` if you aren't a member.

```ts
interface MyOnboarding {
  enabled: boolean;             // NEW — false means this guild has no onboarding at all
  completed: boolean;
  rulesText?: string | null;
  defaultChannelIds: string[];
  prompts: OnboardingPrompt[];  // NEW — only prompts with inOnboarding: true
}
```

Call it right after joining, or on every guild-open (it's cheap). Show the onboarding screen when
`enabled && !completed`. When `enabled` is `false`, never show anything — even if `completed` is
`false`, which can happen for a member who joined while onboarding was on and it was later turned
off. Those members are not restricted.

Render `rulesText` verbatim: respect whitespace and line breaks, don't parse it as markdown or
anything more structured than plain text.

### 3.2 Accepting

```
POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding/accept
```

```jsonc
{
  "responses": [                                    // NEW — omit or send [] if there are no prompts
    { "promptId": "onbp_...", "optionIds": ["onbo_...", "onbo_..."] }
  ]
}
```

`200` on success. Idempotent — accepting twice is a no-op, not an error, and the second call does
**not** re-apply responses.

**A JSON body is required**, even when the guild has no prompts — send `{}` or
`{"responses": []}`. A completely empty request body is rejected by the model binder before the
endpoint runs. This is the one breaking change from v1; see §8.

Errors:

| Status | Meaning |
|---|---|
| `400` | A `required` prompt wasn't answered — the message names the prompt id |
| `400` | More than one option sent for a `singleSelect` prompt |
| `400` | An option id doesn't belong to the prompt it was sent under, or to this guild |
| `404` | You're not a member of this guild |

On success, roles and channel access are applied and restrictions lift **immediately** — no need
to reconnect or re-fetch permissions before the member's next action. Do re-fetch the guild's
channel list and the member's own roles, though: both may have changed.

### 3.3 What "pending" restricts

While `enabled && !completed`, the member:

- **can** view every channel they'd normally have access to, read history, see who's online;
- **cannot** send messages, add reactions, create threads, or connect to voice — these fail `403`,
  the same as for a timed-out member.

The natural UX is to let them browse with the composer disabled and an inline "Accept the rules to
start chatting" prompt, rather than a blocking full-screen modal. A modal works too.

Onboarding is **not retroactive**: turning it on never gates members who were already in the
guild.

---

## 4. Channels & Roles — changing picks after joining **NEW**

Prompts with `inOnboarding: false` never appear in the join flow; they exist only here. Prompts
with `inOnboarding: true` appear in both.

```
GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding/prompts
```

Any member. Returns every prompt, with the member's current picks marked:

```ts
interface MemberPrompt extends OnboardingPrompt {
  options: (OnboardingPromptOption & { selected: boolean })[];
}
```

```
PUT https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding/me/responses
```

```jsonc
{ "responses": [{ "promptId": "onbp_...", "optionIds": ["onbo_..."] }] }
```

Full replace of the member's picks across all prompts — send the complete set, not a delta. A
prompt omitted entirely is treated as "no options selected" and its grants are revoked.

- **Newly selected** options grant their roles and channels.
- **Deselected** options revoke exactly what onboarding granted for them — never a role a
  moderator assigned by hand, and never something still granted by another selected option.
- `required` prompts must still have an answer afterwards → `400`.

Same `400` catalogue as §3.2 otherwise. After a successful call, re-fetch channels and the
member's roles.

---

## 5. Welcome screen **NEW**

The splash shown to someone looking at an invite, before they join.

```
GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/welcome-screen   // any member
PUT https://api.venta.gg/api/v1/guild/guilds/{guildId}/welcome-screen   // ManageGuild
```

```ts
interface WelcomeScreen {
  enabled: boolean;
  description?: string | null;   // 140 chars
  channels: WelcomeChannel[];    // max 5
}

interface WelcomeChannel {
  channelId: string;
  description: string;           // 50 chars
  emoji?: string | null;
  position: number;
}
```

`400` on: more than 5 channels, a `channelId` from another guild, a duplicate `channelId`, or an
over-length string. `position` is normalized like prompts.

**For the pre-join case, don't call the endpoint above** — a non-member can't. The invite preview
carries it instead:

```
GET https://api.venta.gg/api/v1/guild/invites/code/{code}
```

The response gains a `welcomeScreen` field, present only when the guild has one and it's
`enabled`, `null` otherwise. Render it on the invite-accept screen.

---

## 6. Moderator — who hasn't accepted **NEW**

```
GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/members/pending?limit=100&offset=0
```

Requires `ModerateMembers` or `ManageGuild`. Members still sitting on the rules screen:

```ts
interface PendingMember {
  memberId: string;
  userId: string;
  nickname?: string | null;
  joinedAt: string;   // ISO-8601
}
```

Useful for a "3 members haven't accepted the rules" nudge in the moderation view. Pending members
can be kicked and banned normally.

---

## 7. Bots and realtime

- A member completing onboarding emits the member-update event to connected bots — the equivalent
  of Discord's `GUILD_MEMBER_UPDATE` with `pending: false`.
- The Discord-compatible surface exposes `pending` on member payloads and mirrors this config at
  `GET`/`PUT https://api.venta.gg/api/discord/v10/guilds/{guildId}/onboarding` in Discord's
  snake_case shape (`prompts[].options[].role_ids`, `default_channel_ids`, numeric `mode` and
  prompt `type`). It accepts one non-Discord extra field, `rules_text`, so a bot can set the rules
  screen without a second call. That surface is for bot libraries; first-party clients should use
  the `/api/v1/guild/` endpoints documented above.
- Importing a Discord server brings its onboarding prompts and welcome screen across, remapped onto
  the newly created roles and channels. References that don't survive the import — and roles our
  privileged-role rule rejects — are dropped; the rest of the import is unaffected.
- Server templates capture onboarding too, referencing roles and channels by name (ids don't
  survive into a new guild). Unresolvable references are dropped on apply.

---

## 8. What changed from v1

If you already shipped against the original rules-only API, nothing you built breaks:

| | |
|---|---|
| `GET`/`PUT /onboarding` | Same URL, same fields, **plus** `mode` and `prompts`. Omitting `prompts` on `PUT` deletes all prompts — always round-trip the full document. |
| `GET /onboarding/me` | Same fields, **plus** `enabled` and `prompts`. Start gating your screen on `enabled` — this fixes members getting stuck on an empty rules screen after an admin disabled onboarding. |
| `POST /onboarding/accept` | **Breaking:** now requires a JSON body — send `{}` at minimum. Add `responses` once the guild has prompts, or `required` prompts will reject the call. |

Retired limitations from the v1 guide: there is now a multi-step wizard with role and channel
self-assignment (§2, §3), a post-join screen to change those picks (§4), and a moderator report of
who hasn't accepted (§6).
