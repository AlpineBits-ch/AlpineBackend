# Platform status - frontend integration guide

Everything a venta client needs to show "we know, it is us, here is what is happening" instead of a
silent spinner. Written to be worked from independently: no backend reading required, every field and
event spelled out.

Applies to Alpine (desktop and web), venta-mobile, and the landing page. Design document:
[status-and-incidents.md](./status-and-incidents.md).

## URLs in this document

**Every URL is a public gateway URL (`https://api.venta.gg`) and is written out in full.** The public
status page lives on `https://status.venta.gg`, which serves the same data through the same
endpoints - link users there, but read the API from `api.venta.gg` like every other call.

The status endpoints are **anonymous** and **CORS-open**. Do not attach a bearer token; do not wait
for sign-in before calling them. The whole point is that they answer when nothing else does.

---

## 1. The one thing that will surprise you

**You never write the copy.** Not the title, not the body, not the banner text.

The server decides how technical a status message is allowed to be, and the rule is that it is not
technical at all - "Some people may not be able to sign in or create an account", never "identity
5xx rate 31%". If the client composes its own sentence from the component name and a status enum,
that rule lives in three codebases and dies in one of them.

So: render `banner.title` and `banner.body` verbatim. The only thing you may substitute is a
translation, and only when `template` is non-null (§6).

## 2. The one call

```
GET https://api.venta.gg/api/v1/status/summary
```

No auth. Cached 15 seconds server-side and marked as such. This single response drives the banner,
the settings page, and the "is it me or is it them" answer.

```jsonc
{
  "indicator": "degraded",          // operational | degraded | partial_outage | major_outage | maintenance
  "updatedAt": "2026-08-05T12:04:20Z",

  // Present only when indicator != "operational". Null otherwise. This is what you render.
  "banner": {
    "title": "Elevated error rates affecting Sign-in and accounts",
    "body": "Some people may not be able to sign in or create an account. We are investigating.",
    "severity": "warning",          // info | warning | critical
    "incidentReference": "VNT-4KQ7M2XB",
    "url": "https://status.venta.gg/incident?ref=VNT-4KQ7M2XB",
    "template": "elevated_errors",  // null for staff-written incidents - see §6
    "componentKey": "accounts"      // null when more than one component is affected
  },

  "components": [
    {
      "key": "accounts",            // stable, safe to switch on
      "name": "Sign-in and accounts",
      "description": "Signing in, registration, sessions",
      "status": "degraded_performance",
      "statusSince": "2026-08-05T11:58:00Z",
      "uptime90d": 0.9987
    }
    // ... one per component, in display order
  ],

  "incidents": [
    {
      "reference": "VNT-4KQ7M2XB",
      "kind": "incident",           // incident | maintenance
      "title": "Elevated error rates affecting Sign-in and accounts",
      "impact": "minor",            // none | minor | major | critical
      "status": "investigating",    // investigating | identified | monitoring | resolved
      "components": ["accounts"],
      "startedAt": "2026-08-05T11:58:00Z",
      "resolvedAt": null,
      "template": "elevated_errors",
      "url": "https://status.venta.gg/incident?ref=VNT-4KQ7M2XB",
      "updates": [                  // newest first
        {
          "status": "investigating",
          "body": "Some people may not be able to sign in or create an account. We are investigating.",
          "template": "elevated_errors",
          "postedAt": "2026-08-05T11:58:00Z"
        }
      ]
    }
  ],

  "maintenance": [                  // active and upcoming, same object shape, kind = "maintenance"
    {
      "reference": "VNT-9PD3RA7C",
      "kind": "maintenance",
      "title": "Database upgrade",
      "status": "scheduled",        // scheduled | in_progress | completed | cancelled
      "components": ["messages", "servers"],
      "scheduledFor": "2026-08-09T02:00:00Z",
      "scheduledUntil": "2026-08-09T04:00:00Z",
      "impact": "minor",
      "template": null,
      "url": "https://status.venta.gg/incident?ref=VNT-9PD3RA7C",
      "updates": []
    }
  ],

  "recent": [ /* last 7 resolved, same shape, updates omitted */ ]
}
```

Enum values are `snake_case` strings in JSON. Treat every one as open: a value you do not recognise
must fall back to the least alarming rendering you have, never to a crash and never to "major
outage".

### The other three endpoints

```
GET https://api.venta.gg/api/v1/status/incidents?limit=25&offset=0&kind=incident
GET https://api.venta.gg/api/v1/status/incidents/VNT-4KQ7M2XB
GET https://api.venta.gg/api/v1/status/feed.atom
```

History and permalinks. Most clients need none of these - link to
`https://status.venta.gg/incident?ref=...` instead of building an incident screen. Build one only if
you want the timeline in-app.

## 3. When to call it

**Poll every 60 seconds while the app is in the foreground.** Stop when backgrounded or when the tab
is hidden, and fire one immediate call on resume. Do not poll on a timer that survives backgrounding;
a million idle clients polling a status endpoint is its own outage.

**Also call it, once, on any of these:**

- The app fails to reach the API at all (network error, or 502/503/504 from the gateway).
- Sign-in fails with a 5xx.
- The websocket drops and the first reconnect attempt fails.

That is the moment status is worth something: the user is looking at a broken screen and deciding
whether to blame their wifi. Fetch the summary and, if the indicator is not `operational`, replace
your generic "something went wrong" with the banner.

If the summary call itself fails, **say nothing about status** - render your normal error. A failed
status check is not evidence of an incident and "could not load status" is noise.

## 4. Rendering

### The banner

Top of the app, full width, dismissible, above the main content and below any titlebar. Severity maps
to your existing palette: `info` -> neutral or blue, `warning` -> amber, `critical` -> red. Tapping it
opens `banner.url` in the system browser.

Show it when `indicator != "operational"`. That includes `maintenance` - a scheduled window in
progress is worth a quiet info bar.

**Do not block the UI.** No modal, no full-screen takeover, no disabling of features because a
component is degraded. Degraded means some requests fail, and the user retrying is often the right
move.

### Dismissal

Remember the dismissed `incidentReference` **together with the `postedAt` of its newest update**.
Re-show the banner when either changes - a new update on the same incident is new information the
user asked to be told about when they opened the app. Clear the memory when the incident leaves the
response.

### Component list

Only in a settings or help screen ("Platform status"), not in the main UI. Name, a coloured dot, and
`uptime90d` as a percentage with two decimals if you show it at all. Link out to
`https://status.venta.gg` for the real page rather than reimplementing the 90-day strip.

### Feature-level hints (optional, and nice)

Because `components[].key` is stable, a client can be specific where it matters: if `voice` is not
`operational`, put a one-line note in the voice panel; if `previews` is down, do not show a spinner
on link cards. Use the key, never the display name, and always fall back to showing nothing.

## 5. Realtime

Signed-in clients on the existing hub (`/api/v1/ws/hub`) get:

| Event | Payload | Do |
|---|---|---|
| `status.SummaryChanged` | the full summary object above | replace your cached summary, re-render the banner |
| `status.IncidentUpdated` | `{ incident }` - one incident object with `updates` | merge by `reference`; if unknown, refetch the summary |

These are a latency improvement, not a replacement for polling. The hub is authenticated, so a
signed-out user - who is quite possibly signed out *because* of the incident - receives nothing. The
polling in §3 is the load-bearing path; treat realtime as making it faster when it works.

## 6. Localisation

Two kinds of incident, and they are localised differently.

**Generated incidents** (`template != null`) come from a fixed table. Translate them client-side from
`template` + `componentKey`, the same pattern as `SystemMessageVariant` in guild join messages. The
templates:

| `template` | English |
|---|---|
| `elevated_errors` | *Elevated error rates affecting {component}* / *{impact hint} We are investigating.* |
| `unavailable` | *{component} is unavailable* / *{impact hint} We are investigating.* |
| `recovered` | *{component} is operating normally again. We are continuing to watch it.* |

The component display name and its impact hint are English-only on the server. If you translate, ship
your own strings for all twelve component keys; if you do not, render the server's text.

**Staff-written incidents** (`template == null`) are free text in the instance's own language. Render
them as-is. There is no translation path and there will not be one - a machine-translated outage
notice is a liability.

## 7. What the payload never contains

Do not go looking for these; they are withheld deliberately.

- Error rates, request counts, status codes, window sizes, thresholds. All staff-only.
- Internal service or cluster names. `accounts`, not `identity`.
- Anything about who is affected, which accounts, or which region.
- Retracted incidents (a generated incident that lasted under two minutes and healed itself). They
  exist in the database and never in a response.

## 8. Checklist

- [ ] Summary fetched anonymously, no bearer token attached
- [ ] Polls at 60s, foreground only, immediate call on resume
- [ ] One-shot fetch on API failure, sign-in 5xx, and failed websocket reconnect
- [ ] Banner renders server copy verbatim (or a translation keyed off `template`)
- [ ] Unknown enum values degrade to the least alarming rendering
- [ ] Dismissal keyed on reference **and** newest update time
- [ ] Nothing rendered when the status call itself fails
- [ ] No modal, no blocked features, no forced sign-out
