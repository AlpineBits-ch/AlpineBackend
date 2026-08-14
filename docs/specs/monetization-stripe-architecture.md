# Stripe integration architecture (wave 6)

Status: design, 2026-08-14. Supersedes the one-line summaries in
`monetization-implementation-plan.md` §10 wherever the two differ, and records two deliberate
departures from that section - see §12.

This document exists because wave 6 is the first wave where a mistake costs money rather than
capacity, and because three of its decisions (who owns the price, what a subscription writes, and
what a webhook is allowed to trust) are the kind that are cheap now and very expensive to reverse
once real customers exist.

---

## 1. The one-sentence shape

Billing owns the price and mirrors it into Stripe; Stripe owns the money and reports back by
webhook; a webhook never applies a delta, it re-reads the live object and reconciles; and the end
result of all of it is a row in `PlanAssignment`, which is the only thing the entitlement resolver
has ever needed to look at.

---

## 2. Who owns the price

**Billing owns it. Stripe mirrors it.**

The alternative - creating Products and Prices by hand in the Stripe dashboard and referencing them
by id - was rejected outright. It breaks the one property the whole plan catalogue was built for:
`monetization-pricing-model.md` §8 says every number must be editable without a deploy, and a
hand-made Stripe Price makes "edit the Pro tier in the admin console" a change that silently fails
to reach the thing that actually charges the card.

The mapping is exact and it is not a coincidence:

| Billing | Stripe | Shared property |
|---|---|---|
| `Plan` | `Product` | long-lived, renameable, archivable |
| `PlanVersion` | `Price` | **immutable once created** |
| new version on edit | new price on edit | the old one keeps working for whoever is on it |

`PlanVersion` was already immutable-by-construction because that is how grandfathering works. Stripe
`Price` objects are immutable for the same reason. So the sync is not a translation layer, it is the
same idea expressed twice, and "a subscriber stays on the version they bought" is enforced on both
sides by the same mechanism.

### Fields this adds

- `Plan.StripeProductId` - nullable. Null means never synced, which is the normal state for a plan
  that is not sold and the correct state on a self-hosted instance.
- `PlanVersion.StripePriceId` - nullable, same.

### When the sync runs

On publishing a `PlanVersion` that has a `PriceMinorUnits`, and never as a background reconcile
loop. A loop that creates Stripe objects is a loop that can create ten thousand of them, and the
blast radius of the plan console is small enough to make the synchronous path honest: if Stripe is
down, publishing the version fails with a message that says so, rather than succeeding into a state
where the console shows a price nobody can buy.

Every write to Stripe carries a deterministic idempotency key -
`venta:price:{planId}:v{versionNumber}:{currency}:{interval}` and
`venta:product:{planId}` - so a retried publish is a no-op rather than a duplicate. This is not
optional and it is not only for the price: **every** Stripe write in this integration carries one.

### Interval

Monthly only in this wave. `PriceMinorUnits` means the monthly price and the Stripe Price is created
with `recurring.interval = month`. Annual billing is additive when it arrives - a second nullable
amount and a second nullable price id - and deliberately not built now on a catalogue nobody has
subscribed to yet.

---

## 3. What a subscription writes

**A subscription writes a `PlanAssignment`. It is not a new `IEntitlementSource`.**

This is a departure from the plan's WP-16 line, and the domain had already made the call - the class
comment on `PlanAssignment` says, in as many words, that a subscription points at one of these
rather than replacing it. Two reasons to keep it that way:

1. The resolver already reads plan assignments, and WP-11c just finished distributing them to every
   enforcing service with a cache, a revision key and a fail-open story. A parallel source would
   need all of that again, and would have to be reconciled against the first one every time they
   disagreed.
2. "Which numbers apply to this guild" and "who is paying and by what means" are genuinely different
   questions. A guild can be on Pro with no payment behind it at all - an onboarded customer, a
   migrated instance, a promotion, a staff grant. Collapsing the two would make every one of those
   cases a special case.

So: the subscription is the **reason**, the assignment is the **state**. On activation, upsert the
assignment with `AssignedBy = "stripe:{subscriptionId}"` and a reason naming the event. On the end
of the paid relationship, assign the instance's default free plan **explicitly** rather than
deleting the row, so the provenance screen can say "moved to Free because the subscription ended on
the 3rd" instead of showing an absence.

---

## 4. Entities

### `StripeCustomer`

One row per paying account. `UserId` (unique), `StripeCustomerId` (unique), `CreatedAt`. The customer
is the **payer**, always a user account, never a guild - a guild does not have a card, a person does.

### `Subscription`

The commercial record of one recurring relationship.

- `StripeSubscriptionId` - unique.
- `PayerUserId` - who is being charged.
- `SubjectKind` / `SubjectId` - what it pays for. A guild for a guild plan; the payer themselves for
  Venta Plus. These are the same opaque cross-service ids the rest of Billing uses, and they carry
  no foreign key for the same reason `Grant.SubjectId` does not.
- `PlanId` / `VersionNumber` - the pinned version, resolved from the Stripe price id.
- `Status` - mirrored from Stripe, not invented here.
- `CurrentPeriodEnd`, `CancelAtPeriodEnd`, `LatestInvoiceId`.
- `GracePeriodEndsAt` - nullable, §6.
- `LastEventAt` - the `created` of the most recent Stripe event applied, for observability. It is
  **not** used to order anything; see §5.

**A unique index over `(SubjectKind, SubjectId)` filtered to live statuses.** A guild with two active
subscriptions is a double charge, and the place to make that impossible is the database rather than
the code path that happens to be in front of it today.

### `ProcessedStripeEvent`

`EventId` as the primary key, plus type and timestamps. Insert first, process second: the unique
violation *is* the duplicate check, which makes it correct under concurrent delivery of the same
event to two replicas rather than merely usually correct.

### Both directions of identity

Our ids go into Stripe `metadata` on the customer, the subscription and the price
(`venta_subject_kind`, `venta_subject_id`, `venta_plan`, `venta_plan_version`, `venta_payer_user_id`).
Stripe's ids come back onto our rows. A webhook that cannot identify its subject is an incident at
2am, and the cost of preventing it is a dictionary literal.

---

## 5. Webhooks: reconcile against live state, never apply a delta

Stripe delivery is **unordered and at-least-once**. The classic production bug in this integration
is an out-of-order `customer.subscription.updated` carrying a stale status that downgrades a customer
who has already paid.

The rule here is stronger than "be idempotent":

> On any relevant event, **ignore the object in the payload** and `GET` the subscription fresh from
> the Stripe API, then reconcile local state to whatever it says.

That is order-independent by construction rather than by argument. Two events delivered backwards
both read the same live object and both converge on the same answer. The cost is one API call per
event, on a volume that is measured in events per hour, and it buys away an entire class of bug that
is otherwise only found by a customer complaining.

The payload is used for exactly one thing: identifying **which** subscription to go and read.

### Events subscribed

- `customer.subscription.created` / `.updated` / `.deleted`
- `invoice.paid`
- `invoice.payment_failed`
- `charge.dispute.created`

### Transport

The gateway already proxies `/api/v1/billing/{**catch-all}` to Billing (`ProxyConfig`, gated on
`IsHosted`), so the endpoint is a plain `AllowAnonymous` route inside Billing at
`/api/v1/billing/stripe/webhook` and the raw body arrives unmodified. Two things must hold and both
must be verified rather than assumed:

- the route is **not** rate-limited by the gateway, because a 429 to Stripe is a retry storm;
- the endpoint reads the **raw request body**, since the signature covers the exact bytes and any
  JSON round-trip through model binding invalidates it.

The webhook secret lives only in Billing. The gateway does not verify and does not need to.

Public URL: `https://api.venta.gg/api/v1/billing/stripe/webhook`.

---

## 6. Dunning

A failed payment must not take the tier away the same evening. `invoice.payment_failed` sets
`GracePeriodEndsAt = now + 7 days` (configurable) and changes nothing else - the assignment stands.

Downgrade happens when, and only when:

- Stripe status is not one of `active`, `trialing`, `past_due`; **or**
- status is `past_due` and the grace period has elapsed.

`invoice.paid` clears the grace period. A sweeper modelled on `GrantExpirySweeper` performs the
elapsed-grace downgrades, since nothing arrives from Stripe at the moment a grace period ends.

`charge.dispute.created` is **not** an automatic downgrade. It raises an alert and is a human
decision, because the failure mode of automating it is cancelling a paying customer over a card
their bank flagged in error.

---

## 7. Checkout, and what "our own UI" means here

Every screen, every layout, every string and the entire flow are ours, in the Alpine client. What is
**not** ours is the field the card number is typed into: that is a Stripe Elements iframe, served by
Stripe, and the PAN never enters our DOM, our memory or our logs.

This is not a shortcut and it is not negotiable. A raw card input on our own origin moves this
product from PCI SAQ-A to SAQ-D - a different compliance regime, an annual audit, and liability for
a breach we would have designed in on purpose. Stripe Elements is the custom-UI option; Stripe
Checkout is the redirect-to-Stripe option we are correctly not using.

### Flow

The exact shapes are in `billing-checkout-api-contract.md`, **which is authoritative wherever this
summary and that document differ.** This section is the reasoning; that one is the interface.

1. `GET /api/v1/billing/catalogue` - authenticated. Whether anything is for sale, and the sellable
   plans with price, currency, interval, subject kind, and the entitlement values needed to render a
   comparison.
2. `POST /api/v1/billing/subscriptions` `{ planName, subjectKind, subjectId }` - Billing ensures a
   customer, creates the subscription with `payment_behavior: default_incomplete`, and returns the
   subscription plus a `clientSecret`. It does **not** return the publishable key: that already
   reaches the client on the entitlement snapshot, and a third source for one value is how sources
   start disagreeing.
3. The client mounts the Payment Element against the client secret and confirms.
4. The webhook activates the subscription and writes the assignment.
5. The client polls the subscription until it is active. Polling rather than a realtime push because
   the push would be a nice-to-have on top of a poll that has to exist anyway for the reload case.

**The client is never the source of truth for activation.** A successful `confirmPayment` on the
client means the card worked, not that the subscription is live - only the webhook decides that. Any
code path that grants entitlement because the browser said the payment succeeded is a bug, and it is
the one an attacker looks for first.

### Which expansion returns the client secret

Stripe moved this: `latest_invoice.payment_intent` on older API versions,
`latest_invoice.confirmation_secret` from 2025-03-31 onwards. The correct one depends on the pinned
Stripe.net version and the account's API version, and both must be **checked against the installed
SDK** rather than guessed - a wrong guess here fails at runtime with a null reference on the happy
path.

### Management surfaces, all ours

Cancel (at period end), resume, change plan (with an upcoming-invoice proration preview), change
payment method, list invoices. The Stripe-hosted customer portal is configured but linked only from
the support console as an escape hatch, never from the app.

---

## 8. Payment methods

Adding a card outside a purchase uses a SetupIntent and the same Payment Element. Listing returns
brand, last four and expiry - the only card data we ever hold, all of it non-sensitive by design.
Setting a default and detaching are single Stripe calls.

The last remaining payment method on a customer with a live subscription is refused, with a message
that says to cancel the subscription or add another card first. Stripe would otherwise accept the
detach and fail the next invoice, which is a support ticket rather than an error.

---

## 9. Content security policy

Stripe.js must be loaded from `https://js.stripe.com` - self-hosting it is explicitly unsupported by
Stripe and breaks 3DS. The Alpine client runs in a Tauri webview as well as a browser, so **both**
CSPs need `script-src https://js.stripe.com`, `frame-src https://js.stripe.com https://hooks.stripe.com`
and `connect-src https://api.stripe.com`. The Tauri one is the one that gets forgotten, and its
failure mode is a card form that is simply blank.

---

## 10. Self-hosting

Every Stripe surface is gated on `IsHosted` **and** a configured secret key, and the second condition
matters independently: a hosted instance that has not finished setting Stripe up should render the
plan panel exactly as a self-hosted one does rather than showing a buy button that 500s.

Nothing in this wave changes what a self-hosted instance resolves. `SelfHostEverythingSource` still
short-circuits above all of it.

### Sandbox credentials as compiled-in fallbacks

The three Stripe values fall back to the **AlpineBits KLG sandbox** test credentials when their
environment variables are unset, so that a deployment works without a Helm change and production
values are a pure override.

This is a deliberate trade and it has a sharp edge: if a live key is ever meant to be set and the
variable is missing, the service does not fail, it quietly transacts against the sandbox. A missing
publishable key would mean cards tokenised against a sandbox and payments that appear to do nothing.
So the fallback is paired with a **startup warning that fires whenever a test-mode credential is in
use on a `hosted` instance**, naming which one. The warning is the whole reason the fallback is
acceptable; do not remove it and leave the defaults.

Test-mode is detected from the value itself (`pk_test_` / `sk_test_` prefixes), not from a separate
flag, so the warning cannot disagree with reality.

### Operator setup already done in the sandbox

- Webhook destination `we_1U4SLw2c7cgnhryPUqDJS3rG`, active, pointing at
  `https://api.venta.gg/api/v1/billing/stripe/webhook`, API version `2026-04-22.dahlia`, subscribed
  to exactly the six events in §5.
- No products and no prices exist, and none should be created by hand. §2.

`2026-04-22.dahlia` is well past the 2025-03-31 cutover, which settles the open question in §7: the
client secret comes from `latest_invoice.confirmation_secret`, not `latest_invoice.payment_intent`.

---

## 11. Tax and invoices

`automatic_tax.enabled = true` on subscriptions, which requires a customer address - collected by the
Address Element on the same screen as the card, because a second screen after payment is a screen
people abandon. VAT id goes to `customer.tax_ids` for reverse charge.

Invoices are **not** rendered by us. `hosted_invoice_url` and `invoice_pdf` come off the Stripe
invoice and are linked. Rendering a compliant invoice per jurisdiction is a product in its own right
and Stripe already ships it.

Dashboard prerequisites (Stripe Tax registration and an origin address) are operator setup, not code.

---

## 12. Where this departs from the implementation plan

1. **WP-16** is "reconcile subscription state into a plan assignment", not "subscription as an
   `IEntitlementSource`". §3.
2. **WP-17** puts checkout in the Alpine client rather than in a new gateway-served site. The user
   asked for it in the app, and the app is where someone who has just hit a limit already is. The
   `SiteHost` pattern stays available if a web-only checkout is ever wanted.
3. Plan and price objects in Stripe are **created by code from the catalogue**, not by hand in the
   dashboard. §2.

---

## 13. What must be verified before this is called done

- A test card buys a subscription end to end and the guild's ceilings actually change.
- A 3DS-required test card (`4000 0025 0000 3155`) completes.
- The same webhook delivered twice changes nothing the second time.
- Two webhooks delivered out of order converge on the correct state.
- A failed invoice holds the tier for the grace period and drops it after.
- Detaching the last payment method under a live subscription is refused.
- A self-hosted instance shows no Stripe surface at all and still resolves everything to unlimited.
