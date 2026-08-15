# Billing checkout API contract

The interface between the Billing service and the Alpine client for wave 6. Written before either
side exists so both can be built at once; whichever side is finished second is the one that has to
match this document rather than the other way round.

Architecture and the reasoning behind it: `monetization-stripe-architecture.md`.

All paths are the **public** paths as seen by the client. The gateway proxies
`/api/v1/billing/{**catch-all}` to Billing and strips the `billing` segment, so
`/api/v1/billing/catalogue` is served by a Wolverine endpoint declared at `/api/v1/catalogue`.

Money is always **minor units** (`2900` is $29.00) plus a lowercase ISO 4217 `currency`. There is no
float anywhere in this contract, in either direction.

Timestamps are ISO 8601 UTC, serialised as `+00:00` rather than `Z`. Both are valid and mean the same
thing; `+00:00` is what `DateTimeOffset` renders on the entitlement snapshot and every other Echo
endpoint, and making billing the one surface that differs would defeat the same one-screen-one-
representation argument that forces the lowercase `subjectKind` below.

---

## 0. There is no `/config` endpoint

The publishable key already reaches the client: `EntitlementSnapshotBuilder` puts it on the
entitlement snapshot as `stripePublishableKey`, and `EntitlementStore` already exposes a
`stripePublishableKey()` computed that prefers the instance value and falls back to
`environment.stripePublishableKey`. Adding a second endpoint that answers the same question is how
two sources of truth start disagreeing.

So: **the key comes from the snapshot, and whether anything is for sale comes from §1.** The client
needs both to be true before it renders any purchasing surface.

---

## 1. `GET /api/v1/billing/catalogue`

Authenticated. What is for sale, and whether anything is.

```json
{
  "enabled": true,
  "currency": "usd",
  "plans": [
    {
      "name": "pro",
      "displayName": "Pro",
      "description": "For communities that stream.",
      "versionNumber": 3,
      "subjectKind": "guild",
      "priceMinorUnits": 2900,
      "currency": "usd",
      "interval": "month",
      "purchasable": true,
      "entitlements": {
        "voice.max_participants": { "kind": "numeric", "value": 75, "unlimited": false },
        "voice.video_ceiling":    { "kind": "ladder",  "rung": "2160p60", "rank": 6 },
        "guild.bots_installed":   { "kind": "numeric", "value": null, "unlimited": true },
        "guild.vanity_url":       { "kind": "flag",    "granted": true }
      }
    }
  ],
  "ladders": { "...": "the same ladder map the entitlement snapshot publishes" }
}
```

**`entitlements` is byte-identical in shape to the snapshot's own `entitlements`**: a
`Record<string, EntitlementValueDto>` in the three discriminated shapes
(`{kind:"numeric", value, unlimited}`, `{kind:"flag", granted}`, `{kind:"ladder", rung, rank}`),
lowercase discriminators, `value` a real JSON number, and `unlimited: true` with `value: null` where
the ceiling is `long.MaxValue` - which exceeds `Number.MAX_SAFE_INTEGER` and must never be put on the
wire as a number.

This is stated so precisely because the first draft of this document described the encoding in prose
and then gave an example that did not match it, which would have produced exactly the second
formatter the prose was arguing against. **Reuse `EntitlementValueDto`; do not define a parallel
type.** `ladders` rides on the envelope rather than on each plan, once, for the same reason it does
on the snapshot: rung metrics are a property of the ladder, not of who is buying.

`enabled` is answered by the service that actually holds the secret key, which is the only place
that can answer it honestly. It is false when `STRIPE_SECRET_KEY` is unset, and in that case `plans`
is still populated so the comparison table renders - the buy buttons are simply absent.

A self-hosted instance never reaches this endpoint at all: Billing refuses to start under
`LICENSE_MODE=selfhost` and the gateway filters the `/api/v1/billing` route out entirely, so the
client's existing `licenseMode` check is what suppresses the surface there.

- Archived plans and archived versions are absent.
- A plan with no `priceMinorUnits` is present with `purchasable: false` and a null price - that is
  how `free` and `free_user` appear, and the comparison table needs them.
- `subjectKind` is **lowercase** `guild` or `user`. `free`/`plus`/`pro` are guild plans;
  `free_user`/`venta_plus` are user plans.

  Lowercase deliberately, and not the `JsonStringEnumConverter` default of `Guild`/`User` that
  Billing's staff endpoints emit. The entitlement snapshot already sends lowercase, the same client
  screen renders both payloads, and two casings for one concept on one screen is a bug waiting for
  somebody to write `===`. The staff endpoints keep PascalCase - they are a different audience and
  the two are never compared in one place. Serialise these customer-facing DTOs with an explicit
  lowercase converter rather than changing the service-wide default.
- `entitlements` uses the **same value encoding as the entitlement snapshot** the client already
  renders (`Echo.Entitlements/Wire`). It is deliberately the same shape so the client reuses one
  formatter rather than growing a second one that disagrees with the first in some edge case. The
  key names are the real ones from the seeded catalogue - `voice.max_participants`,
  `voice.video_ceiling`, `voice.max_publishers`, `storage.upload_max_bytes`,
  `storage.guild_quota_bytes`, `guild.audit_log_days`, `guild.emoji_slots`,
  `guild.bots_installed`, `guild.vanity_url`, `user.upload_max_bytes`, `user.max_devices`.

---

## 2. `POST /api/v1/billing/subscriptions`

Authenticated. Starts a subscription. Requires `ManageGuild` on the target guild when
`subjectKind` is `Guild`, checked over the bus with `HasUserPermissionToGuildRequest` - not from a
token claim.

Request:

```json
{ "planName": "pro", "subjectKind": "guild", "subjectId": "gld_..." }
```

`subjectId` for a **user** plan is the caller's real user id. The literal `me` is a client-side
sentinel in `EntitlementSubjectRef` and must never be accepted on the wire. The server validates that
the id equals the authenticated caller and returns `not_permitted` when it does not - it does **not**
silently substitute the caller, because a request asking to subscribe somebody else should be refused
rather than quietly rewritten into a different, valid request. Silent coercion is how an
authorization bug hides as a working feature.

### Reopening an abandoned checkout

Somebody who opens checkout, abandons at the card field and opens it again sends a second `POST`. The
unique index over `(SubjectKind, SubjectId)` is filtered to live statuses so that a cancelled
subscription does not block re-subscribing, which means it does **not** stop `incomplete` ones
stacking up - and two of those being confirmed is a double charge.

So the server reuses rather than stacks: an existing `incomplete` subscription for the same subject,
plan and payer is **returned with a freshly retrieved client secret**, and the gateway's create is not
called again. Reopening checkout resumes it, which is what a customer expects anyway. An incomplete
attempt for a *different* plan is cancelled explicitly rather than left for Stripe to expire, so a
stale client secret in a background tab cannot be confirmed into a plan they decided against.

**`already_subscribed` is for live statuses only.** An incomplete subscription is an unfinished
attempt, not a subscription, and returning 409 for one would leave the customer unable to buy
anything until it expired roughly a day later.

Response `200`:

```json
{
  "subscription": { "...": "SubscriptionDto, see 3" },
  "clientSecret": "pi_..._secret_..."
}
```

`clientSecret` may be **null**, which means Stripe had nothing to confirm. The client must treat null
as "go straight to polling" rather than as an error.

There is no `requiresAction` flag. An earlier draft had one, and it was removable precisely because
it was exactly `clientSecret != null` - two fields that can only ever agree are two fields that will
eventually disagree, and then nobody knows which one is authoritative. `clientSecret` is.

**The client is not the source of truth for activation.** A successful `confirmPayment` means the
card worked; only the webhook makes the subscription live. After confirming, poll §3 until `status`
is `active`, then refresh the entitlement snapshot.

### Errors, for this endpoint and every other one in this document

`application/problem+json` with a machine-readable `code`. ASP.NET serialises `ProblemDetails`
extensions **flat onto the problem body**, not nested under an `extensions` key, so `code` is a
top-level member. The client reads both positions anyway, since the cost of that is three lines and
the cost of being wrong is an error that renders as "something went wrong".

| `code` | HTTP | Meaning for the client |
|---|---|---|
| `billing_disabled` | 404 | Stripe is not configured. Unreachable if §1's `enabled` was honoured. |
| `not_purchasable` | 400 | The plan exists but is not sold. |
| `already_subscribed` | 409 | This subject already has a live subscription. Offer "change plan" instead. |
| `not_permitted` | 403 | The caller lacks `ManageGuild`, or is not the payer on a payer-only action. |
| `not_the_payer` | 403 | Specifically: they manage the guild but somebody else's card is behind it. |
| `subscription_lapsed` | 409 | Resume was called on a subscription that has already ended. |
| `last_payment_method` | 409 | Detaching the only card under a live subscription. |
| `unknown_plan` | 400 | No such plan. Same spelling the staff surface already uses. |
| `unknown_subscription` | 404 | No such subscription **or not yours**. |
| `unknown_payment_method` | 404 | No such payment method **or not yours**. |
| `stripe_error` | 502 | Show the message; it is safe to display and already customer-worded. |

The last two deliberately answer identically for "does not exist" and "exists but is not yours".
Distinguishing them would let either endpoint be walked to enumerate other people's ids, and there is
no legitimate caller who needs to tell the two apart.

This table is global on purpose. The first draft documented codes only for subscription creation and
payment-method detach, which left cancel, resume, change and preview with no named failures despite
all four being able to refuse for reasons a person would want explained. An unrecognised code must
still degrade to "the request failed" plus the HTTP status rather than crashing, because this table
will grow.

---

## 3. `GET /api/v1/billing/subscriptions` and `/api/v1/billing/subscriptions/{id}`

Authenticated. The list returns subscriptions the caller **pays for**, plus those on guilds where
they hold `ManageGuild`. Fetching one by id requires being the payer or holding `ManageGuild` on its
subject.

`SubscriptionDto`:

```json
{
  "id": "sub_...",
  "subjectKind": "guild",
  "subjectId": "gld_...",
  "planName": "pro",
  "planDisplayName": "Pro",
  "versionNumber": 3,
  "status": "active",
  "currentPeriodEnd": "2026-09-14T10:12:00Z",
  "cancelAtPeriodEnd": false,
  "gracePeriodEndsAt": null,
  "priceMinorUnits": 2900,
  "currency": "usd",
  "interval": "month",
  "isPayer": true
}
```

`status` is Stripe's own vocabulary, passed through unchanged rather than remapped:
`incomplete`, `incomplete_expired`, `trialing`, `active`, `past_due`, `canceled`, `unpaid`, `paused`.
The client is expected to handle an unrecognised value by showing it as "needs attention" rather
than crashing, because Stripe adds to this list.

`gracePeriodEndsAt` non-null means a payment failed and the tier is being held until that moment.
This is the single most important thing on this screen for a customer whose card expired, and it
needs a plain sentence with a date, not a status chip.

`isPayer` false means the caller manages the guild but somebody else's card is behind it. They may
look, they may not cancel.

`interval` is the period the price is charged over, and it is the same string the catalogue publishes
for the same plan version - `month` in this wave, sourced from the one place that decides it, so the
shop window and the subscription card can never disagree about the cadence of one plan. Without it
the card could not say "$29.00 per month" without a second catalogue fetch keyed on `planName`, and
it would render the amount plus a renewal date and convey the cadence only indirectly.

It is nullable, and it is null in exactly one case: the plan version behind the subscription could
not be resolved, which is the same case that nulls `priceMinorUnits` and `currency`. That is a
degraded row rather than a normal one - the foreign keys make it near-impossible - and the client
should render such a subscription without a price line at all rather than inventing a period for it.
Note that the converse does not hold: a resolvable version with no price still carries an interval,
because the catalogue publishes one for that version too.

The field was added after the first clients were written, so a client must treat it as optional and
fall back to the amount-plus-renewal-date rendering when it is absent. An old server during a rolling
deploy is a real case, not a hypothetical one.

The list returns subscriptions the caller pays for **plus** those on guilds where they hold
`ManageGuild`, and it **includes ended ones** - a subject can legitimately have both an ended
subscription and a live one, so a client picking "the" subscription for a subject must prefer the
non-ended one rather than the first.

---

## 4. `POST /api/v1/billing/subscriptions/{id}/cancel` and `/resume`

Payer only. Cancel sets `cancel_at_period_end`; it does not end anything today, and the copy must
say when access actually stops. Resume clears it, and is valid only while the subscription has not
yet lapsed. Both return the updated `SubscriptionDto`.

There is no immediate-cancellation endpoint. Refunds are a staff action in the support console, not a
button in the app.

---

## 5. Changing plan

`POST /api/v1/billing/subscriptions/{id}/preview-change` with `{ "planName": "plus" }`:

```json
{
  "immediateChargeMinorUnits": 1450,
  "currency": "usd",
  "nextInvoiceTotalMinorUnits": 900,
  "nextInvoiceAt": "2026-09-14T10:12:00Z",
  "lines": [
    { "description": "Unused time on Pro", "amountMinorUnits": -1450 },
    { "description": "Remaining time on Plus", "amountMinorUnits": 450 }
  ]
}
```

`immediateChargeMinorUnits` can be negative (a credit). This must be shown before the change is
committed - proration is the single most frequent billing support question and a preview removes
most of it.

`POST .../change` with the same body applies it and returns the updated `SubscriptionDto`.

---

## 6. Payment methods

- `GET /api/v1/billing/payment-methods` -
  `[{ "id": "pm_...", "brand": "visa", "last4": "4242", "expMonth": 12, "expYear": 2030, "isDefault": true }]`
- `POST /api/v1/billing/payment-methods/setup-intent` - `{ "clientSecret": "seti_..._secret_..." }`
- `POST /api/v1/billing/payment-methods/{id}/default` - `204`
- `DELETE /api/v1/billing/payment-methods/{id}` - `204`, or `409` with code `last_payment_method`
  when it is the only one and a live subscription depends on it. Stripe would otherwise accept the
  detach and fail the next invoice, which turns a refusable action into a support ticket a month
  later.

Brand, last four and expiry are the only card data that exists on our side, and that is the whole
point of Elements.

---

## 7. `GET /api/v1/billing/invoices`

```json
[{
  "id": "in_...",
  "number": "VENTA-0001",
  "status": "paid",
  "amountDueMinorUnits": 2900,
  "currency": "usd",
  "createdAt": "2026-08-14T10:12:00Z",
  "hostedInvoiceUrl": "https://invoice.stripe.com/...",
  "invoicePdfUrl": "https://pay.stripe.com/.../pdf"
}]
```

The two URLs are opened externally. We do not render invoices.

**Caller-scoped, like payment methods**: these are the invoices raised against the caller's own Stripe
customer, not against a guild. A guild plan page therefore shows no invoice history, which is correct
as long as invoices follow the payer - and they do, because the payer is always a person and never a
guild. If a guild-scoped view is ever wanted, this endpoint needs a subject filter and the payer's
other invoices must not leak through it.

`number` is **absent on a draft invoice** - Stripe does not assign one until the invoice is issued -
so a client must render "not issued yet" rather than an empty cell. Ordering and paging are
unspecified; the server returns Stripe's order.

In a Tauri webview these URLs must be opened through the platform's external-link mechanism.
`window.open` navigates the webview itself and replaces the running application.

---

## 8. `POST /api/v1/billing/stripe/webhook`

Anonymous, Stripe only, never called by a client. Documented here so nobody adds authentication to
the whole `/api/v1/billing` prefix and breaks it silently. It must not be rate-limited by the
gateway.

---

## 9. Client rules that are not visible in the shapes above

1. **Both gates before any purchasing UI**: `enabled` from §1 *and* a non-empty
   `stripePublishableKey()` from the entitlement store. Either one missing means no buy button
   anywhere - not a disabled one, not a tooltip, nothing.
2. **Never trust `confirmPayment` for entitlement.** Poll, then refresh the entitlement snapshot.
3. **Never build a card input.** The Payment Element iframe is the only place a card number is
   allowed to exist. A `<input>` collecting a PAN on our origin changes our PCI scope and would have
   to be reverted.
4. **Stripe.js is loaded from `https://js.stripe.com/v3/` at runtime**, never bundled and never
   self-hosted - Stripe does not support it and 3DS breaks. The Tauri CSP needs
   `script-src https://js.stripe.com`, `frame-src https://js.stripe.com https://hooks.stripe.com` and
   `connect-src https://api.stripe.com`.
5. **Load it lazily**, on opening the billing panel, not at app start. It is a third-party script on
   every launch otherwise, including for the overwhelming majority of users who will never buy
   anything.
6. **Every amount is minor units.** Format with the currency from the same payload, never with a
   hardcoded `$`.
