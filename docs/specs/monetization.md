# Monetization: plans, entitlements, promotions and Stripe

Status: **proposal.** Nothing in this document is built. No billing, entitlement, quota or Stripe
concept exists anywhere in the tree today.

The goal is a platform that pays for itself, where the thing a customer pays for lines up with the
thing that costs money, and where a self-hoster never sees any of it.

---

## 1. What actually costs money

Guilds are the cost centre, but not uniformly. Almost the whole bill is one line item.

### 1.1 The SFU is the bill

Cloudflare Realtime bills on **egress GB**. Everything else about a voice room is free. So the cost
of a room is `subscribers x bitrate x minutes`, and the shape of that expression is what matters.

Working numbers (verify against current Cloudflare pricing before pricing anything; the last figure
seen was a free monthly allowance in the ~1,000 GB range and roughly $0.05/GB after it):

| Room | Fan-out | Per hour |
|---|---|---|
| 10 people, audio only (Opus ~32 kbps) | 10 x 9 = 90 streams | **~1.3 GB** |
| 10 people, one 1080p screenshare (~2.5 Mbps) | 9 subscribers | **~10 GB** |
| 50 people, audio only, everyone subscribed to everyone | 50 x 49 = 2,450 streams | **~35 GB** |
| 50 people, one 1080p screenshare | 49 subscribers | **~55 GB** |

Three conclusions fall straight out of that table, and they are the entire basis of the pricing
model:

1. **Video and screenshare cost roughly 40-50x what audio costs.** A single guild running four
   hours a day of 10-viewer 1080p screenshare burns ~1.2 TB/month, which is a real monthly bill for
   one guild that pays nothing today.
2. **Audio fan-out is quadratic**, so a large room is not cheap just because it has no video. The
   50-person audio room costs more than the 10-person screenshare.
3. **The cost driver is concurrent subscribers, not guilds and not members.** A 5,000-member guild
   whose voice channels are empty costs approximately nothing. A 30-member guild that lives in
   screenshare costs more than all of them. Any pricing unit based on member count is priced
   against the wrong variable.

### 1.2 Everything else, in order

| Driver | Where | Relative size | Notes |
|---|---|---|---|
| Attachment storage + egress | `FileService`, `StorageInstance` | Second largest | Grows monotonically. Never shrinks unless retention does. |
| Message history | Scylla / Postgres | Slow burn | Unbounded retention is a promise you cannot un-make cheaply. |
| Realtime hub connections | `Echo.Realtime`, `GuildHub` | Compute, not egress | Scales with concurrent users, not guilds. |
| Bot gateway sockets | `Bots.*` | Compute | One socket per bot per shard. A cheap tier lever. |
| Push (FCM/APNs) | `CallPushService` etc. | Negligible | Free at any realistic scale. |
| Unfurl outbound fetches | `Unfurl.*` | Negligible, abusable | Already rate limited; keep it that way. |

---

## 2. Cheaper before more expensive

Every one of these cuts the bill without charging anyone, and each is worth more than a pricing tier
because it applies to free users too. **Do these regardless of whether monetization ships.**

1. **Active-speaker subscription above N participants.** Above roughly 8-10 people in a room,
   subscribe each client to the top ~5 speakers plus anyone they have pinned, instead of everyone.
   This turns `n^2` into `n*k` and takes the 50-person audio room from 35 GB/hour to ~4 GB/hour.
   This is the single highest-value change in this document.
2. **Simulcast with server-side layer selection.** A viewer in a 200x120 tile does not need 1080p.
   Cap the free tier at 720p30 and pick the layer from the client's rendered size.
3. **Honour client-side pause.** A backgrounded tab or a collapsed tile should drop its subscription,
   not keep paying for pixels nobody sees.
4. **Idle room reaping.** `VoiceReconciler` already exists for desync; extend it to close rooms whose
   last participant left, and to kill sessions whose track has been silent past a threshold.
5. **Screenshare audio off by default.** It doubles the stream count for a feature most shares do not
   use.
6. **Cap concurrent video publishers per room** even on paid tiers. Nine simultaneous 1080p
   publishers in one room is nobody's intended use case and is somebody's bill.

---

## 3. What is being sold

### 3.1 Three candidate shapes

**A. User subscription only (Nitro).** Simple, familiar, no owner friction. Wrong on the thing that
matters: the guild that costs the most pays nothing, because its members are all free. It monetizes
enthusiasm, not consumption.

**B. Guild plan only (SaaS).** Correctly aligned - the guild owner pays for the guild's capacity.
Wrong on human behaviour: community owners are volunteers with no budget, and a paywall in front of
a hobby is a churn event.

**C. Hybrid, plus member-funded boosts. Recommended.** Three revenue surfaces over one entitlement
system:

- **Guild plans** (Free / Plus / Pro), bought by the owner, covering guild-scoped capacity: voice
  room size, video ceiling, storage, retention, bots, emoji, upload size, audit log window.
- **Venta Plus**, a user subscription covering things that travel with the person: bigger uploads
  anywhere, HD publish in any guild that permits it, more devices, longer search history, profile
  cosmetics.
- **Boosts**, where members buy a boost and apply it to a guild, and boost thresholds unlock the
  guild tier. This is how a guild whose owner will not pay still gets funded, and it is proven at
  scale. It also converts the free rider into the payer, which is exactly the population that
  generates the SFU bill.

### 3.2 Metered or capped?

**Capped, with one exception.** Metered voice minutes look attractive because they track cost
exactly, but a variable bill for a community server is unsellable, generates support load, and makes
the product feel dangerous to use. Caps are predictable and are what every comparable product does.

The exception is anything that is both expensive and clearly a business use: cloud recording, RTMP
restreaming, transcription. If those ship, meter them as add-ons through Stripe usage-based pricing.
Do not meter core voice.

### 3.3 Degrade, do not deny

**When a guild hits a capacity limit, reduce quality; do not refuse the action.** A free guild whose
11th member joins a 10-person room should not get "room full" - it should get an audio-only room, or
480p, or a "this guild is at its free limit" banner with an upgrade link. A denial is a support
ticket and a churn event; a degradation is a conversion surface.

Hard denials are reserved for things that cannot degrade: creating the 51st emoji, uploading past
the file size ceiling, installing the 6th bot.

### 3.4 What must never be paywalled

Writing this down now prevents an argument later.

- End-to-end encryption, MFA, and every other security feature. Charging for security is indefensible
  and will be the thing people quote.
- Data export and deletion. Legally mandated; see `docs/specs/privacy.md`.
- Moderation and safety tooling. A guild that cannot moderate itself is your problem, not its
  problem.
- Basic text, and small-room audio voice. If the free tier does not work as a product, nobody stays
  long enough to buy anything.

### 3.5 A starting tier sketch

Numbers are placeholders until section 10 produces real ones.

| Lever | Free | Plus | Pro |
|---|---|---|---|
| Voice room participants | 10 | 25 | 50 |
| Video / screenshare ceiling | 720p30 | 1080p30 | 1080p60 |
| Concurrent video publishers | 2 | 4 | 8 |
| Upload size | 25 MB | 100 MB | 500 MB |
| Guild storage | 5 GB | 100 GB | 1 TB |
| Message history retention | unlimited | unlimited | unlimited |
| Audit log window | 30 d | 90 d | 1 y |
| Custom emoji | 50 | 200 | 500 |
| Bots installed | 3 | 10 | unlimited |
| Vanity invite / custom domain | no | no | yes |
| Guild modules (`GuildFeatures`) | core set | + Forums, Events | all |

Venta Plus (user): upload size raised anywhere, HD publish where the guild permits it, animated
avatar and banner, more registered devices, longer search window, badge.

Note the deliberate choice on **message history retention: unlimited on every tier**. Retention
limits are the classic SaaS lever and they are wrong here, because the thing being deleted is
somebody's conversation with their friends. It is a storage cost you should absorb; the reputational
cost of "your chat is gone" is larger.

---

## 4. The entitlement system

This is the part that has to be right, because Stripe, admin grants, promotions, boosts, self-hosting
and any future App Store purchase are all just *sources* feeding one resolver.

### 4.1 The model

An **entitlement** is a typed key with a value:

| Kind | Example | Merge rule across sources |
|---|---|---|
| Flag | `guild.vanity_url` | logical OR |
| Numeric limit | `voice.max_participants` | **max** |
| Ladder | `voice.video_ceiling` (480p < 720p < 1080p) | highest rank |

Each key is scoped: **guild-scoped**, **user-scoped**, or **paired**. Guild keys resolve only from
guild sources, user keys only from user sources. Paired keys - the ones where both sides have a
say - resolve as `min(guild_ceiling, user_ceiling)`. `voice.video_ceiling` is the motivating case:
the guild sets what it will pay to distribute, the user's Plus sets what they are allowed to publish,
and the effective value is the lower. Getting this backwards means one Plus user in a free guild
costs you 1080p fan-out to twenty people.

### 4.2 Resolution order

Highest wins per key, evaluated against the merge rule above:

1. **Instance license mode** - self-host short-circuits everything (section 5).
2. **Admin override grant** - staff-issued, permanent or expiring (section 6).
3. **Promotional grant** - campaign-scoped, always expiring. Credit-funded purchases land here too,
   as a grant with `Source = Credit` (sections 7 and 8).
4. **Paid subscription** - Stripe (section 9).
5. **Boost-derived tier**.
6. **Plan defaults** - Free.

**Precedence orders and attributes; the merge rule decides the value.** This was ambiguous in an
earlier draft and is now settled, because the two readings differ in a way that matters. Under strict
first-source-wins, an admin grant of Plus laid over a Stripe Pro subscription would *downgrade* the
guild, which is the opposite of what issuing a grant is for. So every source in scope contributes and
the merge rule above picks the value; precedence decides evaluation order, lets the license-mode
source stop evaluation entirely, and decides which source is **credited** on the provenance screen.

Sources are therefore additive and never destructive. An admin grant does not touch the Stripe
subscription, and when the grant expires the paid value is still there because nothing overwrote it.
This is the property that makes "give this guild Pro for three months while we fix their bug" a safe
operation.

The corollary is that **a grant can only ever raise an entitlement, never lower one**. Restricting a
guild below its plan is a moderation action with its own path, not a billing one, and deliberately
does not live here.

### 4.3 Where the code lives

Two pieces, mirroring the split that already worked for voice:

- **`Billing.*` microservice** - the write side. Owns Stripe, subscriptions, grants, promotions,
  redemptions, invoice references, and the audit trail. Its own database. It is the only component
  that ever holds a Stripe key.
- **`Echo.Entitlements` shared library** - the read side, in the shape of `Echo.Voice`: a cached
  resolver consumed in-process by Guild, Messaging, Voice, Identity and the gateway. Not a service,
  for the same reason `Echo.Voice` is not a service - every request path needs it and a bus hop per
  permission check is not affordable.

Cache in Redis, invalidated by a `billing.EntitlementsChanged` event on the bus, with a short TTL as
a backstop so a dropped event self-heals. The security hardening pass already established that the
two failure modes need different backing; the same reasoning applies here, and the TTL is the
difference between "an upgrade takes effect late" and "an upgrade never takes effect".

**Fail open on the resolver, not closed.** If Billing is down, serve the last known entitlement and
fall back to Free defaults only for subjects never seen. A billing outage that mutes everyone's voice
chat is a worse incident than a few hours of unpaid Pro.

### 4.4 Enforcement lives at choke points

`GuildFeatureMap` is the precedent and the reason this is tractable: one table says which permission
bits belong to which module, and `GuildPermissionService` strips them at a single place instead of
every endpoint remembering. Entitlements get the same treatment.

| Entitlement | Enforced at |
|---|---|
| `voice.max_participants`, `voice.video_ceiling`, `voice.max_publishers` | `VoiceRoomService` on join / publish |
| `storage.upload_max_bytes`, `storage.guild_quota` | `FileService` |
| `guild.emoji_slots` | `GuildEmojiService` |
| `guild.bots_installed` | Bot install endpoint |
| `guild.features` | `GuildPermissionService`, clamped exactly like a disabled module |

That last row matters. **Entitlements must not be folded into `GuildFeatures`.** A feature flag is
product state chosen by the owner; an entitlement is commercial state. But the resolver should clamp
`GuildFeatures` down to what the plan includes, and a feature the plan no longer covers should then
strip its permissions through the existing `DisabledPermissions` path with zero new code. Confirm
that the ungated-by-design set in `GuildFeatureMap` (`ManageGuild` above all) stays ungated - a
downgrade must never lock an owner out of the screen where they would upgrade.

---

## 5. Self-hosting

**Default is self-host, and self-host means everything at maximum.**

- `LICENSE_MODE` in `AppEnvironment/Env.cs`, values `selfhost` (default) and `hosted`. In `selfhost`,
  the resolver short-circuits at step 1 to a `SelfHostEverythingSource`, the `Billing.*` service is
  not deployed by `deploy/compose.yaml`, and every billing surface is hidden.
- Defaulting the *other* way is the mistake to avoid. If the shipped default is crippled, every
  self-host bug report starts with "why can't I", and you spend your support budget explaining a
  paywall to people who are not customers.
- `hosted` must be explicit, and the gateway should refuse to start in `hosted` without Stripe
  configuration rather than silently giving everything away or nothing.

**Do not build a license check.** No key server, no phone home, no runtime validation. It is an
anti-feature in a self-hostable product, it will be patched out within a day of anyone caring, and it
poisons the goodwill that makes self-hosting worth offering. If a paid self-host tier is ever wanted,
sell support, updates and a hosted control plane - not a runtime gate.

**Operator ceilings are separate from entitlements.** A self-hoster on maximum entitlements still pays
their own Cloudflare bill, so give them env-level caps (`VOICE_MAX_PARTICIPANTS`,
`VOICE_VIDEO_CEILING`, `STORAGE_UPLOAD_MAX_BYTES`) that clamp *below* the entitlement. Two different
questions: "what is this guild allowed" and "what will this box do". Same clamp, different source.

---

## 6. Admin overrides

The `admin.<instance>` console already exists, is host-gated in the gateway, and resolves staff tier
per request through `StaffAccess`. It gets a Billing section. Grants are Admin-only; Moderator is
read-only. Money is not a moderation power.

**Grant record:**

```
SubjectKind    enum            User | Guild
SubjectId      string
GrantKind      enum            Plan | Entitlements     plan shorthand, or specific keys
Plan           enum?           when GrantKind is Plan
EntitlementsJson string?       when GrantKind is Entitlements
ExpiresAt      DateTimeOffset? null = permanent
Reason         string          required, free text
Source         enum            Staff | Promotion | Boost | Migration
CreatedBy      string          staff user id
RevokedAt / RevokedBy / RevokeReason
```

Audit-logged the same way moderation actions are, and never hard-deleted - a revoked grant stays as a
row. "Who gave this guild Pro and why" must be answerable a year later.

**The provenance screen is the important one.** For any user or guild, show every effective
entitlement key, its resolved value, and *which source won it*. Without that screen, every billing
support ticket is unanswerable and every bug report is "it says I have Pro but it does not work". It
is worth more than the grant UI itself.

Operations the console needs beyond grant/revoke: look up a subject's subscription and its Stripe
state, extend a grant, convert a grant to a permanent one, and force a resolver cache invalidation
for a subject.

---

## 7. Promotions, trials and abuse

### 7.1 The abuse vector, stated precisely

A free trial is attached to a subject. Whatever subject it is attached to, users will mint new ones.

- Trial on the **guild** -> a new guild every month, forever. Guilds are free to create.
- Trial on the **user** -> a new account every month. Accounts are nearly free to create.

So the trial must attach to a subject that is **expensive to re-mint**, and the record of it must
**survive its own expiry**. Both halves are required. A redemption row deleted on expiry is not
anti-abuse, it is a monthly reminder.

### 7.2 The design

**Trials attach to the owner account, and apply to one guild of their choosing.**

- "One Pro trial per account, ever." Moving the trial to a different guild is allowed and costs
  nothing - the clock does not reset. This kills guild-churn completely, because making a new guild
  gains nothing.
- `PromotionRedemption` rows are keyed on `(campaign, subject)` and are **permanent**. Expiry sets a
  field; it never deletes.
- Redemptions are also recorded **against the guild**, so a guild that has already had a Pro trial
  cannot get another when its ownership changes.

**Against account-churn, gate eligibility on identity that is already collected.** `ApplicationUser`
already carries `EmailVerifiedAt`, `PhoneVerifiedAt`, `AgeVerification`, `CreatedAt`, and the
consolidated `UserDevice` set. A trial should require, at minimum, a verified phone and an account
older than some threshold. Store a **salted hash** of the phone number and the device id on the
redemption, so a fresh account with the same phone is recognised as a repeat.

That last point has a privacy consequence: it is processing an identifier for fraud prevention, it
belongs in the privacy policy as a named purpose, and it must be a hash, never the number. The legal
docs convention applies - new version means a new file plus a new manifest entry, keep the old one.

**Card fingerprint is the strongest signal available.** Stripe returns a stable `fingerprint` on a
payment method that is identical across accounts for the same physical card. Requiring a card for a
Pro trial (not charged) and storing the fingerprint hash on the redemption is the single most
effective anti-churn control there is, and it is what every subscription business uses. It also
raises trial quality enormously. The trade-off is a lower trial start rate; take it for Pro, skip the
card for anything cheaper.

### 7.3 Downgrade must not be farmable

The other half of "beta abuse". If a limit creates a **persistent artifact**, one paid month buys it
forever.

- Emoji slots, storage, custom domain, vanity URL: these must degrade on downgrade, not persist. Over
  the emoji cap means the newest are disabled, not deleted. Over the storage cap means uploads are
  frozen, not that files are removed. **Never delete user data on downgrade** - freeze the growth and
  say so plainly.
- Anything that would be destructive to reverse should simply not be a tier lever.

### 7.4 The promotion machinery

One mechanism, several campaign types: trials, gift codes, referral rewards, partner codes,
beta-tester grants, win-back offers.

```
Campaign        code, description, effect, validity window,
                max total redemptions, max per subject (default 1),
                eligibility predicate (verified phone, account age, never-paid, ...)
Effect          plan for N days | entitlement keys for N days | Stripe coupon id
```

**Discounts belong in Stripe; free access belongs in your database.** Do not build a discount engine -
Stripe coupons and promotion codes already do percentage-off, first-N-months, and duration correctly,
including on the invoice and in tax calculation. But do not express free access as a 100%-off Stripe
subscription either, because then granting access requires Stripe to be reachable and configured,
which breaks self-hosting, offline operation and the entire admin-grant path.

### 7.5 Beta, specifically

Whatever is promised during beta becomes permanent by default unless an end date is written down at
the time of the promise. So: issue beta access as a **grant with an explicit `ExpiresAt`**, set now,
visible to the user in-product from day one, with a documented conversion offer. "Free during beta"
with no date is a pricing decision made by accident.

An early-supporter grandfathering flag on the plan is worth having from the start - it is cheap now
and impossible to retrofit fairly.

---

## 8. Promotional credit

A spendable balance the platform can hand out. It is a genuinely different instrument from a discount
code, and it is worth the extra machinery because it does things nothing else in this document can:
apologise for an incident without giving away a tier, reward both halves of a referral, seed a
usage-based add-on, and above all turn a member who was never going to buy a subscription into
somebody who spends on a guild.

### 8.1 The one decision that determines everything else

**Credit is issued, never sold.**

The moment a user can pay cash for a balance, it stops being marketing and becomes **stored value**,
and stored value is regulated: prepaid/gift card statutes, expiry prohibitions in several US states
and parts of the EU, unclaimed-property escheatment, deferred revenue recognition, breakage
accounting, and in some readings e-money licensing. All of that for a feature whose entire purpose is
to give things away.

So, as a hard rule and not a phase-one simplification:

- Credit is granted by the platform. There is no path that converts money into credit.
- Credit is **never refundable, never withdrawable, never convertible back to cash**, and no chain of
  operations may add up to one.
- Credit has **no cash value**, expires, and says so everywhere it is displayed.
- Every SKU purchasable with credit also has a plain cash price. Credit must never be the *only* way
  to obtain something - that pattern forces overbuying, invites the comparison to game currencies,
  and attracts exactly the regulatory attention the rule above avoids.

If selling credit is ever genuinely wanted, it is a separate project with legal input, and the ledger
below must keep purchased and promotional lots in strictly separate kinds so the two can never be
spent, expired or reversed by the same rules.

### 8.2 Denomination: points, not currency

Two options, and the choice has consequences.

| | Currency-denominated ("€5.00 credit") | Abstract points ("500 credits") |
|---|---|---|
| Price list | One, shared with cash | A second one, in points |
| Multi-currency | Breaks - a wallet must pick a currency | Unaffected |
| User comprehension | Immediate | Needs the cash price shown alongside |
| Legal framing | Looks like money, invites "give me the €5" | Clearly a promotional token |

**Recommendation: points.** The FX argument alone settles it - the moment there is a USD price and a
EUR price, a currency-denominated wallet has to pick one and then every cross-currency purchase is an
exchange-rate decision you are not equipped to make. The legal framing is the second reason and the
better one.

Set the point price of each SKU from an internal peg (say 100 points to the euro) so the two price
lists never drift, but keep that peg internal. It is a tool for setting prices, not a user-facing
exchange rate, and publishing it re-creates the money framing that points exist to avoid. Always show
the cash price next to the point price so nobody has to guess what a credit is worth.

### 8.3 What credit buys

Credit purchases **time-boxed entitlement grants** - the same records section 6 already defines, with
`Source = Credit`. It does not touch Stripe, does not create an invoice, and is not a tax event,
because nothing was sold.

Sensible catalogue:

| SKU | Why it is a good sink |
|---|---|
| 30 days of Plus / Pro on a guild | Direct trial of the thing you want them to subscribe to |
| A boost | The best one - it routes credit to the member-funded model in section 3.1 |
| 30 days of Venta Plus | Converts the individual |
| Event capacity: one room raised to Pro limits for 24h | Matches how communities actually spike |
| Metered add-on balance (recording, transcription) | The natural pairing; see 8.7 |

Deliberately **not** purchasable with credit: anything that creates a persistent artifact (extra
emoji slots, vanity URL, storage). Section 7.3 already established those must degrade on downgrade,
and a credit-funded artifact is the same farm with an extra step.

**Time-based purchases queue, they do not overlap.** If a guild is already Pro until the 30th and
somebody spends credit on 30 days of Pro, the new grant must start on the 30th, not run concurrently
and evaporate. Getting this wrong produces the worst possible support ticket: "I spent my credit and
got nothing." The resolver in section 4.2 handles overlapping sources fine; it is the *purchase* that
has to be smart about start dates.

### 8.4 Wallets are user-scoped; targets are not

One wallet per user. Guilds do not hold balances.

The user spends their own credit on themselves *or* on a guild they belong to. A guild-held balance
sounds natural and creates an ownership dispute the first time a guild changes hands or splits, plus
a "whose money was that" question with no good answer. Staff wanting to give a guild something use an
admin grant (section 6) directly - no wallet needed, and the audit trail is better.

### 8.5 The ledger

Append-only. **There is no mutable balance column**; the balance is the sum of entries, with a
materialized cache for reads that can always be rebuilt from the entries.

```
CreditEntry
  UserId          string
  Amount          long            signed; positive issue, negative spend
  Kind            enum            Issue | Spend | Expiry | Reversal | Adjustment
  LotId           string?         which lot a Spend/Expiry consumed
  CampaignId      string?         for Issue
  GrantId         string?         for Spend - the entitlement grant it produced
  IdempotencyKey  string          unique; the whole concurrency story
  Reason          string          required on Adjustment and Reversal
  CreatedBy       string?         staff id when hand-issued
```

**Lots and expiry.** Every issue creates a lot with its own `ExpiresAt`. Spending consumes
**earliest-expiring first**. Without lot-level FIFO, expiry does not really work: a single balance
number cannot say which part of it was about to lapse. Recommended default is 12 months per lot, with
a notification at 30 days out.

Expiry is not only a cost control - it is an **abuse control**, because it bounds what a farmed
stockpile is ever worth. (Note the asymmetry that reinforces 8.1: expiring *issued* credit is
completely normal, while expiring *purchased* credit is illegal in several jurisdictions.)

**Atomicity.** The deduction and the grant it produced are written in **one transaction**, which is
possible only because both live in `Billing.*`'s own database. This is a concrete argument for
keeping grants in Billing rather than scattering them: the alternative is a distributed
deduct-then-create with an outbox, a reconciler, and a class of "credit gone, nothing bought" bugs
that users notice immediately and never forgive.

**Never negative.** Two concurrent spends must not both pass the balance check. Take a row lock on
the wallet for the duration of the spend transaction, and make the `IdempotencyKey` unique so a
retried request cannot double-deduct. This is not somewhere to be clever.

### 8.6 Abuse

Credit is the most money-like object in the system, so it attracts the most abuse, and every control
from section 7 applies to credit-issuing campaigns unchanged: permanent redemption records, hashed
phone and device identity, per-campaign and per-subject caps.

On top of that:

- **Non-transferable by default.** Gifting credit is a fraud amplifier - farm accounts, consolidate
  balances, spend once. If gifting is ever wanted, gate it on verified phone plus account age plus a
  per-window cap, and make a gifted lot non-giftable onward so chains cannot form.
- **Cap the wallet balance** per account. There is no legitimate reason to hold a thousand euros of
  promotional credit.
- **Void on fraud ban.** Banning an account for fraud writes a `Reversal` for its outstanding lots.
  The entries stay; the balance goes to zero.
- **Automated issuance needs a budget.** Any campaign that issues without a human in the loop
  (referrals, incident compensation) carries a total cap and an alert, or a single loop bug becomes a
  five-figure liability overnight.
- **No spend-then-refund loop.** Since credit only ever buys non-refundable grants, this closes by
  construction. Keep it that way: the day something credit-purchased becomes refundable, check where
  the refund goes.

### 8.7 Where this actually earns its keep

- **Incident compensation.** There is already a status page with an incident record. A confirmed
  incident can propose a credit campaign scoped to the affected guilds - a concrete apology instead
  of a status update, issued through machinery that already knows who was affected.
- **Referral, both sides.** The referrer and the referred each get a lot. Both halves matter; one-way
  referral rewards convert badly.
- **Support goodwill.** Moderators and support staff get a small per-agent issuance budget so they can
  resolve a complaint without escalating to "give them a free month of Pro".
- **Seeding usage-based add-ons.** If recording or transcription ships as metered, a standing monthly
  credit allowance is by far the best way to get people to try it. This is the one place where credit
  and metering fit together perfectly, and it is why 3.2's "meter only the expensive extras" and this
  section are the same plan.
- **Win-back.** A lapsed subscriber is much cheaper to re-activate with credit than with a discount,
  and it does not train them to wait for sales.

### 8.8 Surfaces

**Admin console** (Admin tier only, same as grants): view a wallet and its full ledger with
provenance, issue credit with a mandatory reason, reverse an entry, run and cap campaigns, see
per-campaign issuance against budget.

**User-facing**: balance, what it can buy, the ledger in plain language ("500 credits, expires 12
March"), and the expiry warning. The ledger being visible is not a nicety - an invisible balance
generates support tickets at a rate that exceeds the feature's value.

**Self-hosting**: in `selfhost` mode wallets are meaningless, since everything already resolves to
maximum. Hide the surface entirely rather than showing an infinite or zero balance, both of which
read as a bug.

---

## 9. Stripe, with your own UI

"Custom UI with Stripe" has one correct answer: **Stripe Elements in your own page**, not Stripe
Checkout. Card data goes directly from the browser to Stripe and never touches your servers, so you
stay in the light-touch PCI scope, and you keep total control of the design.

### 9.1 The subscription flow

1. Client posts to Billing: "subscribe guild X to Pro, monthly".
2. Billing finds or creates a Stripe **Customer**, idempotently keyed on your user id, storing
   `StripeCustomerId` on your side and your ids in Stripe `metadata` in both directions. Both
   directions matters: every Stripe object should be traceable back to a guild without a lookup
   table, and a webhook that cannot identify its subject is an incident.
3. Billing creates the **Subscription** with `payment_behavior: default_incomplete`, expanding the
   latest invoice's payment intent, and returns the `client_secret`.
4. The client confirms with the **Payment Element**. SCA / 3-D Secure is handled inside that confirm
   step - this alone is why you do not hand-roll a card form for European customers.
5. The client polls entitlements briefly and shows the new state.

**Webhooks are the source of truth, never the client's "it worked" callback.** The events that matter
are `customer.subscription.created|updated|deleted`, `invoice.paid`, `invoice.payment_failed`, and
`charge.dispute.created`. Verify the signature. Process idempotently keyed on the Stripe event id
(store handled ids). Delivery is not ordered, so **reconcile against the object's current state
rather than applying a delta** - an out-of-order `updated` that downgrades someone who just upgraded
is the classic bug.

### 9.2 The rest of the surface, still custom

- **Cancel, resume, change plan, change payment method**: direct API calls, trivially wrapped in your
  own screens. Keep Stripe's hosted Customer Portal configured as a per-customer escape hatch for
  cases support cannot resolve, but do not put it in the normal flow - it is a different design
  language and a redirect away from your product.
- **Proration on upgrade**: let Stripe compute it (`proration_behavior: create_prorations`) and show
  the exact number in your own UI first by previewing the upcoming invoice. Custom UI with correct
  arithmetic.
- **Invoices**: expose Stripe's `hosted_invoice_url` and `invoice_pdf`. Do not render invoices
  yourself; the formatting is a tax-law problem, not a design problem.
- **Idempotency keys on every write call**, without exception.
- **Dunning**: enable Smart Retries, and hold the tier for a ~7 day grace period after
  `invoice.payment_failed` before downgrading. A failed card is nearly always a card, not a decision.

### 9.3 Two things that get forgotten and are expensive

**VAT / sales tax.** Selling digital services to EU consumers means VAT at the customer's rate, which
means EU OSS registration, which means collecting and evidencing customer country, and offering a VAT
id field so business customers can reverse-charge. Turn on **Stripe Tax** and let it do the
calculation and the evidence. This is not optional and it is the single most commonly deferred piece.
Get local advice for the seller entity's own jurisdiction before the first charge, not after.

**Apple and Google in-app purchase.** `venta-mobile` is a real client. If a subscription is sold or
unlocked inside the app, Apple requires it to go through IAP at a 15-30% cut, and enforcement here is
not theoretical. Two viable paths: sell only on the web with no link or mention from inside the app,
or implement IAP as a second payment source. **The entitlement architecture in section 4 is what
makes the second path cheap** - an App Store receipt becomes another `IEntitlementSource` at the same
precedence as Stripe, and nothing downstream changes. Decide this before building the mobile paywall,
because it changes the flow, not just the plumbing.

### 9.4 Not a Stripe problem, but adjacent

Legal documents need a paid-services section, a refund and cancellation policy, and a statement of
what happens to data on downgrade. Existing convention: a new version is a new file plus a new
`manifest.json` entry, keeping the old one, mirrored to the landing site.

---

## 10. You cannot price this without measuring it

Every number in section 3.5 is a guess, and guesses about SFU egress are expensive guesses.

**Build per-guild usage metering first, before any pricing decision and before any Stripe code.** A
`VoiceUsageMeter` riding on the existing `VoiceReconciler` can record, per room per interval,
`(subscriber count x track kind x seconds)` and aggregate to a daily per-guild figure. That is a
close enough proxy for egress GB to price against, and it is cheap because the reconciler already
walks exactly this state.

It pays for itself three times: it is the only honest input to pricing, it is the abuse detector
("this guild is doing 40x the p99"), and it is the meter if usage-based add-ons ever ship.

Storage and history need the same treatment, but they are slower moving and can follow.

---

## 11. Rollout order

Deliberately sequenced so nothing is blocked on Stripe, and so pricing is set with data.

| Phase | Contents | Money? |
|---|---|---|
| **0** | Usage metering. Cost engineering from section 2. | No |
| **1** | `Echo.Entitlements` resolver, all sources stubbed, every limit set to today's behaviour. Self-host mode. Admin grants + provenance screen. | No |
| **2** | Define plans. Enforce Free limits at the choke points, degrade-not-deny. Announce with notice. | No |
| **3** | `Billing.*`, Stripe Elements checkout, webhooks, subscription management screens, Stripe Tax. | **Yes** |
| **4** | Promotions, trials, referral and gift codes, redemption anti-abuse. | Yes |
| **5** | Credit: wallet, lot ledger, spend catalogue, admin issuance, campaign budgets. | Yes |
| **6** | Boosts, gifting, IAP if mobile requires it. | Yes |

Credit lands at phase 5 rather than earlier for one reason: its whole value is what it can be spent
on, and until plans, boosts or metered add-ons exist there is nothing worth buying. Issuing credit
before there is a catalogue produces a balance nobody can use, which is worse than no credit at all.
The one thing worth pulling forward is the *ledger shape* in 8.5 - lots, append-only entries,
idempotency keys - because retrofitting lot-level expiry onto a plain balance column is a migration
nobody enjoys.

Phase 1 before phase 3 is the load-bearing ordering decision. It means the first paying customers can
be onboarded by hand - an admin grant and a manual invoice - while the Stripe integration is still
being built, and it means the day Stripe breaks, nobody loses access to anything.

---

## 12. Open questions

1. **Seller entity and jurisdiction** - drives VAT treatment, and whether Stripe Tax alone is
   sufficient.
2. **Boost price point and threshold curve** - how many boosts equal Plus, equal Pro. Needs the
   phase 0 numbers.
3. **Does the mobile app sell, or only display?** Section 9.3. Decide before the paywall is designed.
   Note that credit *spending* inside the app is a separate question from credit *sales*, which
   section 8.1 rules out entirely - a balance that was given away and is spent on the platform's own
   features is not an in-app purchase.
4. **Free tier voice ceiling** - the most consequential single number in the document, and the one
   that decides whether the free tier is a product or a demo.
5. **Federation** - a federated guild's voice traffic is carried by somebody. Which instance pays,
   and what a hosted instance owes a self-hosted one, is unmodelled and should stay out of scope
   until phase 6.
