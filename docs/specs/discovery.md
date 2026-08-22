# Discovery

Public communities and recruitment postings, searchable across the instance, gated on the
publishing guild's plan.

Two product asks arrived separately. A roleplay guild wants to advertise a game and take
applications from players who are not members yet. Any guild on a paid plan wants to be findable at
all. They are the same object at different sharpness: a guild-owned, plan-gated, publicly indexed
card that a stranger finds, then either joins or applies to. Building them apart means building the
plan gate, the index, the browse surface, the join state machine and the report path twice, and then
watching them drift.

This spec treats them as one subsystem in a new service.

---

## 1. Why a separate service

Discovery could have gone in `Guild.Application`, which already owns listings' natural parent, or
split across Guild and Social, which already owns the game catalog and the social graph. Both were
rejected.

Guild-only puts user interests in the guild service, which is the wrong owner, and turns the game
name join into either a cross-service call on the query path or a snapshot with its own freshness
problem. Split-ownership makes every ranked query need the caller's interests from another service's
database.

The deciding argument is that the read model here is not the guild's. It is a denormalized,
rank-ordered, full-text index over guilds, postings, topics and per-user interests, with a write
rate near zero and a read rate that follows browsing rather than chatting. That does not belong
inside a service whose hot path is channel permissions.

The cost of a separate service is low here because of one existing fact: **every service hosts
`EchoRealtimeHub` itself.** `Guild.Application` injects `IHubContext<EchoRealtimeHub>` directly in
around a dozen handlers. Discovery pushes its own realtime events with no relay hop, which is the
tax a separate service would normally pay.

Projects follow the house layout: `Discovery.{Domain,Contracts,Application,Infrastructure,Tests}`.

---

## 2. What Discovery owns, and what it mirrors

Owned outright, in Discovery's own Postgres database:

`Listing`, `Posting`, `Application`, `ApplicationAnswer`, `Tag`, `UserInterest`, `Report`.

Mirrored locally, never a synchronous cross-service read on the query path:

| Mirror | Source | Why it is projected |
|---|---|---|
| `guild_profile` | Pulled from Guild on a 6-hour TTL | Name, icon, banner, member count, active-member count, enabled modules, rendered on every card and used by the ranking function. Pull, not event-projected: Guild publishes no guild-lifecycle events today, only the `...ForBots` family, and adding five to feed a card is a larger change to Guild than this feature earns. |
| `game_topic` | Social's game catalog | Must be joinable inside the ranked query. About 900 KB gzipped for the whole catalog, so a local copy is cheap. |

Deliberately not projected: **entitlement standing**. The plan gate is checked live through
`Echo.Entitlements` at publish time and posting-create time. A stale projection answering "yes"
sells something that was not bought, which is the one staleness here with a real cost.

`guild_profile` staleness is tolerable and self-correcting: a renamed guild shows its old name on a
card until the row's TTL expires and a feed request pulls it fresh. Worth stating rather than
discovering, because it means the guild name on a Discovery card is not authoritative and must not be
used for anything but display.

### 2.1 The consistency seam

Publishing writes to Discovery and returns immediately. There is no window where a guild has
published and Discovery does not know, because Discovery is the writer. The seam is the other
direction: a guild renamed a moment ago still shows its old name until the next card page pulls that
guild past its TTL.

This is why `guild_profile` carries `projected_at`. A projection older than 24 hours for a guild
with a published listing is a reconcile trigger, not an error - the pull is demand-driven, so a row
stays that stale only when no feed page has asked for its guild in all that time.

---

## 3. Topics

Interests and listing subjects share one vocabulary, because the whole point is that a profile
saying "The Isle" and a listing saying "The Isle" meet.

`TopicRef` is a pair, `(kind, id)`:

```
game:{gapp_id}     resolves against the mirrored game catalog
tag:{slug}         a canonicalized free-form topic
```

### 3.1 Games come from the existing catalog

`Social.Domain/Aggregate/GameApplication.cs` already carries everything a topic vocabulary needs and
was built for a different purpose:

- `Name`, the canonical display name.
- `Aliases[]`, documented as being for "search and de-duplication".
- `SteamAppId`, present for roughly three quarters of the catalog.
- `IsEnabled`, which suppresses a bad row without deleting it.
- `Source`, whose `Community` value already means "submitted by a user and accepted, the seeder must
  never touch it".

That is the alias-and-merge machinery that a new tag table would have had to grow, already built and
seeded from `Social.Infrastructure/Seed/game-catalog.seed.json.gz`. Reusing it means "MSFS 2024",
"MSFS2024" and "Microsoft Flight Simulator" collapse to one topic on day one rather than after a
staff cleanup.

Discovery mirrors `(id, name, aliases, steam_app_id, is_enabled)` and nothing else. The executable
matching rules stay in Social, where they are the point.

### 3.2 Tags cover what is not a game

A `tag` table for subjects the game catalog cannot hold: `dnd-5e`, `freeform`, `play-by-post`,
`dark-fantasy`, `art`, `study`, `west-marches`.

```
tag
  slug          primary key
  display_name
  alias_of      nullable self-reference, set when staff merge two tags
  usage_count   denormalized, refreshed on write
```

Slugs normalize on write: NFKD, lowercase, punctuation stripped, whitespace folded to hyphen. A tag
whose `alias_of` is set resolves through to its target on read, so a merge fixes every existing
listing and profile without a data migration.

### 3.3 One picker

A single autocomplete endpoint searches games by name and alias and tags by slug and display name,
in one trigram query, with games ranked above tags. Games have real aliases and real names; tags are
the tail. Ranking games first is what makes people converge on the existing topic instead of minting
a near-duplicate, which is the failure mode that kills free-form tagging.

A listing carries 1 to 8 topics. A profile carries up to 25 interests.

### 3.4 What falls out later, and is not built now

Activity detection already resolves a running process to a `gapp_id`. Once interests exist, "you
have played The Isle for 40 hours, add it to your interests?" is one prompt away, and it fills the
coldest part of the cold start. It is out of scope here and needs its own consent design, but the
data model must not close the door: `user_interest` carries a `source` column
(`Manual | Suggested | Imported`) from the first migration so that prompt does not need one later.

---

## 4. Entitlements

Two flags, following `guild.vanity_url` in `Echo.Entitlements/Keys/EntitlementKeys.cs:52` exactly:

```csharp
EntitlementKey.Flag("guild.public_listing", EntitlementScope.Guild, false);
EntitlementKey.Flag("guild.recruitment",    EntitlementScope.Guild, false);
```

Both granted from Plus upward. Self-host grants both, matching the precedent that
`SelfHostEntitlementTests` pins for the vanity URL.

They are two keys rather than one because pricing may later split them, and splitting a shipped
single key means a migration and a support conversation. They resolve identically today.

Denial answers 403 in the shape `Guild.Application/Endpoints/VanityUrlEndpoint.cs:93` already
returns, so the client's existing error copy path handles it:

```json
{ "error": "public_listing_not_entitled", "message": "This guild's plan does not include a public listing." }
```

### 4.1 Downgrade

The case worth designing rather than discovering. Discovery consumes `entitlements.Changed` and
moves an affected listing to `Suspended`. It is never deleted.

The listing leaves the feed. Its content, its postings and its application history survive intact.
The owner sees the reason on the listing editor. Re-subscribing restores it in one action with
nothing retyped.

Open applications on a suspended listing stay open and stay answerable. A guild that lapses for a
week must not lose the applications it already received, and an applicant must not be silently
dropped because someone else's card expired.

---

## 5. Listing

One per guild, at most one. The community's identity.

```
listing
  id                disc_ prefixed
  guild_id          unique
  headline          <= 80 chars
  pitch             <= 600 chars
  topics            1..8 TopicRef
  language          BCP-47
  join_policy       Open | Application
  links             0..3, host allowlist, see section 8
  state             Draft | Published | Suspended | Unlisted
  published_at      nullable
  last_bumped_at    nullable
  search_vector     generated, GIN indexed
  created_at, updated_at
```

Banner and icon are not stored. They come from `guild_profile`, so a guild that changes its icon
changes its card without touching Discovery.

`Unlisted` is owner-initiated withdrawal. `Suspended` is imposed, either by plan loss or by staff.
They render differently to the owner and identically to everyone else, which is the whole reason
they are separate states.

---

## 6. Posting

Many per listing. Where a listing is identity, a posting is an ask with a lifetime.

```
posting
  id                post_ prefixed
  listing_id
  kind              Campaign | OneShot | Freeform | Community | Team | Other
  title             <= 100 chars
  body              <= 4000 chars
  topics            0..8, inherits the listing's when empty
  seats_total       nullable
  seats_filled
  join_policy       inherits the listing's, may differ
  schedule          <= 120 chars, free text, "Sundays 19:00 CET"
  closes_at         nullable
  reject_cooldown_days   default 30
  questions         0..5
  state             Draft | Open | Closed | Filled | Suspended
```

Roleplay fields, all nullable, on every posting:

```
  system            "D&D 5e", "Pathfinder 2e", "Freeform"
  tone              "Heroic", "Grim", "Comedic"
  posting_rate      "A post a day", "A few a week"
  gm_provided       bool
```

They exist on every row and render only when the guild has `Personas` or `Scenes` enabled, read from
`guild_profile`. That is how "generic, with roleplay fields optional" stays one model. A second
posting type for non-roleplay guilds would have duplicated the entire application flow.

`seats_total` is nullable because an open community drive has no seat count and forcing one would
make every such posting lie.

### 6.1 Questions

```
question
  posting_id
  ordinal        0..4
  prompt         <= 200 chars
  kind           ShortText | LongText | SingleChoice
  choices        for SingleChoice, 2..6, each <= 60 chars
  required       bool
```

Five is the cap because the review surface renders answers in full, and a queue you have to scroll
is a queue that gets batch-rejected. This replaces the application bots people run today without
becoming a form builder.

---

## 7. Applications

```
   none --apply--> Pending --accept--> Accepted --redeem--> Joined
                      |                    |
                      |                    +-- invite expiry --> Expired
                      +-- reject --------> Rejected
                      +-- withdraw ------> Withdrawn
```

```
application
  id              appl_ prefixed
  posting_id
  applicant_id
  pitch           <= 2000 chars, free text, always present
  answers         one per question
  state           Pending | Accepted | Rejected | Withdrawn | Expired | Joined
  decided_by      nullable
  decided_at      nullable
  reason          <= 500 chars, optional, shown to the applicant
  invite_code     nullable, set on accept
```

Accept sends `MintApplicationInviteCommand` to Guild over the bus and waits for
`MintApplicationInviteResponse`, following the request/response pattern
`CreateBotGuildMemberCommand` already uses. Membership and roles then stay on the one code path that
already exists, rather than growing a second way to join a guild.

Reject carries an optional reason. Never required: forcing a reason on a rejection produces "no" a
hundred times, which is worse than silence because it looks considered.

### 7.1 The invite must bind to the applicant

`CreateInviteDto.targetUserId` is documented today as advisory and explicitly not enforced at
redemption. An accept-minted invite with `maxUses: 1` can therefore be burned by whoever receives
the code first, not by the person who was accepted.

That is a prerequisite, not a follow-up. `MintApplicationInviteCommand` needs Guild to support a
bound invite whose redemption checks the redeeming user against `targetUserId` and 403s otherwise.
Scope it to invites created with a new `Bound` flag so existing advisory behaviour is untouched.

Invite TTL is 7 days. Expiry moves the application to `Expired`, which is distinct from `Rejected`
and must read as such to both sides.

### 7.2 Limits

- One open application per posting per user.
- Ten open applications per user across the instance.
- Reapplying after a rejection waits out the posting's `reject_cooldown_days`, default 30.
- Withdraw is allowed from `Pending` only, and frees the slot immediately.

The per-posting limit is the one that matters. The instance-wide cap costs nothing to enforce and
keeps review queues meaningful.

---

## 8. Moderation and safety

Public, instance-wide, user-authored content. Report and takedown, automated write-time rules, and
an age floor on the half of this that puts strangers in touch with each other.

### 8.1 Reports

Any signed-in user may report a listing or a posting. Reason is an enum plus optional text, rate
limited per reporter. Staff resolution moves the target to `Suspended` with a reason the owner sees.

### 8.2 Write-time rules

Applied on create and on every update, not only on publish:

- Length caps as specified per field above.
- Invite codes and bare URLs stripped from `listing.pitch` and `posting.body`. Links live only in
  the dedicated `links` field, at most three, against a host allowlist in configuration.
- `application.pitch` and answers are exempt. They are private to the reviewing guild, never
  indexed, and an applicant linking their own writing is the point rather than the abuse.
- A banned-term list from configuration.
- Bump rate limit, section 9.

The link restriction is the load-bearing one. A free-text field that renders links on a
publicly-indexed page is an SEO and phishing surface, and the allowlist is cheaper than the abuse.

### 8.3 An age floor of 16 on recruitment

Recruitment is gated at 16. Community discovery is not.

The distinction is what each one actually does. Browsing communities and joining one is the same act
as redeeming an invite, which has no age floor today and does not acquire one here. Recruitment is
different in kind: it advertises a small group to strangers, collects a written application from a
named individual, and ends with an adult deciding whether to bring that person into a private space.
That is a contact pattern, not a directory, and it gets a floor.

**What is gated:** the Looking-for-players surface, creating a posting, and submitting an
application. Nothing else. A 14-year-old still browses communities, still joins open ones, still
uses every other part of the product.

**Discovery never learns a birth date.** It asks Identity a question and gets a boolean:

```
MeetsMinimumAgeRequest  { UserId, MinimumAge }
MeetsMinimumAgeResponse { Meets }
```

Answering with the date instead would put a minor's birth date in a second service's database for no
gain, and the privacy spec's purge (T1-9) would then have two places to reach.

**The gate fails closed, and the existing helper fails open.** `AgeVerification.IsMinorAt` returns
`false` when no birth date was ever recorded, because a bot account and a purged account both leave
the default `DateOnly` and neither should be treated as a child. That default is right for its
callers and wrong for this one. `MeetsMinimumAgeRequest` must answer `false` on an unknown age, not
reuse `IsMinorAt`. A bot has no birth date and cannot apply to anything, which is correct anyway.

**Self-declared is the bar.** `AgeVerification.Level` distinguishes self-declaration from AI
estimation from government ID. Requiring anything above `SelfDeclaration` would mean an ID check to
join a D&D game. The birth date collected at registration is what this reads.

**16, not the age of majority.** `ApplicationUser.AgeOfMajority` is 18 and governs a different
question. This is its own named constant; a deployment that wants a different floor changes one
value and does not touch majority.

**Caching.** Discovery caches the boolean per user for 6 hours. A user who turns 16 waits at most
that long. Caching the answer rather than the input is what keeps the birth date out of Discovery,
and a longer TTL would be a worse answer to a question that changes exactly once per user.

**In the client** the tab is absent rather than disabled, matching how every gated module entry point
behaves. A direct link to a posting answers a plain sentence saying recruitment is 16 and over,
because a blank 403 on a link a friend sent you reads as a bug.

---

## 9. Ranking

```
score = 0.55 * interest_overlap
      + 0.25 * freshness
      + 0.20 * health
```

**interest_overlap** is matched topics over listing topics. Dividing by the listing's topic count
rather than the match count is what stops a listing tagged with all eight slots outranking a focused
one by breadth alone. Zero when the user has set no interests, in which case freshness and health
carry the feed.

**freshness** decays exponentially from `last_bumped_at` with a 7-day half-life.

**health** is log-scaled active members over the last 14 days from `guild_profile`. This is what
stops a dead guild bumping its way to the top of a feed forever.

**With a text query**, `ts_rank_cd` over `search_vector` multiplies in and dominates. Relevance
first, then the score above as the tiebreak.

No paid placement. Selling feed position makes the ordering a thing users distrust, and it is not
recoverable once shipped.

### 9.1 Bump

Once per 72 hours per listing. The cooldown renders as a countdown on the button rather than failing
silently, because a bump that appears to work and does nothing is worse than a disabled button.

### 9.2 Every card says why

The feed response carries `matchedTopics` per card. The client renders them as chips. A feed that
cannot explain why it surfaced something is a feed people stop opening, and the data is free because
the join already computed it.

---

## 10. Realtime events

All prefixed `discovery.`, all pushed from Discovery's own `IHubContext<EchoRealtimeHub>`.

| Event | Audience |
|---|---|
| `discovery.ListingPublished` | guild members |
| `discovery.ListingUpdated` | guild members |
| `discovery.ListingUnlisted` | guild members |
| `discovery.ListingSuspended` | guild members |
| `discovery.PostingCreated` | guild members |
| `discovery.PostingUpdated` | guild members |
| `discovery.PostingClosed` | guild members |
| `discovery.PostingFilled` | guild members |
| `discovery.ApplicationSubmitted` | holders of `ManageGuild` |
| `discovery.ApplicationWithdrawn` | holders of `ManageGuild` |
| `discovery.ApplicationAccepted` | the applicant's devices |
| `discovery.ApplicationRejected` | the applicant's devices |
| `discovery.ReportFiled` | holders of `ManageGuild` |
| `discovery.InterestsChanged` | the acting user's other devices |

There is no separate pending-count event. The count is derived from the submitted and withdrawn
events by the store that holds the queue, and a second event carrying the same fact is a second
thing that can disagree.

`InterestsChanged` exists so a user editing interests on desktop sees their phone's feed change.
Without it the second device silently ranks against stale interests, which presents as "the feed is
wrong" rather than as a sync bug.

---

## 11. HTTP surface

Behind `/api/v1/discovery/{**catch-all}` on the gateway. Wolverine endpoints throughout, per house
rules, with state-changing operations as Wolverine handlers relying on middleware for the commit.

```
GET    /discover                      the ranked feed. query, topics, kind, language, cursor
GET    /discover/postings             the postings tab, same filter vocabulary
GET    /topics/search                 the shared autocomplete, games and tags in one result

GET    /guilds/{guildId}/listing      owner view, includes Draft
PUT    /guilds/{guildId}/listing      upsert draft
POST   /guilds/{guildId}/listing/publish     entitlement gate
POST   /guilds/{guildId}/listing/unlist
POST   /guilds/{guildId}/listing/bump

POST   /guilds/{guildId}/postings     entitlement gate
PATCH  /postings/{id}
POST   /postings/{id}/close

POST   /postings/{id}/applications    apply
GET    /guilds/{guildId}/applications the review queue
POST   /applications/{id}/accept
POST   /applications/{id}/reject      optional reason
POST   /applications/{id}/withdraw    applicant only
GET    /me/applications               the applicant's own tracker

GET    /me/interests
PUT    /me/interests

POST   /reports
```

Cursor pagination throughout. Offset pagination over a feed whose ordering shifts under you produces
duplicates and gaps, and this feed's ordering shifts on every bump.

---

## 12. Client

Angular client, `WebstormProjects/Alpine`.

**State.** `src/app/stores/discovery.store.ts` built on the two store features in
`stores/foundation/`: `withKeyedIndex` keyed by feed-query hash, `withOptimisticEntities` carrying
apply and withdraw so both settle without a refetch. HTTP stays in
`src/app/services/discovery-api.service.ts`.

**Realtime.** Payload types in `src/app/services/realtime-events.ts`, and exactly one entry on the
`LISTENERS` array in `services/realtime-listeners.ts`. A listener left off that array stays asleep
until something injects it, and every event before that moment is lost.

**Navigation.** Three new `MainView` variants in `features/main-page/navigation.service.ts`:
`discover`, `applications` for a guild's review queue, `my-applications` for the applicant's own
tracker. Not routes: this app's navigation is signal-driven, with only `authentication` and
`overview` in the route table.

**Feature folder.** `src/app/features/discovery/` holding the destination, the listing editor, the
posting editor, the review queue and the interest picker.

**Entitlements.** The publish gate reads through the existing `EntitlementStore`, which already
caches guild-scoped snapshots for exactly the server-stated TTL and invalidates on
`entitlements.Changed`. Nothing new is needed on the client for the gate itself.

**Strings.** `src/assets/i18n/locales`, flat dot-separated keys, shipped in the same commit as the
code.

---

## 13. Interface

### 13.1 Discover is a destination

A permanent entry at the foot of the guild rail, below the guild list and above the add-guild
button. Not a modal.

A modal makes Discovery a thing you open to complete a task you already decided on. A destination
makes it a thing you open because you are bored, which is the traffic a marketplace actually runs
on. The rail placement also means it is reachable from inside any guild without losing your place.

### 13.2 Two tabs over one search box

Communities and Looking for players. The same card grammar, different emphasis.

A community card leads with identity: banner, name, member count, topic chips, the one-line pitch. A
posting card leads with the ask: what is being run, seats left, when it plays, how long applications
stay open. Urgency belongs on the posting and never on the community, because a community with a
countdown on it is a community that looks like it is closing.

### 13.3 The empty query is the product

With no search term the feed is the user's interests, and each card states the topics it matched as
chips. Not implied by position, stated.

If a user has no interests set, the first screen is the interest picker rather than a generic grid.
This is also the only place the interest data gets collected at any volume, so making it the cold
start is not a consolation screen, it is the acquisition path.

### 13.4 Applications review as a queue, not a table

One application at a time, full width: the applicant, their pitch, and their answers rendered as
prose under each question. Accept, Reject, Skip.

Reject opens the reason field inline, empty, never required. A table of ten applications gets skimmed
and batch-rejected; a queue gets read. The whole value of replacing an application bot is that
someone actually reads the answers.

### 13.5 The paywall is a finished preview

A free guild composes the entire listing and sees the real card it would ship, with a single upgrade
bar beneath it. The draft persists, so upgrading turns it on with one action and nothing retyped.

A disabled form communicates that a feature exists. A filled-in preview of your own community
communicates what you are missing, which is the thing that sells.

### 13.6 Applications need a home

`my-applications` is a thin timeline of what you sent, its state, and the reason if you were turned
down. It is also where the Join button lives for an accepted invite.

Without it, applying is posting into a void, and the accepted state has nowhere to land except a
notification the user may have dismissed.

---

## 14. Federation

Designed here, not built. Discovery is instance-only in v1.

The listing shape is already instance-addressable: `guild_id` is prefixed and the `guild_profile`
projection carries an origin. What a federated Discovery needs beyond that:

**Crawl or push.** Push, on the existing Ed25519-signed federation transport. A peer announces
listing changes to instances that have subscribed to its Discovery feed. Crawling means every
instance polls every peer and the cost grows quadratically for content that changes weekly.

**Per-peer trust.** A remote listing must carry its origin visibly on the card and must be
rankable-down as a class. An instance that federates with a peer it later distrusts needs to demote
before it defederates, or the only available action is total.

**Moderating what you do not host.** A local report on a remote listing cannot suspend it at the
source. It suspends the local mirror and forwards the report to the origin. The local action must
succeed even when the origin never answers, and the UI must not imply the remote copy was affected.

**Interest topics across instances.** `game:{gapp_id}` ids are local to Social's catalog and will
not match across instances. Federated topic matching needs a stable cross-instance key, and
`SteamAppId` is the obvious candidate for the three quarters of the catalog that has one. The
remainder needs a name-normalized fallback. This is the piece that most wants deciding before the
schema sets, which is why `game_topic` mirrors `steam_app_id` from the first migration even though
v1 never reads it.

---

## 15. Infrastructure

A new service touches two other repositories. Both are Argo-synced from `main`.

**`WebstormProjects/infrastructure`** (Terraform)

- `variables.tf`, `db_names`: add `discovery`. The comment there is load-bearing. A service whose
  database is missing from this list crash-loops on startup, and the entry must match
  `DATABASE_NAME` in the chart's configmap.
- `modules/argocd/templates/argocd-apps.yaml`: one `Application` block, project `echo`, path
  `discovery`, automated sync with prune and self-heal, copied from the `social` block.

**`WebstormProjects/alpine-infra`** (Helm)

- `discovery/` with `Chart.yaml`, `values.yaml` and `templates/{configmap,deployment,service,hpa}.yaml`,
  copied from `social/`.
- Image `ghcr.io/alpinebits-ch/discovery-application`, health at `/discovery/health`.
- Configmap needs `DATABASE_NAME: "discovery"` and the standard database host, port and user.
- The telemetry consent block in Social's configmap is not needed. Only Guild, Messaging and Social
  host that gate.

**`RiderProjects/Echo`**

- `.github/workflows/docker-build.yml`: one matrix row, `dockerfile: Discovery.Application/Dockerfile`,
  `image: discovery-application`.
- `Echo/Proxy/ProxyConfig.cs`: route `/api/v1/discovery/{**catch-all}`, cluster, health destination,
  and `Services__Discovery` defaulting to `http://discovery.default.svc.cluster.local`.

---

## 16. Shape of the code

Constraints on the implementation, not on the product. They are here because a new service is the
easiest place in a codebase to grow a god object, and because the next person to extend this will be
a human reading it cold.

**No service class owns more than one of: reading the feed, writing a listing, deciding an
application, projecting a mirror.** Four responsibilities, four owners. `DiscoveryFeedQuery`,
`ListingWriteService`, `ApplicationDecisionService`, and the projection handlers. A single
`DiscoveryService` holding all four is the predictable failure and is worth refusing early.

**Ranking is a pure function with its own tests.** Score inputs in, score out, no database and no
clock. The SQL that gathers the inputs is a separate, boring query. Section 9's weights change more
than anything else here, and they must be changeable without touching a query.

**Topic resolution goes through one seam.** Games and tags meet in exactly one place, and every
caller takes a resolved `TopicRef`. Two code paths that both know how a slug normalizes is how the
vocabulary fragments in the database rather than in the picker.

**The client keeps network out of components.** HTTP in `discovery-api.service.ts`, state in the
store, components read signals. The editors are dumb about transport.

**Comments record what the code cannot say.** A constraint, a trap, or why an obvious alternative
was rejected. Not narration, not rationale essays, not restating the next line.

---

## 17. Decisions taken, so they are not relitigated

| Decision | Rejected alternative |
|---|---|
| One subsystem for communities and recruitment | Two features sharing nothing but a paywall |
| A separate Discovery service | Guild-owned, or split Guild and Social |
| Topics reuse the game catalog | A new tag table with its own alias machinery |
| Two entitlement keys, same tier today | One key, unsplittable later |
| Downgrade suspends, never deletes | Deleting a lapsed guild's listing and its applications |
| Accept mints a bound single-use invite | Server-side join with no consent moment |
| Up to five custom questions | Free text only, or a form builder |
| Relevance then decaying freshness, damped by activity | Newest first, or paid placement |
| Any signed-in user may browse and apply | Gating the demand side of a marketplace |
| Instance-only, federation designed | Federating from day one |
| Recruitment has an age floor of 16, discovery has none | One floor over the whole feature, or none |
| Identity answers a boolean, Discovery never stores a birth date | Projecting the birth date like any other profile fact |
| The age gate fails closed on an unknown age | Reusing `IsMinorAt`, which fails open by design |

---

## 18. Order of work

Three plans, each independently useful.

**One.** The service, its infrastructure, the topic model, user interests, listings, the ranked
feed, and the Discover destination. Ships public communities in full.

**Two.** Postings, applications, the bound-invite prerequisite in Guild, the age gate from section
8.3, the review queue and the applicant tracker. Ships recruitment.

**Three.** Reports, staff takedown, and the write-time content rules.

Plan two has two cross-service prerequisites and both belong at its front rather than in the middle
of it: the bound invite in section 7.1, and `MeetsMinimumAgeRequest` in section 8.3. Neither is
discoverable from Discovery's own code, and the age gate in particular must exist before the first
posting endpoint does, not after.
