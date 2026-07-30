# Guild Onboarding — Discord parity implementation plan

Audience: backend engineers working in `Guild.*`, `Bots.*`, `Import.*`.
Status: **implemented 2026-07-30** — all six phases landed in one pass. Kept as the design record;
the client-facing contract lives in `Guild.Application/docs/onboarding-frontend-guide.md`.

Deviations from the plan as written, all deliberate:

- The config read/write path was extracted into `OnboardingConfigService` (not foreseen below) so
  the first-party endpoint, the Discord-compatible bot handler and the importer share one
  implementation of validation + reconciliation.
- `POST /onboarding/accept` now requires a JSON body (`{}` at minimum). Wolverine's model binder
  rejects a truly empty body before the endpoint runs, so v1's no-body call is a breaking change —
  documented in the frontend guide's migration section rather than worked around.
- Phase 3.3 (config `Version` / re-prompting) was **not** built. It was optional in the plan and
  nothing in the parity gap needs it; the explicit `reprompt` flag remains the design if it's ever
  wanted.
- `FilterGrantableRolesAsync` filters in memory, not in SQL: `Permissions` is a ulong flag enum
  stored as `numeric`, and Postgres has no `&` for `numeric`. The in-memory unit tests could not
  catch this — the E2E scenario did.

Today's onboarding (`GuildOnboardingConfig` + `OnboardingCompletedAt`) implements Discord's
**Membership Screening** — a rules screen that soft-mutes a new member until they accept. It does
not implement Discord's **Server Onboarding** (question prompts that assign roles and unlock
channels), the **Channels & Roles** tab, or the **Welcome Screen**. This document plans all of it,
plus the correctness fixes the current v1 needs regardless.

Phases are ordered so each one ships independently and leaves the system in a working state.
Phase 1 is a prerequisite for everything else; Phase 4 is independent and can be pulled forward if
the client wants it sooner.

---

## 0. Conventions that apply to every phase

- Entities live in `Guild.Domain/Entity/`. Ones with their own lifecycle derive
  `BaseEntity<T>, IPrefixedEntity` with a static `Prefix` and a `Create(params)` factory (see
  `GuildScheduledEvent.cs`). One-row-per-guild config entities are plain classes keyed on
  `GuildId` (see `GuildAutoModConfig.cs`, `GuildOnboardingConfig.cs`).
- Register `DbSet`s and `modelBuilder.Entity<T>(...)` config in
  `Guild.Infrastructure/Persistence/MicroserviceContext.cs`. String lists use
  `.HasColumnType("text[]")`; nested object graphs use `OwnsOne(...).ToJson()` (see
  `TemplateSnapshot`, and note its comment — every nested collection needs its own `OwnsMany`).
- New enums mapped to Postgres need `options.MapEnum<T>()` in `OnConfiguring` **and** carry the
  usual annotation churn into every subsequent migration. Adding a value to `AuditActionType`
  rewrites the enum annotation list in the next migration — expected, see
  `20260730123810_AddCategoryUpdatedAuditActionType.cs`.
- Endpoints are Wolverine classes in `Guild.Application/Endpoints/`, `[Authorize]` on the class,
  services injected with `[NotBody]`. **Do not call `SaveChangesAsync` manually** — Wolverine's
  middleware commits the injected `DbContext`.
- Audit entries go through `AuditLogService.Log(...)`, which queues onto the same change tracker
  so the entry only exists if the action committed.
- Before any `dotnet ef` command on this machine, refresh `$env:Path` from Machine+User.
- Unit tests: `Guild.Tests/Endpoints/`, `Guild.Tests/Services/` using `TestGuildContext` +
  `FakeDistributedCache`. Flow tests: `Echo.E2E.Tests/Scenarios/`.

### Caps (ours, chosen to sit near Discord's)

| Thing | Cap |
|---|---|
| `RulesText` | 4000 chars |
| `DefaultChannelIds` | 25 |
| Prompts per guild | 10 |
| Options per prompt | 25 |
| Roles/channels per option | 10 each |
| Prompt/option title | 100 chars |
| Option description | 100 chars |
| Welcome-screen channels | 5 |

These are enforced server-side and documented in the frontend guide; they are not Discord's exact
published limits and should not be described as such.

---

## Phase 1 — Correctness pass on the existing v1

No new concepts. Fixes defects that will otherwise be inherited by every later phase.

### 1.1 Pending members must not be stripped when onboarding is off

`GuildPermissionService.GetMembershipAsync` (`Guild.Application/Services/GuildPermissionService.cs:79`)
computes `onboardingPending` purely from `OnboardingCompletedAt is null`, ignoring whether the
guild's onboarding is currently enabled. If an admin disables onboarding, or blanks `RulesText`,
members who joined while it was on stay permanently stripped of send/react/threads/voice, and
`/onboarding/me` returns `rulesText: null` so the client has no screen to render.

Two changes, both wanted:

1. **Read-side guard.** Extend the projection in `GetMembershipAsync` to also read the guild's
   `GuildOnboardingConfig.Enabled` (a `.Where(c => c.GuildId == guildId).Select(c => c.Enabled)`
   scalar query, or a join). Only treat pending as restricting when the config is enabled. The
   result is cached under the existing per-`(guild, user)` key, so the extra query is once per
   cache miss.
2. **Write-side cleanup.** When `PUT /onboarding` flips `Enabled` from true to false, bulk-set
   `OnboardingCompletedAt = UtcNow` for every pending member of that guild, then invalidate each
   affected member's permission cache. There is no bulk invalidate helper today — loop over
   `InvalidateUserPermissionsCacheAsync` and cap the work by only selecting members with
   `OnboardingCompletedAt == null`.

### 1.2 `/onboarding/me` exposes `enabled`

Add `enabled` to the response of `GetMyStatus` (`OnboardingEndpoint.cs:69`). Clients currently
cannot distinguish "no onboarding configured" from "onboarding configured with empty rules".

### 1.3 Validate `PUT /onboarding`

In `UpdateConfig` (`OnboardingEndpoint.cs:36`):

- `DefaultChannelIds`: dedupe, reject ids that do not resolve to a channel in this guild
  (`ctx.Channels.Where(c => c.GuildId == guildId)`), enforce the 25 cap → `400`.
- `RulesText`: enforce the 4000-char cap → `400`.
- Keep the existing "rules text required when enabled" rule for now; Phase 2 relaxes it to
  "rules text *or* at least one prompt".

### 1.4 Emit a member-update event on accept

`Accept` (`OnboardingEndpoint.cs:87`) flips the member from pending to active — Discord's
`GUILD_MEMBER_UPDATE` with `pending: false`. Inject `[NotBody] IMessageBus bus` and publish
`MemberUpdatedForBots { GuildId, UserId }` after the flip (only when it actually flipped).
`Bots.Application/Gateway/Handlers/MemberUpdatedForBotsHandler.cs` already fans this out to
connected bot gateways, so no work is needed on the Bots side for this item.

### 1.5 Audit metadata

`auditLog.Log(...)` for onboarding currently passes no metadata. Pass a small diff object
(`{ Enabled, RulesTextChanged: bool, DefaultChannelCount }`) — matching how `RoleEndpoint.cs:153`
records what changed. Do not log `RulesText` itself into the audit table.

### 1.6 Tests

- `GuildPermissionServiceTests`: pending member + onboarding **disabled** → permissions **not**
  stripped; pending member + enabled → stripped (this case exists today, keep it).
- `OnboardingEndpointTests`: disabling onboarding completes pending members; invalid channel id →
  `400`; oversize rules text → `400`; `/me` returns `enabled`; accept publishes
  `MemberUpdatedForBots` exactly once and not on the second accept.

**Size:** ~1 day. No migration.

---

## Phase 2 — Onboarding prompts (the core parity work)

Discord's `PUT /guilds/{id}/onboarding` takes the whole config as one document: `enabled`,
`mode`, `default_channel_ids`, and a full `prompts[]` tree. Each prompt option grants
`role_ids[]` and/or `channel_ids[]`, applied the moment the member answers.

### 2.1 Domain

New enums in `Guild.Domain/Enums/`:

```csharp
public enum OnboardingPromptType { MultipleChoice, Dropdown }

/// <summary>Default: only DefaultChannelIds count toward "what a new member sees".
/// Advanced: channels reachable through prompt options count too.</summary>
public enum OnboardingMode { Default, Advanced }
```

New entities in `Guild.Domain/Entity/`:

```csharp
public class GuildOnboardingPrompt : BaseEntity<GuildOnboardingPrompt>, IPrefixedEntity
{
    public static string Prefix { get; } = "onbp";

    public string GuildId { get; set; }
    public virtual Aggregates.Guild Guild { get; set; }

    public string Title { get; set; }
    public OnboardingPromptType Type { get; set; }
    public bool SingleSelect { get; set; }
    public bool Required { get; set; }

    /// <summary>False = not shown during the join flow, only in the post-join
    /// "Channels &amp; Roles" screen (Phase 3).</summary>
    public bool InOnboarding { get; set; } = true;
    public int Position { get; set; }

    public virtual ICollection<GuildOnboardingPromptOption> Options { get; set; } = [];
}

public class GuildOnboardingPromptOption : BaseEntity<GuildOnboardingPromptOption>, IPrefixedEntity
{
    public static string Prefix { get; } = "onbo";

    public string PromptId { get; set; }
    public virtual GuildOnboardingPrompt Prompt { get; set; }

    public string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>Unicode emoji or a guild emoji id — same loose convention the client already
    /// uses for reactions; not validated against GuildEmoji.</summary>
    public string? Emoji { get; set; }

    public List<string> RoleIds { get; set; } = [];
    public List<string> ChannelIds { get; set; } = [];
    public int Position { get; set; }
}
```

`GuildOnboardingConfig` gains `public OnboardingMode Mode { get; set; } = OnboardingMode.Default;`.

Two member-state tables — they are deliberately separate:

```csharp
/// <summary>What the member picked. Survives admin edits to the option.</summary>
public class GuildMemberOnboardingResponse
{
    public string MemberId { get; set; }          // composite PK (MemberId, OptionId)
    public virtual GuildMember Member { get; set; }
    public string OptionId { get; set; }
    public virtual GuildOnboardingPromptOption Option { get; set; }
    public string PromptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What onboarding actually granted, recorded at grant time. Revocation (Phase 3) only
/// ever touches rows in this table, so a role a moderator assigned by hand is never stripped
/// because the member deselected an option that happens to reference the same role — and an
/// admin editing an option's RoleIds afterwards can't cause us to revoke the wrong role.</summary>
public class GuildOnboardingGrant : BaseEntity<GuildOnboardingGrant>, IPrefixedEntity
{
    public static string Prefix { get; } = "onbg";

    public string GuildId { get; set; }
    public string MemberId { get; set; }
    public string OptionId { get; set; }
    public string? RoleId { get; set; }              // exactly one of RoleId / ChannelId set
    public string? ChannelId { get; set; }
    public string? ChannelPermissionId { get; set; } // the ChannelPermission row we created
}
```

Indexes: `(GuildId, Position)` on prompts, `(PromptId, Position)` on options,
`(MemberId)` on responses and grants.

### 2.2 Config API

`GET`/`PUT /api/v1/guilds/{guildId}/onboarding` (`ManageGuild`, unchanged) grow to:

```jsonc
{
  "enabled": true,
  "mode": "Default",                    // "Default" | "Advanced"
  "rulesText": "1. Be nice",            // optional once prompts exist
  "defaultChannelIds": ["chan_..."],
  "prompts": [{
    "id": "onbp_...",                   // omit to create
    "title": "What brings you here?",
    "type": "MultipleChoice",
    "singleSelect": false,
    "required": true,
    "inOnboarding": true,
    "position": 0,
    "options": [{
      "id": "onbo_...",
      "title": "Gaming",
      "description": "Get the gamer role",
      "emoji": "🎮",
      "roleIds": ["role_..."],
      "channelIds": ["chan_..."],
      "position": 0
    }]
  }]
}
```

`PUT` is a **whole-document replace**, matching Discord. Reconcile rather than delete-and-recreate:
ids present in the payload and in the DB are updated in place, ids absent from the payload are
deleted, entries without an id are created. This preserves `GuildMemberOnboardingResponse` rows
across ordinary edits. Deleting an option cascades its response rows; it does **not** revoke
already-granted roles or channels (documented behavior — matches Discord, and mass-revoking on an
admin's config edit would be a footgun).

DTOs go in `Guild.Application/Dtos/Request/` alongside `UpdateOnboardingConfigDto`.

### 2.3 Validation — `OnboardingValidationService`

Put this in `Guild.Application/Services/` rather than inline in the endpoint; Phase 3 and the
Discord-compat endpoint (Phase 5) both need it.

Structural:
- caps from §0; each prompt has ≥1 option; each option has ≥1 role or channel;
- `enabled` requires `RulesText` **or** ≥1 prompt with `InOnboarding = true`;
- positions normalized to 0..n-1 on write (don't trust client positions);
- all `ChannelIds` resolve to channels in this guild; all `RoleIds` to roles in this guild;
- reject the `@everyone` role (`RoleType.Everyone`) in options — it's already universal.

Security — **the important part.** An option that grants a role is self-service role assignment,
so a careless config is a privilege-escalation path:

- Reject any role carrying `Superadmin`, `ManageGuild`, `ManagePermissions`, `ManageChannel`,
  `KickMembers`, `BanMembers`, `ModerateMembers`, `ViewAuditLog`, `EditAnyMessage`,
  `DeleteAnyMessage`, `ManageAnyThread`, `ManageEmojis`, or the wiki `EditAnyWikiPage` /
  `DeleteWikiPages` / `ManageWikiStructure` family. Keep the blocked set as a single `Permissions`
  constant next to `MuteStrippedPermissions` in `GuildPermissionService` so it is reviewable in one
  place.
- Require the configuring user to pass `CanManageRoleAsync(userId, guildId, roleId)` for every
  referenced role — you cannot wire up a role you couldn't assign by hand.
- **Re-run both checks at apply time**, not just config time. A role that was harmless when the
  prompt was written can be granted `ManageGuild` later; the apply path must refuse to hand it out
  and skip that role (grant the rest of the option, log it) rather than fail the member's join.

### 2.4 Apply path — `POST /api/v1/guilds/{guildId}/onboarding/accept`

Body becomes optional-but-typed:

```jsonc
{ "responses": [{ "promptId": "onbp_...", "optionIds": ["onbo_..."] }] }
```

Sequence:

1. Member exists (`404` otherwise). If already completed → `200` no-op (unchanged contract).
2. Validate: every `required` + `inOnboarding` prompt answered; `singleSelect` prompts have ≤1
   option; option ids belong to the named prompt and guild → `400` with the offending prompt id.
3. Re-run the role safety checks from §2.3.
4. Grant roles: insert `RoleMember` rows for roles the member doesn't already hold.
5. Grant channels: insert member-scoped `ChannelPermission` rows
   (`MemberId`, `ChannelId`, `AllowPermissions = ViewChannel`). Reuse whatever helper
   `PermissionOverwriteEndpoint` uses so the overwrite shape stays consistent.
6. Record `GuildMemberOnboardingResponse` + `GuildOnboardingGrant` rows.
7. Set `OnboardingCompletedAt`, `InvalidateUserPermissionsCacheAsync`, publish
   `MemberUpdatedForBots` (Phase 1.4 already added the publish).

All of it in one Wolverine-committed unit of work — a partial grant that leaves the member
completed but role-less is the failure mode to avoid.

`GET /onboarding/me` returns the prompts to render (`inOnboarding: true` only) alongside the
existing `completed` / `rulesText` / `defaultChannelIds` / `enabled` fields.

### 2.5 Audit

Add to `AuditActionType`: `OnboardingPromptCreated`, `OnboardingPromptUpdated`,
`OnboardingPromptDeleted`. `PUT` emits one entry per structural change plus the existing
`OnboardingConfigUpdated`. (Migration will rewrite the enum annotation block — expected.)

### 2.6 Migration + tests

`dotnet ef migrations add AddGuildOnboardingPrompts -p Guild.Infrastructure`.

Tests:
- validation service: each rule above, one test per rejection, plus the privileged-role and
  hierarchy rejections;
- reconciliation: update-in-place keeps response rows, missing id deletes, new entry creates;
- apply: roles + overwrites granted, cache invalidated, event published, required-prompt omission
  → `400`, single-select violation → `400`, role that became privileged after config is skipped
  but the rest of the option applies;
- `GuildPermissionServiceTests`: a member-scoped `ViewChannel` overwrite from onboarding actually
  makes the channel visible.

**Size:** ~4–5 days. One migration.

---

## Phase 3 — "Channels & Roles" (post-join re-answering)

Discord lets members revisit prompts at any time; prompts with `in_onboarding: false` *only* live
there. Phase 2 already stores `InOnboarding` and the grant provenance, so this is mostly diffing.

### 3.1 Endpoints

- `GET /api/v1/guilds/{guildId}/onboarding/prompts` — any member of the guild. Returns **all**
  prompts (both `inOnboarding` values) with a `selected: bool` per option, derived from the
  member's `GuildMemberOnboardingResponse` rows.
- `PUT /api/v1/guilds/{guildId}/onboarding/me/responses` — body identical to accept's
  `responses`. Full replace of the member's picks.

### 3.2 Diff semantics

Compute `added` / `removed` option sets against existing response rows.

- **Added**: same grant path as §2.4 steps 3–6.
- **Removed**: for each `GuildOnboardingGrant` row belonging to a removed option —
  - skip if any *still-selected* option grants the same role/channel;
  - otherwise delete the `RoleMember` row / the `ChannelPermission` row referenced by
    `ChannelPermissionId`, then delete the grant row.
  - Never touch a role or overwrite with no grant row behind it.
- Required prompts must still be answered after the change → `400`.
- Invalidate the permission cache, publish `MemberUpdatedForBots`.

### 3.3 Optional: re-prompting on rules change

Add `Version` (int) to `GuildOnboardingConfig` and `AcceptedVersion` to `GuildMember`. `PUT`
accepts an explicit `reprompt: true` flag that bumps `Version`; the pending check becomes
`AcceptedVersion < config.Version`. Keep it opt-in and explicit — silently re-muting an entire
server because an admin fixed a typo in the rules is not acceptable behavior, and Discord's own
`version` field on the verification form is a timestamp, not a re-gate trigger we should imitate
blindly.

Tests: revoke-only-what-onboarding-granted (seed a manually assigned role with the same id and
assert it survives), re-select after deselect, required-prompt guard, cache invalidation.

**Size:** ~2–3 days. One small migration (only if 3.3 is included).

---

## Phase 4 — Welcome screen

Independent of Phases 2–3.

```csharp
public class GuildWelcomeScreen           // one row per guild, keyed on GuildId
{
    public string GuildId { get; set; }
    public virtual Aggregates.Guild Guild { get; set; }
    public bool Enabled { get; set; }
    public string? Description { get; set; }          // 140 chars
    public DateTimeOffset UpdatedAt { get; set; }
    public virtual ICollection<GuildWelcomeChannel> Channels { get; set; } = [];
}

public class GuildWelcomeChannel : BaseEntity<GuildWelcomeChannel>, IPrefixedEntity
{
    public static string Prefix { get; } = "wlcm";
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public string Description { get; set; }           // 50 chars
    public string? Emoji { get; set; }
    public int Position { get; set; }
}
```

- `GET`/`PUT /api/v1/guilds/{guildId}/welcome-screen` — `PUT` requires `ManageGuild`, `GET` is
  readable by any member.
- Surface it on the **invite preview** (`GET /api/v1/invites/code/{code}`,
  `InviteEndpoint.cs:92`) so the client can render the welcome splash *before* the user joins —
  that is the whole point of the feature. Only include it when `Enabled`.
- Max 5 channels, all validated against the guild; dedupe by `ChannelId`.
- Audit: `WelcomeScreenUpdated`.

Migration `AddGuildWelcomeScreen`. Tests: cap enforcement, cross-guild channel rejection, invite
preview includes it only when enabled.

**Size:** ~1–2 days.

---

## Phase 5 — Ecosystem wiring

Each item is small; together they are what makes the feature real rather than an isolated table.

### 5.1 Discord-compatible bot REST

New `Bots.Application/Endpoints/Discord/DiscordGuildOnboardingEndpoint.cs`:

- `GET`/`PUT /api/discord/v10/guilds/{guildId}/onboarding`, snake_case Discord shape
  (`prompts[].options[].role_ids`, `default_channel_ids`, `mode` as `0|1`, prompt `type` as
  `0|1`). Requires MANAGE_GUILD + MANAGE_ROLES equivalent, i.e. both `ManageGuild` and the
  per-role `CanManageRoleAsync` checks already in §2.3.
- Needs Wolverine contracts in `Guild.Contracts/Bus/Request|Response`
  (`GetGuildOnboardingRequest/Response`, `UpdateGuildOnboardingRequest/Response`) with handlers in
  `Guild.Application` delegating to the same validation service — the Bots service must not reach
  into the Guild database directly for this.

### 5.2 `pending` on member payloads

`DiscordGuildMemberEndpoint.ListMembersAsync` currently emits `user/nick/roles/joined_at`. Add
`pending`. Requires `ListGuildMembersResponse` (and the single-member request used by the gateway
handlers) to carry `OnboardingCompletedAt` or a precomputed `Pending` bool. With Phase 1.4's
event already flowing, bots then see the full Discord lifecycle: join with `pending: true`,
`GUILD_MEMBER_UPDATE` with `pending: false` on accept.

### 5.3 Discord import

`Import.Application/Discord/DiscordApiClient.cs` gains `GET /guilds/{id}/onboarding` and
`GET /guilds/{id}/welcome-screen`; payload types into `DiscordApiPayloads.cs`.
`StartDiscordStructureImportHandler` / `DiscordStructureReconciliationService` already build a
Discord-id → Echo-id map for channels and roles — reuse it to remap `channel_ids` / `role_ids`
inside prompt options. Drop (and log) any option reference that doesn't resolve, and run the
imported config through the same validation service so an imported prompt can't smuggle in a
privileged role. Extend `Import.Application/docs/discord-import-implementation.md`.

### 5.4 Server templates

`TemplateSnapshot` (`Guild.Domain/Entity/GuildTemplate.cs`) is a JSON blob that deliberately holds
no ids. Add `TemplateOnboarding` carrying rules text, mode, and prompts whose options reference
roles/channels **by name** (templates outlive the source guild, so ids are meaningless). On apply,
resolve names against the freshly created roles/channels and skip unresolvable references. Note in
the snapshot's doc comment that this is why names are used.

### 5.5 Pending-members report

`GET /api/v1/guilds/{guildId}/members/pending` (`ModerateMembers` or `ManageGuild`) — retires the
"no moderator visibility into who hasn't accepted" limitation in the current frontend guide.
Paginated, returns member id / user id / nickname / joined at.

**Size:** ~3–4 days total, parallelizable across the five items.

---

## Phase 6 — Docs and end-to-end

- `Guild.Application/docs/onboarding-frontend-guide.md` — **done up front**, rewritten as the full
  client contract (prompts, accept body, Channels & Roles, welcome screen, caps, error catalogue,
  v1 migration notes). The welcome screen lives in this same doc rather than a separate guide: from
  a client's point of view it is one join experience. Keep it in sync as phases land.
- E2E scenario in `Echo.E2E.Tests/Scenarios/GuildOnboardingFlowTests.cs`: configure onboarding with
  a prompt → join by invite → member is pending and cannot send → accept with responses → role and
  channel overwrite present, member can send → change picks via Channels & Roles → role revoked.

**Size:** ~1–2 days.

---

## Risks and decisions to be aware of

1. **Privilege escalation is the main risk in this feature.** Prompt options hand out roles with
   no moderator in the loop. The blocked-permission set and the double check (config time *and*
   apply time) in §2.3 are not optional hardening — they are the feature's security boundary.
2. **Permission cache invalidation is per `(guild, user)`.** Any bulk operation (Phase 1.1's
   auto-complete on disable, a future mass re-prompt) must loop. There is no bulk invalidate today;
   if these paths grow, add one rather than scattering loops.
3. **Deleting a prompt option does not revoke what it granted.** Deliberate, matches Discord, and
   documented in both the frontend guide and the endpoint. Revocation happens only through a
   member deselecting in Channels & Roles.
4. **Discord's ≥7 default channels / ≥5 @everyone-sendable requirement is intentionally not
   enforced** — it is tied to Discord's Community program, which has no analogue here. Expose it as
   an advisory `readiness` object on `GET /onboarding` if the client wants to show the same nudge.
5. **`DefaultChannelIds` stay advisory** (they don't grant visibility). Prompt options are the
   mechanism that actually changes what a member can see. Keep that distinction explicit in the
   docs — it is the most likely thing for a frontend to get wrong.
6. **Enum migrations churn.** Every new `AuditActionType` value rewrites the enum annotation block
   in the next migration. Review those diffs for accidental value drops.

## Suggested sequencing

| Order | Phase | Blocked by | Size |
|---|---|---|---|
| 1 | Phase 1 — correctness | — | ~1d |
| 2 | Phase 2 — prompts | 1 | ~4–5d |
| 3 | Phase 4 — welcome screen | — (can run in parallel with 2) | ~1–2d |
| 4 | Phase 3 — Channels & Roles | 2 | ~2–3d |
| 5 | Phase 5 — ecosystem wiring | 2 (5.2 only needs 1) | ~3–4d |
| 6 | Phase 6 — docs + E2E | all | ~1–2d |
