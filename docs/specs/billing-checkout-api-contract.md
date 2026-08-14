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

Timestamps are ISO 8601 UTC with a `Z`.

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
      "subjectKind": "Guild",
      "priceMinorUnits": 2900,
      "currency": "usd",
      "interval": "month",
      "purchasable": true,
      "entitlements": [
        { "key": "voice.max_participants", "kind": "Numeric", "value": "75" },
        { "key": "voice.video_ceiling",    "kind": "Ladder",  "value": "2160p60" },
        { "key": "guild.vanity_url",       "kind": "Flag",    "value": "true" }
      ]
    }
  ]
}
```

`enabled` is answered by the service that actually holds the secret key, which is the only place
that can answer it honestly. It is false when `STRIPE_SECRET_KEY` is unset, and in that case `plans`
is still populated so the comparison table renders - the buy buttons are simply absent.

A self-hosted instance never reaches this endpoint at all: Billing refuses to start under
`LICENSE_MODE=selfhost` and the gateway filters the `/api/v1/billing` route out entirely, so the
client's existing `licenseMode` check is what suppresses the surface there.

- Archived plans and archived versions are absent.
- A plan with no `priceMinorUnits` is present with `purchasable: false` and a null price - that is
  how `free` and `free_user` appear, and the comparison table needs them.
- `subjectKind` is `Guild` or `User`, matching `EntitlementSubject`. `free`/`plus`/`pro` are guild
  plans; `free_user`/`venta_plus` are user plans.
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
{ "planName": "pro", "subjectKind": "Guild", "subjectId": "gld_..." }
```

Response `200`:

```json
{
  "subscription": { "...": "SubscriptionDto, see 3" },
  "clientSecret": "pi_..._secret_...",
  "requiresAction": true
}
```

`clientSecret` may be **null**, which means Stripe had nothing to confirm. The client must treat null
as "go straight to polling" rather than as an error.

**The client is not the source of truth for activation.** A successful `confirmPayment` means the
card worked; only the webhook makes the subscription live. After confirming, poll §3 until `status`
is `active`, then refresh the entitlement snapshot.

Errors, as `application/problem+json` with a machine-readable `code` in `extensions`:

| `code` | HTTP | Meaning for the client |
|---|---|---|
| `billing_disabled` | 404 | Stripe is not configured. Unreachable if §1's `enabled` was honoured. |
| `not_purchasable` | 400 | The plan exists but is not sold. |
| `already_subscribed` | 409 | This subject already has a live subscription. Offer "change plan" instead. |
| `not_permitted` | 403 | The caller lacks `ManageGuild`. |
| `stripe_error` | 502 | Show the message; it is safe to display and already customer-worded. |

---

## 3. `GET /api/v1/billing/subscriptions` and `/api/v1/billing/subscriptions/{id}`

Authenticated. The list returns subscriptions the caller **pays for**, plus those on guilds where
they hold `ManageGuild`. Fetching one by id requires being the payer or holding `ManageGuild` on its
subject.

`SubscriptionDto`:

```json
{
  "id": "sub_...",
  "subjectKind": "Guild",
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
  when it is the only one and a live subscription depends on it.

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
