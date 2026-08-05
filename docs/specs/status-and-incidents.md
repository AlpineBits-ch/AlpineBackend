# Status page and incidents

`status.venta.gg` - a public page that says whether venta is working, and an incident record behind
it that staff write from the moderation console. Plus a detector that opens an incident on its own
when a part of the platform starts failing, so the page is not silent during the ten minutes nobody
has noticed yet.

Owned by **the gateway** (the ASP.NET project in `Echo/`, for historical reasons; everything
user-facing calls it the gateway and the word "Echo" appears nowhere on a public page). Same
placement, and for the same reasons, as the moderation console and the support site - see
[moderation-and-support.md](./moderation-and-support.md) §1.

Client work is in [status-frontend-guide.md](./status-frontend-guide.md).

---

## 1. Why the gateway

A status page is a read-across of every service, so a service that owned it would have to ask all the
others anyway. But the stronger reason is the detector: **the gateway is the only process that
already sees every request and every response.** It proxies them. It also already runs YARP's active
and passive health checks against every backend (`Echo/Proxy/ProxyConfig.cs`), so it holds both
signals a status page needs - "requests are failing" and "the thing is not answering at all" -
without adding Prometheus, OpenTelemetry, or a metrics store to a stack that has none of them today.

The second reason is availability. A status page hosted inside the thing it reports on is a known
joke, but the failure mode we actually have is a single backend service falling over, not the whole
cluster. The gateway is the last component to go: if it is down, nothing resolves and the page could
not have been served from a sibling service either. What it does mean is that **the status page must
not depend on any backend to render** - see §6.

## 2. Hostname

`status.<host>`, resolved by `SiteHost.Resolve("status", "STATUS_DOMAIN")`, exactly like `admin.*`
and `support.*`. `STATUS_DOMAIN` wins; otherwise it is derived from `INSTANCE_URL` by the same
replace-first-label rule (`api.venta.gg` + `status` -> `status.venta.gg`). A request for some other
`status.*` host gets `UseSiteHostDiagnostics`' plain-text 404 naming the bound host and the variable
to set.

### Read-only, and anonymous on purpose

Unlike the support host, nothing on `status.*` accepts a write. The page is a `GET`-only client of
`/api/v1/status/*`, which is anonymous. There is no form, no token in a URL, and no session - so
there is nothing on this host to steal, and it can be cached hard at the edge.

The API stays reachable on this host (same-origin, same as the other two sites), and the status
endpoints are additionally **CORS-open to `*`**: the landing page on `venta.gg` and the web client on
its own origin both need to read the summary, the response carries no credentials and no personal
data, and requiring an allowlist entry per venta property is a footgun that surfaces as a blank
banner rather than an error.

Client routes to rewrite to `index.html`: `["/incident", "/history", "/maintenance"]`. An explicit
list, not a catch-all, for the reason given in `SiteHosting.SupportClientRoutes`.

## 3. The records

Four tables in the gateway's `MicroserviceContext`, snake_case by convention, every enum
`.HasConversion<string>().HasMaxLength(32)`. Entities live in `Echo.Domain/Entities/Status/`.

### `StatusComponent` (`stcp_`)

A named piece of the platform as a user would describe it, not as we deploy it. "Sign-in and
accounts", not "identity-cluster". Seeded once from a built-in catalog on first startup, editable
afterwards from the console.

| Field | Notes |
|---|---|
| `Key` | Stable machine name (`accounts`, `messages`, ...). Clients localise off this; it never changes once shipped. |
| `Name` | Public display name, editable. |
| `Description` | One line under the name on the page. |
| `ImpactHint` | The plain sentence used in auto-generated copy: "Some people may not be able to sign in or create an account." Written once, by us, per component. See §5. |
| `Clusters` | The YARP cluster ids this component watches, as a string array. Empty = not automatically monitored (a component staff drive by hand). |
| `Position` | Display order. |
| `IsVisible` | Hidden components still collect samples; they just do not render. Lets us add a component and watch it for a week before showing it. |
| `Status` | Current `ComponentStatus`, written by the detector, read by everything. |
| `StatusSince` | When it last changed. Drives "degraded for 14 minutes". |
| Thresholds | `DegradedRate`, `OutageRate`, `MinimumVolume`, all nullable, falling back to the instance defaults in `EchoConfigurations`. A component that is noisy by nature gets tuned without moving everyone else. |

The seeded catalog, and the clusters each maps to:

| Key | Name | Clusters |
|---|---|---|
| `accounts` | Sign-in and accounts | `identity-cluster`, `identity-connect-cluster`, `identity-oauth-cluster` |
| `messages` | Direct messages | `messaging-cluster` |
| `servers` | Servers and channels | `guild-cluster` |
| `voice` | Voice and video | `guild-cluster` (+ the realtime signal, §4) |
| `friends` | Friends and presence | `social-cluster` |
| `bots` | Bots and apps | `bots-cluster` |
| `previews` | Link previews | `unfurl-cluster` |
| `imports` | Discord import | `imports-cluster` |
| `federation` | Federation | `federation-cluster`, `federation-document-cluster` |
| `isle` | The Isle integration | `isle-cluster` |
| `realtime` | Realtime connection | none (gateway-local, §4) |
| `api` | API gateway | none (gateway-local, §4) |

This list mirrors `ProxyConfig.GetClusters()` and `DocsCatalog.Services`, which are already required
to mirror each other. **Three places, and now four.** A startup check logs a warning naming any
cluster id in the seed that YARP does not know about, and any cluster YARP knows about that no
component watches - a silent typo here means a service nobody is monitoring.

### `StatusIncident` (`incd_`)

One record for both incidents and scheduled maintenance, because the timeline is identical and two
tables would mean two of every query. `Kind` separates them.

| Field | Notes |
|---|---|
| `Kind` | `Incident` \| `Maintenance` |
| `Reference` | `PublicReference.New()` -> `VNT-XXXXXXXX`. The permalink is `/incident?ref=...`. |
| `Title` | Public. |
| `Impact` | `None` \| `Minor` \| `Major` \| `Critical` |
| `Status` | `IncidentStatus`, §4 |
| `Origin` | `Manual` \| `Automatic` |
| `Template` | Null for manual incidents; a machine name (`elevated_errors`, `unavailable`, `recovered`) for generated ones, so clients can localise. §5. |
| `AutoComponentId` | Set only on generated incidents. Carries a **partial unique index** `WHERE resolved_at IS NULL AND origin = 'Automatic'` - the dedupe across gateway replicas, §4. |
| `Components` | Many-to-many join `status_incident_components`, each row carrying the `ComponentStatus` this incident asserts for that component. |
| `StartedAt` / `ResolvedAt` | |
| `ScheduledFor` / `ScheduledUntil` | Maintenance only. |
| `Confirmed` | A human has touched this generated incident. Once true, the detector never resolves or retracts it again - it belongs to a person now. |
| `DetectionDetail` | Staff-only. The numbers: window, sample count, error rate, which destinations were unhealthy. Never serialised on a public endpoint. |
| `CreatedByUserId` | Null for generated. |

### `StatusIncidentUpdate` (`incu_`)

The timeline. Append-only; there is no edit and no delete, because a status page that quietly
rewrites what it said at 14:05 is worth less than no status page.

`IncidentId`, `Status` (the lifecycle state as of this update - the page renders the state beside
each entry, which is the whole point of the format), `Body` (max 4000), `Template` (nullable, as
above), `AuthorUserId` (null for generated), `PostedAt`.

Correcting an update means posting another one. Staff see a note saying so in the console.

### `StatusDayRollup`

`ComponentId` + `Day` (UTC date) unique. `OperationalSeconds`, `DegradedSeconds`, `OutageSeconds`,
`MaintenanceSeconds`, `IncidentCount`. Written by the probe on each tick by adding the elapsed
interval to whichever bucket the component was in. Ninety days retained, pruned by the same loop.

This is the 90-bar uptime strip, and it is the only history there is: **we are not storing per-request
metrics.** The rollup is a state integral, not a sample of a metrics store we do not have.

## 4. States

### Incident lifecycle

`Investigating` -> `Identified` -> `Monitoring` -> `Resolved`

The four everyone else uses, and the four the user asked for. Meaning, as staff should read it:

- **Investigating** - something is wrong, we do not yet know what. Every generated incident opens here.
- **Identified** - we know the cause. Not "we have fixed it".
- **Monitoring** - a fix is deployed and the signal looks right, but not for long enough.
- **Resolved** - over. Sets `ResolvedAt`.

Moving backwards is allowed (Monitoring -> Investigating is a normal thing to have to do) and simply
posts an update with the earlier state. Resolved -> anything is a reopen and requires an admin, so
that "resolved" keeps meaning something.

`Postmortem` deliberately does not exist as a fifth state. A postmortem is a link, and it goes in a
final update.

### Maintenance lifecycle

`Scheduled` -> `InProgress` -> `Completed`, plus `Cancelled` from either of the first two. A
`Scheduled` maintenance whose window has started but which nobody has moved to `InProgress` shows on
the page as scheduled and flags in the console after 15 minutes.

### Component status

`Operational` \| `DegradedPerformance` \| `PartialOutage` \| `MajorOutage` \| `UnderMaintenance`

### Overall indicator (derived, never stored)

`operational` \| `degraded` \| `partial_outage` \| `major_outage` \| `maintenance` - the worst
component status, except that `UnderMaintenance` only wins when nothing else is worse than
`Operational`. Nobody wants "under maintenance" on the banner while sign-in is down.

## 5. The detector

A `StatusProbe` `BackgroundService` in the gateway, evaluating every 20 seconds over a rolling 60
second window. Its inputs, in order of authority:

1. **Proxied responses.** Middleware registered ahead of the proxy awaits `next()` and then reads
   `HttpContext.GetReverseProxyFeature()?.Route.Config.ClusterId` together with the response status
   and elapsed time, into a lock-free ring of three 20-second buckets per cluster. This also catches
   the 502/503 YARP itself produces when a cluster has no healthy destination, which still carries
   the cluster id.
2. **Destination health**, from YARP's `IProxyStateLookup`. This is what covers an outage with no
   traffic: at 04:00 a dead service produces no failing requests because nobody is asking.
3. **Gateway-local signals** for the two components with no cluster: `api` counts the gateway's own
   unhandled 5xx, and `realtime` counts SignalR connection failures and hub method exceptions.

### What counts as an error

5xx only. Every 4xx is excluded - a wrong password, a missing message and a rate-limited client are
all the system working. Requests aborted by the client (`HttpContext.RequestAborted`) are excluded
too: a user closing a tab mid-upload is not an outage, and on mobile networks there are a lot of them.

### Thresholds

Defaults, seeded into `EchoConfigurations` and overridable per component:

| | Default | |
|---|---|---|
| Window | 60s | evaluated every 20s |
| Minimum volume | 20 requests | below this, only destination health can open an incident |
| Degraded | error rate >= 5% | for **2 consecutive** evaluations |
| Outage | error rate >= 25%, or every destination unhealthy | for **2 consecutive** evaluations |
| Recovery | error rate < 2% | for **5 consecutive** evaluations (~100s) |

Two-in-a-row to open and five-in-a-row to close, with a lower bar to close than to open: a deploy
rolling one pod produces a single ugly window, and a status page that flaps is worse than one that is
thirty seconds late.

### Opening, and not opening twice

The gateway runs more than one replica, and each sees only its own traffic. Rather than build a
shared window, each replica evaluates independently and the **partial unique index on
`(auto_component_id) WHERE resolved_at IS NULL AND origin = 'Automatic'`** decides who wins: the
second insert conflicts and is swallowed. Consequence, stated so it is a choice and not a surprise:
if one replica is unhealthy toward a backend and the others are fine, an incident still opens. That
is the right bias for a status page.

A component cannot open a second generated incident within 30 minutes of its last one resolving; the
detector reopens the previous incident and appends an update instead. Otherwise a service that dies
every four minutes writes a wall of separate incidents.

### Closing, and retracting

- The signal recovers and **no human has touched it** (`Confirmed == false`): the detector posts a
  `recovered` update, sets `Resolved`, and if the whole thing lasted under 120 seconds it **retracts**
  it - `IsRetracted`, hidden from every public read, still in the table and still in the console.
  Under two minutes with no user reports is a blip, and publishing blips trains people to ignore the
  page.
- The signal recovers and a human **has** touched it: the detector posts nothing and resolves
  nothing. It adds an internal note to `DetectionDetail` and leaves it. A person owns it.

Any staff write to a generated incident - an update, an edit, a resolve, or the explicit
`POST /confirm` - sets `Confirmed`.

### The copy is not technical

**This is a hard rule.** A generated incident says what a user would notice, not what a monitor
measured. No percentages, no status codes, no window sizes, no service names as we deploy them.

| Template | Title | Body |
|---|---|---|
| `elevated_errors` | `Elevated error rates affecting {Component.Name}` | `{Component.ImpactHint} We are investigating.` |
| `unavailable` | `{Component.Name} is unavailable` | `{Component.ImpactHint} We are investigating.` |
| `recovered` | (no title change) | `{Component.Name} is operating normally again. We are continuing to watch it.` |

So `accounts` produces "Elevated error rates affecting Sign-in and accounts" over "Some people may
not be able to sign in or create an account. We are investigating." That is the register.

The numbers do exist, and staff need them - they go in `DetectionDetail`, rendered in the console
next to the incident and in the live signals view (§7), and they never appear on a public endpoint or
in any client payload.

## 6. Public API

Anonymous, `GET` only, CORS `*`, under `/api/v1/status`. Errors keep the gateway's `{ code, message }`
shape. A dedicated rate-limit policy partitioned by IP (120/min) rather than the shared user policy:
every client polls this, and it must not spend an anonymous caller's API budget.

```
GET  /api/v1/status/summary
GET  /api/v1/status/incidents?limit=&offset=&kind=
GET  /api/v1/status/incidents/{reference}
GET  /api/v1/status/feed.atom
```

`summary` is the one call the page and every client needs: indicator, components with current status
and 90-day uptime, active incidents with their updates, active and upcoming maintenance, and the last
seven resolved incidents. Shape is in the [frontend guide](./status-frontend-guide.md).

**It is served from an in-memory snapshot**, rebuilt by the probe every 20 seconds and on every staff
write. So the page renders with zero database queries on the request path and keeps rendering if
Postgres is unavailable - which is precisely one of the outages it exists to report. The snapshot
carries `Cache-Control: public, max-age=15`. `incidents` and the feed do hit the database; they are
history, and history not loading during an outage is acceptable in a way that the summary is not.

## 7. Admin API and console

`/api/v1/admin/status/*`, on `AdminControllerBase`, so: `[Authorize]`, `ResolveStaffAsync()` per
request, `{code:"staff_required"}` / `{code:"admin_required"}` refusals, and an audit row on every
mutation.

```
GET    components                      staff
PATCH  components/{id}                 admin      name, description, hint, order, visibility, thresholds
POST   components                      admin
GET    signals                         staff      live per-component rates, volumes, destination health
GET    incidents?state=&kind=          staff
POST   incidents                       admin      create (incident or maintenance)
GET    incidents/{id}                  staff
PATCH  incidents/{id}                  admin      title, impact, components, schedule
POST   incidents/{id}/updates          admin      the timeline write; carries the new status
POST   incidents/{id}/confirm          admin      take ownership of a generated incident
POST   incidents/{id}/resolve          admin
POST   incidents/{id}/retract          admin      hide from public reads; never deletes
```

**Admin, not moderator, for every write.** Publishing to `status.venta.gg` is a public statement in
venta's name, which is a different act from actioning a report; moderators read the views and cannot
post. It is a one-line change if that turns out to be wrong operationally.

In the console (`Echo/wwwroot/admin/`) this is one new rail item, `data-view="status"`, with the
`admin-only` class the Federation and Audit items already use. Three panes: **Signals** (the live
table, the technical view, with a "declare an incident" button that prefills from the component),
**Incidents** (open at the top, generated-and-unconfirmed flagged), and **Components**. The incident
editor is a title, an impact, a component multi-select, and a body box with the lifecycle state as a
segmented control - posting is one action, not two.

## 8. The page

`Echo/wwwroot/status/{index.html, app.js, status.css}`. Raw HTML, vanilla JS, no build step, no
`innerHTML`, `/assets/venta.css` for tokens and `/assets/icons.js` for icons - identical to the
support site and for the reasons written down in `moderation-and-support.md` §8.

Layout, top to bottom:

1. **The verdict.** One sentence, large, coloured by indicator: "All systems operational." Nothing
   above it. A visitor who reads one line must get the answer.
2. **Active incidents**, each with its timeline newest-first, every entry stamped with its state.
3. **Components**, name and status, grouped by nothing - a flat list of twelve reads faster than
   three groups of four.
4. **90-day uptime**, one bar per component per day, `title` on each bar with the date and the
   percentage. Hover only; no tooltip library.
5. **Scheduled maintenance**, if any is upcoming.
6. **Recent history**, last seven resolved, linking to the permalink page.

It polls `summary` every 30 seconds while the tab is visible (`visibilitychange`, and stop polling
when hidden - a status page left open in a background tab for a week should not be a load source).

Icons needed beyond the existing set: `activity`, `check-circle`, `alert-triangle`, `wrench`.

## 9. Realtime

Signed-in clients get `status.IncidentUpdated` and `status.SummaryChanged` on the existing hub
(`Echo.Realtime/EchoRealtimeHub.cs`), pushed by the gateway with `IHubContext<EchoRealtimeHub>` -
which will be the gateway's first use of the hub as a sender, so it is a new dependency in
`Program.cs` and a new entry to regenerate into `asyncapi.json` via `Docs.Generator`.

The hub is `[Authorize]`. **Anonymous visitors on `status.*` cannot receive it and must poll**, which
is why the page polls and why the summary snapshot is cheap. Do not add an anonymous hub for this.

## 10. Deployment

`STATUS_DOMAIN`, added everywhere `ADMIN_DOMAIN` and `SUPPORT_DOMAIN` already appear:

- `deploy/compose.yaml` (env passthrough on the gateway service) and the root `compose.yaml`
- `deploy/install.sh` - `--status-domain` arg, prompt, default, `.env` write, Caddyfile block
- `deploy/Install-VentaStack.ps1` - the same four
- The Caddy block is a copy of the support one: `reverse_proxy echo:8080` with `X-Forwarded-Proto`
  and `X-Echo-Proxy-Auth`, Host preserved.

DNS and the deployment side are being handled separately.

## 11. Tests

- `Echo.Tests/Sites/SiteAssetPathTests.cs` and `SiteHostTests.cs` - add the status site to the
  `[TestCase]` lists, so its assets, icons and script are checked like the other two.
- Detector unit tests over a fake clock and a synthetic bucket ring: opens after two bad windows and
  not one; does not open below minimum volume; opens on all-destinations-unhealthy with zero traffic;
  closes only after five clean windows; retracts under 120s; does **not** retract or resolve once
  `Confirmed`; the replica-dedupe insert conflicts rather than throwing.
- Controller tests pinning the public payload shapes, in the style of `test(moderation): pin the
  request payloads the pages actually send`, plus a negative test asserting `DetectionDetail` and
  staff fields appear in no public response.
- An architecture-style test asserting every cluster in `ProxyConfig.GetClusters()` is watched by
  exactly one seeded component.

## 12. Out of scope

Named so their absence is a decision rather than an oversight.

- **Email or webhook subscriptions.** The mailer exists (`Echo/Moderation/ModerationMailer.cs`), so
  this is a small follow-up, but subscription lists are a personal-data surface with their own
  consent and unsubscribe requirements and they are not v1.
- **Latency and response-time graphs.** We have no metrics store and the rollup is a state integral,
  not a histogram. "Degraded performance" here means errors, not slowness.
- **Per-region status.** One deployment.
- **Postmortems as records.** A link in a final update.
- **Third-party dependency rows** (Cloudflare SFU, the mail provider, the push services). Worth doing
  and easy to add later as components with no clusters and manual status; not seeded now, because a
  row we never update is worse than no row.
