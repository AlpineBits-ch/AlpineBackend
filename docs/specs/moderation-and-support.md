# Moderation console and support site

Two new public surfaces served by the gateway on their own hostnames, and the moderation record
behind them.

* `admin.<instance>` - the staff console: the report queue, user lookup, bans, appeals, the support
  inbox, and instance-level numbers.
* `support.<instance>` - the public surface: open a support ticket, follow it up, and appeal a ban.

Both are static pages served out of `Echo/wwwroot`, gated on the `Host` header exactly like
`docs.<instance>` already is. Nothing new is deployed: no container, no service, no database. The
moderation record lives in the gateway's own Postgres schema, and the one thing the gateway cannot
own - whether an account may sign in - stays in Identity and is set over the bus.

---

## 1. Why the gateway and not a service

Every other feature in this repo that owns state got a service. This one deliberately does not, and
the reason is worth writing down because the shape looks inconsistent.

A moderation console is a *read-across*: it needs the user row from Identity, the message body from
Messaging, the guild from Guild, and the report itself. A dedicated service would own the reports
and then fan out to all three for everything else, which is the same set of bus calls the gateway
would make, plus a hop. The gateway is also already the only component that terminates every public
hostname, so it is the only place the host-gating can happen at all.

What that costs: the gateway's `MicroserviceContext` grows from one table to six, and gateway
deploys now carry migrations that matter. That is accepted. What it must not cost is the gateway
becoming an authority on things it does not own - see §4.

---

## 2. Hostnames

`DocsEndpoints` already solved this once, and got it wrong once in a way worth not repeating: it
prefixed `docs.` onto the instance host, so an instance on `api.venta.gg` bound `docs.api.venta.gg`
and every request to the real `docs.venta.gg` fell through to an empty 404.

That derivation - replace the first label when the instance is already on a subdomain, prepend when
it is on a bare domain - moves to `SiteHost` and is shared by all three sites. `DocsEndpoints`
keeps its public `DeriveFrom`/`Normalise` methods as forwarders so its existing tests and behaviour
are untouched.

| Instance URL | admin | support | docs |
|---|---|---|---|
| `https://api.venta.gg` | `admin.venta.gg` | `support.venta.gg` | `docs.venta.gg` |
| `https://chat.example.com` | `admin.example.com` | `support.example.com` | `docs.example.com` |
| `https://example.com` | `admin.example.com` | `support.example.com` | `docs.example.com` |
| `http://localhost:8080` | `admin.localhost` | `support.localhost` | `docs.localhost` |

Overridable per site with `ADMIN_DOMAIN` and `SUPPORT_DOMAIN`, normalised the same way `DOCS_DOMAIN`
is (a scheme, port or trailing path is stripped rather than rejected - writing
`ADMIN_DOMAIN=https://admin.venta.gg` should not produce a silent 404).

A request arriving for some *other* `admin.*` or `support.*` hostname gets the same diagnostic 404
the docs site now gives: a plain-text body naming the host that is actually bound and the variable
to set. That message is the entire reason the docs bug was ever found.

**Self-hosting.** Nothing here is `venta.gg`-specific: the hosts derive from `INSTANCE_URL`, so an
instance at `https://chat.example.com` gets `admin.example.com` and `support.example.com` with no
configuration. The installers prompt for both, default to the derived value, and write a Caddy block
per host.

### Same-origin, on purpose

The API is reachable on the admin host (YARP matches on path, not host), so the console calls
`/api/v1/...` and `/connect/token` same-origin and needs no CORS entry. This is already true of the
docs host. The *pages* exist only on their own host; the API exists on all of them.

---

## 3. The record

Six tables, all in the gateway's `MicroserviceContext`. Ids are the usual prefixed ULIDs.

### `ModerationReport` (`rprt_`)

What a user filed. Immutable except for its triage fields.

```
ReporterUserId    string?        null for reports opened by staff from a support ticket
TargetUserId      string         the account being reported - always set, even for a message report
SubjectKind       enum           User | Message | Channel | Guild
SubjectId         string?        message/channel/guild id when SubjectKind is not User
Reason            enum           Spam | Harassment | HateSpeech | ViolentThreats | SelfHarm
                                 | SexualContent | ChildSafety | Impersonation | Malware
                                 | IllegalContent | Other
Details           string         reporter's own words, 4000 max
EvidenceJson      string?        client-supplied snapshot, 16 KB max - see below
Status            enum           Open | Triaged | ActionTaken | Dismissed | Duplicate
Priority          enum           derived from Reason at creation; Critical for ChildSafety,
                                 SelfHarm and ViolentThreats
AssignedToUserId  string?
ResolvedByUserId  string?
ResolvedAt        DateTimeOffset?
Resolution        string?        staff note; required to leave Open
DuplicateOfId     string?
```

**Evidence has to come from the reporter's client, and that is a real constraint, not an
implementation detail.** Direct messages are end-to-end encrypted (see `docs/specs/mls-*`); the
server holds ciphertext and cannot produce the plaintext of a reported DM at review time. So a
report of an encrypted message carries a snapshot the reporting client captured from its own
decrypted view, and the console labels it as such. A moderator is looking at *what the reporter says
they saw*, attested by nothing. For unencrypted surfaces (guild channels) the console re-reads the
message live and shows both.

There is no way around this that does not break the encryption guarantee, and pretending the
snapshot is authoritative would be worse than saying so in the UI.

### `ModerationAction` (`mact_`)

What staff did. Append-only; a reversal is a new row, and a revocation stamps the original.

```
TargetUserId      string
ActorUserId       string         the staff member
Kind              enum           Note | Warning | Suspension | Ban | Unban
Reason            enum           same ReportReason set - what the user is told
PublicNote        string         shown to the user in the appeal flow and any notification
InternalNote      string?        never leaves the console
ExpiresAt         DateTimeOffset?  null on a Ban = permanent; always set on a Suspension
RevokedAt         DateTimeOffset?
RevokedByUserId   string?
RevocationReason  string?
ReportId          string?        the report that prompted it, when there was one
Reference         string         short human-readable code the user quotes when appealing
```

`Reference` is a 10-character Crockford-base32 code (`VNT-XXXXXXXX`) minted per action. It exists
because the appeal form is anonymous - a banned user cannot sign in to prove who they are - so they
need something to type that identifies the action without being guessable from their user id.

### `ModerationAppeal` (`apel_`)

```
ActionId          string         FK to the action being appealed
ContactEmail      string         normalised; how the decision reaches them
SubmittedByUserId string?        set when the appeal came from a signed-in session
Body              string         2000 max
Status            enum           Pending | UnderReview | Granted | Denied
DecidedByUserId   string?
DecidedAt         DateTimeOffset?
DecisionNote      string?        required on both Granted and Denied
```

**One appeal per action, and a denial is final.** A second submission is refused with a 409 naming
the existing appeal's status; when that status is `Denied` the response carries `final: true` and
says so in plain words, and the decision email says it again. Without that, an appeal form is a
mailbox flood with a "submit" button - and, worse, someone waits weeks for a second review that was
never going to happen.

Final means final *to the user*. A moderator can still revoke the action afterwards of their own
accord, and there is no state that prevents it - an instance that could not correct itself after new
information would be worse than one that occasionally has to. What the user is told is the honest
version of that: the appeal route is closed, a moderator may still lift it at their discretion, and
that is not something they can request again. The lifecycle is therefore:

```
suspension/ban issued  ──►  appeal (once)  ──►  Granted ──► moderator issues Unban ──► restored
                                            └─► Denied  ──► final; no further appeal
                                                             └─(staff discretion only)─► Unban
```

**Granting an appeal does not automatically unban.** It records the decision and surfaces the action
in the console as appeal-granted; a moderator then issues the `Unban`. Two steps because "the appeal
was reasonable" and "this account is now allowed back" are genuinely different judgements, and
because an automatic unban makes the appeal queue a privilege-escalation target.

### `SupportTicket` (`supt_`) and `SupportTicketMessage` (`supm_`)

```
SupportTicket
  ContactEmail      string
  RequesterUserId   string?      set when opened from a signed-in session
  Subject           string       200 max
  Category          enum         Account | Billing | Technical | Safety | Privacy | Other
  Status            enum         Open | AwaitingRequester | AwaitingStaff | Resolved | Closed
  AssignedToUserId  string?
  Reference         string       VNT-XXXXXXXX, quoted by the requester
  AccessTokenHash   byte[]       SHA-256 of the token handed back at creation
  LastActivityAt    DateTimeOffset

SupportTicketMessage
  TicketId          string
  AuthorKind        enum         Requester | Staff | System
  AuthorUserId      string?
  Body              string       8000 max
  IsInternal        bool         staff-only note; never serialised on the public read
```

A ticket is readable without an account: `GET /api/v1/support/tickets/{reference}?token=...`. The
token is 32 random bytes, base64url, returned exactly once at creation and stored only as a SHA-256
hash - the same shape as an API key. Reference alone is not enough; a reference is short enough to
guess and appears in email subject lines.

`IsInternal` messages are filtered in the query, not in the serialiser. A staff note that leaks
because someone added a field to a DTO is the obvious failure mode here.

### `ModerationAuditEntry` (`maud_`)

Every staff mutation: actor, action, subject id, a one-line detail, and the caller's address. Mirrors
Identity's `IdentityAuditEvent`, which is the existing precedent for "who did this, and when". Read
back at `/api/v1/admin/audit`.

---

## 4. What the gateway does *not* own

The gateway records that a ban happened. Whether the account can sign in is `ApplicationUser.Status`
in Identity, and `IsSigninAllowed()` is the only thing that gates it. So a ban is two writes and the
order matters:

1. `SetUserModerationStatusRequest` over the bus → Identity flips `UserStatus.Banned`, writes its own
   `IdentityAuditEvent`, and answers.
2. Only on success does the gateway commit the `ModerationAction` row.

Done the other way round, a bus failure leaves a console showing a ban that never took effect. Done
this way, the failure leaves an account banned with no console record - which is visible to the
banned user, reported by them, and recoverable. Neither is nice; this one is the one that gets found.

Identity refuses to ban an account whose `UserType` is `Admin`, and refuses to act on itself. The
gateway checks the same things first for a decent error message, but the refusal that counts is
Identity's, because it is the one holding the row.

Unban restores `UserStatus.Active` - and only from `Banned`. An account in `PendingDeletion` or
`PurgeInProgress` is not made active by an unban; the request is refused with the current status.

---

## 5. Staff authorisation

`UserType` already has `Moderator` and `Admin`, and `UserAdministrativeHandler` already answers
`IsUserAdministrative` - but only `true` for `Admin`. Federation's `InstanceAdminPolicy` depends on
that exact meaning, so it does not change. The response gains a `Role` field alongside it
(`None | Moderator | Admin`); existing callers read `IsAdministrative` and see no difference.

Two tiers, checked against the database on every request rather than from a token claim, because a
token outlives a demotion:

| | Moderator | Admin |
|---|---|---|
| Report queue, triage, dismiss | ✅ | ✅ |
| Warn, suspend | ✅ | ✅ |
| Ban, unban | ✅ | ✅ |
| Appeals: review and decide | ✅ | ✅ |
| Support tickets | ✅ | ✅ |
| Instance numbers, user search | ✅ | ✅ |
| Audit log | - | ✅ |
| Act on another staff account | - | ✅ |
| Promote and demote staff | - | ✅ |
| Federation admin | - | ✅ |

The split is deliberately shallow. A deeper permission model here would be guessing at an org chart
this instance does not have; the meaningful line is "can see what other staff did" and "can act on
staff", and both belong to Admin.

### Changing someone's role

`POST /api/v1/admin/users/{id}/role` with `{ "role": "Default" | "Moderator" | "Admin" }`. Admin only
- a moderator who could promote themselves would be an administrator with extra steps, so this is
the one endpoint where the Moderator tier buys nothing.

Identity holds the refusals, because Identity holds the row:

* **`self_action`** - you cannot change your own role. It is never intentional, and it is exactly how
  an instance's only administrator locks themselves out.
* **`last_administrator`** - the final *active* administrator cannot be demoted. Counted live, over
  `Active` accounts only: a banned admin is not somebody who can undo this. Without the guard, one
  click leaves an instance nobody can administer, recoverable only by editing the database by hand.
* **`bot_account`** / **`invalid_role`** - `Bot` is neither assignable nor promotable. Bot-ness is a
  property of how the account was created (`ApplicationUser.CreateBot` skips email, age verification
  and the welcome path), not a tier.
* **`account_inactive`** - a banned or half-deleted account cannot be *promoted*. Demotion to
  `Default` stays available, because stripping staff from someone who has just been banned must not
  be blocked by the fact that they were banned.

Audited twice on purpose: an `IdentityAuditEvent` against the account whose tier changed, so it lands
on their own security timeline, and a `ModerationAuditEntry` against the acting administrator. Both
log at Warning. This is the write that decides who can act on everyone else.

Sign-in to the console is the ordinary password grant against `/connect/token` with `client_id=echo`,
same as the desktop client, including the `mfa_required` / `mfa_invalid` responses. There is no
separate staff credential. The console then checks `GET /api/v1/admin/session` and shows the sign-in
form again on 403, so a non-staff account that logs in correctly gets told it is not staff rather
than silently seeing an empty queue.

---

## 6. API

All admin routes are `[Authorize]` plus a database role check. All support routes are anonymous and
rate-limited on the gateway's existing per-caller policy.

### Staff - `/api/v1/admin`

```
GET    /session                      who am I, and what tier
GET    /stats                        instance numbers (§7)
GET    /users?q=&status=&type=&limit=&offset=
GET    /users/{id}                   detail + action history + reports for and against
GET    /reports?status=&reason=&priority=&assignee=&limit=&offset=
GET    /reports/{id}
PATCH  /reports/{id}                 assign / status / resolution / duplicateOf
POST   /users/{id}/actions           issue Note | Warning | Suspension | Ban | Unban
GET    /users/{id}/actions
POST   /users/{id}/role              promote / demote staff        (Admin only)
POST   /actions/{id}/revoke          revoke a live suspension or ban
GET    /appeals?status=&limit=&offset=
GET    /appeals/{id}
POST   /appeals/{id}/decide          { granted, note }
GET    /tickets?status=&category=&assignee=&limit=&offset=
GET    /tickets/{id}                 includes internal notes
POST   /tickets/{id}/messages        { body, internal }
PATCH  /tickets/{id}                 status / assignee
GET    /audit?actor=&subject=&limit=&offset=      (Admin only)
```

`/api/v1/admin/federation/**` is already routed to the Federation service by YARP. These are gateway
controllers, mapped before `MapReverseProxy`, and none of them collide with that prefix.

### Public - `/api/v1/support`

```
POST   /tickets                      { email, subject, category, body } -> { reference, token }
GET    /tickets/{reference}?token=
POST   /tickets/{reference}/messages?token=       { body }
POST   /appeals                      { reference, email, body } -> { appealReference }
GET    /appeals/{reference}?email=   status only, no decision detail until decided
```

`POST /appeals` answers the same 202-shaped body whether or not `reference` names a real action -
the response must not confirm that a given action code exists. A bad code is logged and dropped, the
same way the password-reset route treats an unknown address.

### Signed-in users - `/api/v1/reports`

```
POST   /                             { targetUserId, subjectKind, subjectId, reason, details, evidence }
GET    /mine                         reports this account filed, and their status
```

Rate-limited to the authenticated bucket, plus a per-target duplicate guard: the same reporter
reporting the same subject twice within 24 hours updates the existing open report rather than
creating a second one.

---

## 7. The numbers

`GET /api/v1/admin/stats` is answered by Identity over the bus, because Identity owns the user rows
and a gateway that queries another service's database directly is the thing this architecture
exists to avoid.

```
totalUsers            all rows, including tombstones
activeUsers           Status = Active
bannedUsers           Status = Banned
pendingDeletion       Status = PendingDeletion or PurgeInProgress
deletedUsers          Status = Deleted
botAccounts           UserType = Bot
staffAccounts         UserType = Moderator or Admin
unverifiedEmail       EmailVerifiedAt is null, excluding bots and tombstones
newUsers24h/7d/30d    CreatedAt within the window
```

Plus, counted locally in the gateway: open reports, reports by priority, unresolved reports older
than 48 hours, open appeals, open tickets, and tickets awaiting staff.

The one number deliberately absent is anything like "messages sent" or "active users today". Neither
is derivable without either a Scylla scan or a presence sweep, both of which are expensive enough
that a dashboard auto-refreshing them is a self-inflicted outage. If they are wanted later they need
a counter, not a query.

---

## 8. The pages

Hand-written HTML, CSS and JavaScript, no build step and no framework - the same choice the docs page
made, for the same reason: these are served straight out of `wwwroot` by a .NET container that has no
Node in it, and a build step would mean the pages could silently be stale relative to the source.

Colours come from `Alpine`'s theme (`src/app/models/theme.model.ts`) so the console looks like the
product, with a light theme as well since a moderation queue is often read in daylight.

### Icons

Icon files, not inlined markup. Each icon is its own `.svg` under `wwwroot/assets/icons/`, copied
from `Alpine/node_modules/primeicons/raw-svg` (MIT) with the venta mark from
`Alpine/src/assets/branding/logo-mark.svg`. A small `icons.js` finds every `[data-icon]` element,
fetches the file once, caches the parsed SVG, and injects a clone. Icons inherit `currentColor`, so
the same file works in both themes and on any surface.

`/icons/*` is served on both the admin and support hosts from one shared folder - the pages are
separate, the icon set is not.

---

## 9. Deployment

| Variable | Default | Effect |
|---|---|---|
| `ADMIN_DOMAIN` | derived from `INSTANCE_URL` | hostname the console is served on |
| `SUPPORT_DOMAIN` | derived from `INSTANCE_URL` | hostname the support site is served on |

Both installers prompt for these next to the existing docs prompt, default to the derived value, and
emit a Caddy block per host pointing at the same `echo:8080` container with the Host header
preserved - a copy of what `$DOCS_DOMAIN` already does, and it must stay a copy: the gateway decides
what to serve from the Host header, so a proxy that rewrites it serves nothing.

The gateway runs `Database.Migrate()` on startup (`EchoPersistence.UseInfrastructure`), so the
migration adding these tables applies itself on the next deploy. On a multi-replica gateway that is a
concurrent-migration race - two replicas starting together both try to take the migration lock and
one can fail its startup probe. Roll the gateway to a single replica for the deploy that carries this
migration, then scale back up.

---

## 10. Out of scope

Named here so their absence is a decision rather than an oversight.

* **Automated detection.** No spam classifier, no hash matching, no CSAM scanning. Reports are
  human-filed and human-reviewed. Hash matching against a known-material list is the obvious next
  step and needs a legal decision before a technical one.
* **Guild-level moderation.** Guilds already have their own bans, kicks, mutes and audit log
  (`project_guild_moderation_completion`). This is instance-level and does not reach into them.
* **Federated reports.** A report against a user on a remote instance is recorded locally and is not
  forwarded. Federation has no moderation transport, and inventing one unilaterally would be a
  protocol change.
* **Bulk actions.** No "ban all accounts matching this". A console that can ban a hundred accounts in
  one click is a console that will, once, by accident.
* **Email notification of moderation outcomes.** The record and the appeal flow are here; wiring
  `EmailService` to send "your account was actioned" is a follow-up, and needs the template set that
  `Identity.Application/Templates` owns.
