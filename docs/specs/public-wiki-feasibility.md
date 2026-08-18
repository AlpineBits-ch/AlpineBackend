# Public wiki hosting - feasibility report

**Question asked:** can a guild's wiki be served to an anonymous browser on `wiki.venta.gg`, so a
page can be linked to somebody who has no account and has not joined?

**Short answer: yes, and the plumbing is nearly free.** The gateway already hosts four sites on
sibling hostnames with the exact pattern this needs, the storage columns for "publish this" already
exist, and no new service or infrastructure is required. Estimated effort is **~3 weeks for one
developer**, and the first week of it is work `roleplay-guilds.md` §4.1 already lists as a
prerequisite for something else.

**The risk is not the plumbing, it is the default.** `WikiPage.Visibility` defaults to `Public` and
has never been read by anything. If external serving is wired to that column as it stands, the
switch publishes every page written in every guild since the wiki shipped, retroactively, in one
deploy. That is the whole security story in one sentence, and §3.1 is about not doing it.

Recommendation: **build it, on a new opt-in flag, read-only, on its own origin, `noindex` by
default, gated on the same entitlement as vanity URLs.**

---

## 1. What exists today

| Piece | File | State |
|---|---|---|
| Page storage | `Guild.Domain/Entity/WikiPage.cs` | Content, slug, tags, icon, cover, revisions |
| Visibility column | `WikiPage.Visibility`, `wiki_visibility` Postgres enum | Written at `WikiEndpoint.cs:171` and `:234`, read by nothing |
| Publish permission | `ModulePermissions.PublishWikiPublicly` (bit 8) | Appears only in `GuildFeatureMap`'s clamp, checked at no endpoint |
| Module gate | `GuildFeatures.Wiki` | Real, clamps all nine wiki permission bits |
| Read endpoints | `Guild.Application/Endpoints/WikiEndpoint.cs` | All `[Authorize]`, all behind `ViewWiki` |
| Anonymous endpoints in Guild | `WebhookEndpoint.cs:134`, `ProductCatalogEndpoint.cs:39` | `[AllowAnonymous]` precedent exists |
| Sibling-host site hosting | `Echo/Sites/SiteHosting.cs`, `SiteHost.cs` | `admin.`, `support.`, `status.`, `auth.`, each on `<LABEL>_DOMAIN` |
| Public anonymous API precedent | `Echo/Controllers/StatusController.cs` | `AllowAnyOrigin` CORS plus a per-IP fixed-window limiter |
| Site CSP precedent | `Echo/Sites/AuthSiteSecurity.cs` | `default-src 'none'`, no `unsafe-inline`, HSTS, frame-ancestors none |
| Guild slug | `Guild.VanityUrl`, `VanityUrlEndpoint.cs` | Unique, normalised, entitlement-gated |
| Markdown parser in tree | `Markdig` 1.3.2 in `Messaging.Domain` | Used for link extraction, not rendering |

Two things follow from that table. The **gateway side is a copy of an existing pattern**, not a
design problem. And the **domain side is half-built already**: somebody wrote the column and the
permission bit for exactly this and stopped.

---

## 2. Feasibility

Yes, on every axis that usually kills this kind of request.

**No new service.** The renderer is a static site in `Echo/wwwroot/wiki`, served by the branch in
`SiteHosting.MapVentaSites` on a `WIKI_DOMAIN` host, the same as the status page. The read API is
one more endpoint class in `Guild.Application` behind the existing `/api/v1/guild/{**catch-all}`
YARP route, so `ProxyConfig.cs` does not change at all.

**No new infrastructure.** Content is already in Postgres in the Guild service. An anonymous read
is a cheaper query than the authenticated one, because it skips `GuildPermissionService` entirely.

**No client work in Alpine or venta-mobile** for the reading half. The publish toggle needs a
control in the guild settings UI eventually, but the API can ship and be driven by curl first.

**The gateway already answers on the hostname.** `SiteHost.Resolve` derives `wiki.<instance host>`
from `INSTANCE_URL` with no configuration, and `UseSiteHostDiagnostics` already turns a
misconfigured `wiki.` request into an explanation rather than a silent 404. A published wiki lives
one label below that - see §6 - which is the one place this costs more than the four sites before it,
because that label needs its own DNS record and its own certificate.

---

## 3. The security implications

### 3.1 The default is wrong, and reusing it is the one unrecoverable mistake

`CreateWikiPageParams.Visibility` defaults to `WikiVisibility.Public` and `CreateWikiPage` defaults
it again at `WikiEndpoint.cs:171`. Every page in the database is `public` unless somebody
deliberately changed it, and nobody had a reason to, because the column did nothing. In context
"public" plainly meant *visible to the guild*, since that is the only audience that existed.

So `WikiVisibility.Public` must not become the external switch. Publishing needs its own column,
default off, and it needs two of them:

* **`Wiki.PublishedSlug` on the wiki root** - the guild-level opt-in. Null means the guild is not on
  `wiki.venta.gg` at all and no page of it can be, whatever any page says.
* **`WikiPage.PublishedAt` (nullable timestamptz)** - the per-page opt-in, and the `lastmod` for the
  sitemap for free.

A nullable column rather than a third `wiki_visibility` member, deliberately: adding a member to an
enum mapped by `HasPostgresEnum` crashes the service at startup until migrated, and unit tests
cannot catch it. Two nullable columns are one ordinary migration with no startup cliff.

Keep `WikiPage.Visibility` meaning what it always meant, and make it mean it - see §5, stage 1.

### 3.2 The response DTOs leak people

`WikiPageDto` is `[Facet(typeof(WikiPage), nameof(WikiPage.Revisions))]`, which is *the whole
entity minus revisions*: `AuthorId`, `LastEditorId`, `GuildId`, `Visibility`, `Tags`. `WikiCommentDto`
carries `AuthorId`. Reusing either on an anonymous route publishes the user ids of everyone who has
ever edited an internal page, plus the guild id, to anyone who asks.

The public route needs its own narrow DTO written by hand: title, slug, content, icon, cover,
category, `PublishedAt`, and nothing else. Whether an author is credited at all is a product
decision that should default to no. Somebody who wrote a page for forty people in a private server
did not consent to their handle being on a page Google can reach, and the page predates the feature
that would publish it.

Comments, reactions, watcher counts and revision history stay off the public surface entirely.
Watcher and reaction counts are a membership-size oracle for a private guild; revision history is an
edit-pattern oracle about named individuals.

### 3.3 Untrusted content on a venta.gg origin

Page content is an opaque string written by anyone holding `CreateWikiPages` in any guild on the
instance. Serving it from `wiki.venta.gg` means arbitrary user text is executed in a venta.gg
origin's context, which is a materially different proposition from rendering it inside the
authenticated app.

What limits the blast radius today: no cookie in this system is set on a parent `.venta.gg` domain,
so there is nothing for a `wiki.` origin to steal from `app.` or `auth.`. What does not: the OIDC
sign-in flow lives on a sibling host, and an XSS on any `*.venta.gg` origin is a credible phishing
platform against it regardless of what it can read.

Required, not optional:

* A CSP modelled on `AuthSiteSecurity` - `default-src 'none'`, `script-src 'self'`, no
  `'unsafe-inline'`, `frame-ancestors 'none'`, `base-uri 'none'`.
* Rendering through Markdig with raw HTML disabled and a link/scheme allowlist. Not a
  `[innerHTML]` bind.
* `img-src` restricted to `'self'` plus the instance's own media origin.

### 3.4 `CoverUrl` is an unvalidated absolute URL

`WikiEndpoint.cs` caps `CoverUrl` at 2048 characters and validates nothing else. The comment says it
points at already-uploaded storage; nothing enforces that. On an authenticated page that is a minor
tracking-pixel problem. On a public page it is a per-visitor IP-and-user-agent beacon pointed at an
address the page author chose, on every anonymous view, including views by people who have never
heard of the guild.

Publishing must require `CoverUrl` to be instance-hosted, or proxy it. Same for any image in the
body once bodies render.

### 3.5 Enumeration

Route on `Wiki.PublishedSlug`, never on `guildId`. A public route taking a guild id lets anyone walk
the id space to learn which guilds exist and which have wikis, which is information the rest of the
API is careful not to give away - `InternalLinkEmbeds.WikiPageStub` deliberately resolves a wiki
link to *identity and nothing else* rather than calling into Guild, for this reason.

Unpublished and nonexistent must both be **404**, never 403. A 403 confirms the thing exists.

### 3.6 Publishing is a moderation and liability change, not a visibility change

Content in a private guild is something the operator stores. Content on `wiki.venta.gg` is something
the operator publishes, under its own brand, to the open internet. That changes the takedown
obligation, the abuse profile, and what the domain's reputation is exposed to. Practically:

* A public wiki is free SEO-bearing hosting on a real domain. Spam farms and phishing pages find
  that within weeks of it being discoverable. Assume it, do not hope.
* `noindex` by default, with indexing as a separate later opt-in. `rel="nofollow ugc"` on every
  outbound link.
* Gate publishing on the same entitlement as `VanityUrl` (`VanityUrlEndpoint.cs:46` already computes
  `Active = VanityUrl is not null && entitled`). A payment step is the cheapest spam filter
  available and it is already built.
* Wire public pages into the existing report flow and the moderation console, and give the console
  an unpublish action. Shipping the publish path without the unpublish path is the actual mistake to
  avoid here.

**How a reader reports a page.** Through the support form, because that is the only surface they
have: the wiki host carries no session and no script, so its reader is anonymous and cannot reach
`POST /api/v1/reports`, which is `[Authorize]` and needs a target account they have no way to name -
a published page credits nobody (§6). Every rendered document therefore links to
`support.<instance>/contact?wiki={slug}/{page-slug}`, and the support form turns that into a `Safety`
ticket whose first line is the page's URL. The address is in the ticket text rather than in a column
of its own, and the console reads it back out of the text a moderator is already reading.

**How a moderator takes it down.** The console's own view, on any report or ticket that names a page
and on a pasted address in any of the forms one is quoted in. It shows what the address serves
anonymously - read in the console rather than by visiting the live page, because deciding from memory
after it is gone is the failure mode - and takes the page, or the whole wiki, off.

That is deliberately not the guild's own publication route. `PUT /guilds/{id}/wiki/publication` is
gated on `PublishWikiPublicly` inside the guild, which an instance moderator does not hold and must
not be given: it would be a permission to publish as well as to unpublish, in a guild they are not a
member of. The console goes over the bus instead, on the operator's authority, and the guild's audit
log records that it happened.

The console says two things plainly, because both are true and neither is obvious. A takedown only
stops this instance serving the page - §3.7 - so anything that already fetched it still has its copy.
And the guild keeps the page and can publish it again, so a takedown that has to stick needs an
action against the account as well.

### 3.7 Unpublishing does not unpublish

Once a page has been fetched and indexed, removing the flag removes it from `wiki.venta.gg` and from
nowhere else. Account deletion and the privacy machinery in `privacy.md` currently never have to
reason about third-party caches, and after this they do, for this one surface. `noindex` by default
is most of the mitigation; the rest is saying so plainly in the publish confirmation, so the guild
owner is the one making the irreversible choice.

### 3.8 The gateway's routes are not host-constrained

Every `RouteConfig` in `ProxyConfig.cs` matches on path alone, so `wiki.venta.gg/api/v1/messaging/...`
proxies exactly as `api.venta.gg` does. This is already true of `admin.`, `support.` and `status.`
and has been fine, because none of those render third-party content. A host that does render it is
the first one where an XSS gets a same-origin path to the authenticated API surface.

Bearer tokens are per-origin and the `wiki.` origin holds none, so this is a hardening item rather
than a live hole. Adding `Hosts` to the service routes is the clean fix and is worth doing while
the reason is fresh.

### 3.9 Anonymous load

An uncached anonymous route into Postgres is a free amplification target. Copy `StatusRateLimiting`:
a per-IP fixed window using `ClientIpResolver`, plus output caching on the page read and a real
`Cache-Control`. Content that changes on human edit timescales tolerates a 60-second cache without
anyone noticing.

---

## 4. What it does not solve

Search across public wikis, cross-instance federation of published pages, and custom domains
(`wiki.someguild.com` with certificate issuance per guild) are all out. Custom domains in particular
look like a small addition and are not: they are an ACME integration, a domain-verification flow and
a per-tenant certificate store.

---

## 5. Build order

Ordering is by dependency. Each stage is useful on its own.

1. **Enforce `Visibility` on the authenticated read path, and make `PublishWikiPublicly` real.**
   `GetWiki` and `GetWikiPage` filter `Private` pages to those the caller may see;
   `PublishWikiPublicly` becomes the permission that changes any publish state. No new columns, no
   new hostname, nothing external. This is `roleplay-guilds.md` §4.1, it is a genuine access-control
   fix independent of this report, and it is the stage where a mistake is cheap.
2. **The two publish columns plus the guild-level slug**, one migration, both defaulting to
   unpublished, with the entitlement check and the `CoverUrl` origin restriction on the publish call.
3. **The anonymous read API.** A new `PublicWikiEndpoint` in `Guild.Application` with
   `[AllowAnonymous]`, hand-written narrow DTOs, per-IP limiter, output cache, slug routing, 404 on
   anything not published. No gateway change - it rides the existing `/api/v1/guild/` route.
4. **The `wiki.` site.** `WIKI_DOMAIN`, `Echo/wwwroot/wiki`, an entry in `SiteHosting`, its own CSP
   branch, a sanitising Markdig renderer, Open Graph tags, `robots.txt` and a sitemap driven by
   `PublishedAt`.
5. **Deploy wiring and moderation.** `deploy/compose.yaml`, `install.sh`, `Install-VentaStack.ps1`,
   the Caddy host blocks, `deploy/README.md`'s site count, plus report-and-unpublish in the
   moderation console.

Stage 1 is roughly a week including tests; 2 and 3 together another; 4 and 5 the third.

---

## 5.5 Deployment

| Variable | Default | Effect |
|---|---|---|
| `WIKI_DOMAIN` | derived from `INSTANCE_URL` | the wiki site's apex |

Derived exactly as the other four sites are, by `SiteHost.Resolve("wiki", "WIKI_DOMAIN")`: the first
label of `INSTANCE_URL`'s host is replaced when it is already a subdomain, and prepended when it is
not. A self-hoster who sets nothing gets `wiki.<their host>` and a working site. The override is
normalised rather than rejected, so `WIKI_DOMAIN=https://wiki.example.com/` is not a silent 404, and
a request for some other `wiki.*` name answers with the diagnostic naming the host that is bound.

Both installers prompt for it beside the four existing site prompts and write two Caddy blocks: the
apex, and `*.<apex>` for the published wikis.

**Two DNS records, and two kinds of certificate.** `wiki.<domain>` behaves like the other sites.
`*.wiki.<domain>` is the new one, and a wildcard *certificate* for it needs the DNS-01 challenge, a
Caddy build carrying the provider's plugin, and an API credential - none of which the bundled image
has. The generated Caddyfile uses on-demand issuance instead: the first request for a name triggers
an ordinary HTTP-01 challenge for that one name, gated by an `ask` endpoint so it cannot become an
open certificate mill. The gateway answers that at `/api/v1/wiki/certificate-check?domain=`, with
`200` only for a slug some guild has actually published, `503` when the Guild service did not answer
- a refusal gets cached, and a certificate refused during a restart would keep failing afterwards -
and `403` for anything that is not a name beneath this instance's wiki apex.

An instance running its own proxy needs the same two things and is told so, with the ask URL, in
`external-proxy` mode. Until that certificate exists the per-wiki names must not be advertised
anywhere: the path form on the apex serves the same page on the ordinary certificate.

---

## 6. Decisions

**A published page credits nobody.** No author, no editor, no display name, no user id anywhere on
the public surface. Somebody who wrote a page for forty people in a private server did not consent to
their handle being on a page anyone can reach, and the page predates the feature that would publish
it. The only person-shaped thing a public page carries is the publishing guild's own name and
description, which is the guild identifying itself.

**The slug is `Wiki.PublishedSlug`, separate from `Guild.VanityUrl`.** It uses the same grammar,
normalization and reserved-word list (`VanitySlug`) and the same entitlement, but a guild can publish
a wiki under a different name from its invite link, and unpublishing does not disturb the invite.

**Each published wiki gets a hostname: `{slug}.wiki.<instance>/{page-slug}`.** The slug is a label
rather than a path segment, which is a stricter grammar - at most 63 characters, lowercase
alphanumerics and hyphens, no leading or trailing hyphen, no underscore. `VanitySlug`, which mints
the slug, is already a subset of that (3-32 characters, `^[a-z0-9]+(-[a-z0-9]+)*$`, and `www` is in
its reserved list), so no published slug can produce an unreachable host. `WikiHost.IsLabel` states
the rule on the gateway side so the two cannot drift apart silently.

**The apex keeps the path form and redirects it, permanently.** `wiki.<instance>/{slug}` and
`/{slug}/{page-slug}` 301 to the subdomain. Keeping it costs a route, and it is the address that
still serves while a per-wiki certificate is missing - a subdomain without one fails at TLS, which a
browser presents as a security warning rather than as a page that is not there. The canonical link
and the Open Graph URL name the subdomain, so one page never advertises two addresses. The apex
itself serves no wiki: a directory of published wikis is the enumeration §3.5 routes around.

**Rendered server-side.** No client routing, because there is no client script at all: the gateway
renders the document, which is what makes `script-src 'none'` possible on the one host that displays
third-party prose, and is also the only way per-page Open Graph tags work. `SiteHosting.ServeSite` is
untouched; the wiki host serves its two assets as endpoints instead, because `StaticFileMiddleware`
stands down once routing has picked an endpoint and any file under `/{page-slug}` would silently stop
being served.

**A household may not publish.** `GuildFeaturePresets.Household` does include `GuildFeatures.Wiki`,
so the capability would otherwise be inherited; both the publish call and the anonymous read refuse
`GuildKind.Household`.

**There is no sitemap.** Every page is `noindex` until indexing becomes its own opt-in, so a sitemap
would list pages nothing may index, and a cross-guild one is exactly the enumeration §3.5 routes
around. `robots.txt` disallows everything.

**The wiki host answers on nothing but the wiki.** §3.8's hardening is done for this host rather than
across `ProxyConfig`: the site's endpoints carry a marker, and anything else selected on that host is
a 404 before it reaches the proxy. Adding `Hosts` to every service route would have changed
behaviour for self-hosted instances that serve the API and the app from one name.
