# Privacy - settings, consent, and data rights

Status: **specification**, 2026-08-04. Covers Tier 0-2 of the privacy audit.

The audit that produced this found that Echo stores three privacy flags and a DM-filter setting,
exposes them read-only, and enforces none of them. Everything below either makes an advertised
control real or adds one a production social platform is expected to have.

---

## 0. The current state, precisely

| Thing | Where | Reality |
|---|---|---|
| `PrivacySettings` `[Flags]` enum - `AllowDataCollection`, `AllowVoiceRecordedInClips`, `AllowDataUseForPersonalization` | `Identity.Domain/Enums/PrivacySettings.cs` | Persisted on `user_preferences`. **No write path, no read path.** Grep returns the enum declaration, the DbContext `MapEnum`, and the aggregate constructor. Nothing else. |
| `DirectMessageSettings` - `FilterAll`/`FilterNonFriends`/`AllowAll` | `Identity.Domain/Enums/DirectMessageSettings.cs` | Same. Messaging hardcodes friends-only and leaves `// TODO: We have to check the users privacy bit settings here` at `ConversationEndpoints.cs:257` and `:578`. |
| `RelationshipStatus.Blocked` | `Social.Domain/Enums/RelationshipStatus.cs:9` | In the enum and in the published OpenAPI. `FriendEndpoint.cs` implements create/accept/reject/revoke only. **Blocking does not exist.** |
| `OnlineStatus.Hidden` | `Social.Domain/Enums/OnlineStatus.cs:6` | Settable, and honoured by `@here` fan-out. But `GuildController.cs:144` projects the raw cached status onto `MemberDto.Status`, and `guild.PresenceChanged` broadcasts the raw string - so peers can tell "invisible" from "offline". |
| `JsonSettings` | `UserController.cs:181-218` | Unvalidated, unbounded, unschema'd client blob. Nothing in it is enforced. |
| `Tombstone()` | `ApplicationUser.cs:324` | Scrubs username/email/phone/bio/password/SteamId/JsonSettings/master key. **Leaves `BirthDate` and the entire `AgeVerification` value object.** `LoginSession` and `IdentityAuditEvent` rows keep IP + user-agent bound to the user id forever. |
| Purge fan-out | `Echo/Sagas/AccountDeletion.cs:30` | Six participants. **Bots and Isle are not among them.** |

What already works and must not regress: account deletion with grace period and cross-service
saga; per-session revoke with IP/UA (`LoginSession`); the append-only `IdentityAuditEvent` log;
MLS/E2EE and device protection levels; federation instance blocking and the SSRF guard.

---

## 1. Foundation - `UserPrivacySettings`

Everything in Tiers 0-2 depends on one owned, writable, cross-service-readable settings record.
Build this first; nothing else can be built against a moving target.

### 1.1 Storage

New entity `Identity.Domain/Entities/UserPrivacySettings.cs`, own table, 1:1 with
`ApplicationUser` (FK `user_id`, unique). **Not** added to `UserPreferences` - that entity is
projected wholesale through a Facet onto `GET /users/self`, and privacy settings need their own
endpoint, their own defaults, and their own change events.

Explicit columns, not a bit-flags enum. The existing flags enum is not queryable, cannot carry
per-field defaults, and Postgres-mapped `[Flags]` enums serialize to comma-joined strings.

```csharp
public class UserPrivacySettings : BaseEntity<UserPrivacySettings>, IPrefixedEntity
{
    public static string Prefix { get; } = "upvs";
    public string UserId { get; set; } = null!;

    // ── Data use (consent; all default FALSE - opt-in, never opt-out) ──
    public bool AllowDataCollection { get; set; }
    public bool AllowPersonalization { get; set; }
    public bool AllowVoiceRecordingInClips { get; set; }

    // ── Contactability ──
    public DirectMessagePolicy DirectMessagePolicy { get; set; } = DirectMessagePolicy.Friends;
    public FriendRequestPolicy FriendRequestPolicy { get; set; } = FriendRequestPolicy.Everyone;

    // ── Discoverability ──
    public bool DiscoverableByUsername { get; set; } = true;
    public bool DiscoverableByEmail { get; set; }          // default false
    public bool DiscoverableByPhone { get; set; }          // default false

    // ── Profile field visibility ──
    public Visibility MutualServersVisibility { get; set; } = Visibility.Friends;
    public Visibility MutualFriendsVisibility { get; set; } = Visibility.Friends;
    public Visibility ConnectionsVisibility  { get; set; } = Visibility.Friends;
    public Visibility BirthdayVisibility     { get; set; } = Visibility.Nobody;

    // ── Presence & activity ──
    public bool ShareActivity { get; set; } = true;              // "playing Isle"
    public bool AllowPositionalVoiceCapture { get; set; } = true;

    // ── Messaging behaviour ──
    public bool SendReadReceipts { get; set; } = true;
    public bool SendTypingIndicators { get; set; } = true;
    public int? DmRetentionDays { get; set; }                    // null = keep forever

    // ── Safety ──
    public ExplicitContentFilter ExplicitContentFilter { get; set; } = ExplicitContentFilter.UnknownSenders;

    // ── Push ──
    public bool HidePushContent { get; set; }

    public int Version { get; set; }     // bumped on every write; carried on the change event
}
```

New enums in `Identity.Domain/Enums/`:

```csharp
public enum DirectMessagePolicy  { Everyone, FriendsAndServerMembers, Friends, Nobody }
public enum FriendRequestPolicy  { Everyone, FriendsOfFriends, ServerMembers, Nobody }
public enum Visibility           { Everyone, Friends, Nobody }
public enum ExplicitContentFilter{ Off, UnknownSenders, Everyone }
```

Register each with `options.MapEnum<T>()` in `Identity.Infrastructure/Persistence/MicroserviceContext.cs`
alongside the existing ones.

### 1.2 Migration and backfill

One migration, `AddUserPrivacySettings`:

1. Create `user_privacy_settings` with the defaults above.
2. Backfill one row per existing user, translating the legacy values:
   - `PrivacySettings.AllowDataCollection` → `allow_data_collection`
   - `PrivacySettings.AllowVoiceRecordedInClips` → `allow_voice_recording_in_clips`
   - `PrivacySettings.AllowDataUseForPersonalization` → `allow_personalization`
   - `DirectMessageSettings.AllowAll` → `DirectMessagePolicy.Everyone`;
     `FilterNonFriends` → `Friends`; `FilterAll` → `Nobody`
3. **Leave the legacy columns in place.** Do not drop `user_preferences.privacy_settings` or
   `.direct_message_settings` in this migration - clients still read them off `GET /users/self`.
   Mark both `[Obsolete]` in the domain and schedule removal for a later release.

`ApplicationUser.Create` and `CreateBot` must both mint a `UserPrivacySettings` row, exactly as
they already mint `UserPreferences` (see `ApplicationUser.cs:208` and `:265`, and the
`User.Tests/ApplicationUserBotTests.cs` test that exists because a missing owned entity breaks
`SaveChanges`).

> Migrations in this repo are generated, not hand-written, except where a data move requires it.
> The backfill here does require it - generate the schema migration, then hand-add the `UPDATE`
> statements. Do **not** run two `dotnet ef` processes concurrently against this project.

### 1.3 Public REST surface (Identity)

```
GET   /api/v1/privacy-settings          → 200 UserPrivacySettingsDto
PATCH /api/v1/privacy-settings          → 200 UserPrivacySettingsDto
```

`PATCH` semantics, matching the notification-settings endpoints already in Guild: **every field
optional, omitted means "leave alone"**. No read-modify-write from the client. Reject unknown
fields with `400` rather than ignoring them.

Every successful write:
- bumps `Version`
- writes an `IdentityAuditEvent` with a new action `privacy-settings.changed` and a `Detail`
  naming the changed fields (never the values of anything sensitive)
- publishes `UserPrivacySettingsChangedEvent`

### 1.4 Cross-service access

Follow the existing request/response pattern exactly - see
`Identity.Contracts/Bus/Request/GetUserProtectionLevelRequest.cs` and its handler at
`Identity.Application/Consumers/GetUserDevicesHandler.cs`.

New in `Identity.Contracts`:

```csharp
// Bus/Request/GetUserPrivacySettingsRequest.cs
public class GetUserPrivacySettingsRequest { public ICollection<string> UserIds { get; set; } = []; }

// Bus/Response/GetUserPrivacySettingsResponse.cs
public class GetUserPrivacySettingsResponse { public ICollection<UserPrivacySettingsSummary> Settings { get; set; } = []; }

// Bus/Events/UserPrivacySettingsChangedEvent.cs
public class UserPrivacySettingsChangedEvent { public string UserId { get; set; } = null!; public int Version { get; set; } }
```

Batch by design (`UserIds`, not `UserId`) - every caller resolves policy for a set of people, and
the per-user shape is what makes `ConversationEndpoints` do N sequential bus calls today.

**Caching.** A `PrivacySettingsCache` in each consuming service, Redis-backed, keyed
`privacy_settings:user_id:{id}`, invalidated by `UserPrivacySettingsChangedEvent`. Redis rather
than in-memory: these are read on every DM send and every friend request, and a stale in-memory
copy on one pod means a user's "block DMs" toggle silently doesn't apply on that pod. Fail
**closed** - if the cache and the bus both fail, apply the restrictive default (`Friends` for DMs,
`Nobody` for anything else), never `Everyone`.

### 1.5 Acceptance criteria

- [ ] `GET`/`PATCH /api/v1/privacy-settings` round-trip every field
- [ ] `PATCH` with `{}` changes nothing and returns current state
- [ ] `PATCH` with an unknown field returns `400`
- [ ] Backfill translates all three legacy flags and all three DM settings correctly
- [ ] A new user and a new bot both get a settings row
- [ ] `UserPrivacySettingsChangedEvent` evicts the cache in every consuming service
- [ ] Cache miss + bus failure yields restrictive defaults, asserted by test
- [ ] Legacy `user_preferences` columns still read correctly off `GET /users/self`

---

## Tier 0 - make the existing surface real

### T0-1 Writable preferences
Covered by §1.3. Additionally: keep `GET /users/self` returning the legacy `userPreferences`
block unchanged (additive-only), and add the new settings under a separate `privacySettings` key
so a v1 client is unaffected.

### T0-2 Enforce the DM policy
Replace both `// TODO: We have to check the users privacy bit settings here` sites
(`Messaging.Application/Endpoints/ConversationEndpoints.cs:257`, `:578`) with a real check against
the **recipient's** policy - the current code checks the *initiator's* friend list, which is the
wrong direction.

Resolution order, per recipient:

| Recipient policy | Admitted when |
|---|---|
| `Everyone` | always (subject to blocking) |
| `FriendsAndServerMembers` | friends, or share ≥1 guild where the recipient has not disabled server DMs (T2-14) |
| `Friends` | friends only |
| `Nobody` | never |

A blocked initiator is refused before policy is consulted. Existing conversations are not
retroactively closed - a policy change governs *new* conversations and *new* one-to-one sends,
not membership of a group the user already joined.

Return `403` with a machine-readable code (`recipient_dm_policy`, `blocked`), not the current
`400 "User cannot be added to conversation if not friends"` - the client needs to distinguish
"not allowed" from "malformed".

Same check on the call-token path at `:578`.

### T0-3 Blocking
Social owns this; `RelationshipStatus.Blocked` already exists.

```
POST   /api/v1/relationships/{userId}/block     → 204
DELETE /api/v1/relationships/{userId}/block     → 204   (idempotent)
GET    /api/v1/relationships/blocked            → 200   (paged)
```

Blocking is **one-directional and asymmetric**: A blocking B stops B from reaching A, and is not
visible to B as a distinct state (B sees the same thing as "not friends"). Blocking an existing
friend removes the friendship. Blocking cancels any pending request in either direction.

Enforcement points - a block must be honoured in all of them, or it is theatre:

| Surface | Service | Behaviour |
|---|---|---|
| Friend request | Social | refuse, `403 blocked` |
| DM / group invite | Messaging | refuse; blocked user cannot be added to a conversation with the blocker |
| Message send in existing DM | Messaging | refuse |
| Call token | Messaging | refuse |
| Mentions / `@here` fan-out | Guild, Messaging | blocker receives no notification from the blocked user |
| Realtime pushes | Guild, Social | no `social.*` or presence events flow between the pair |
| Profile read | Social | blocked user gets the minimal public projection only |
| Federated inbound | Federation | drop at the inbox boundary |

New bus contract in `Social.Contracts`: `GetBlockRelationshipsRequest { UserIds }` →
`{ (BlockerId, BlockedId)[] }`, plus a `UserBlockedEvent` / `UserUnblockedEvent` for cache
eviction. Same Redis-cache-with-fail-closed rule as §1.4.

Purge: blocks referencing a purged user are deleted by Social's `PurgeUserDataCommandHandler`.

### T0-4 Gate telemetry on consent
`AllowDataCollection` currently gates nothing while Sentry and OpenTelemetry initialize
unconditionally (`AppEnvironment/SentryInstance.cs`, each `Program.cs`).

- Error/crash reporting is **service-operational** and stays on, but must carry no user identifier
  unless `AllowDataCollection` is set - pseudonymize to a per-install random id otherwise, and
  scrub `Email`, `UserName`, `PhoneNumber` and request bodies from every event and breadcrumb.
- Product analytics and any personalization signal require `AllowPersonalization`. There is no
  such pipeline today; the gate must exist before one is added, so add the check point and a test
  that fails if a personalization consumer reads user data without it.

If a flag cannot be honoured, delete it. A stored consent flag that gates nothing is worse than no
flag: it is a false representation to the user.

`SentryPrivacy.HasDataCollectionConsent` is the one hook, and every service must set it - a service
that leaves it unset is not "unconfigured", it is fail-closed-by-accident and will stay that way.
Identity answers from its own table (`DataCollectionConsentSnapshot`). Social, Guild and Messaging
have no table, so they answer from their existing Redis-backed `PrivacySettingsCache` through
`AppEnvironment/TelemetryConsent.cs`, which resolves ahead of time on a 15-second loop - the SDK
callback is synchronous and must never wait on Redis or on a bus call. Anything unresolved, stale
or over the tracking cap answers "no consent".

### T0-5 Stop leaking `Hidden`
Two sites, both in Guild:
- `Guild.Application/Controllers/GuildController.cs:144` - projects the cached presence string
  straight onto `MemberDto.Status`
- `Guild.Application/Bus/Events/Realtime/GuildLifecycleHandler.cs` - broadcasts the raw status

Introduce a single projection helper: **`Hidden` renders as `Offline` for every viewer except the
user themselves.** `Hidden` must never appear on the wire to a third party. Apply it in Social's
profile projections too. Keep the enum member - the leak is in the projection, not the model.

The `@here` fan-out already treats `Hidden` as absent (`MessageCreatedHandler.cs:178`); that
behaviour is correct and must not change.

### T0-6 Contain `JsonSettings`
`GET`/`PUT /users/self/settings` accepts an arbitrary `JsonElement` and stores it forever.

- Cap the serialized size (16 KB) and reject `413` above it
- Reject a non-object root with `400`
- Cap nesting depth
- Document it as **client-owned UI state with no server semantics**, and state explicitly that
  nothing privacy-relevant may live there - anything that must be enforced belongs in §1.1

---

## Tier 1 - legal and compliance floor

### T1-7 Data export (GDPR Art. 15 / 20)
Mirror the deletion architecture, which already solves the hard part.

```
POST /api/v1/data-exports        → 202  { exportId, status: "Pending" }
GET  /api/v1/data-exports        → 200  [ { exportId, status, requestedAt, completedAt,
                                            expiresAt, failureReason, missingServices } ]
GET  /api/v1/data-exports/{id}/download → 302 to a short-lived signed URL
```

- `DataExportRequest` entity in Identity: `UserId`, `Status`
  (`Pending|Running|Ready|Partial|Failed|Expired`), `RequestedAt`, `CompletedAt`, `ExpiresAt`,
  `ArtifactKey`, `FailureReason`, `MissingServices`
- `ExportUserDataSaga` in `Echo/Sagas/`, shaped exactly like `AccountDeletionSaga`: fan
  `ExportUserDataCommand` to every participant, collect `ExportUserDataResponse` fragments, assemble
- Participants: identity, social, guild, messaging, federation, import, **bots, isle** (unlike the
  deletion saga, which omits the last two - see T1-9)
- Each service returns its own JSON fragment; the assembler writes one zip with a
  `manifest.json` naming each fragment's producing service and row counts
- **Rate limit one request per user per 24h**, and expire the artifact after 7 days

#### Terminal states

An export ends in one of four states, and the distinction between the first two is the whole point:
under Art. 15, "here is everything we hold about you" and "here is most of it" are different
answers, and a subject told their export is *ready* reasonably believes they received everything.

| Status | Archive | Downloadable | Counts against the 24h limit | Means |
|---|---|---|---|---|
| `Ready` | yes | yes, until `ExpiresAt` | **yes** | Every participating service produced its section. |
| `Partial` | yes | yes, until `ExpiresAt` | **no** | The archive was assembled and uploaded, but at least one service's section is absent. `missingServices` names them; `failureReason` says the same in one sentence. |
| `Failed` | no | no - `409` | **no** | No archive was produced at all (assembly or upload threw). |
| `Expired` | deleted | no - `410` | n/a | The seven-day window closed. The row survives; so does `missingServices`. |

- **`Partial` is decided by `AssembleUserDataExportCommandHandler`, from the fragments themselves.**
  Any fragment carrying an `Error` is a service whose section is absent - whether it answered with a
  failure, or whether `ExportUserDataSaga`'s deadline elapsed with it still silent and the saga wrote
  a stand-in fragment in its place (T1-9). Both are the same hole to the subject. No extra bus
  contract was needed: `AssembleUserDataExportCommand` already carried the errors.
- **A `Partial` archive is still downloadable**, and deliberately so. It is the subject's own data,
  produced in answer to a statutory request; refusing to serve it because two of eight services were
  down would turn "some of this is missing" into "you get nothing". The download route gates on
  *"is there an artifact"* (`DataExportRequest.IsDownloadable`), never on `Status == Ready`.
- **A `Partial` does not consume the rate-limit window**, on the same reasoning as `Failed` plus one
  more: it is the only one of the two where the subject has been handed something that looks like an
  answer, so charging it would cost them a statutory day *and* leave them holding an incomplete
  disclosure they were told to wait to re-request. The cost the limit guards is bounded here - an
  archive missing whole services is by definition the cheap one, and it expires on the same clock.
- **Client compatibility.** `Partial` is a new value in an existing string-valued field, so a client
  written before it existed sees an unrecognised status. Two consequences worth stating: a tolerant
  client (`status: string`) renders it as unknown and, if it gates its download button on
  `status === "Ready"`, hides a download the server would happily serve; a strict client with a
  closed enum fails to parse the row. Both degrade *safely* - neither shows an incomplete export as
  complete, which is what keeping the status binary would have done for every client. The client fix
  is to treat `Ready` and `Partial` alike for download and to render `missingServices`.
  `missingServices` is always present and never null, including on `Ready`, where it is `[]`.
- `Partial` needs **no database enum change**: `status` is a `varchar(32)` holding the member name,
  which is why it was made a string column in the first place. The only schema change is the
  `missing_services text[]` column (`20260804084416_AddDataExportMissingServices`).
- Download is authenticated *and* the artifact key is unguessable; log every download as an
  `IdentityAuditEvent` (`data-export.downloaded`) - an export is the single densest bundle of
  personal data in the system and a stolen session downloading one must be visible, exactly as
  backup reads already are
- The export must **not** contain other users' personal data: message bodies the user sent, yes;
  other members' emails, no

### T1-8 Retention
Nothing in the system has a TTL today. Add config-driven retention with a sweep per owning service,
modelled on the existing `AccountDeletionPurgeSweepService`.

| Data | Default | Owner |
|---|---|---|
| `LoginSession.IpAddress` / `UserAgent` | scrub at 90 days; keep the row | Identity |
| `IdentityAuditEvent.IpAddress` | scrub at 180 days; **keep the row forever** (append-only audit) | Identity |
| Revoked `LoginSession` rows | delete at 180 days | Identity |
| `DataExportRequest` artifacts | delete at 7 days | Identity |
| Deleted-message tombstones | 30 days | Messaging |
| Orphaned media/attachments | 30 days after last reference | Messaging/Social |
| User-set DM retention (`DmRetentionDays`) | opt-in, off by default | Messaging |

Every TTL is an `AppEnvironment/Env.cs` setting with the default above. Scrubbing an IP must not
delete the audit row - the event is the record, the IP is the incidental detail.

### T1-9 Complete the tombstone and the purge
- `ApplicationUser.Tombstone()` must also clear `BirthDate` and reset `AgeVerification` to a
  purged state. Retain only a non-identifying `WasVerifiedAdult` boolean if the deployment needs it
  for legal defence; otherwise clear it entirely.
- Scrub `IpAddress`/`UserAgent` on the user's `LoginSession` rows and `IpAddress` on their
  `IdentityAuditEvent` rows during purge, keeping the rows.
- Add **bots** and **isle** to `AccountDeletionSaga.ParticipatingServices`. Each needs a
  `PurgeUserDataCommandHandler`:
  - Isle: `Player.UnlinkUserId` plus scrub of any stored positional/voice data
  - Bots: transfer or disable applications the purged user owned; a bot app must not be orphaned
    into an unadministrable state
  Both are called out in the saga's own doc comment as deliberate follow-ups. This is that follow-up.
- Adding participants makes the fan-out wider and therefore likelier to stall, so both sagas carry a
  deadline (`Env.SagaDeadlines`, one hour each by default). On expiry they log at `Error` naming the
  individual services that did not acknowledge, increment
  `echo.privacy_saga.deadline_exceeded{saga,service}` and raise a Sentry event. **A purge deadline
  never marks the purge complete** - it re-arms and keeps alerting, because reporting an erasure
  that did not happen is worse than a visible stall; an export deadline does resolve the request,
  with an explicit error section per missing service, because a disclosure can say what it is
  missing. See the doc comments on `Echo/Sagas/AccountDeletion.cs` and `Echo/Sagas/ExportUserData.cs`.
  A deadline-resolved export is reported as **`Partial`**, not `Ready` - see the terminal-state table
  in T1-7 - so "the archive says what is missing" does not depend on anybody unzipping it.
- Deletion must propagate into backups, or the retention window of backups must be documented and
  shorter than the deletion SLA. A restore that resurrects a purged account is a reportable breach.
  This is an operations decision that cannot be made in code and **has not been made**: see
  [`docs/runbooks/backup-deletion-propagation.md`](../runbooks/backup-deletion-propagation.md) for
  the requirement, the two acceptable resolutions, what is actually backed up in this deployment,
  and the empty decision record that has to be filled in.

### T1-10 Versioned consent records
No record exists today that a user ever accepted anything.

```csharp
// Identity.Domain/Entities/
public class LegalDocument   { Id; DocumentType (Terms|Privacy|Cookies); Version; EffectiveAt; ContentHash; Url; }
public class UserConsent     { Id; UserId; DocumentType; Version; AcceptedAt; IpAddress; }
```

```
GET  /api/v1/legal/documents            → current version of each document
GET  /api/v1/legal/consents             → what the caller has accepted
POST /api/v1/legal/consents             → { documentType, version }
```

- Registration records consent for the then-current Terms and Privacy versions, with IP
- Publishing a new version leaves existing consents intact and marks the account as having an
  outstanding consent; the client is told via a `consentRequired` array on `GET /users/self`
- Withdrawal of *optional* consent (T0-4 flags) is immediate and must not degrade core service.
  Terms/Privacy are not withdrawable while the account is active - the withdrawal path there is
  account deletion, and the client should say so

### T1-11 Minor protections
`AgeVerification.BirthDate` is captured and drives nothing.

Derive `IsMinor` (jurisdictional age, default 18, configurable; digital-consent age 16 where that
is what applies) and enforce **server-side**, not as a client default:

- `DirectMessagePolicy` floor of `Friends`; `Everyone` is refused
- `AllowPersonalization` forced false and not settable
- `DiscoverableByEmail`/`ByPhone` forced false and not settable
- `AllowVoiceRecordingInClips` forced false
- Explicit content filter floor of `UnknownSenders`

A `PATCH` that would violate a floor returns `403 minor_restriction` naming the field. Re-evaluate
on birthday rollover - a user who ages out gets the settings unlocked, not silently kept restricted.

### T1-12 Legal document hosting
There is no privacy policy or ToS anywhere in the repo.

Serve versioned documents (markdown in-repo, rendered) at public URLs, referenced by
`LegalDocument.Url` and hashed into `ContentHash` so a silent edit is detectable.

**Write placeholders with an explicit `<!-- LEGAL REVIEW REQUIRED -->` banner and a structural
outline only.** Do not draft operative legal text - that is counsel's job, and a plausible-looking
generated policy is worse than an obvious placeholder because it will ship.

### T1-13 DSR intake
An admin surface for rights requests that arrive out-of-band (email, post) and for non-account-holders:

```
POST   /api/v1/admin/dsr           → open a request { subjectEmail, type, notes }
GET    /api/v1/admin/dsr           → queue
PATCH  /api/v1/admin/dsr/{id}      → progress / close with disposition
```

Admin-only, every action audited with the acting staff id. Types: `Access`, `Erasure`,
`Rectification`, `Portability`, `Objection`. Track the statutory clock (30 days) and surface
overdue items - an unanswered request is the violation, not a wrong answer.

---

## Tier 2 - the controls users expect

### T2-14 Per-guild DM toggle
Discord's most-used privacy control. New `GuildDirectMessagePreference` in Guild:
`(UserId, GuildId, AllowDirectMessages)`, default from the user's global policy.

```
GET /api/v1/users/me/guild-privacy                    → all overrides
PUT /api/v1/guilds/{guildId}/privacy  { allowDirectMessages }
```

Consumed by T0-2's `FriendsAndServerMembers` branch: a shared guild only admits a DM if the
recipient has not disabled DMs *for that guild*.

### T2-15 Friend-request policy
Enforce `FriendRequestPolicy` in `Social.Application/Endpoints/FriendEndpoint.cs` at
`WolverinePost("/api/v1/relationships")`:

| Policy | Admitted when |
|---|---|
| `Everyone` | always (subject to blocking) |
| `FriendsOfFriends` | ≥1 mutual friend |
| `ServerMembers` | ≥1 shared guild |
| `Nobody` | never |

`Everyone` is the default (matching Discord) - the block list is the escape hatch. Refuse with
`403 friend_request_policy`; do **not** leak *which* rule refused, and do not reveal whether the
target exists.

### T2-16 Discoverability

**Status: implemented, with one flag live and two deliberately inert. Read the finding before
changing anything here.**

#### What the audit found

`DiscoverableByEmail` and `DiscoverableByPhone` were **vacuous**. A sweep of every service for a way
to resolve a user from an email address or a phone number found none:

| Area | Result |
|---|---|
| Identity `.Email` / `.NormalizedEmail` queries | login, password reset, email verification, registration uniqueness - all self-service flows about the **caller's own** account, none of them one person finding another |
| `AdminDsrController` | resolves `subjectEmail` → user id, admin-gated, result never returned to a non-staff caller. Staff tooling, not discovery |
| `PhoneNumber` | **never queried at all.** There is a unique index on it and nothing reads it |
| Contact import / address-book upload / friend-request-by-email / invite-by-email | do not exist - no route, DTO, handler or entity |
| Guild invites | code-based only |
| Import service | maps guild *structure* (categories/channels/roles). It performs no Discord-user → Echo-user matching of any kind and contains no reference to email or phone |
| Federation | no WebFinger, no `acct:` handle, no actor resolution by handle |
| Identity bus contracts | there is no `GetUserByEmailRequest` and no `GetUserByPhoneRequest` |

So there was nothing to gate. **No endpoint was invented to justify the settings.**

#### What was built instead

`Social.Application/Services/UserDirectory.cs` - the single chokepoint for finding a user by
anything a human typed. `FindAsync(DirectoryKey, value)` resolves *and* gates in one step:

- `DirectoryKey` has one member per `Discoverable*` flag: `Username`, `Email`, `Phone`
- `IsDiscoverableBy` maps each key to its own flag, with `_ => false` for anything unrecognised
- `ResolveAsync` is the **only** resolver table. `Username` queries `Profiles.UserName`; `Email` and
  `Phone` return null, because Social's `Profile` stores neither
- `FriendEndpoint`'s username lookup goes through it and nothing else does a raw identifier query

The point is placement: someone adding email lookup later writes one line in `ResolveAsync` and gets
`DiscoverableByEmail` applied whether or not they had heard of it. The alternative - leaving the
flags unreferenced until a lookup appears - is how an ungated lookup ships.

Pinned by `Social.Tests/Services/UserDirectoryTests.cs`: the per-key flag table, an unknown key
failing closed, email and phone resolving nothing *even with their flag switched on*, and
"not discoverable" being byte-identical to "no such user".

#### Rules that hold

- `DiscoverableByUsername` defaults **true** - an exact-username lookup is how people find each other
  here. `ByEmail`/`ByPhone` default **false** and are forced false for minors (T1-11)
- A non-discoverable user and a nonexistent one produce the same `null` from the directory and the
  same `403 friend_request_policy` from the endpoint. Nothing distinguishes them in status or body
- An Identity outage makes everyone undiscoverable, never everyone discoverable
  (`PrivacySettingsCache.RestrictiveDefaults` sets all three flags false)

#### Known limits

- **Residual timing difference.** The not-found path returns before the privacy lookup; the
  not-discoverable path performs one cache read first. The responses are identical, so this is not an
  enumeration oracle over the wire, but it is not constant-time either. Closing it would mean issuing
  a bus call for identifiers nobody holds, which trades a measurable oracle for a real amplification
  vector. Documented rather than papered over
- **Chokepoint, not a wall.** Nothing stops a future contributor from writing
  `ctx.Profiles.FirstOrDefaultAsync(...)` at a call site and bypassing the directory. There is no
  architecture test for this yet; the guard is the class's placement and its doc comment
- **Identity is not covered.** The equivalent chokepoint for a lookup added on Identity's side would
  have to live in Identity, and does not exist. Anyone adding a `GetUserByEmailRequest` must gate it
  themselves - the flags are on the summary `GetUserPrivacySettingsRequest` already returns
- Unrelated but adjacent: `UserVerificationEndpoint` returned `202` for an unknown address and `400
  "User already verified"` for a known confirmed one, which is an email-existence oracle to any
  anonymous caller. It returns no identity, so it is not discoverability in this item's sense, but it
  is the same class of leak `PasswordResetEndpoint` deliberately avoids. **Now fixed** - see
  "Anonymous account enumeration" below, which also closes the larger one on `/connect/token`

### T2-17 Profile field visibility

Apply `MutualServersVisibility`, `MutualFriendsVisibility`, `ConnectionsVisibility`,
`BirthdayVisibility` in `Social.Application/Controllers/ProfileController.cs` - in the
`GET /{id}` and `GET /by-user/{id}` projections and in the bus handlers
(`GetProfileByUserIdHandler`, `GetProfilesByUserIdsHandler`) so other services cannot route around
them.

Do the filtering **in the projection**, never client-side: a field the viewer may not see must not
be in the payload at all.

**Status: all four gates live and all four sourced.** `ProfileProjectionService` is the single seam;
`ProfileVisibility.Project` is the single gate. A hidden field is not merely omitted from the body -
it is never *fetched*, so a bug in the projector cannot leak data the service never loaded.

| Field | Source | Default |
|---|---|---|
| `mutualFriends` | Social's own relationship graph | `Friends` |
| `mutualServers` | Guild, `GetSharedGuildsRequest` | `Friends` |
| `connections` | Identity, `GetUserConnectionsRequest` | `Friends` |
| `birthday` | Identity, `GetUserBirthdaysRequest` | **`Nobody`** |
| `activity` | nothing reports rich presence yet - enforcement point only (T2-19) | `ShareActivity` |

#### Birthday

`GetUserBirthdaysRequest { UserIds }` → `GetUserBirthdaysResponse { UserBirthdaySummary[] }`,
handled by `Identity.Application/Consumers/GetUserBirthdaysHandler.cs`. Batched like
`GetUserPrivacySettingsRequest`, for the same reason.

`BirthdayVisibility` stays at **`Nobody`** by default and must not be widened: a full date of birth
is identity-theft-grade on its own and it is what the minor floors are derived from.

**Two gates, not one.** Social applies the per-viewer gate - it is the only side that knows the
reader's relation to the subject. Identity applies a viewer-*independent* floor on top: an account
whose `BirthdayVisibility` is `Nobody` gets no answer at all, because there is no viewer for whom
answering could be correct. That floor is strictly weaker than Social's gate, so the two cannot
disagree about which was authoritative; what it buys is that the most re-identifying field on the
account cannot be pulled over the bus by a future caller who forgets to gate it. An account with no
settings row gets the same treatment.

Every "no" is the same null: hidden, never recorded (bot accounts), purged (T1-9), and unknown id.
`default(DateOnly)` is reported as null, never as `0001-01-01`.

**Known gap:** there is no "hide the year" granularity. Discord has one, and the year is the part
that makes a DOB useful to an attacker. Today the field is all-or-nothing per viewer class.

#### Connections

`GetUserConnectionsRequest { UserIds }` → `GetUserConnectionsResponse { UserConnectionsSummary[] }`
where each summary carries `ExternalConnectionSummary { Type, ExternalId, DisplayName? }`, handled by
`Identity.Application/Consumers/GetUserConnectionsHandler.cs`.

Steam (`ApplicationUser.SteamId`) is the only link type that exists, and it is treated as the
**first** one: the field is a list of typed entries, not a `steamId` key, so a second provider is an
additive change on both the bus contract and `ProfileConnectionDto`.

A raw SteamID64 is a stable cross-platform correlation handle - it resolves to a public Steam
profile, a friend list and a play history - so it sits behind `ConnectionsVisibility` like everything
else, never appears in the stranger or blocked projection, and gets the same viewer-independent
`Nobody` floor in Identity as the birthday. An account with **no settings row** is refused outright,
which is stricter than the shipped `Friends` default: a missing row means something unexpected
happened, and the safe reading of "unexpected" is not to hand out the handle.

`ProfileConnectionDto`'s item shape changed from `{ type, name, verified }` to
`{ type, externalId, displayName?, verified }`. Not a wire break: `connections` had only ever
serialized as `[]`, because no source was wired to it until now.

### T2-18 Read receipts and typing indicators
`SendReadReceipts` and `SendTypingIndicators`, both default true, both **reciprocal**: a user who
does not send read receipts does not receive them either. Anything else lets a user take without
giving, which is the one property that makes the setting unusable in practice.

Enforce at the emit site in Messaging and Guild (`GuildTypingHandler`), not at the render site.

### T2-19 Activity and positional voice
- `ShareActivity` gates "playing Isle" presence in Social and Guild projections
- `AllowPositionalVoiceCapture` gates Isle's proximity voice registration
  (`VoiceTrackRegistry` / the Cloudflare SFU path). When false, the user may still speak in
  non-positional channels but is not registered for positional capture

### T2-20 Explicit content filter
`ExplicitContentFilter` (`Off | UnknownSenders | Everyone`, default `UnknownSenders`) applied to DM
attachments. Scan/classification integration is out of scope here - this spec adds the setting, the
enforcement point, and a pluggable `IMediaClassifier` with a no-op default, so the control exists
and is honoured the moment a classifier is wired in.

### T2-21 Voice-clip consent
`AllowVoiceRecordingInClips` is an account-level flag and **is not valid consent to record other
participants**. Any clip feature must capture consent per session, per participant, at record time,
and must refuse to record a participant whose flag is false.

There is no clip feature today. This spec's requirement is that the enforcement point exists and
fails closed, so the feature cannot ship without it.

### T2-22 User-set DM retention
`DmRetentionDays` (null = forever). Messaging's retention sweep deletes the user's own messages
older than the window. Applies to **messages the user sent**, in every conversation - deleting the
other side's messages from their own history is not the user's right to exercise.

### T2-23 Push content privacy
`HidePushContent` - when set, every push payload for that user carries no message body, author
name, or channel name; only routing ids. The encrypted-message path already does this
("You have a new encrypted message"); reuse it.

---

## Cross-cutting rules

1. **Fail closed.** Any policy resolution that cannot reach its data applies the restrictive
   default. Never `Everyone` on error.
2. **Enforce server-side.** A client-side privacy control is a suggestion. Every item here has a
   server enforcement point, including the ones with obvious client UI.
3. **Filter in projection.** Data the viewer may not see must not be in the response body.
4. **Additive API changes.** Existing response shapes keep their fields; new state arrives under new
   keys. v1 clients must not break.
5. **Indistinguishable refusals.** "Blocked", "not discoverable", and "does not exist" must be
   indistinguishable to the refused party wherever an enumeration oracle would otherwise exist.
6. **Audit the sensitive ones.** Privacy-settings changes, export requests, export downloads, and
   DSR actions all write `IdentityAuditEvent` rows.
7. **Tests per change: normal, edge, negative.** Negative cases here are the point - the test that
   matters is the one asserting a refusal.

## Anonymous account enumeration

Rule 5 above applies to the *pre-auth* surface too, and that is where it was being broken hardest.
T2-16 goes to some trouble to make "not discoverable" and "does not exist" indistinguishable to a
logged-in viewer; several anonymous endpoints in Identity answered the same question outright, to
callers with no account at all.

**Fixed:**

| Endpoint | Was | Now |
| --- | --- | --- |
| `POST /connect/token` (password grant) | `404` unknown user, `403 "Email not verified."` before the password check, `401` wrong password | `401` for unknown user *and* wrong password, identical body. The two `403`s moved behind the password check |
| `GET api/v1/user/generate-verification-code` | `202` unknown, `200` known unverified, `400 "User already verified"` known verified | `202` for everything; mail sent only when there is something to send |
| `GET api/v1/user/verify-email` | `202` unknown, `400` in three different wordings for known | `400` with one wording for every unusable code, unknown address included |
| `POST api/v1/user/reset-password` | `400 "Invalid code"` unknown vs `400 "Invalid or expired code"` / `"Too many incorrect attempts"` known | `400` with one wording for all of them |
| `GET api/v1/user/request-password-reset` | already uniform `202`, but only the *status* was - the send blocked the response on the account-exists branch | `202`, with the render and send moved off the request path |
| `POST api/v1/authentication/register` | `200 {userId}` for a free address, `400 "Email already exists"` for a taken one | `202` with a fixed body and no user id, for both. A taken address creates nothing and its owner is mailed a "someone tried to sign up" notice |

Two things generalise from this:

- **A uniform status code is not a uniform response.** Two of these agreed on `400` and leaked
  through the body; one agreed on both and leaked through a several-hundred-millisecond mail send.
  The regression tests compare a fingerprint of status + observable headers + body, not a status code
- **Collapsing refusal reasons is worth the UX.** "Expired", "wrong" and "too many attempts" told
  apart give a two-request oracle that survives a uniform `202` on the request route: ask for a code,
  then submit a wrong one, and only a real account has anything cached to be *wrong* against. All
  three collapse to one string, and "request a new one" is the right instruction in every case anyway

**Deliberately kept:**

- `403 "Email not verified."` and `403 "User is not allowed to sign in"` at `/connect/token`, but
  only *after* a correct password. The client needs the precise reason to route the user to the
  verification screen, and a caller holding the credential learns nothing they could not already
  establish
- The `423` lockout answer, which an attacker can use to confirm an account exists by guessing at it
  five times. Telling a user under attack that they are under attack is worth more than the bit

### Registration

The last of the six, and the only one that needed a product decision rather than a patch: closing it
changes the success contract. `POST api/v1/authentication/register` now answers **`202` with a fixed
body** whether or not the address is taken, the `userId` is gone from the response entirely, and a
taken address creates nothing at all - no user row, no password hash, and no T1-10 consent record
against an account the caller does not own. The account holder is mailed instead of the caller: a
"someone tried to sign up with your address" notice, or the verification code they are missing if
their own account is still unverified. Client migration: `docs/specs/registration-contract-change.md`.

Three things this one adds to the list above:

- **Returning a fake id would have been worse than the leak.** The only way to keep a `userId` in the
  response was to invent one for the taken branch, and a client that stores a fabricated id and then
  acts on it fails later, elsewhere, and silently. The id now comes from the `sub` claim of the token
  the user gets after verifying, which is where a client should have been reading it anyway
- **Sending mail on an anonymous route is a new weapon.** The endpoint now sends mail to an address
  the caller does not control, so without a cap it is a mail cannon aimed at anyone. `RegistrationNoticeThrottle`
  caps notices per address per 24h in the same Redis the one-time codes already live in, and the HTTP
  response is identical whether or not a notice was actually sent - a caller that could see
  "throttled" would have the oracle back at a lower resolution
- **The order of the surviving refusals is the fix.** Every refusal registration can still give - age
  floor, taken username, malformed address - is decided *before* the address is looked up, so none of
  them can be a function of whether it is registered. Checked in the other order, submitting a taken
  username (or an underage birth date) with the address you are probing answers `400` for a free
  address and `202` for a registered one, which is the same oracle wearing the fix as a disguise

**Deliberately kept - a distinguishable "username already taken" (`400`).** Usernames are discoverable
by design here: `DiscoverableByUsername` defaults true and username lookup is how friend requests are
addressed, so "that name is taken" tells an attacker nothing an account holder cannot already
establish, and it routes around no control T2-16 sets up. Email is the opposite - nothing in the
product resolves one user's address for another, so registration would be the only place it leaked.
The UX side decides it on its own: the server owns the username namespace, so a user who cannot be
told "pick another" cannot proceed, and collapsing it into the uniform `202` would silently drop
those registrations while telling the user to check an inbox that will never receive anything. The
earlier fix that stopped a duplicate username echoing raw Postgres constraint text to anonymous
callers still stands - the explicit check refuses with one fixed sentence and no database detail.

## Out of scope

Reporting/abuse flow, staff-access controls, bot data scopes, federation privacy propagation,
metadata-exposure documentation, and backup deletion propagation are Tier 3 and are **not** covered
by this spec.
