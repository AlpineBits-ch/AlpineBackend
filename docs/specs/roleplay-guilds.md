# Roleplay guilds

First-class support for text roleplay: people writing as characters, in scenes, over months.

Two audiences share one broken primitive today.

* **Play-by-post TTRPG.** D&D, Pathfinder, Starfinder, run asynchronously over days. Needs dice,
  character sheets, turn order, a GM. Served today by Avrae, RPG Sage, Rod of Discord, Modron.
* **Freeform / original-character roleplay.** No dice, prose-heavy, character-driven. Served today
  by Tupperbox and PluralKit, plus an application bot, plus an activity tracker.

Both run on Discord webhook proxying, and every pain point below is downstream of that one choice.
The whole of this spec rests on a single structural advantage: Echo can put a character on a
message without giving up who actually typed it.

That advantage is currently theoretical. §9.1 lists four places where the author identity is already
dropped before it reaches storage, the realtime fan-out, push, or another instance. Those are
prerequisites, not follow-up work.

---

## 1. Why webhook proxying is the wrong primitive

Tupperbox and PluralKit delete the user's message and repost it through a webhook. The character
appears, and the author is destroyed. What breaks, per PluralKit's own FAQ and Discord's docs:

* Proxied messages are not users, so they cannot be blocked, and blocking the bot does not help.
* They cannot be reply-pinged.
* Editing needs a reaction workaround rather than the normal edit path.
* The member list renders wrong on Discord mobile.
* External emoji need the bot to have joined the source guild.
* The audit log records webhook creation, not who posted through it. A moderator seeing abuse from a
  character has no attribution path. Tupperbox ships a reaction that DMs you the real sender, which
  is precisely a workaround for the missing author.
* Character data lives in a third party's database keyed to a Discord id, with no export.
* One bot outage takes character speech away from every server on it at once.

Echo does not have to accept any of this. `Message` already carries `AuthorDisplayName` and
`AuthorAvatarUrl` as per-message overrides (`Messaging.Domain/Entities/Message.cs:89`) alongside a
real `AuthorId`. Those were added for webhooks. Pointing a persona at them, and leaving `AuthorId`
as the user, means blocking, reply-pings, `EditOwnMessages` and `DeleteAnyMessage` keep working with
no special cases, and the audit trail stays intact.

That is the feature. Everything else here is scaffolding around it.

### 1.1 The other gaps

| Gap | What people do instead |
|---|---|
| 2000-character ceiling | Write in Google Docs or pastebin, split the post, lose formatting and reply threading |
| No searchable archive | Plot points get buried; long campaigns become unreadable |
| No turn or pacing tracking | Manual `@` mentions, or a bot's `/turn` and `/done` |
| Client-rolled dice | Trust the roller; forum play-by-post sites solved this with server rolls and a log |
| No character approval flow | A modal bot, a staff channel, approve/deny buttons, a manual role grant |
| IC/OOC separation | A naming convention and a hand-made twin channel |

Echo enforced no message length limit on the send path at all, which is a generous accident rather
than a decision. It is now a plan entitlement - see §9.2.

---

## 2. Personas are not members

A persona is a costume, not a subject of authorization. It gets its own entity and never a
`GuildMember` row.

`MemberType.Persona` exists in the enum and in the `member_type` Postgres enum with zero usages
anywhere in the solution. It is evidence of an old intention, not of a design. Postgres will not
drop an enum value cleanly, so it stays where it is and this section is the reason it must not be
used.

**The decisive argument.** `GuildPermissionService.cs:179` resolves a member with
`Where(m => m.UserId == userId && m.GuildId == guildId).FirstOrDefaultAsync()`, with no ordering and
no type filter, and the supporting index at `MicroserviceContext.cs:381` is not unique. A second row
for the same user in the same guild does not fail; it makes permission resolution, mute enforcement
and the onboarding gate return an arbitrary row. That is the security boundary failing silently and
non-deterministically, which is worse than failing loudly.

The same `(UserId, GuildId)` lookup pattern appears in `MemberEndpoint`, `InviteEndpoint`,
`GuildReadHandler`, `GuildMaterializationHandlers`, `AbsenceEndpoint`, `HouseholdGuildEndpoint` and
`MaintenanceEndpoint`. `GuildMembers` is touched by 59 files in `Guild.Application`. Extending the
entity means auditing all of them and adding a type filter to nearly every one, where each miss is a
silent bug rather than a compile error.

Federation makes this worse rather than better. `GuildMaterializationHandlers` already writes shadow
`GuildMember` rows for remote users, keyed on the federated id and flagged with `FederatedServerId`,
and it finds them with the same unfiltered `AnyAsync` on `(GuildId, UserId)`. Adding a second
population of non-user rows to that table compounds an ambiguity that already exists.

Three further reasons:

* **Cardinality.** `GuildMember` is one row per `(guild, user)`, assumed everywhere. A persona is N
  per `(guild, user)`. Same table, different primary key.
* **The child collections become nonsense.** `RoleMembers` would let a costume hold `BanMembers`.
  `ReadStates` are keyed on `MemberId`, so unread counts and mention badges would fan out per
  costume. `PermissionOverwrites` would give a fictional character per-channel overwrites.
* **Half the entity is meaningless.** `InviteId`, `InviteCode`, `TemporaryMembership`,
  `TemporaryEvictionDueAt`, `MutedUntil`, `OnboardingCompletedAt`, `SharePhoneForPayments`,
  `FederatedServerId`, and the four allow/deny masks.

And the one that matters most: a persona with a `GuildMember` row would have an id shaped exactly
like a member id, in the same table, through the same DTOs. Sooner or later somebody puts it in
`AuthorId`, most likely in a Discord-compat mapping where a persona superficially resembles a
webhook author. That reintroduces the bug in §1 deliberately. A separate entity with its own `pers_`
prefix makes the mistake impossible to make by accident.

### 2.1 What a persona still needs from the member model

Two capabilities do not follow from "not a member" and need deciding rather than inheriting.

**Mentioning a character.** `Message.Mentions` is a list of user ids and Guild's mention and inbox
pipeline keys on them. Typing `@Mayor Cogsgrove` is the most natural thing a roleplayer will try. It
must either resolve to the owning user, which leaks who plays whom and is a privacy failure specific
to this audience, or be a distinct mention kind that notifies the owner without naming them. The
second is correct. `ChannelBroadcastMention` and `BroadcastMentionKind` are the nearest existing
shape to copy.

The Mentions tab says which character was pinged: `InboxMentionDto.personaId` alongside
`kind: "Persona"`. It is read back out of the message body rather than stored on the index row -
that is where the mention was resolved from in the first place, it costs no schema change to a
cross-service index, and a grant lost since then simply stops matching. Only characters that reach
the caller are named, so the field can never disclose somebody else's character, let alone its owner.

**Turn state is per-persona.** §5 orders scenes by persona, but `MemberAbsence.UserId` is a user, so
the nudge path needs a persona-to-owner resolution step. It is one lookup, but it has to exist.

---

## 3. The persona model

Ownership and speaking rights are separate. Scope decides who the persona belongs to; grants decide
who may speak as it.

```
Persona                            // pers_
    Scope           : User | Guild
    OwnerUserId?                   // Scope == User
    OwnerGuildId?                  // Scope == Guild
    HomeProfileId?                 // which guild copy is currently the reference, see 4.3
    Name, AvatarUrl, Pronouns, Color, ShortBio, IsRetired

PersonaGuildProfile                // one per (Persona, Guild)
    PersonaId, GuildId
    DisplayName?, AvatarUrl?, Tag? // per-guild overrides
    ProxyPrefix?, ProxySuffix?
    WikiPageId?                    // this guild's character page, see 4
    UpstreamRevisionNumber?
    ApprovalState, ApprovedByUserId, ApprovedAt
    LastApprovedRevisionNumber?    // see 4.2
    ChangesRequestedReason?

PersonaGrant                       // Scope == Guild only
    PersonaId, RoleId? | UserId?

Guild
    RequirePersonaApproval         // whether a persona must be signed off before it may speak
```

`HomeProfileId` deliberately carries no foreign key. §3.3 makes losing the reference copy a
*promotion* decision rather than a cascade, and a `SetNull` FK here would also close a
Persona-to-Profile reference cycle.

**User-scoped personas are global.** PluralKit members work in every server the bot is in, with
per-server display names and a per-server `servertag`; Tupperbox tuppers follow the account
everywhere with per-server autoproxy. Global identity with per-guild overrides is the shape the
community already converged on, and guild-scoped-only personas would be a regression for anybody
migrating.

**Guild-scoped personas are the shared-character answer.** A Narrator, a town guard, a recurring
antagonist belongs to the guild, and `PersonaGrant` gives a role the right to speak as it. RP servers
restrict Narrator and Gamemaster tuppers to staff by social convention because the tooling cannot
express it, and RPG Sage scopes `npc::` to the GM for the same reason.

Not supported: two people co-owning a personal character. No evidence of demand was found, and it
has none of the moderation clarity the guild-scoped case has.

Shared personas are where the author-stays-real decision pays off most. When three GMs share the
Narrator, the audit log, moderation, blocking and reply-pings still resolve to whichever one typed
the message. Under Tupperbox a shared staff tupper is genuinely anonymous. That makes this a safety
argument, not a convenience one.

### 3.1 Proxy prefix uniqueness

The prefix that selects a persona when typing must be unique across everything one user could mean
in one guild: their own personas, plus every guild-scoped persona they hold a grant on. That
constraint spans two tables and cannot be expressed as a unique index on `PersonaGuildProfile`,
which has no owning-user column and, for guild-scoped personas, no owning user at all.

It is enforced in the resolver, on write, against the union of both sets. The check must be written
once and shared by the send path and the persona-edit path, because a prefix that resolves to two
personas is a silent mis-attribution rather than an error.

### 3.2 Autoproxy

Per-channel state on the server (`Off`, `Latch`, `Sticky`), not client configuration. Server-side is
the point: it works identically on every client, it survives a client that has never heard of
personas, and it cannot be taken down by a third party. §10.2 covers what that same property costs.

### 3.3 Lifecycle

`PurgeUserDataCommandHandler.cs:50` carries a comment recording that
`GuildDirectMessagePreference` needed explicit purging precisely because it is keyed on
`(UserId, GuildId)` and survives leaving a guild. `Persona` and `PersonaGuildProfile` are the same
shape and will be missed the same way unless written down now.

| Event | Rule |
|---|---|
| Account deletion | Personas and profiles purged. Denormalised name and avatar stay on historic messages, as with any other author display data |
| Guild deletion | `PersonaGuildProfile` and grants cascade. Any `Persona.HomeProfileId` pointing at a deleted profile is nulled and re-pointed by the same sweep |
| Leaving a guild | Profile and approval survive, matching `GuildDirectMessagePreference`. Autoproxy state is cleared |
| Retire | Persona stops being selectable, keeps rendering on historic messages, stays visible in the chronicle |
| Delete | Allowed only when the persona has no messages. Otherwise it retires, so `Message.PersonaId` never dangles |
| Losing a grant | Guild-scoped persona stops being selectable immediately. Historic messages are untouched |

Editing a message never re-resolves the persona. The overrides are denormalised at send time and no
read path in Messaging re-resolves them, so an edit keeps whatever identity the message was sent
under. That is the correct behaviour and it needs no code, only this sentence.

### 3.4 Limits

Nothing caps personas today because nothing exists. A persona is a free display name plus avatar
with no rate limit attached, which is an abuse primitive. PluralKit caps members at 1000 per system
for this reason. Cap per user per guild, and globally per user, before shipping rather than after.

---

## 4. The character page is a wiki page

There is no separate character-sheet entity. The prose and the stats live on one wiki page, with the
structured half as an infobox, which is how wikis have always solved this.

```
WikiPage
    + PersonaId   : string?        // unique per (GuildId, PersonaId)
    + InfoboxJson : jsonb?

WikiRevision
    + InfoboxJson : jsonb?         // see 4.1
```

`WikiPage` already carries `Icon`, `CoverUrl`, `Tags`, `CategoryId` and `ParentPageId`, so most of
what makes a character page worth showing off exists. The infobox follows the `Message.EmbedsJson`
precedent of opaque JSON, except this one is Postgres-only and can therefore be real `jsonb` and
stay queryable. Each wiki category carries an infobox template of field list, types and required
flags, structurally the same thing `GuildOnboardingPrompt` already does.

Dice read the infobox: `@sheet.perception` is a field lookup, not a parallel storage system.

Folding the sheet into the page also gets `WikiComment`, `WikiPageWatcher` and `WikiPageReaction`
for free, so character pages have discussion, follow and reactions without new code, and
`ParentPageId` gives Character to Backstory / Relationships / Gallery.

### 4.1 Revisions do not currently cover the infobox

The claim that folding into the wiki buys edit history for free is only true after one fix.
`WikiRevision` snapshots `PageId, Content, EditorId, RevisionNumber, Summary` and nothing else, and
`WikiEndpoint.cs:225` derives `contentChanged` from `dto.Content` alone, creating a revision only in
that branch. An infobox-only edit would version nothing and bump no revision number.

So `WikiRevision` gains `InfoboxJson`, and the change-detection at `WikiEndpoint.cs:225` widens to
cover it. Without both, three things silently fail: "who quietly buffed their own stats" is not a
diff, approval has nothing to review, and §4.2's "behind by N" badge reads zero forever for pure
stat edits.

With that in place, approval rides on revisions rather than being a second state machine. Editing an
approved character marks the page as having unapproved changes and the reviewer sees a diff.

### 4.2 Approval is still a small state machine

`ApprovalState` lives on `PersonaGuildProfile`; the diff machinery lives on `WikiPage` revisions.
Folding the sheet into the wiki removes the duplicate *storage*, not the workflow. The rules:

* A profile is `Draft`, `Submitted`, `Approved` or `ChangesRequested`.
* An edit to an approved page records `LastApprovedRevisionNumber` on the profile. Anything above it
  is pending.
* A pending edit does not block speaking. The approved revision keeps rendering; the character stays
  playable. Blocking speech on a typo fix is how approval queues become resented.
* A guild may require approval before a persona speaks at all, which gates first use, not edits.
  That switch is `Guild.RequirePersonaApproval` and defaults to off, so turning roleplay on does not
  silently gate an existing guild's members behind a queue nobody is staffing yet.

### 4.3 One character, many guilds

The wiki is hard guild-scoped: `Wiki.GuildId`, `WikiPage.GuildId`, and `ViewWiki` /
`EditAnyWikiPage` are guild permissions clamped by `GuildFeatureMap`.

| Option | Verdict |
|---|---|
| One shared page across guilds | No. Needs `WikiPage.GuildId` nullable, which breaks every wiki query, every permission check and the module clamp |
| An unrelated page per guild | No. This is the status quo people complain about |
| A per-guild copy with an upstream pointer | Yes |

Adopting a persona into a guild copies the reference page's content, infobox, icon and cover into a
fresh `WikiPage` in that guild, recording `UpstreamRevisionNumber` on the profile. The copy is
entirely local and entirely guild-owned, so nothing about wiki storage, permissions or search
changes.

This is correct rather than merely affordable. The same character genuinely is different in
different servers: different canon, different allowed content, sometimes a different game system
entirely. A 5e stat block and a PbtA playbook cannot be one shared blob, and a guild's moderators
must be able to edit a character page in their guild without the edit landing in somebody else's
server.

Three consequences that follow from the copy being editable, and are easy to miss:

* **Pulling from upstream is a merge, not a copy.** A guild's moderators may have edited the local
  page. A pull that fast-forwards silently discards their work. The pull presents a three-way diff
  and defaults to keeping local edits.
* **There is no canonical URL.** `WikiPage.Slug` is derived once at creation and embeds the page id,
  so every copy has a different link. A character has no single address to share, which undercuts
  the reason §4.4 matters. Either the persona gets its own stable public route, or this is an
  accepted limitation and says so.
* **Promotion leaves orphaned upstream numbers.** When the reference guild is deleted and another
  copy is promoted, the remaining copies hold `UpstreamRevisionNumber` values from a history they
  never shared. Promotion nulls them, and those copies show as diverged rather than behind.

Fork drift is the accepted cost: a player updates the reference and six guilds never pull. The
"behind by N" badge is the mitigation, and it is the same tradeoff any fork carries.

There is no canonical page, only a pointer. `Persona.HomeProfileId` names whichever guild copy is
currently the reference, so a character's page is never hostage to somebody else's server.

### 4.4 Public pages

Linking a character page outward is a good half of the appeal, and a page only visible to people who
already joined the guild loses it. That work is scoped separately in
[public-wiki-feasibility.md](./public-wiki-feasibility.md), which also documents the trap: 
`WikiPage.Visibility` defaults to `Public` and has never been read, so wiring external serving to
that column as it stands would retroactively publish every wiki page ever written. This spec depends
on that report's opt-in flag, not on the existing column.

One fact worth carrying here because it makes the fix cheaper than it looks:
`ModulePermissions.PublishWikiPublicly` is not in the @everyone defaults, which grant only `ViewWiki`
plus the household bits (`Role.cs:146`). Enforcing it is therefore not a silent widening for existing
guilds.

---

## 5. Scenes

Scene state is a side table keyed on the channel, not columns on `Channel`.

```
ChannelType.Scene                  // a thread variant

SceneState
    ChannelId                      // PK and FK
    ParticipantPersonaIds
    TurnOrder
    CurrentTurnPersonaId?
    TurnStartedAt?                 // the other end of the clock a client draws
    TurnDeadlineAt?
    TurnNumber                     // "turn 47" is what makes a scene read as a game
    PostCount
    ConclusionNote?
    Status : Open | Active | Paused | Concluded
    OocThreadId?
```

`Channel.cs:52` justifies the forum columns living on `Channel` on the grounds that every one of
them is a sort key or a filter on the forum post listing. None of these six are, and two are arrays,
so the same reasoning puts them in a side table.

Stale-turn nudges push a reminder, escalate to the GM, and allow a skip. They respect
`MemberAbsence`, which was built for households declaring a holiday and is exactly "skip my turn
until Friday". Two things that does require: `GuildFeatures.Presence` must be in the Roleplay preset,
because every `AbsenceEndpoint` route gates on it, and the nudge needs the persona-to-owner
resolution from §2.1.

The OOC companion thread is created and linked at scene creation, because every guide on running
roleplay says to pair them and every server does it by hand.

Async games stalling silently is the most common way a play-by-post game dies. Modron exists solely
to nag when play has stalled, which is the clearest possible signal that this belongs in the
platform.

### 5.1 Scene is a thread everywhere or nowhere

`ChannelValidator.cs:17` rejects whitespace in channel names for every type except
`ChannelType.Thread`, and `Channel.Create` validates unconditionally. A scene called "The Siege of
Blackwater" throws on creation. That rule widens to Scene.

More broadly, `Channel.ParentChannelId` and `CreatedByUserId` are documented as thread-only, and
there are 32 `ChannelType.Thread` comparisons across 17 files including `ThreadEndpoint`,
`ForumPostEndpoint`, `GuildPermissionService`, `MessageCreatedHandler`, `ForumAutoArchiveService` and
`GuildTemplateEndpoint`. A scene that is a thread variant in the domain but recognised by none of
them would not appear in thread lists, never auto-archive, escape `ManageOwnThreads` and
`ManageAnyThread`, and emit no `THREAD_CREATE` to bots.

Every one of those call sites is reviewed and switched to a shared "is a thread-shaped channel"
helper alongside the existing `IsForum` and `IsHouseholdModule`.

### 5.2 Where scenes live

Guild owns all of it. A scene is a channel, turn order is persona-shaped, the nudge reads
`MemberAbsence`, and `ManageScenes` is a `ModulePermissions` bit - every input is already Guild's.

Routes follow §15's conventions and the `PersonaGate` three-check order (module, membership,
permission), gated on `GuildFeatures.Scenes`:

| Verb | Route | Permission |
|---|---|---|
| POST | `/api/v1/guilds/{guildId}/channels/{channelId}/scenes` | `ManageScenes`. Creates the scene thread and its OOC companion |
| GET | `/api/v1/guilds/{guildId}/scenes` | Membership, then `ViewChannel` per scene. `waitingOnMe`, `includeConcluded`, `includeArchived`, `limit` |
| GET | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}` | `ViewChannel` |
| PATCH | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}` | `ManageScenes`. Status, deadline, cast, turn order, conclusion note. The cast is applied first, so one call can add a character and put them in the rotation |
| POST | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/participants` | `ManageScenes` |
| DELETE | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/participants/{personaId}` | `ManageScenes` |
| POST | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/turn/advance` | `ManageScenes`, or the persona whose turn it is |
| POST | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/turn/skip` | `ManageScenes` |
| POST | `/api/v1/guilds/{guildId}/scenes/{sceneChannelId}/turn/nudge` | `ManageScenes`. Chases the current turn now, ignoring the grace period and quiet hours |

The create body is `{name, description?, oocName?, participantPersonaIds?, turnOrder?,
turnLengthHours?, status?}`. `participantPersonaIds` may be omitted, in which case the cast is
`turnOrder`: a client that asks the question once should not have to send the same list twice.
`status` accepts `Open` or `Active` and nothing else, and `Active` opens the first turn on the spot,
so starting a scene is one call rather than a create followed by a patch. The clock is
`turnLengthHours`, never a deadline: a create has no turn to put a deadline on.

Refusals from these routes answer `{error, message}`, with `error` one of `scene_parent_not_text`,
`scene_status_not_openable`, `persona_not_adopted`, `turn_order_not_in_cast`, `persona_not_in_scene`,
`persona_already_in_scene` (409), `scene_not_active`, `no_turn_to_nudge`.

"Is the game waiting on me" is the headline question of the whole feature, so it is one request:
`waitingOnMe` filters to scenes whose turn belongs to a character the caller may speak as, resolved
from the set `PersonaService` already caches per (user, guild) and drops when a grant is revoked.
Each row carries the scene's name, status, the character on the clock with its name and avatar, both
ends of the clock, the turn number and the size of the cast, so a list needs no second call per row.

The same shape deliberately does not go on `ChannelDto`. That DTO is a `Facet` over the `Channel`
entity, and scene state is a side table by §5's own argument, so badging a sidebar row would mean
either columns on `Channel` or a join on every channel-list fetch of every guild - paid by every
instance that has no scenes at all. A client that wants sidebar badges reads the scene list once and
keys it by channel id; the realtime events below keep it current.

A scene's cast travels with its display data: `SceneDto.participants[]` carries each character's
name, avatar, colour and tag, plus `isAway` and `isCurrentTurn`. A scene renders other players'
characters, and the persona list at §15.2 answers a different question - what the caller may speak
as - so without this a turn order is a column of ids. §15.2's cast route is the same data for a
guild rather than for one scene.

`awayPersonaIds` says which of the cast the rotation is stepping over because their players declared
an absence, so a skip renders as deliberate rather than as a bug. It names characters and carries no
dates and no note: an absence's window and note are the member's, and pairing either with a character
would map that character to a member on the absence board. In a scene with one participant that
inference is available anyway from the fact that the single character is being stepped over; the
field does not make it worse, and hiding it there would only make honest scenes render worse.

The turn advances on its own when the persona whose turn it is posts in the scene, which is what
makes this feel like play rather than administration. `/turn/advance` exists for the case where
somebody passes without posting.

Two hub events keep every other participant's rail honest, both addressed to the guild's present
members the way the rest of the guild's events are. `guild.SceneTurnChanged` fires wherever the turn
moves - the automatic advance on a post included, which is the case that matters - and carries
`guildId, channelId, previousPersonaId, currentTurnPersonaId, turnStartedAt, turnDeadlineAt,
turnNumber, status`. `guild.SceneUpdated` fires when the cast, the order, the status or the clock
changes and carries the rest of the state. A client that advances its own rail locally on seeing a
post will drift; these are what it should follow instead.

Two more bracket the scene's life. `guild.SceneCreated` carries `guildId, channelId,
parentChannelId, name, oocThreadId, status, participantPersonaIds, turnOrder, currentTurnPersonaId,
turnStartedAt, turnDeadlineAt, turnNumber, turnLengthHours` - the pair of `guild.ThreadCreated`
events that go out alongside say two threads appeared, not that a game started. `guild.SceneConcluded`
carries `guildId, channelId, status, conclusionNote, turnNumber, postCount, concludedAt` and fires
once, on the transition; a later edit to a concluded scene's note is an edit to a chronicle and
arrives as `guild.SceneUpdated` only.

`SceneStatus` is a new `HasPostgresEnum`, so it needs a migration alongside `ChannelType.Scene`.

The nudge is a hosted sweep in the shape of `ForumAutoArchiveService`, which already walks channels
on a timer for a due-date condition. It resolves the persona to its owner (§2.1), checks
`MemberAbsence.Covers`, and escalates to whoever holds `ManageScenes` after a second miss.

The nudge leaves the hub and goes to the phone. `guild.SceneTurnNudge` carries
`guildId, channelId, sceneName, personaId, turnStartedAt, turnDeadlineAt, turnNumber, nudgeCount,
escalated` - a reminder about a game somebody had forgotten has to name the game, and the GM
escalation reads differently enough from the player nudge to need the flag. Alongside it Guild
publishes `SceneTurnPushRequested`, which Messaging turns into FCM: one copy for whoever answers for
the character, one for the escalation holders. The push renders under the character's name and never
the account's - the same rule a persona message's push already follows - and where the character
cannot be named it sets `personaHidden` and masks rather than falling back to the account. Recipients
go through the same mute and mobile-push resolution every other push producer uses, and a guild
inside its quiet hours has the whole nudge held for a later pass rather than its push dropped.

---

## 6. Dice

Server-rolled and recorded, which removes the trust problem that forum play-by-post sites solved
years ago and Discord bots reintroduced.

```
MessageType.DiceRoll
DiceRoll
    MessageId, ChannelId, RollerUserId, PersonaId?
    Expression, Results, Total
    Visibility : Public | GameMasterOnly | Blind
```

System-neutral notation only: `NdX+M`, keep and drop, advantage, exploding. RPG Sage's position that
the core engine stays game-system-neutral is right, and building a 5e rules engine is a different
product.

**Public rolls need no new primitive and ship on their own.** They are an ordinary message with a
type and a side table.

**Hidden rolls do, and it is bigger than it looks.** An earlier draft of this spec claimed
per-recipient visibility was already needed for bot ephemeral responses, citing
`discord-parity.md` §3.3. That is stale. Bots shipped ephemeral a different way:
`DiscordInteractionEndpoint.cs:91` pushes over the realtime hub to the invoking user and never
writes to the message store, and `Bots.Tests` asserts exactly that. So there is no second caller,
and the shipped mechanism cannot serve dice anyway, because a blind roll must be persisted and later
revealed, which a transient hub push cannot do.

What per-recipient visibility actually costs in Messaging:

* Scylla partitions `messages` on `context_id` alone. There is no recipient dimension and adding one
  is a partition-key change, not the additive `ALTER` the other columns get.
* Filtering after the fetch breaks paging: `LIMIT ?` returns N rows and the filter returns fewer, so
  pages under-fill non-deterministically.
* `MessageSearchEntry` has no visibility column, so a hidden roll's text would be searchable by
  everyone in the channel.

Given that, hidden and blind rolls wait, and `discord-parity.md` §3.3 gets corrected as part of this
work rather than left to mislead the next reader.

---

## 7. The chronicle

Export a concluded scene, or a whole campaign, as something readable: Markdown, EPUB, PDF. Grouped
by persona with avatars, OOC stripped, in order.

This is the thing forum play-by-post has always had and chat has never had, and it is why people
tolerate worse software to keep it. A two-year campaign that can be read back is the retention
argument for the entire feature set.

Two limits to state rather than discover:

* **Encrypted channels get no chronicle and no search.** `MessageSearchEntry` indexes only `Plain`
  messages and MLS channels store ciphertext. This is the same exclusion `message-previews` made
  explicit, and it is a design boundary rather than a gap.
* **Searching by character does not exist yet.** `SearchEndpoint` takes a free-text query plus one
  channel or conversation id. There is no `from:` parsing, no author predicate and no guild-wide
  scope; `MessageSearchEntry.AuthorId` is stored and never queried. Guild-wide search with filters is
  still open in `discord-parity.md`. Adding `persona_id` to the index is a one-column change that is
  only useful once that lands, so it ships with it rather than here.

---

## 8. Permissions and feature flags

`GuildKind.Roleplay`, with a preset switching on the modules below plus `Threads`, `Forums`, `Wiki`,
`Events`, `Moderation` and `Presence` (§5).

`GuildFeatures` bits 12 to 19 are free:

| Bit | Feature |
|---|---|
| 12 | `Personas` |
| 13 | `Scenes` |
| 14 | `Dice` |
| 15 | `Chronicle` |

There is no separate `CharacterSheets` flag, since §4 puts the character page on the wiki. Note that
`GuildFeatures` has no dependency mechanism, so "Personas requires Wiki" cannot be declared. It is
enforced where the feature set is written, and the Roleplay preset includes both.

`ModulePermissions` must be added at bit 24 or above, per the mapping table in that file:

| Bit | Permission | Owning feature |
|---|---|---|
| 24 | `UsePersonas` | `Personas` |
| 25 | `ManageAnyPersona` | `Personas` |
| 26 | `ApprovePersonas` | `Personas` |
| 27 | `ManageScenes` | `Scenes` |
| 28 | `RollDice` | `Dice` |
| 29 | `RollHidden` | `Dice` |
| 30 | `ExportChronicle` | `Chronicle` |

The owning-feature column is not decoration. `GuildFeatureMap.ModulePermissionOwners` is what makes
a module bit clampable, and the file asserts that every bit in the module mask is owned by some
module. A bit absent from that array is never clamped and stays granted with its module off, which
is the exact property that justifies putting these bits in `ModulePermissions` at all.

`Guild.Contracts.ExternalModulePermission` is the by-name wire mirror and gains all seven in
lockstep.

`ManageOwnPersonas` is deliberately absent. The dividing line in `ModulePermissions` is "can this bit
ever be meaningless", meaning the guild switched the module off. Creating and editing your own global
persona is not a per-guild capability, and a user in zero roleplay guilds holds no guild mask at all.
It belongs on the account surface next to the global persona list, not in either guild mask.

`PersonaGrant` covers speaking as a guild-scoped persona; `ManageAnyPersona` covers editing it.

`UsePersonas` is in `Role.DefaultEveryoneModulePermissions`, which looks like a widening and is not
one. `GuildPermissionService` runs every check through `GuildFeatureMap.IsPermissionAvailable`, so a
bit owned by a disabled module is refused before the holder's mask is consulted - the grant is inert
in any guild without `GuildFeatures.Personas`. The constant is also read only when @everyone is
created, so no existing guild's stored mask changes. The alternative, special-casing the grant at
guild creation for `GuildKind.Roleplay`, buys nothing and adds a branch.

---

## 9. Changes outside Guild

### 9.1 Prerequisites, because the author identity does not currently survive

Three independent places drop it. All three are pre-existing and all three must be fixed before §1's
claim is true.

**It never reaches storage.** `CreateMessageCommand.cs:44` builds `CreateMessageParams` with 18
fields and omits `AuthorIdType` entirely, so it falls to its `User` default on every persisted
message. Webhook executions set it correctly at `WebhookEndpoint.cs:180` and it is discarded one hop
later. `PublishEndpoint` reads it back off the row, so crossposts inherit the wrong value too.
Adding `AuthorIdType.Persona` is a no-op until this is fixed.

The same omission drops `MlsGeneration`, so an encrypted message was persisted without the
generation it was sealed under while the event carried it. Unrelated to personas, real, and fixed in
the same place.

**It does not reach Messaging's own domain events.** `MessageCreated` and `MessageUpdated` carry
neither the author type nor the display overrides, which is the layer that starves both of the two
below: push reads the event, and the cross-service event is built from it.

**It does not reach the realtime fan-out.** `Guild.Contracts.MessageCreatedForChannel` carries
`AuthorId`, mentions, embeds, components and attachments, but no `AuthorDisplayName`,
`AuthorAvatarUrl` or `AuthorIdType`. That contract feeds Guild's realtime and bots fan-out, so a
persona message arrives at connected clients with no character on it. This envelope has a
demonstrated history of being under-populated.

**It does not reach push.** `MessageCreatedHandler.cs:101` sets
`SenderName = profile.Profile?.UserName`. A persona message pushes under the real account name, so
every recipient's lock screen leaks who is playing the character. For this audience that is the one
place the author-stays-real design bites back, and it needs the override threaded through.

### 9.2 Messaging

* `AuthorIdType` gains `Persona`; `MessageType` gains `DiceRoll`.
* `Message` gains `PersonaId`.
* `Message.SelectColumns` is used only by `ScyllaMessageRepository`. The Cassandra `Mapper`
  registration in `ScyllaContext` needs the column too, or `GetMessageAsync` and
  `GetPinnedMessagesAsync` silently return null for it. `EfCoreMessageRepository` does not use
  `SelectColumns` and needs a Postgres migration instead.
* Send accepts `personaId`; Guild resolves it and populates the existing display overrides.
  `AuthorId` is never touched.
* Server-side per-channel drafts, so a long post survives a refresh. Small, and disproportionately
  valued, because the current answer is Google Docs.
* A maximum content length, which is a **plan entitlement rather than a constant**. The key is
  `guild.message_max_length`, numeric and guild-scoped, following `GuildEmojiSlots` exactly, with a
  user-scoped sibling for direct messages on the same reasoning as `UserUploadMaxBytes`. Free guilds
  get 4,000 characters, Plus and Pro get 15,000, and 15,000 is also a hard instance cap that no
  configuration may exceed - Scylla is holding these bodies.

  A refusal reports `EntitlementDegradationReason.GuildPlanLimit`, so a client can distinguish "too
  long for this server's plan" from "too long, full stop". An instance with no catalogue configured
  must land on a usable limit: nothing configured resolving to nothing available is the shape that
  once made account credit unspendable.

### 9.3 Bots

The Discord-compat wire format has no persona concept. Mapping `AuthorIdType.Persona` to a
webhook-shaped author would make personas indistinguishable from real webhooks on that surface,
which is the ambiguity §1 exists to remove. The mapping is decided in `Bots.Application` alongside
the existing payload classes and documented there, and `MessageCreatedForChannel.MessageType` is a
four-member subset mirror that needs `DiceRoll` adding or dice messages fan out as ordinary ones.

### 9.4 Import

Tupperbox and PluralKit both export JSON, and migration is the strongest reason for an existing
community to move. The `Import.*` service is less reusable than it looks: `ImportJob.DiscordGuildId`
is required and non-nullable, `ImportEntityType` is `{Category, Channel, Role}`, and the service is
Discord API and gateway clients end to end with no file-upload path. What is reusable is the
`ImportJob` and `ImportJobStatus` shape and the purge and export handlers. The rest is new.

---

## 10. Security

### 10.1 Impersonation is the open hole

Nothing validates the display overrides today. `WebhookRequestDto` carries no annotations and
`WebhookEndpoint.cs:182` passes the supplied name and avatar straight through: no length cap, no URL
scheme or host validation, no collision check. Webhooks get away with it because they sit behind
`ManageWebhooks`. `UsePersonas` is meant to reach nearly everyone, which changes the risk entirely.

Without a fix, any member posts as "Server Owner" with the owner's avatar. Two things make it worse:
`AuthorIdType` never reaches storage (§9.1), so a client that wanted to badge non-user authors
cannot; and §3.2's server-side autoproxy means an unpatched client renders a persona
indistinguishably from its owner, which is the same sentence as its main selling point.

Required before `Personas` ships: a name-collision policy against member nicknames and role names in
the same guild, an avatar URL allowlist restricted to instance-hosted media, length caps, and a
wire-level persona marker that clients cannot omit.

### 10.2 What is already safe

Ban, timeout, mute and block evasion are closed by the design rather than by new code, because all
four key on `AuthorId`: mute is read off the member row in `GetMembershipAsync`, permission checks
resolve by user id, and `BlockCache` filters on `AuthorId`. A persona changes none of it.

One pre-existing gap this feature inherits rather than creates: `MessageCreatedHandler.cs:43` fans
out over the realtime hub before block filtering, so blocking currently suppresses push but not
realtime. Personas make it more visible without making it worse.

---

## 11. Federation

Federation is the part of this codebase most often forgotten, so this section states what breaks
before anything is built, with the evidence.

### 11.1 Persona identity does not survive federation today

A persona message crossing an instance boundary loses its character at five independent layers.

| Layer | File | What it carries |
|---|---|---|
| Cross-service event | `Guild.Contracts/Bus/Events/MessageCreatedForChannel.cs` | `AuthorId`, no display fields |
| Outbound provider | `IFederationProvider.SendMessageAsync` | `channelId, messageId, content, senderId` |
| Wire DTO | `Federation.Application/Dtos/Events/Bidirectional/Messaging/MessageCreated.cs` | The serialized envelope, distinct from the provider signature above |
| Wire contract | `Federation.Contracts/.../FederatedMessageCreatedReceived.cs` | `EventId, OriginInstanceId, SenderId, ChannelId, MessageId, Content` |
| Materialization | `Messaging.Application/Bus/Federation/MessagingMaterializationHandlers.cs` | Constructs `Message` with nine fields, none of them display overrides |

So a character speaking in a federated channel arrives on the remote instance as a plain message from
a raw federated user id. Not degraded: absent. The same four layers explain why §9.1's realtime gap
and this one are the same root cause seen twice.

### 11.2 What federating a persona requires

`FederatedResourceReceived.SenderId` is documented as `<localId>:<domain>`, and
`MessagingOutboundHandlers.IsFederated` detects a foreign id by testing for a colon.

**The persona id is not part of the wire contract at all.** An earlier draft said a materialized
`PersonaId` "must never be resolved against the local `Persona` table", which is a convention that
can be forgotten. Simply not carrying the id is strictly stronger: materialization has nothing to
assign, so the rule is enforced by the type rather than by care.

That gives the design rule: **a federated persona is display data, not an entity.** The remote
instance stores the name and avatar that arrived with the message and renders them. It does not
create a shadow `Persona`, because it cannot authorize one, cannot approve one, and has no wiki page
for it.

This is a deliberate departure from how `GuildMaterializationHandlers` treats members, where a shadow
`GuildMember` row is created with the federated id used as the username placeholder. Members need
shadow rows because permissions resolve against them. Personas do not, because §10.2 establishes that
every authorization decision keys on `AuthorId`, which federates already.

Concretely:

* `MessageCreatedForChannel`, `IFederationProvider.SendMessageAsync`, the wire contract and the
  materialization handler each gain the display name, avatar and author type. Fixing §9.1's first
  two layers is most of this.
* Materialization sets the overrides from the wire and leaves `PersonaId` null, because a local
  `PersonaId` would be a dangling reference to another instance's row.
* Approval is per-instance and does not federate. A guild that requires approval applies it to local
  personas; a federated character arrives already spoken.

### 11.3 What deliberately does not federate

Scenes, turn order, dice and the chronicle are single-instance in a first version. Turn order over a
DAG-resolved event stream with no ordering guarantee across instances is a distributed consensus
problem, and losing a turn nudge is worse than not having one. Scene channels federate as ordinary
channels; their turn state does not.

A federated instance that has none of these features still receives the messages, because they are
ordinary messages with an extra display name. That is the property worth preserving, and it is why
persona identity belongs on the message rather than in a side channel.

### 11.4 Why the verifier had to be fixed first, and what still constrains the rollout

`SignedFederationEvent.IsValid` used to verify `JsonSerializer.SerializeToUtf8Bytes(Payload)` - a
re-serialization of the payload it had just deserialized, rather than the bytes that were signed.
`System.Text.Json` writes nulls and enum defaults, so a receiver holding a newer payload type
re-serialized fields the sender never signed and verification failed. That made **any** additive
field on **any** federation wire event break verification between mismatched versions; personas were
simply the first change to add one.

The signature now covers the exact bytes of the `payload` member as they appear in the received
body, sliced out with `Utf8JsonReader` rather than re-encoded. `SignedFederationEvent` is sealed with
a private constructor and cannot be deserialized by `System.Text.Json` at all, so it cannot be
model-bound from a body and then checked against a reconstruction - the guard is structural rather
than a comment someone deletes.

No protocol version bump was needed, which is worth recording because it is not obvious. The old
sender signed the output of the same serializer it then embedded, so the bytes it signed were
already the bytes on the wire; slicing them back out accepts a pre-fix sender unchanged. A version
bump would have made the fix itself the incompatible rollout it exists to prevent.

There is deliberately no "fall back to re-serializing if the byte check fails" path. It would still
demand a valid signature, but it would accept a body whose *unknown* fields had been rewritten or
injected in flight, since those are exactly the bytes a re-serialization discards. On the boundary
that decides who may write into an instance, that is a signature-strippable hole.

**The rollout constraint that remains.** An instance still running the old verifier cannot accept a
payload carrying a field its build does not know, and nothing on our side can change that. So the
verifier upgrade has to reach federated partners *before* any new wire field ships. That upgrade is
compatible in both directions, so it can go out one instance at a time - but persona identity must
not federate until it has.

One related gap is untouched: `FederationDagService` stores `PayloadJson` as a re-serialization, so
an inbound event's unknown fields are lost in the record and the backfill endpoint re-serves a lossy
copy. Backfill is instance-authenticated rather than signed, so this is not a signature problem, but
it does mean an instance cannot relay a newer peer's extra fields.

---

## 12. Migrations and traps

* `channel_type`, `guild_kind` and `member_type` are `HasPostgresEnum` in Guild's model snapshot.
  `GuildKind.Roleplay` and `ChannelType.Scene` each need a Guild migration or the service crashes at
  startup, and unit tests cannot catch it. `SceneStatus` is a third new enum in the same service.
* The same trap applies to **Messaging**, which the first draft of this document missed while naming
  it. `author_id_type` and `message_type` are both `HasPostgresEnum` there, so `AuthorIdType.Persona`
  and `MessageType.DiceRoll` need a Messaging migration each.
* Never hand-edit the migrations. Leftover work goes in a separate EF-generated empty migration plus
  `Sql()`.
* `ChannelValidator` rejects whitespace for every type but `Thread` (§5.1).
* `ChannelType.Scene` must be added deliberately to `ChannelTypeExtensions` and to the 32
  `ChannelType.Thread` comparisons.
* Personas must not acquire a `GuildMember` row at any point, including through the import path.
* The dual-provider `ToQueryString` harness in `Guild.Tests` applies to any new persona query. EF
  InMemory cannot fail on untranslatable LINQ.
* Scylla `RowSet` is single-pass, which matters for the chronicle export more than anywhere else in
  this document, since it is the one feature that reads an entire channel's history.

---

## 13. Build order

Ordering is by dependency, not by schedule.

The prerequisites in §9.1 come first and are worth doing regardless of this spec, since all three are
existing defects. Personas, the message length decision and drafts follow, and together they are the
migration argument on their own: this is the only part where "we did it properly and Discord
structurally cannot" is literally true. Impersonation controls (§10.1) ship with personas rather than
after them.

Character pages come next and depend on the revision fix in §4.1 and on the public-wiki work being
scoped. Cross-guild adoption depends on character pages. Scenes depend on the thread-shape audit in
§5.1 and on `Presence` being in the preset. Public dice can land any time after personas; hidden
rolls wait for per-recipient visibility, which §6 argues is now a single-consumer change and
therefore harder to justify. The chronicle is last, and is the retention hook rather than the
acquisition one.

Federation work in §11.2 rides along with §9.1 rather than being a later pass, because it is the same
four layers.

Freeform roleplay is the larger and worse-served audience. Play-by-post TTRPG has Avrae and is harder
to move.

---

## 14. Open questions

* Whether a guild may refuse externally-created personas outright, or only require approval.
* Whether a character gets a stable public URL of its own, or accepts having one link per guild
  (§4.3).
* Whether `PersonaGrant` should ever span instances for shared-canon settings. §11.2 says no for a
  first version, and the reason to revisit would be federated campaigns rather than federated chat.

---

## 15. Locked API surface, first phase

This section is the coordination contract. The first phase is §9.1's prerequisites, personas, and
character pages. Scenes, dice and the chronicle are not in this surface and their endpoints are not
to be guessed at.

Conventions follow `AbsenceEndpoint`: Wolverine HTTP attributes, class-level `[Authorize]`,
`[NotBody]` on injected services, DTOs in `Guild.Application/Dtos/{Request,Response}`.

**The paths below are service-internal.** Through the gateway they carry the service segment, so
`/api/v1/personas` is reached as `/api/v1/guild/personas` and `/api/v1/guilds/{guildId}/personas` as
`/api/v1/guild/guilds/{guildId}/personas`. This is the first thing a client author gets wrong.

### 15.1 Account-level personas

A user-scoped persona is global, so it is not under `/guilds`.

| Verb | Route | Notes |
|---|---|---|
| GET | `/api/v1/personas` | The caller's own personas |
| POST | `/api/v1/personas` | Creates `Scope = User` |
| GET | `/api/v1/personas/{personaId}` | Owner only |
| PATCH | `/api/v1/personas/{personaId}` | Owner only |
| DELETE | `/api/v1/personas/{personaId}` | Retires instead if the persona has messages, and says so in the response |

### 15.2 Guild personas and grants

| Verb | Route | Permission |
|---|---|---|
| GET | `/api/v1/guilds/{guildId}/personas` | `UsePersonas`. Everything the caller may speak as here: their own adopted personas plus granted guild-scoped ones |
| GET | `/api/v1/guilds/{guildId}/personas/cast` | Membership. Every character the guild has adopted, as `PersonaCastMemberDto[]`: `personaId, name, avatarUrl, color, pronouns, tag, isRetired`, and nothing about who plays them |
| POST | `/api/v1/guilds/{guildId}/personas` | `ManageAnyPersona`. Creates `Scope = Guild` |
| PATCH | `/api/v1/guilds/{guildId}/personas/{personaId}` | `ManageAnyPersona` |
| DELETE | `/api/v1/guilds/{guildId}/personas/{personaId}` | `ManageAnyPersona` |
| GET | `/api/v1/guilds/{guildId}/personas/{personaId}/grants` | `ManageAnyPersona` |
| POST | `/api/v1/guilds/{guildId}/personas/{personaId}/grants` | `ManageAnyPersona`. Body carries exactly one of `roleId` or `userId` |
| DELETE | `/api/v1/guilds/{guildId}/personas/{personaId}/grants/{grantId}` | `ManageAnyPersona` |

The two GETs answer different questions and neither substitutes for the other: the first is the
composer's ("what may I speak as"), the second is everybody else's ("what is this character called").
Rendering a turn order, a cast picker or a `<@pers_...>` token is the second question, so it is
served as denormalized display data rather than as a lookup per row - the same reasoning that puts
`authorDisplayName` on a message.

### 15.3 Adoption, overrides and approval

| Verb | Route | Permission |
|---|---|---|
| PUT | `/api/v1/guilds/{guildId}/personas/{personaId}/profile` | `UsePersonas`, owner. Adopts into the guild and sets per-guild overrides |
| DELETE | `/api/v1/guilds/{guildId}/personas/{personaId}/profile` | Owner, or `ManageAnyPersona` |
| POST | `/api/v1/guilds/{guildId}/personas/{personaId}/profile/submit` | Owner. `Draft` or `ChangesRequested` to `Submitted` |
| POST | `/api/v1/guilds/{guildId}/personas/{personaId}/profile/approve` | `ApprovePersonas` |
| POST | `/api/v1/guilds/{guildId}/personas/{personaId}/profile/request-changes` | `ApprovePersonas`. Body carries a reason |
| GET | `/api/v1/guilds/{guildId}/personas/pending` | `ApprovePersonas`. The approval queue |

### 15.4 Autoproxy

| Verb | Route |
|---|---|
| GET | `/api/v1/guilds/{guildId}/channels/{channelId}/autoproxy` |
| PUT | `/api/v1/guilds/{guildId}/channels/{channelId}/autoproxy` |

Body is `{ "mode": "Off" | "Pinned" | "Sticky", "personaId": string? }`. `personaId` is required for
`Pinned`, optional for `Sticky` as a starting value, and ignored for `Off`.

`Pinned` and `Sticky` share one stored persona column. The difference is who writes it: under
`Pinned` only this endpoint does, so the persona holds until changed; under `Sticky` the send path
writes it back on every proxied message, so it trails whichever character spoke last.

The pinned mode is deliberately not called `Latch`. PluralKit's `latch` is this surface's `Sticky`,
so reusing the word for the opposite behaviour would mislead precisely the users migrating from it.

### 15.5 Character pages

The page itself is an ordinary wiki page through the existing wiki endpoints. Two operations are
specific to personas.

| Verb | Route | Notes |
|---|---|---|
| POST | `/api/v1/guilds/{guildId}/personas/{personaId}/page` | Creates this guild's character page, copying from the reference copy when one exists, and links it to the profile |
| POST | `/api/v1/guilds/{guildId}/personas/{personaId}/page/pull` | Three-way merge from the reference copy. Returns the diff and does not apply when `strategy` is `preview` |

The wiki DTOs gain `infoboxJson` on the page and `infoboxTemplateJson` on the category. Both are
opaque JSON to every consumer except the infobox renderer.

### 15.6 Sending

`CreateMessageDto` gains `personaId`, optional. When present the server resolves it, checks the
grant or ownership, and writes `AuthorDisplayName`, `AuthorAvatarUrl`, `AuthorIdType = Persona` and
`PersonaId`. `AuthorId` is unchanged.

Resolution order on the send path: a leading backslash suppresses everything; otherwise an explicit
`personaId` wins; otherwise a proxy prefix or suffix match on the content; otherwise the channel's
autoproxy state; otherwise no persona.

The backslash outranks an explicit id deliberately. A switcher selection is ambient state that
persists across messages, while the backslash is typed into one message and means exactly one thing.
Ranked below the id it would survive into the body of a message that went out in character anyway,
failing at both of its jobs at once. A prefix match strips the prefix and suffix from the stored
content; the backslash is stripped too.

**Encrypted channels cannot use the server-side paths, and clients must handle that.** The server is
handed ciphertext, so it can match no prefix and strip nothing: `Content` is passed as null and only
an explicit `personaId` resolves. A client in an encrypted channel is therefore *required* to run
the prefix, suffix, backslash and autoproxy rules itself, strip the affixes before sealing, and send
`personaId` explicitly. A client that does not will publish `m: ` verbatim into the ciphertext, in
exactly the rooms this audience most wants to be private.

This is the one place the persona design leans on client cooperation, which is worth stating plainly
rather than discovering. It is tolerable only because getting it wrong is cosmetic and self-inflicted
rather than an authorization failure: the grant check still runs server-side on the explicit id.

### 15.7 Response shapes

```
PersonaDto
    id, scope, ownerUserId?, ownerGuildId?
    name, avatarUrl, pronouns?, color?, shortBio?
    isRetired, createdAt

PersonaGuildProfileDto
    personaId, guildId
    displayName?, avatarUrl?, tag?
    proxyPrefix?, proxySuffix?
    wikiPageId?, upstreamRevisionNumber?, upstreamState: "current"|"behind"|"diverged"
    approvalState, approvedByUserId?, approvedAt?, changesRequestedReason?
    canSpeak                       // resolved for the calling user

PersonaGrantDto
    id, personaId, roleId?, userId?
```

`MessageDto` gains `personaId`. It already carries `authorDisplayName` and `authorAvatarUrl`, which
remain the fields clients render.

**The guild list returns `PersonaGuildProfileDto[]`, each carrying a nested `persona`**, so listing a
guild's cast is one call. Do not merge the two flat, and do not wrap them - a client guessing here
guesses wrong, which is what happened.

**`infoboxTemplateJson` is `{ "fields": [ { "key", "label", "type", "required", "group" } ] }`.** A
renderer draws only fields with content, keeps values whose key the template no longer names, and
tolerates a template it cannot parse. §4 calls this structurally the same thing
`GuildOnboardingPrompt` does, and §15.5 calls it opaque; both are true, but a client cannot render
"opaque" and this is the shape.

### 15.8 Realtime events

Published on the existing guild hub, matching `guild.*` naming. Three audiences, and which one an
event gets is a privacy decision rather than a performance one: the guild's present members for
anything the cast route already serves to any member, the character's players plus the module's
permission holders for a review or a grant, and one user for their own composer state.

| Event | Audience | Payload |
|---|---|---|
| `guild.PersonaCreated` | guild, or the owner for a personal character | `guildId?, personaId, scope, name, avatarUrl, pronouns, color, shortBio, isRetired, updatedAt` |
| `guild.PersonaUpdated` | every guild it is adopted into, plus the owner | as above |
| `guild.PersonaDeleted` | every guild it was adopted into, plus the owner | `guildId?, personaId, retired` |
| `guild.PersonaAdopted` | guild | `guildId, personaId, name, avatarUrl, color, tag, wikiPageId, approvalState, canSpeak` |
| `guild.PersonaProfileChanged` | guild | `guildId, personaId, approvalState, canSpeak` |
| `guild.PersonaUnadopted` | guild | `guildId, personaId` |
| `guild.PersonaReviewRequested` | ApprovePersonas holders, plus the character's players | `guildId, personaId, name, wikiPageId, approvalState, isResubmission, submittedAt` |
| `guild.PersonaReviewCompleted` | same | `guildId, personaId, name, approvalState, approved, reviewedByUserId, reviewedAt, reason, canSpeak` |
| `guild.PersonaGrantCreated` / `Deleted` | ManageAnyPersona holders, plus the grantee | `guildId, personaId, grantId, roleId, userId` |
| `guild.PersonaPageCreated` | guild | `guildId, personaId, pageId, title, categoryId` |
| `guild.PersonaPagePulled` | guild | `guildId, personaId, pageId, strategy, upstreamState, upstreamRevisionNumber, referenceRevisionNumber, conflictCount` |
| `guild.DiceRolled` | whoever can see the channel | `guildId, channelId, rollId, messageId, rollerUserId?, personaId, expression, total, breakdown, reason, visibility, createdAt` |
| `guild.AutoproxyChanged` | the caller alone | `guildId, channelId, mode, personaId` |

`canSpeak` on the broadcast is the profile's own state - not retired, approval satisfied - and not a
per-recipient answer. An earlier draft said "resolved for the calling user", which a guild-wide hub
broadcast has no way to mean. Per-user grant resolution stays on the GET.

The reviewer's `reason` rides on `guild.PersonaReviewCompleted` and never on the guild-wide
`guild.PersonaProfileChanged`: it is feedback for the character's players, not for the room. A grant
is on the same footing, because who may speak as a character is what the gated grant list answers.
`guild.DiceRolled` withholds `rollerUserId` when the roll went out in character.

A persona message needs no new event. It arrives on the existing message-created event, which gains
the display fields per §9.1.

The scene events are in §5: `guild.SceneCreated`, `guild.SceneUpdated`, `guild.SceneTurnChanged`,
`guild.SceneConcluded` and `guild.SceneTurnNudge`.

Client reference: `Guild.Application/docs/roleplay-realtime-frontend-guide.md`.

### 15.8.1 The inbox

Three roleplay rows appear on the Waiting-on-you tab, as `InboxTaskKind` values alongside the
household ones.

| Kind | Who gets the row | `dueAt` |
|---|---|---|
| `SceneTurn` | whoever answers for the character on the clock in an `Active` scene | the turn deadline |
| `PersonaReview` | ApprovePersonas holders, one row per `Submitted` profile | none |
| `PersonaChangesRequested` | the owner of a sent-back personal character; ManageAnyPersona holders for a guild-owned one | none |

An approval queue lives in a guild's cast rather than in any one channel, so
`InboxBreadcrumbDto.channelId`, `channelName` and `channelType` are nullable and are null on those
two kinds. Every unread group, every mention and every other task kind still carries all three.

An approved profile whose page has been edited past what was signed off is a queue row on
`GET /guilds/{guildId}/personas/pending` but is deliberately not an inbox row: resolving it costs a
page-revision lookup per character, and the inbox spans every guild the caller is in.

None of the three has an inbox event of its own. They appear and disappear on
`guild.SceneTurnChanged`, `guild.SceneTurnNudge`, `guild.PersonaReviewRequested` and
`guild.PersonaReviewCompleted`, which is what a client refetches on.

A row can also be put away by hand with
`DELETE /inbox/tasks/{kind}/{targetId}?guildId={guildId}`. The tab is derived state, so a
dismissal is stored as a timestamp against `(user, kind, guild, target)` rather than as a delete,
and the row returns as soon as its own stamp moves past it - the turn stamp for `SceneTurn`, the
profile's `UpdatedAt` for the two approval kinds. The guild is part of the key because a character
can be submitted in two guilds at once and `targetId` is the same persona in both.

### 15.9 Errors

| Case | Status |
|---|---|
| Persona not resolvable, or no grant | 403 |
| Proxy prefix collides with another persona the caller can use | 409, naming the other persona |
| Display name collides with a member nickname or role name in the guild | 409 |
| Avatar URL not instance-hosted | 400 |
| Speaking with an unapproved persona where the guild requires approval | 403 |

### 15.10 Gaps in this surface, found by building against it

Two clients implemented §15 and hit the same edges. These are unbuilt, not undecided.

**An avatar has nowhere to come from.** §15.9 refuses an avatar URL that is not instance-hosted, and
§15 offers no way to create one. Both clients reached for the message-attachment upload, which is
the wrong shape: it mints an attachment bound to a message context. `POST /personas/{id}/avatar` is
wanted, returning an instance-hosted URL the guard will accept.

**The approval queue cannot be triaged.** `PersonaGuildProfileDto` has no `submittedAt`, so "waiting
three days" is unrenderable and a queue cannot be ordered by age. There is also no count endpoint, so
badging the review button costs a full list fetch. The inbox closes half of this - the queue is a
`PersonaReview` row on `/inbox/tasks` and moves the header badge - but the profile DTO still has no
timestamp of its own, and `guild.PersonaReviewRequested` carries `submittedAt` off the profile's
`UpdatedAt`, which any later edit also moves.

**Push cannot distinguish "no persona" from "a persona whose name is withheld".** A client that masks
by default would hide ordinary senders; one that does not leaks the account behind a character onto a
lock screen. The push payload needs an explicit `personaHidden` flag for the withheld case. Note that
`AuthorDisplayName` and `AuthorAvatarUrl` are plain columns rather than sealed content, so sending
them even for an encrypted message is possible and makes the withheld case rare.

### 6.1 Where dice live

**Guild owns the roll.** The alternative - Messaging, on the grounds that a roll is a message - was
rejected for three reasons:

* `RollDice` and `RollHidden` are `ModulePermissions`, which resolve in Guild. The existing
  `HasUserPermissions` bus handlers switch only on the core `Permissions` enum, so a roll endpoint in
  Messaging would need a new module-permission bus request built for one caller.
* Persona attribution resolves in Guild, and a roll made in character is the normal case.
* Guild already creates messages through `CreateMessageCommand` in `GuildEndpoint`, `InviteEndpoint`,
  `ThreadEndpoint` and `GuildTemplateEndpoint`. This is an established path, not a new one.

The `DiceRoll` record therefore lives in Guild's Postgres, keyed by the `MessageId` that Messaging
returns. Guild already holds state about Messaging-owned data in exactly this shape -
`Channel.MessageCount` and `LastMessageId` are the precedent. Keeping it out of Scylla also matters:
a roll has structured, queryable fields and Scylla is the wrong store for those.

```
POST /api/v1/guilds/{guildId}/channels/{channelId}/rolls
    { "expression": "2d6+3", "personaId": null, "reason": "Perception" }
```

Gated on `GuildFeatures.Dice` and `RollDice` through `PersonaGate`'s three checks, then
`SendMessages` on the channel, because a roll is a post.

**v1 accepts public rolls only.** `Visibility` exists on the record so the column is there when
per-recipient visibility lands, but the endpoint **rejects** `GameMasterOnly` and `Blind` with a
clear error rather than accepting and downgrading them. Silently honouring a privacy request in the
weakest possible way is how `discord-parity.md` §3.3 described flag 64 before it was fixed, and it is
worse than refusing.

**The parser is the whole risk.** It is pure, total, and takes untrusted text: `4d6kh3`, `2d20adv`,
`1d10!`, `3d6+2d4-1`. Bound dice count, die size and expression length before evaluating, because
`999999d999999` is a denial of service with no parser bug required. Rolls come from
`RandomNumberGenerator`, not `Random`, since the entire point is that the result is not the roller's
to influence.

Sheet-linked rolls (`@sheet.perception` reading the character page's infobox, whose shape §15.7 now
pins) are the feature that ties dice to characters, and they are v1.1 - the parser must exist first.
