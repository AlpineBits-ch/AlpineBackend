# Household modules - frontend integration guide

Audience: web/desktop/mobile client engineers.

Everything a shared household needs that a chat server doesn't: the shopping list, the chore rota,
the shared-expense ledger, the bills that are due before anybody has paid them, the pantry, the
week's meals, the boiler, house decisions, who's home, who's away, quiet hours, and time-boxed guest
access.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call a
microservice directly. Guild endpoints are reached under the `/api/v1/guild/` prefix; the gateway
strips the `guild` segment before forwarding, which is why the paths read
`/api/v1/guild/channels/{channelId}/...`. That doubled-looking segment is correct.

**Prerequisite:** every endpoint here is gated on a `GuildFeatures` module. Read
[guild-kind-and-features-frontend-guide.md](./guild-kind-and-features-frontend-guide.md) first -
it explains `kind`, `features`, and the wire format (`features` is a **comma-separated name
string**, not a bitmask). Nothing below works in a guild whose module is off.

---

## 1. The shape of it

Seven of the ten modules are **channel types**. A household guild's sidebar contains channels
whose contents are structured rows instead of messages:

| `channel.type` | Module | Holds |
|---|---|---|
| `List` | `Lists` | Shopping / todo items |
| `Chores` | `Chores` | Chore definitions and their generated occurrences |
| `Ledger` | `Ledger` | Expenses, shares, settlements, bills |
| `Pantry` | `Pantry` | Stock for one location (fridge, freezer, cellar) |
| `Decisions` | `Decisions` | House decisions and votes |
| `Meals` | `Meals` | Recipes and the week's meal plan |
| `Maintenance` | `Maintenance` | Appliances, service history, who to call |

They come back from the normal channel endpoints alongside `Text` and `Voice`, so your sidebar
already receives them - **it just needs to know not to open a message composer for them**. That's
the single biggest integration point: a `List` channel has no message history and no
`POST /messages`.

The other three are guild-scoped, not channels: **home status and absence**, **quiet hours** and
**guest access** (§14-§16).

### New channel types are additive

`ChannelType` gained `List`, `Chores`, `Ledger`, `Pantry`, `Decisions` at the end of the enum, and
then `Meals` and `Maintenance` after those. A client that doesn't recognise a type should render it
as an inert placeholder rather than assuming `Text` - that's the failure mode that produces a
composer posting into a shopping list. It is also why the two newest values were appended rather
than slotted in beside their relatives: Npgsql maps this enum by name, and appending is the only
addition Postgres can make to an existing enum type without a rewrite.

### Creating them

Ordinary `POST /api/v1/guild/guilds/{guildId}/channels` with the new `type`. `400` if the guild
doesn't have that module:

```
Channel type 'Ledger' is not enabled for this guild.
```

### Household guilds are seeded

Creating a guild with `kind: "Household"` (see the features guide) provisions a starter tree
instead of the usual Text/Voice pair:

```
Home     # general (Text) · # groceries (List) · # chores (Chores) · # meals (Meals)
House    # pantry (Pantry) · # ledger (Ledger) · # decisions (Decisions) · # upkeep (Maintenance)
Voice    # house (Voice)
```

One channel per module, so nothing is hidden behind a settings tour. `systemChannelId` still points
at `# general`. A starter **house manual** is seeded into the wiki at the same time (§18).

---

## 2. Permissions

Fifteen values, all gated on their module - a Community guild returns `403` for every one of them
regardless of roles, **including for the guild owner**.

| Permission | Module | Allows | In `@everyone` |
|---|---|---|---|
| `AddListItems` | Lists | Add items; edit/delete your own | yes |
| `CheckOffListItems` | Lists | Tick and untick | yes |
| `ManageLists` | Lists | Clear a list, delete anyone's item | Flatmates |
| `CompleteChores` | Chores | Complete, skip, swap, nudge an occurrence | yes |
| `ManageChores` | Chores | Create/edit/delete chores, set effort weights | Flatmates |
| `AddExpenses` | Ledger | Add an expense; edit/delete your own; post a bill you are paying | yes |
| `ManageLedger` | Ledger | Edit anyone's expense, record third-party settlements, own the bill schedules | Flatmates |
| `ManagePantry` | Pantry | Add/edit/delete stock and thresholds, scan, consume, restock | yes |
| `CreateDecisions` | Decisions | Open and close decisions | yes |
| `VoteDecisions` | Decisions | Support / abstain / block | yes |
| `PlanMeals` | Meals | Add a recipe, put something on the plan, edit your own | yes |
| `ManageMeals` | Meals | Edit anyone's recipe or planned meal, point the plan at a list | Flatmates |
| `LogMaintenance` | Maintenance | Mark something broken, log a repair or a service | yes |
| `ManageMaintenance` | Maintenance | Add/edit/delete assets, remove anyone's log entry | Flatmates |
| `ManageGuests` | GuestAccess | Grant and revoke temporary roles | Flatmates |

They resolve **per channel**, so a channel overwrite granting control of one list doesn't grant
every list. Viewing any module's contents needs only `ViewChannel` on that channel.

### `@everyone` holds the participation bits

The nine marked "yes" are part of `Role.DefaultEveryonePermissions` and are back-filled onto every
existing guild. An ordinary member can shop, tick, complete a chore, add an expense they paid, put
things in the pantry, plan Thursday's dinner, say the washing machine is dead, and vote - with no
role setup at all.

They are granted in **every** guild, not only households, because the feature gate does the real
work: in a Community guild all nine are clamped out of the resolved mask, and they light up the
moment somebody enables the module. So `GET /guilds/{id}/@me` in a Community guild will not list
them.

`ManagePantry` is in that set despite the name. There is no separate "add stock" bit, so without it
nobody could put anything in the fridge. `LogMaintenance` is there for the same shape of reason: the
person who discovers the washing machine is dead is whoever tried to use it, and a house where only
a moderator can say so is a house that finds out later, usually by somebody else loading it.

**Two bits joined that set in wave two** - `PlanMeals` and `LogMaintenance` - by data migration
(`20260807164830_BackfillHouseholdWaveTwoEveryonePermissions`). Members can suddenly do things they
could not do yesterday, so **re-fetch `GET /guilds/{id}/@me` after this deploy** rather than trusting
a cached mask.

### The Flatmates role

A guild created with `kind: "Household"` is seeded with a second role, **`Flatmates`**, position 1,
holding the owner. It carries the six manage bits above, and its membership is the **default chore
rotation pool**.

Adding someone to Flatmates is the product's way of saying "this person lives here". It is a
deliberate action and it is the one that distinguishes a flatmate from a guest who joined by
invite - a guest holds `@everyone` and is therefore never assigned the bins, though they can still
read the house manual and tick things off. Surface it in your member UI as something more meaningful
than a role chip.

---

## 3. Lists

```
GET    /api/v1/guild/channels/{channelId}/list-items?includeChecked=false
POST   /api/v1/guild/channels/{channelId}/list-items
PATCH  /api/v1/guild/list-items/{itemId}
POST   /api/v1/guild/list-items/{itemId}/check          DELETE to untick
DELETE /api/v1/guild/list-items/{itemId}
POST   /api/v1/guild/channels/{channelId}/list-items/reorder
DELETE /api/v1/guild/channels/{channelId}/list-items/checked      // "clear done"
```

```ts
interface ListItem {
  id: string;
  channelId: string;
  text: string;
  quantity?: string | null;      // free text - "2", "2 packs", "a bunch"
  note?: string | null;
  section?: string | null;       // free-text grouping, e.g. "Dairy"
  assigneeUserId?: string | null;
  addedByUserId: string;
  isChecked: boolean;
  checkedAt?: string | null;
  checkedByUserId?: string | null;
  position: number;
  sourcePantryItemId?: string | null;   // set when the pantry added this line (§5)
  createdAt: string;
}
```

`quantity` is deliberately a string. Nothing computes on it, and forcing a number+unit pair makes
the common case slower to type.

**Editing and deleting**: your own items need `AddListItems`; someone else's needs `ManageLists`.
Ticking is always `CheckOffListItems` - checking things off is the collaborative part.

Caps: 200 chars per `text`, 500 items per list (`400` beyond).

**Reorder** takes a partial list. Ids you omit keep their relative order *after* the ones you sent,
so a drag-and-drop payload of just the moved neighbourhood is fine.

Lists also receive lines written by two other modules: the pantry's restock loop (§5) and the meal
plan's shopping-list button (§12). Both stamp `sourcePantryItemId` or a recipe-titled `section`
respectively, so you can badge them.

### Realtime

`guild.ListItemCreated` · `guild.ListItemUpdated` · `guild.ListItemChecked` ·
`guild.ListItemDeleted` · `guild.ListItemsReordered` · `guild.ListCleared`

All carry `{ guildId, channelId, ... }`. Apply optimistically then reconcile - the defining use
case is two people in the same shop, and a tick has to strike through on the other phone within
the second or they buy it twice.

Check/uncheck is **idempotent**: ticking an already-ticked item returns `200` with the item
unchanged and emits nothing. Don't treat a repeat as an error.

---

## 4. Chores

```
GET/POST /api/v1/guild/channels/{channelId}/chores
PATCH    /api/v1/guild/chores/{choreId}          DELETE to remove
GET      /api/v1/guild/channels/{channelId}/chores/occurrences?from=&to=
POST     /api/v1/guild/chore-occurrences/{id}/complete    DELETE to un-complete
POST     /api/v1/guild/chore-occurrences/{id}/skip
POST     /api/v1/guild/chore-occurrences/{id}/swap
POST     /api/v1/guild/chore-occurrences/{id}/nudge
GET      /api/v1/guild/channels/{channelId}/chores/balance?days=30
```

```ts
interface Chore {
  id: string; channelId: string;
  title: string; description?: string | null;
  intervalDays: number;          // 1-365
  anchorAt: string;              // the first due date; the cadence steps from here
  effortMinutes: number;         // 1-600 - the fairness weight
  rotationRoleId?: string | null;   // the pool: whoever holds this role
  fixedAssigneeUserId?: string | null;
  graceHours: number;            // before it counts as overdue
  isPaused: boolean;
  nextDueAt: string;
}

interface ChoreOccurrence {
  id: string; choreId: string; channelId: string;
  title: string;                 // denormalized for board rendering
  dueAt: string;
  assignedUserId: string;
  effortMinutes: number;         // snapshot at generation time
  completedAt?: string | null;
  completedByUserId?: string | null;
  skippedAt?: string | null;
  isOverdue: boolean;
  nudgedAt?: string | null;      // see the caveat under "The nudge"
}
```

A chore needs **either** `rotationRoleId` **or** `fixedAssigneeUserId` (`400` otherwise). The
rotation pool is just a role's membership, so adding someone to the rota is giving them the role.

### The rotation is not round-robin

The next occurrence goes to whoever in the pool has completed the **fewest weighted minutes** over
the last 30 days. Worth surfacing in your UI copy, because it's the behaviour people notice: a
plain rota rewards skipping (your turn comes round again regardless), and weighting by
`effortMinutes` stops "take the bins out" counting the same as "clean the bathroom".

`/swap` reassigns to the lightest-loaded *other* member - the one-tap answer to "I can't do the
bins tonight". `400` if nobody else is in the rotation.

### The nudge

```
POST /api/v1/guild/chore-occurrences/{occurrenceId}/nudge     CompleteChores, no body
```

The one-tap answer to the other half of the problem: the bin is full, it is not your turn, and
somebody has to say so. Success is `200`:

```ts
{ occurrenceId: string; nudgedAt: string }
```

`CompleteChores`, deliberately not `ManageChores` - nudging is something flatmates do to each other,
not something a moderator does to a member.

**The nudger is never named.** Not in the alert, not in the realtime event, not in the response,
and your UI must not name them either. The entire value of the feature is that the app does the
asking so that nobody in the house has to become the person who nags; attributing it puts the social
cost straight back where the feature exists to take it from.

`400`, in the order they are checked:

| Condition | Message |
|---|---|
| You are the assignee | `You can't nudge yourself about your own chore` |
| Already completed | `That chore is already done` |
| Already skipped | `That chore was skipped` |
| Not yet past `dueAt + graceHours` | `That chore isn't overdue yet` |

`409`, with a body you should render rather than treat as a failure:

```ts
{ error: "Somebody already nudged about this recently", nextNudgeAt: string }
{ error: "The house is in quiet hours", quietUntil: string }
```

Two things to get right here:

- **The cooldown is 12 hours per occurrence, not per sender.** Four flatmates walking past the same
  full bin is not four nudges; it is a pile-on. The cooldown is checked before quiet hours.
- **Quiet hours *reject* a nudge rather than deferring it.** This is the opposite of `chore.due`
  (§19), which is held until the window ends. A reminder is the server's own timing and can wait; a
  nudge is somebody pressing a button now, and a nudge that silently lands at seven the next morning
  is about a bin that was emptied hours ago. Render `quietUntil` as "you can nudge again after
  07:00", not as an error.

Realtime: `guild.ChoreOccurrenceNudged` carrying `{ guildId, channelId, occurrenceId, nudgedAt }`
and no sender.

**Caveat on `nudgedAt`.** The field exists on `ChoreOccurrence` so you can grey the button rather
than offering an action that will `409`, and the cooldown is per occurrence so the answer is the
same for every member looking at it. But today it is only populated on the nudge response and on
`guild.ChoreOccurrenceNudged`; the occurrence list, complete, skip and swap responses and
`guild.ChoreOccurrenceUpdated` all serialize it as `null` regardless of the stored value. Treat a
`null` from those surfaces as "unknown", not as "nobody has nudged", and be ready for a `409` you
did not predict. This is a server-side gap, not a contract you should design around permanently.

### Balance

```ts
interface ChoreBalanceEntry {
  userId: string;
  completedMinutes: number;
  completedCount: number;
  balanceMinutes: number;   // relative to your share of the window - negative = behind
  presentDays: number;      // how much of the window you were actually here for
}
```

`days` defaults to 30 and is clamped to 1-365 rather than rejected. The array is ordered by
`completedMinutes` ascending, so the lightest-loaded member is first, and an empty rotation pool is
`200` with `[]`.

**The balance is now weighted by presence.** Your expected share is the house's total completed
minutes times your fraction of everybody's present days, and `balanceMinutes` is what you actually
did minus that. Somebody who was in Lisbon for a fortnight is no longer reported as being behind
for the fortnight they were not here.

`presentDays` is on the row precisely so the board can *explain* the number instead of only showing
it. "Behind by 40 minutes" reads as an accusation; "behind by 40 minutes over the 16 days you were
here" reads as arithmetic, and arithmetic is what people accept. Render it.

Absences (§15) are what move `presentDays`. With the `Presence` module off, or with nobody having
declared anything, every member's `presentDays` equals the whole window and the arithmetic collapses
to exactly the flat average it was before - so nothing changes for a house that does not use the
feature.

### Two behaviours to render correctly

- **Skipping still does not credit the balance.** A skipped chore stays unpaid work, which is
  exactly what makes the rotation land back on the same person. Presence weighting did not change
  this: being away excuses you from your *share*, and skipping does not. Don't show a skip as done.
- **`completedByUserId` may differ from `assignedUserId`**, and the balance credits the
  *assignee*. That's deliberate - crediting the doer would let one flatmate farm the ledger by
  doing everyone's easy chores. Show both ("Ben did Anna's washing-up").

Occurrences are generated by the server (on creation, then on schedule, with a 5-minute reconcile
sweep as a backstop). Clients never create them.

A chore whose `anchorAt` is months in the past generates **one** occurrence for the current period,
not one per missed slot. Don't build UI that expects historic occurrences to be backfilled.

Realtime: `guild.ChoreCreated` · `guild.ChoreUpdated` · `guild.ChoreDeleted` ·
`guild.ChoreOccurrenceCreated` · `guild.ChoreOccurrenceUpdated` · `guild.ChoreOccurrenceNudged`

**`guild.ChoreOccurrenceUpdated` always carries `{ guildId, channelId, occurrence }`.** Skipping
used to send `{ occurrenceId, skipped: true }` instead; it no longer does. `POST /skip` also
returns the updated occurrence rather than an empty `200`.

### Reminders

The assignee is notified when an occurrence falls due - realtime **and** push, so a closed app
still gets it. This arrives as a **household alert** (§19), with `kind: "chore.due"` and
`targetId` set to the occurrence id.

Sent to the assignee only, at most once per occurrence.

Two behaviours to expect:

- **Quiet hours apply.** A reminder that would fire inside the guild's window is held until the
  window ends (§16). Nothing is emitted in the meantime - don't show a pending state.
- **Nothing arrives for a chore more than 12 hours overdue.** It is just overdue at that point, and
  the board already says so. This is also what stops a guild returning from an outage buzzing
  everyone at once.

A member who has muted the guild still receives the realtime event and gets no push.

---

## 5. Pantry

```
GET/POST /api/v1/guild/channels/{channelId}/pantry-items
PATCH    /api/v1/guild/pantry-items/{itemId}      DELETE to remove
GET      /api/v1/guild/guilds/{guildId}/pantry/expiring?days=3
GET/PUT  /api/v1/guild/channels/{channelId}/pantry/config
POST     /api/v1/guild/channels/{channelId}/pantry-items/scan
POST     /api/v1/guild/pantry-items/{itemId}/consume
POST     /api/v1/guild/pantry-items/{itemId}/restock
GET      /api/v1/guild/guilds/{guildId}/pantry/barcodes?q=
```

```ts
interface PantryItem {
  id: string; channelId: string;
  name: string;
  quantity: number;              // decimal here - it's compared against the threshold
  unit?: string | null;
  lowThreshold?: number | null;  // null = restock tracking off for this item
  expiresAt?: string | null;
  isLow: boolean;
  restockedAt?: string | null;   // set while it's sitting on the shopping list
  addedByUserId: string;
  barcode?: string | null;       // null for anything entered by hand
}

interface PantryConfig {
  channelId: string;
  restockListChannelId?: string | null;   // must be a List channel in this guild
  expiryWarningDays: number;              // 1-90
}
```

`barcode` is accepted on create and on patch (`clearBarcode: true` wins over any `barcode` sent in
the same request), capped at 64 characters. Setting one by hand does **not** teach the guild's
barcode table - only a scan does.

### The restock loop

When `quantity` drops to or below `lowThreshold`, the server **appends the item to the configured
restock list** and stamps `restockedAt`. The created `ListItem` carries `sourcePantryItemId` -
badge it ("added by the pantry") so people know why it appeared.

`restockedAt` is the idempotency guard. It's released when:
- the quantity climbs back above the threshold, **or**
- the list line is deleted or cleared as bought.

So the same item won't be added twice while it's already on the list, and buying it re-arms the
loop for next time. If `restockListChannelId` is null the whole loop is off, whatever individual
thresholds say.

### Capture: scan, consume, restock

The reason these exist: maintaining a pantry by hand is itself a chore, and houses abandon it inside
two weeks. **Optimise these three screens for keystrokes, not for features.** Every one of them
needs `ManagePantry`.

```
POST /channels/{channelId}/pantry-items/scan
  { barcode, quantity?, name?, unit?, expiresAt? }
  → { item: PantryItem, created: boolean, learned: boolean }
```

**There is no third-party barcode lookup and there must not be one.** The guild learns its own
products: the first scan of an unknown code asks for a name, and every scan after that autofills
from what the house itself recorded. That is the whole design, and it is why a scan of a code nobody
has seen before is `400 Name is required the first time a barcode is scanned here` rather than a
lookup against somebody's product database.

- `created: false` means an existing item on that channel carried the code and was topped up rather
  than duplicated (oldest row wins).
- **`learned: true` is the one moment worth prompting.** It fires once per code per guild, when the
  house has just taught itself a product. Confirm the name there and be silent every other time.
- Quantity added is `quantity ?? the learned default ?? 1`.
- Sending a changed `expiresAt` releases the expiry stamp, so a corrected date warns again.

Other `400`s: `Barcode is required`, `Barcode must be 64 characters or fewer`,
`Quantity must be greater than zero`, `Name must be 100 characters or fewer`.

```
POST /pantry-items/{itemId}/consume   { amount?, all? }    → PantryItem
POST /pantry-items/{itemId}/restock   { amount? }          → PantryItem
```

`amount` is **optional and defaults to 1**; only a value at or below zero is rejected
(`Amount must be greater than zero`). `all: true` takes the quantity to zero.

Consume is the one-tap "used it up" and it runs the existing low-stock and restock loop, so the same
alerts fire from it as from a hand edit. Restock also ticks off the shopping-list line the pantry
created, which is what closes the loop without anybody going back to the list.

```
GET /guilds/{guildId}/pantry/barcodes?q=
  → PantryBarcode[]   // bare array, at most 50, no paging
```

```ts
interface PantryBarcode {
  barcode: string; name: string; unit?: string | null;
  defaultQuantity: number; lowThreshold?: number | null;
  timesSeen: number; lastUsedAt: string;
}
```

Guild-scoped and needs only membership plus the `Pantry` module - a learned product is not the
property of one fridge. `q` matches the barcode by **prefix** and the name by **contains**, both
case-insensitively, ordered by `timesSeen` then recency.

### Expiring

`/pantry/expiring` spans **every pantry in the guild the caller can see**, not one channel -
"what needs eating" is a question about the house. Results are filtered per-channel by
`ViewChannel`, so a guest with access to one pantry can't enumerate a private one.

**`days` is optional and per-pantry.** Omit it and each pantry uses its own `expiryWarningDays`, so
a freezer set to 14 days and a fridge set to 2 both behave correctly in one response. Pass `days`
only to override every pantry at once (for a "what goes off this month" view). This previously
ignored the config entirely and used a flat three days for everything.

### The expiry sweep

A background sweep warns each pantry about stock inside **its own** `expiryWarningDays`, without
anyone opening the board. This arrives as a household alert (§19) with `kind: "pantry.expiring"` and
`targetId` set to the **pantry channel id**.

- **One alert per pantry, not per item.** The body reads "Milk, Yoghurt and 2 more are about to go
  off"; the full list is in `data.items`.
- **At most once per item.** Editing `expiresAt` releases the stamp, so a corrected date warns
  again on the new date. Clearing it stops the warning entirely.
- **Nothing arrives for something more than 7 days past its date.** That is compost, not news, and
  it is what stops a guild returning from an outage announcing a year of leftovers.

Realtime: `guild.PantryItemCreated` · `guild.PantryItemUpdated` · `guild.PantryItemDeleted`.
An automatic restock also emits `guild.ListItemCreated` on the **list** channel. There is
deliberately no `guild.PantryItemScanned`: a scan is a create or an update, and inventing a third
event would mean every client had to handle all three to stay in sync.

---

## 6. Ledger

```
GET      /api/v1/guild/channels/{channelId}/expenses?limit=50&cursor=&category=
POST     /api/v1/guild/channels/{channelId}/expenses
PATCH    /api/v1/guild/expenses/{expenseId}      DELETE to remove
GET      /api/v1/guild/channels/{channelId}/ledger/balances
GET      /api/v1/guild/channels/{channelId}/ledger/settle-suggestion
POST     /api/v1/guild/channels/{channelId}/ledger/settlements
GET/PUT  /api/v1/guild/channels/{channelId}/ledger/config
```

### Money is integer minor units. Always.

`amountMinor` is a whole number of rappen/cents. **Never send `12.34`** - send `1234`. Every split
and balance is integer arithmetic, which is what guarantees shares sum to the total and balances
sum to exactly zero. Format for display client-side using the channel's `currency`.

One currency per ledger channel (`ledger/config`, ISO-4217). Changing it relabels; it does not
convert existing amounts - worth a confirmation dialog.

```ts
interface Expense {
  id: string; channelId: string;
  payerUserId: string;           // who actually paid
  description: string;
  amountMinor: number;
  currency: string;
  occurredAt: string;
  splitKind: 'Equal' | 'Shares' | 'Exact';
  category: ExpenseCategory;     // never null; defaults to 'Uncategorized'
  createdByUserId: string;       // who entered it - often not the payer
  shares: { userId: string; shareValue: number; amountMinor: number }[];
}

type ExpenseCategory =
  | 'Uncategorized' | 'Groceries' | 'Rent' | 'Utilities' | 'Internet' | 'Household'
  | 'Transport' | 'EatingOut' | 'Entertainment' | 'Health' | 'Pets' | 'Repairs' | 'Other';
```

| `splitKind` | `shareValue` means | Notes |
|---|---|---|
| `Equal` | ignored | **Empty `shares` = everyone in the guild.** The common case (rent, internet) |
| `Shares` | a weight | "Anna counts double, she has the big room" |
| `Exact` | that person's exact `amountMinor` | Must sum to the total, else `400` |

Remainders are distributed server-side, deterministically: 1000 across 3 is 334/333/333. Never
compute shares client-side and send them as `Exact` - you'll disagree with the server on rounding.

### Categories

`category` is accepted on create and on patch, and filters the listing via `?category=`. It is
**coarse on purpose**: the question it answers is "what does this flat cost per month, roughly", and
a taxonomy fine enough to argue about is a taxonomy nobody fills in.

`Uncategorized` is the zero value, so every expense that predates this migrates into it. There is no
"clear" flag on the patch, because `Uncategorized` is itself a value you can send. An unknown value
is `400 Unknown category` on all three routes rather than being stored and silently dropped by the
rollup later.

### Balances and settling

```ts
interface LedgerBalance { userId: string; netMinor: number }   // + = the house owes them
interface TransferSuggestion { fromUserId: string; toUserId: string; amountMinor: number }
```

Balances always sum to zero and members at zero are omitted - an empty array means the house is
settled. `settle-suggestion` returns at most n−1 transfers (four flatmates settle with two
payments, not six). Recording a settlement doesn't move money; it records that someone paid.

`settle-suggestion` deliberately carries **no payment handles**. Those are end-to-end encrypted and
the server cannot read them, so it is your client that joins the suggestion to the blobs it has
decrypted itself (§10).

### Listing is paged

`GET /expenses` returns:

```ts
{ items: Expense[]; nextCursor: string | null }
```

`limit` defaults to 50, caps at 200. Pass `nextCursor` back as `cursor` for the next page; `null`
means you have reached the end. A malformed cursor is a `400`, not a silent first page. The
`category` filter is applied before the keyset predicate, so a cursor issued while filtered stays
meaningful.

The old shape was a hard `Take(200)` with no way to ask for more, so any ledger past 200 expenses
was quietly truncated.

**Permissions:** adding an expense you paid needs `AddExpenses`; recording one on someone else's
behalf, or editing someone else's, needs `ManageLedger`. Same for settlements: your own, or
`ManageLedger` for a third-party one.

Two things that `400` where they used to be accepted:

- A `payerUserId`, or either party to a settlement, who is **not a member of the guild**. A
  non-member in the money graph is a balance nobody can ever clear.
- Reassigning `payerUserId` to someone else via `PATCH` without `ManageLedger`. Create already
  required it; the patch path did not, so create-then-patch walked around the check.

Every expense and settlement mutation, and every currency change, is written to the guild audit
log.

Realtime: `guild.ExpenseCreated` · `guild.ExpenseUpdated` · `guild.ExpenseDeleted` ·
`guild.SettlementRecorded`. Re-fetch balances after any of them.

---

## 7. Bills - an obligation before it is an expense

```
GET    /api/v1/guild/channels/{channelId}/recurring-expenses      ViewChannel
POST   /api/v1/guild/channels/{channelId}/recurring-expenses      ManageLedger
PATCH  /api/v1/guild/recurring-expenses/{templateId}              ManageLedger
DELETE /api/v1/guild/recurring-expenses/{templateId}              ManageLedger
GET    /api/v1/guild/channels/{channelId}/bills?status=&from=&to= ViewChannel
POST   /api/v1/guild/bills/{billId}/post   { amountMinor?, occurredAt? }
POST   /api/v1/guild/bills/{billId}/skip   { reason? }            ManageLedger
```

**This is the point of the module, and it should drive your UI.** The ledger records money already
spent. A bill is the other half: an obligation before it is an expense. "Rent is due Friday and you
owe 850" is a different sentence from "Anna paid rent, you owe her 850", and until this existed the
ledger could only say the second - so somebody re-typed rent and its split every month and then
chased people by hand. Render upcoming bills as something owed to the future, not as ledger history,
and keep them out of the expense list.

```ts
interface RecurringExpense {
  id: string; channelId: string;
  description: string;
  amountMinor?: number | null;   // null = the amount varies, and each period waits for a figure
  currency: string;
  payerUserId: string;
  splitKind: 'Equal' | 'Shares' | 'Exact';
  category: ExpenseCategory;
  recurrenceUnit: 'Day' | 'Week' | 'Month' | 'Year';
  recurrenceInterval: number;
  anchorAt: string;
  nextDueAt: string;
  leadDays: number;              // 0-30, how far ahead it is generated and announced
  autoPost: boolean;
  isPaused: boolean;
  createdByUserId: string;
  shares: { userId: string; shareValue: number }[];
}

interface BillOccurrence {
  id: string; recurringExpenseId: string; channelId: string;
  description: string;
  dueAt: string;
  amountMinor?: number | null;
  currency: string;
  status: 'Pending' | 'Posted' | 'Skipped';
  expenseId?: string | null;     // set once posted
  postedByUserId?: string | null;
  skippedByUserId?: string | null;
  skipReason?: string | null;
  needsAmount: boolean;          // pending, and nobody has said what it cost
  isOverdue: boolean;            // pending and past dueAt
}
```

### The cadence is calendar-aware

`recurrenceUnit` is not a day count, and that is the whole reason it exists. Rent is due on the
first of the month, not every thirty days; stepping in calendar months keeps a bill anchored to its
day of the month for good, where a day count drifts a day earlier every February. The 31st clamps to
the 30th or the 28th, which is also what a landlord does.

### Rules to render correctly

- **`autoPost` is only legal with a fixed `amountMinor`.** Auto-posting a bill nobody has priced
  would have to invent a figure or post a zero, and both silently corrupt balances people settle in
  their bank accounts. `400 AutoPost needs a fixed AmountMinor - a bill whose amount varies has to
  be confirmed each period`.
- **A variable bill cannot be posted without an amount.** That is what `needsAmount` is for: show a
  "what was it?" field, not a "post" button that cannot work.
- **Only an `Equal` split may leave `shares` empty**, and an empty `Equal` split means everyone in
  the guild *resolved at post time*, so somebody who moves in next month is included without anybody
  editing the schedule. `400 A Shares or Exact split has to name its participants` otherwise.
- **Editing a schedule moves pending bills rather than regenerating them.** An amount somebody typed
  off a paper letter is not lost to a cadence correction, and posted or skipped bills never move -
  they are history. Where a new cadence maps two old bills onto one slot, the duplicate is dropped.
- **Deleting a template deletes the bills it has not posted, and nothing else.** The expenses it
  already produced survive; cancelling a standing order must not delete the twelve months of rent
  everybody has already settled against.
- **Skipping is not deleting.** "The flat was empty in August" and "we do not pay this any more" are
  different statements and a house needs both. `400` if the bill is already posted - delete the
  expense instead.

### Posting

`POST /bills/{id}/post` needs `ManageLedger`, **or `AddExpenses` when the caller is the payer**.
Confirming what you yourself just paid the landlord is the ordinary case, and requiring a moderator
for it would mean the one person who knows the figure cannot enter it.

It returns the updated `BillOccurrence`, not the expense - follow `expenseId` if you need the ledger
row. **It is idempotent**: a repeat post returns the bill the first one produced, writes no second
audit entry, broadcasts nothing and above all sends no second notification telling the house it owes
rent again. Double-posting rent does not look like an error; it looks like everybody owing twice as
much.

`POST /bills/{id}/skip` returns `204`, not the occurrence. Take the updated row from
`guild.BillOccurrenceUpdated`.

### Listing

`GET /bills` is **bounded, not paged** - at most 500, soonest first, filtered by `status`
(`Pending` | `Posted` | `Skipped`, case-insensitive; `400` otherwise) and by a `from`/`to` window on
`dueAt`. A ledger's bills are one row per schedule per period, so a house three years in with a
dozen standing orders is a few hundred; the expense list next door is the unbounded one and pages
for exactly that reason. Do not build a cursor loop against this.

Realtime: `guild.RecurringExpenseCreated` · `guild.RecurringExpenseUpdated` ·
`guild.RecurringExpenseDeleted` · `guild.BillOccurrenceCreated` · `guild.BillOccurrenceUpdated`.

Bills generate, auto-post and announce themselves on the same 5-minute sweep that generates chores,
so a schedule entered the day before rent is due appears on the board immediately rather than after
the next pass. Auto-posting happens at `dueAt`, not at generation: generation runs up to thirty days
ahead so people can see what is coming, and putting the charge into the ledger that early would mean
the balance board says somebody owes February's rent in January.

Three alert kinds come out of this - `ledger.bill_due`, `ledger.bill_needs_amount` and
`ledger.bill_posted`. See §19, and read the note about `ledger.bill_posted`'s `targetId`.

---

## 8. Receipts

```
GET    /api/v1/guild/expenses/{expenseId}/receipts     ViewChannel
POST   /api/v1/guild/expenses/{expenseId}/receipts     multipart, field `file`
DELETE /api/v1/guild/receipts/{receiptId}
```

```ts
interface ExpenseReceipt {
  id: string; expenseId: string;
  fileName: string; contentType: string; sizeBytes: number;
  uploadedByUserId: string; uploadedAt: string;
  url?: string | null;       // presigned, ~10 minutes
}
```

The point is not archival, it is trust. A shared ledger works only while everybody believes the
numbers in it, and the two things that quietly erode that belief are "was that 84 or 48" and "what
was that two-hundred-franc shop actually for". Neither is answerable from a description and neither
is worth the argument.

**Never cache a receipt URL.** It is presigned and minted per request, and it is deliberately not
stored: persisting a URL would persist its expiry, and the first symptom of that is a receipt that
renders for ten minutes after upload and then 403s forever. Re-list to refresh.

- **Max 4 per expense**, 8 MB each, and the type allowlist is JPEG, PNG, WebP, HEIC/HEIF and PDF.
  The stored content type comes from the allowlist rather than from your request, so an upload cannot
  choose how a browser interprets its own bytes.
- **Reading is plain `ViewChannel`** - a receipt is part of the expense, and anybody who can see the
  amount can see what it was for.
- **Writing pivots on who *entered* the expense, not who paid it.** Attaching to your own entry needs
  `AddExpenses`; attaching to or deleting from somebody else's needs `ManageLedger`, because it
  changes what their record says.
- Deleting is committed before the object is removed, and a storage hiccup is swallowed. "Take that
  photo down" must not become an error the user retries against a row that is already gone.

Realtime: `guild.ExpenseReceiptAdded` · `guild.ExpenseReceiptDeleted`, both carrying
`{ guildId, channelId, expenseId, ... }`.

---

## 9. What the flat costs - the spending rollup

```
GET /api/v1/guild/channels/{channelId}/ledger/summary?from=&to=&groupBy=month|category
```

```ts
interface LedgerSummary {
  channelId: string; currency: string;
  from: string; to: string;
  totalMinor: number;
  myShareMinor: number;        // your half of the shop, not what you happened to pay for
  byCategory: { category: ExpenseCategory; totalMinor: number; myShareMinor: number; count: number }[];
  byPeriod:   { period: string; totalMinor: number; myShareMinor: number; count: number }[];  // "2026-07"
  byPayer:    { userId: string; paidMinor: number }[];
  clamped: boolean;
}
```

This answers "what does this flat cost per month", which is the first thing anybody asks about a
shared household and about the only number that changes what people do. A ledger that can add
expenses but cannot answer it is a bookkeeping chore people stop doing.

Gated on `ViewChannel` alone. Every member of a ledger channel can already page every expense in it,
so a rollup of those same rows is not a wider disclosure, and gating it higher would put the one
number worth reading behind a moderator bit.

Things to get right:

- **The window defaults to the last six months and is capped at 1096 days.** A longer request is
  *shortened rather than refused*, by moving the start forward and keeping the end you asked for -
  and **`clamped: true` says so**. Render that; a total that quietly covers less than what was asked
  for is a number somebody will act on and be wrong about. `400` only if `from` is after `to`.
- **`byPeriod` is not zero-filled.** Months the house spent nothing in are absent. Do not render them
  as zero data points - a gap is a fact about the ledger, and inventing rows for it would make an
  empty ledger answer with six months of zeroes that look like data.
- **Show `Uncategorized` as its own bucket**, never folded into `Other`. A rollup that hides the size
  of its own gap is worse than no rollup; "we do not know what a third of this was" is the useful
  part.
- `groupBy` is a filter on *work*, not on meaning. Omit it and both breakdowns come back; `month` or
  `category` skips the queries behind the other one. The grand totals are the same either way.
- `byCategory` is biggest-bucket-first, `byPeriod` is chronological, `byPayer` is
  largest-contributor-first. `byPayer` is deliberately not a balance: it says who has been carrying
  the cash flow, which is a different question from who owes whom.

Everything is whole minor units, so both breakdowns sum back to `totalMinor` exactly.

---

## 10. Getting paid back - encrypted payment handles

```
GET    /api/v1/guild/guilds/{guildId}/payment-handles                 every member's sealed blob,
                                                                     plus shared phone numbers
GET    /api/v1/guild/guilds/{guildId}/payment-handles/recipients      the devices to seal to
PUT    /api/v1/guild/guilds/{guildId}/payment-handles                 your own only
DELETE /api/v1/guild/guilds/{guildId}/payment-handles                 your own only
PUT    /api/v1/guild/guilds/{guildId}/payment-handles/phone-sharing   your own opt-in

PUT    /api/v1/identity/users/self/phone                               your own number
DELETE /api/v1/identity/users/self/phone                               your own number
```

**Two kinds of payment detail live on this screen and they do not have the same protection.** The
sealed handles are ciphertext the server cannot open. The phone number is plaintext the server read,
could log, and would hand over with the database. They arrive in separate lists for that reason -
see §10.6. Do not merge them into one "payment details" collection in your model; once you have, no
UI you write afterwards can tell a user which is which.

**The server holds ciphertext and nothing else.** There is no plaintext IBAN column, no provider
column, no validation of either, and no code anywhere in Guild that builds a payment link. Every one
of those existed in an earlier draft and every one of them is a way to answer a question about
somebody's banking from the database alone.

What that means for you is blunt: **the work moved to the client.** The server validates sizes,
membership, and that you are writing your own row. It validates nothing about the contents, because
it cannot. So all of this is now yours:

- the handle list itself - kind (IBAN, PayPal, Revolut, Wise, Venmo, Other), value, label. There is
  deliberately no `PaymentHandleKind` on the server: which provider somebody banks with is itself
  covered by the requirement, so a plaintext `kind` column would leak it even with the value
  encrypted.
- **IBAN validation (mod-97).** A mistyped IBAN either bounces a week later or reaches a stranger.
- **payment URI construction.**
- **Swiss QR-bill payload generation.**

There is no permission bit and no route that writes somebody else's, exactly as with home status:
where your money should go is only ever yours to assert. Deleting is idempotent - a `404` would tell
a caller whether a row existed, which is the one bit this route should not leak.

### State the guarantee honestly

The encryption protects against **database disclosure** - a backup, a dump, a curious query. It does
**not** protect against a malicious or compromised server, because the server serves the key
directory and could substitute a key: whoever can change what Identity returns for Ben's device can
have Anna's client seal to a key they hold, invisibly. Do not write UI copy claiming more than that.
`hasValidCertificate` and `certificateRevokedAt` on each recipient are what let a client decline to
seal to a device that cannot prove whose it is; nothing forces you to look, and if the app already
has safety-number or device-verification UI from the MLS stack, reuse it rather than inventing a
second scheme.

### Reading

```ts
interface PaymentHandleDirectory {
  guildId: string;
  deviceId: string;              // echoed back - see below
  memberRosterVersion: number;
  members: {                     // ciphertext the server cannot open
    userId: string;
    ciphertext: string;          // base64
    nonce: string;               // base64
    version: number;
    memberRosterVersion: number;
    updatedAt: string;
    wrappedKey?: string | null;  // base64, or null
  }[];
  phoneNumbers: {                // plaintext the server read - not the same thing
    userId: string;
    phoneNumber: string;         // E.164
    updatedAt: string;
  }[];
  sharingPhoneNumber: boolean;   // your own opt-in, echoed for the settings toggle
}
```

**`GET` requires a registered `X-Device-Id` header and `400`s without one.** Not a `403`, and not an
empty result: answering an unidentified caller with zero wraps is indistinguishable from "nobody has
shared with you yet", which is the one wrong answer that looks exactly like a right one. The response
echoes `deviceId` back so a client that sent the wrong one can tell.

You get **every member's blob, but only the wraps for the calling device**. Other devices' wraps are
useless to you, and how many devices a person has is metadata worth not handing out.

### A missing wrap is a state, not an error

`wrappedKey: null` means that member has not shared with this device. Somebody who joined the house
yesterday has no wrap from anybody until each of them re-seals, because only the owner's devices hold
the content key. The correct UI is **"Anna hasn't shared how to pay her with you yet"** with a nudge,
never an error and never a blank.

Compare each blob's `memberRosterVersion` against the directory's: a mismatch means that member has
not re-sealed since somebody joined or left. Prompt **the owner** to re-seal when their own row is
behind; only they can.

### Sealing

`GET .../recipients` returns who to seal to and the key to seal to each of them with, scoped to this
guild's members. There is deliberately no route that resolves devices for a bare user id - that would
be a device-enumeration oracle for any authenticated caller who could name an account.

```ts
interface PaymentHandleRecipients {
  guildId: string;
  memberRosterVersion: number;    // seal with this, and store it on the blob
  recipients: {
    userId: string; deviceId: string; deviceName?: string | null;
    publicKey: string;            // base64, long-term identity key (not an MLS init key)
    hasValidCertificate: boolean;
    certificateRevokedAt?: string | null;
    isActive: boolean;
  }[];
  unresolvedMemberIds: string[];
}
```

**`unresolvedMemberIds` being non-empty means the list is incomplete** and sealing against it would
leave those people out with a blob they silently cannot open. Only an empty array lets you treat the
recipient list as the whole house. Revoked and inactive devices are flagged rather than filtered, for
the same reason: a client handed a shorter list than the roster actually has cannot tell a small
household from a tampered response.

`PUT` body:

```ts
{ ciphertext: base64, nonce: base64, version?: number, wraps: { recipientUserId, recipientDeviceId, wrappedKey }[] }
```

Limits: ciphertext 8 KiB, nonce 64 bytes, 200 wraps, 1 KiB per wrapped key, 128 chars per device id.
An **empty `wraps` list is legal** and means "sealed to nobody yet", so a client that has not yet
fetched the recipient roster can still store its own details. `PUT` returns
`{ guildId, userId, memberRosterVersion }`.

Realtime: `guild.PaymentHandlesChanged`, guild-scoped, carrying
`{ guildId, userId, memberRosterVersion }` or `{ guildId, userId, removed: true }`. It is not written
to the audit log, on purpose: the entry would say "somebody changed their payment handles at 14:03",
which every member can already read off `updatedAt`, and would add a moderator-readable record of
update timing without adding a fact.

### Paying: what actually works

These are conclusions from research, not starting points. Do not re-litigate them in six months.

**TWINT cannot be prefilled or deep-linked by anyone outside a merchant contract.** TWINT states its
API is not publicly accessible and not available on request. Its QR codes carry a per-transaction,
server-minted pairing token with no recipient or amount field, and TWINT's own certified SDK cannot
construct one. The "scan and type your own amount" sticker is a **merchant** product that private
individuals are barred from, and **TWINT's terms forbid reproducing those QR codes online or in
messaging**. So: do not build a "store your TWINT QR" feature. The ceiling for TWINT is a phone
number shown large, a copy button, and a launch button.

**The Swiss QR-bill (Swiss Payments Code) is the mechanism that works.** Free, official, offline,
person-to-person, and it prefills both recipient *and* amount. Spec v2.3, 34 LF-delimited lines,
structured address mandatory since November 2025, reference type `NON`, EC level M, Swiss cross
overlay. Generated on the debtor's device from the creditor's decrypted IBAN and scanned in their
banking app. **This is the one to build.**

**Phone numbers are not in the blob.** Identity owns the number; a copy sealed in a household blob
would be a second number that drifts and that nothing on this side can read to reconcile. The
per-household opt-in is §10.6.

### 10.6 The phone number, and why it is the weakest thing on this screen

TWINT's ceiling, per the research above, is *a phone number shown large with a copy button*. So the
number carries real money, and it is the one value here that nothing checks.

**Two separate acts, two separate calls.** Entering a number is `PUT /api/v1/identity/users/self/phone`
with `{ phoneNumber }` and is account-wide. Showing it to a household is
`PUT .../payment-handles/phone-sharing` with `{ share: boolean }` and is **per guild, off until
turned on**. A number entered once must not follow the account into every server it joins, so there
is no single switch that does both and you should not build UI that implies there is.

- Identity normalises to E.164 and `400`s anything it cannot: no leading `+`, a leading `00`, a
  leading zero after the `+`, stray letters, under 6 or over 15 digits. Spaces, hyphens, dots and
  brackets are stripped, so a paste from a contact card is fine. `00` is refused rather than
  rewritten, because rewriting it is wrong in enough countries to silently produce a stranger's
  number - surface the `400` and ask the user to fix it.
- `DELETE` on the same path removes it, idempotently, `204` either way.
- `GET /api/v1/identity/users/self` already returns `phoneNumber`, so there is no `GET` here.

**Deleting the number clears every guild opt-in.** This changed, and it changes what you should show
in the remove dialog. It used to leave them standing, on the argument that an opt-in with no number
behind it reads identically to no opt-in - which held right up until a number existed again:
recording a *different* one months later put it straight in front of every household the account had
ever opted into, with no prompt and nothing for its owner to do. So removal now revokes, everywhere,
and re-sharing afterwards is a deliberate act per household.

- **Drop any "your households will still be able to see a new number" warning.** It is no longer
  true, and a warning that has stopped being true is worse than none.
- The clearing happens asynchronously over the bus, so a directory read taken in the same instant may
  still report `sharingPhoneNumber: true`. Do not build a spinner or a poll around it - it converges
  on its own and the flag is inert while there is no number to publish.
- **Changing** the number does *not* clear anything. A replacement is the same person keeping the
  same intent, and un-sharing every household because somebody fixed one mistyped digit would be a
  silent, unexplained withdrawal. Word your edit and remove flows accordingly: only remove revokes.

**Nothing verifies the number.** SMS verification was designed and dropped: with Firebase Phone Auth
the client calls Google directly, so the SMS is sent and paid for before our backend is ever
involved and no server-side limit can gate the spend - the controls that would have to carry it (App
Check enforced, project quotas, region policy, a billing kill switch) are all Firebase console
configuration. A paid provider was not worth it at this volume. **Do not render a verification
badge**; there is no field for one and there will not be a false one added, because a badge that
always reads "unverified" teaches people to stop reading badges. Say it in words next to the number
instead, once: *this is the number Anna entered, we have not checked it*.

Two consequences you have to design for:

- **Two accounts may hold the same number, deliberately.** Uniqueness is only safe when something
  proves the claim. Without that, one person mistyping a digit into a stranger's real number would
  permanently lock that stranger out of ever entering their own, unrecoverably. So there is no
  conflict error to handle.
- **`updatedAt` is the only signal there is**, and it is a weak one. Worth showing when a number is
  old; not worth dressing up as assurance.

**Absence means nothing in particular, and must keep meaning nothing in particular.** A member with
no entry in `phoneNumbers` has either not opted in for this guild or not entered a number at all,
and the response deliberately does not say which. That is not an oversight to work around by
cross-referencing another endpoint: telling one flatmate that another *specifically declined to
share with them* is a worse disclosure than the number. Render the absence as
**"Anna hasn't shared a number"** and stop there.

Gated on `GuildFeatures.Ledger` and on membership, like the rest of §10. There is no permission bit
and no route that sets somebody else's opt-in - publishing a person's phone number is not a
moderator action. The change is not broadcast over realtime either: an event would announce the
moment somebody withdrew their number, which is the one change a person might reasonably make
quietly. Clients pick it up on the next directory read.

**The number is not SMS-verified**, and your copy must not imply it is. Never label it "verified".
The real check happens inside TWINT, which shows the recipient's name once a number is entered: tell
the payer to confirm that name before sending. It is free, it is the check people already trust, and
it catches exactly the failure an SMS would have caught.

**EPC QR / Girocode** is the eurozone equivalent, at lower confidence - get the spec PDF before
shipping that generator, or ship Swiss-only first.

**PayPal.me** takes an amount (`https://paypal.me/{handle}/{amount}{CURRENCY}`), but warn the user:
there is no friends-and-family URL parameter and business accounts can only accept goods-and-
services, so an amount-bearing link may cost the recipient around 3% and we cannot detect which
applies.

**No payment app returns a result.** Every flow therefore ends with an explicit "mark as paid" that
records a settlement, and the ledger keeps calling settlements *claims, not facts*. Never write copy
implying the app confirmed a payment; the payer can silently edit the amount in almost every wallet.

**Do not probe for installed wallets.** iOS 26+ deprecates `canOpenURL` and drops the declarable
scheme cap from 50 to 25. Let the user pick their wallet once, in settings, and honour it.

---

## 11. Decisions

```
GET/POST /api/v1/guild/channels/{channelId}/decisions
PUT      /api/v1/guild/decisions/{decisionId}/vote
POST     /api/v1/guild/decisions/{decisionId}/close
DELETE   /api/v1/guild/decisions/{decisionId}          // soft-cancel
```

### This is not a poll - don't build poll UI

An option is carried when quorum is met and **nobody has blocked it**. One reasoned block beats any
amount of support. Household questions aren't well served by majority rule: the person who has to
live with the downside should be able to stop it, and everyone else should be able to read why.

```ts
interface Decision {
  id: string; channelId: string;
  title: string; description?: string | null;
  createdByUserId: string;
  closesAt?: string | null;
  quorum?: number | null;        // non-abstain votes needed
  status: 'Open' | 'Decided' | 'Blocked' | 'Cancelled' | 'Expired';
  outcomeOptionId?: string | null;
  options: { id: string; title: string; position: number; supportCount: number; isBlocked: boolean }[];
  blocks: { userId: string; optionId?: string | null; reason: string }[];
  myVoteOptionId?: string | null;
  myVoteKind?: 'Support' | 'Abstain' | 'Block' | null;
}
```

Vote body: `{ kind, optionId?, reason? }`.

| Rule | |
|---|---|
| `Block` **requires** a `reason` | `400` otherwise - a veto nobody can see the reasoning for is how a house ends up in a silent standoff |
| `Support` requires an `optionId` | `400` otherwise |
| `Block` with `optionId: null` | Objects to the whole decision, not one option |
| One vote per member | Re-voting replaces; `PUT` is the upsert |

Render `blocks` as **objections to resolve**, prominently and with the reason - not as a tally row.
`isBlocked` on an option means it cannot win no matter what `supportCount` says.

**Statuses:** `Blocked` means every option was vetoed. It is deliberately *not* "the least-hated
option wins" - "we couldn't agree" is a result. `Expired` means quorum was never reached
(abstentions don't count toward it). Decisions with a `closesAt` are resolved automatically within
5 minutes of it passing.

Realtime: `guild.DecisionCreated` · `guild.DecisionUpdated` · `guild.DecisionClosed` ·
`guild.DecisionCancelled`

---

## 12. Meals

```
GET      /api/v1/guild/channels/{channelId}/recipes?limit=50&cursor=     ViewChannel
POST     /api/v1/guild/channels/{channelId}/recipes                      PlanMeals
GET      /api/v1/guild/recipes/{recipeId}                                ViewChannel
PATCH    /api/v1/guild/recipes/{recipeId}   DELETE to remove             own: PlanMeals, else ManageMeals
GET      /api/v1/guild/channels/{channelId}/meal-plan?from=&to=          ViewChannel
POST     /api/v1/guild/channels/{channelId}/meal-plan                    PlanMeals
PATCH    /api/v1/guild/meal-plan/{entryId}  DELETE to remove             see below
POST     /api/v1/guild/channels/{channelId}/meal-plan/shopping-list      PlanMeals + AddListItems
GET      /api/v1/guild/channels/{channelId}/recipes/cookable?expiringDays=&limit=   ViewChannel
GET/PUT  /api/v1/guild/channels/{channelId}/meals/config                 ViewChannel / ManageMeals
```

```ts
interface Recipe {
  id: string; channelId: string;
  title: string; description?: string | null;
  servings: number;              // 1-50
  prepMinutes?: number | null;
  sourceUrl?: string | null;
  createdByUserId: string;
  ingredients: { position: number; text: string; matchName?: string | null; isOptional: boolean }[];
  createdAt: string;
}

interface MealPlanEntry {
  id: string; channelId: string;
  date: string;                  // a plain date, "2026-08-13" - no time zone
  slot: 'Breakfast' | 'Lunch' | 'Dinner' | 'Other';
  recipeId?: string | null;
  recipeTitle?: string | null;   // denormalized so a week renders in one request
  freeText?: string | null;
  cookUserId?: string | null;
  servings?: number | null;
  position: number;
}

interface MealPlanConfig {
  channelId: string;
  shoppingListChannelId?: string | null;   // must be a List channel in this guild
  pantryChannelId?: string | null;         // must be a Pantry channel in this guild
}
```

**`date` is a plain date on purpose.** Thursday dinner is Thursday dinner wherever your phone is, so
do not parse it into an instant and do not shift it for the viewer's zone.

An entry needs **either** `recipeId` **or** `freeText` (`400` otherwise) - a row with neither renders
as an empty cell nobody can interpret or delete with confidence. Most of a real week is "leftovers"
rather than a recipe, so make the free-text path the fast one.

Caps: 200 recipes per channel, 60 ingredients per recipe, 150-char titles, 200-char ingredient lines
and free text, servings 1-50. The meal-plan window defaults to today plus six days and is capped at
60 days.

**Recipes are paged**: `{ items, nextCursor }`, `limit` default 50 / max 200, cursor `{title}|{id}`.
A malformed cursor is a `400`.

**Editing pivots on creator *or* cook.** Your own recipe or entry needs only `PlanMeals`; anybody
else's needs `ManageMeals`. The cook counts as an owner of the entry deliberately - swapping yourself
out of Thursday is the single most common edit there is, and needing a moderator bit for it would
mean asking permission not to cook. Ingredients are replaced wholesale on patch, not merged.

Deleting a recipe leaves the plan's entries in place and only clears the link, so it does not
silently erase a week somebody has already agreed to cook.

### Plan to shopping list

```
POST /channels/{channelId}/meal-plan/shopping-list
  { from, to, listChannelId?, includeOptional?, skipPantry? }
  → { added: ListItem[], skippedInPantry: string[], skippedOnList: string[], truncated: boolean }
```

**This is the highest-value screen in the module.** A meal plan that has to be transcribed onto the
shopping list by hand is a plan that gets abandoned in week three, at which point the answer to
"what are we eating" goes back to being takeaway.

It collects the window's ingredients, drops what the configured pantry already has and what an
unchecked line on the target list already covers, and appends the rest with `section` set to the
recipe title.

Two permissions on two channels: `PlanMeals` on the meals channel is the right to read the plan,
`AddListItems` on the target list is the right to write to it. Checking only the first would turn
"can plan meals" into "can append a hundred rows to any list in the house". `400` if no list is
configured and none is passed, or if the list is not a `List` channel in the same guild.

- **Render both skip reasons.** A shopper who opens the list and finds no onions has no way to tell
  a working pantry check from a broken button, and will not press it twice. "Onions - you already
  have some" costs one line and buys the trust the feature runs on.
- **`added` is the created `ListItem` rows, not a count.** `skippedInPantry` and `skippedOnList` are
  ingredient lines as text.
- **Ingredients are deliberately not scaled by `servings`.** A recipe for two put on the plan for
  four does not double its quantities. Scaling free-text quantities ("a bunch", "1 tin") is not
  something a machine can do without being wrong, and being wrong in a shop is worse than being
  literal.
- **Matching is word-boundary containment on a normalized `matchName`**, not fuzzy search. A client
  that supplies its own `matchName` still gets it normalized, because a stored un-normalized match
  name produces a row that never matches anything and the symptom - one ingredient always bought
  again - is invisible until somebody is standing in a shop.
- `truncated: true` means the per-call line cap was hit. Offer "run it again for the rest" rather
  than quietly shipping half a week.

### Cookable

```
GET /channels/{channelId}/recipes/cookable?expiringDays=&limit=
  → { items: CookableRecipe[], reason?: string | null }
```

```ts
interface CookableRecipe {
  recipe: Recipe;
  haveCount: number;       // ingredients the pantry can cover, optional ones included
  missingCount: number;    // required ingredients it cannot - optional lines excluded
  expiringCount: number;   // covering pantry items inside the expiry horizon
  expiringNames: string[];
  missing: string[];
}
```

Ranked by `expiringCount` descending, then `missingCount` ascending. **This is the food-waste payoff
and the reason keeping a pantry up to date is worth the effort** - without it a pantry is a chore
that only ever tells you what you already put in it. Two sort keys a person can predict; do not add
a scoring slider.

`expiringDays` defaults sensibly and is clamped to 1-90. `missingCount` excludes optional
ingredients on purpose: they are garnish, and counting them would rank a recipe you can absolutely
cook tonight below one you cannot.

**`reason` is why `items` is empty when it is empty** - no pantry module, no configured pantry, or
nothing in stock - and is null when the empty answer is a genuine ranking. Render it. A bare empty
array would leave the house believing the feature is broken when it is merely unconfigured.

### Cooking reminders

Whoever is named as `cookUserId` gets one alert on the morning of the day, at 08:00 in the guild's
quiet-hours time zone (UTC if none is configured), as `kind: "meals.cooking_today"` with `targetId`
set to the plan entry (§19). At most once per entry.

Re-dating an entry or handing it to a different cook **releases that stamp**, so moving Thursday's
curry to Saturday does not cost it its only reminder and the new cook is told about the new day.
Nothing is ever sent about a past date - a "you're cooking today" that arrives about yesterday is not
a late reminder, it is a wrong one - and nothing is sent more than 12 hours after the intended
moment, which is roughly 20:00 local.

Realtime: `guild.RecipeCreated` · `guild.RecipeUpdated` · `guild.RecipeDeleted` ·
`guild.MealPlanEntryCreated` · `guild.MealPlanEntryUpdated` · `guild.MealPlanEntryDeleted`.

---

## 13. Maintenance

```
GET/POST /api/v1/guild/channels/{channelId}/maintenance-assets    ViewChannel / ManageMaintenance
GET      /api/v1/guild/maintenance-assets/{assetId}               ViewChannel
PATCH    /api/v1/guild/maintenance-assets/{assetId}  DELETE       ManageMaintenance
PUT      /api/v1/guild/maintenance-assets/{assetId}/status        LogMaintenance
POST     /api/v1/guild/maintenance-assets/{assetId}/serviced      LogMaintenance
GET      /api/v1/guild/channels/{channelId}/maintenance-records?assetId=&limit=&cursor=
POST     /api/v1/guild/channels/{channelId}/maintenance-records   LogMaintenance
PATCH    /api/v1/guild/maintenance-records/{recordId}  DELETE     own: LogMaintenance, else ManageMaintenance
GET      /api/v1/guild/guilds/{guildId}/maintenance/attention     member + Maintenance
```

```ts
interface MaintenanceAsset {
  id: string; channelId: string;
  name: string;
  location?: string | null; brand?: string | null; model?: string | null;
  serialNumber?: string | null;
  purchasedAt?: string | null; warrantyUntil?: string | null;
  vendorName?: string | null; vendorPhone?: string | null; vendorEmail?: string | null;
  notes?: string | null;
  serviceIntervalDays?: number | null;    // 1-3650
  lastServicedAt?: string | null; nextServiceAt?: string | null;
  status: 'Ok' | 'NeedsAttention' | 'Broken' | 'OutOfService';
  statusNote?: string | null;
  isServiceOverdue: boolean;              // computed server-side
  isWarrantyExpiring: boolean;
  addedByUserId: string;
}

interface MaintenanceRecord {
  id: string; assetId?: string | null; channelId: string;
  title: string; description?: string | null;
  performedAt: string; performedByUserId: string;
  vendorName?: string | null;
  costMinor?: number | null; currency?: string | null;
  expenseId?: string | null;              // a pointer into the ledger; nothing is ever created there
}
```

`isServiceOverdue` and `isWarrantyExpiring` are computed rather than stored, so a client never has to
know the sweep's cutoffs or carry a clock the server disagrees with.

### Two permissions, and the split is the interesting part

`ManageMaintenance` owns the **catalogue**: adding the boiler, correcting its serial number,
deleting an asset the house no longer has.

`LogMaintenance` owns **what happened**: marking something broken and logging a repair. Marking
something broken is deliberately *not* a moderator action - the person who discovers the washing
machine is dead is whoever tried to use it, which may be a guest or the house sitter. **Make that one
tap from anywhere the asset appears.**

### `Broken` and `OutOfService` are not synonyms

The first means it stopped working and somebody must deal with it. The second means the house took it
out of use deliberately. Only the first is urgent and only the first notifies. Do not offer them as
two labels for one idea, and do not collapse them in your filters.

`maintenance.broken` fires on the **transition into** `Broken`, not on the value: somebody editing
the note on an already-broken machine three times must not buzz the house three times.

### A service does not clear a `Broken` status

`POST /maintenance-assets/{id}/serviced` takes
`{ performedAt?, title?, notes?, vendorName?, costMinor?, currency?, expenseId? }` and returns
`{ asset, record }`. It writes the log entry and marks the asset serviced in one call, because if
they were two the half that got skipped would always be the log - the half that answers "when did we
last have it looked at" a year later.

It records **when the service was actually done** and schedules the next one from there, not from the
date it was supposed to happen. And it **deliberately leaves `status` alone**: a visit is not proof
the thing works. Whoever establishes that clears the status themselves, which is a separate and
honest act. Do not auto-clear it in your UI either.

Note the field naming asymmetry: the `serviced` body calls it `notes`, and it lands on the record as
`description`, which is also what `POST /maintenance-records` calls it.

Editing an asset's warranty date or service interval **releases the corresponding notification
stamp**, so correcting a mistyped year does not silently cost the asset its only warning - which is
the one warning in this module worth actual money.

Records are keyset paged, newest first: `{ items, nextCursor }`, cursor `{performedAt:O}|{id}`,
`limit` default 50 / max 200, malformed cursor `400`. Deleting an asset keeps its records: the
history of what was done to a machine outlives the machine.

`costMinor` is minor units like everything else, `currency` is validated as ISO-4217, and `expenseId`
is checked to exist in this guild - a dangling pointer is worse than none, because the client would
render a link to money nobody can find.

### The attention board

```
GET /guilds/{guildId}/maintenance/attention
  → { asset: MaintenanceAsset, reasons: string[] }[]
```

Spans every maintenance channel in the guild the caller can see, `ViewChannel`-filtered, because
"what needs doing" is a question about the house rather than about the cellar specifically.

`reasons` are stable tokens: **`broken`**, **`needs_attention`**, **`service_overdue`**,
**`warranty_expiring`**. An asset can carry more than one and the client should show them all - a
broken machine that is also out of warranty is a different conversation from either on its own. They
travel with the asset rather than being re-derived client-side, because you would have to
reimplement the warranty window and the overdue cutoff to get the same answer and the two would drift
the first time either changed.

**Give `warranty_expiring` real prominence.** The warranty date is the one date in a house that
nobody tracks and that is expensive to have missed; the horizon is 30 days, and already-lapsed
warranties are not listed because there is nothing left to do about them.

Realtime: `guild.MaintenanceAssetCreated` · `guild.MaintenanceAssetUpdated` ·
`guild.MaintenanceAssetDeleted` · `guild.MaintenanceRecordCreated` ·
`guild.MaintenanceRecordUpdated` · `guild.MaintenanceRecordDeleted`. A `serviced` call emits both a
record-created and an asset-updated event.

---

## 14. Home status - "who's home"

```
GET    /api/v1/guild/guilds/{guildId}/home-status
PUT    /api/v1/guild/guilds/{guildId}/home-status
DELETE /api/v1/guild/guilds/{guildId}/home-status
```

```ts
interface HomeStatus {
  userId: string;
  kind: 'Home' | 'Out' | 'Asleep' | 'DoNotDisturb' | 'OnMyWay';
  note?: string | null;      // ≤100 chars
  expiresAt: string;
}
```

`PUT` body: `{ kind, note?, expiresInMinutes? }` - default 12 hours, capped at 7 days.

**This is not connection presence.** The existing online/offline presence means "their app is
connected"; this means "they're in the flat". Keep them visually distinct or you'll confuse both.

**It decays on purpose.** A status nobody clears stops being asserted rather than claiming someone
is asleep three days later - a stale board is worse than no board. `GET` never returns expired
entries, and a member with no live status is simply absent from the array.

You can only ever set your **own** status. There's no permission for it and no way to set someone
else's; "Anna is asleep" is only Anna's to assert.

Realtime: `guild.HomeStatusChanged`.

---

## 15. Absence - "I'm away"

```
GET    /api/v1/guild/guilds/{guildId}/absences?from=&to=   any member
POST   /api/v1/guild/guilds/{guildId}/absences             your own only, no permission bit
PATCH  /api/v1/guild/absences/{absenceId}                  your own, or ManageGuild
DELETE /api/v1/guild/absences/{absenceId}                  your own, or ManageGuild
```

```ts
interface Absence {
  id: string; guildId: string; userId: string;
  startAt: string; endAt: string;    // endAt is exclusive
  note?: string | null;              // ≤100 chars
  createdByUserId: string; createdAt: string;
}

// POST and PATCH return:
{ absence: Absence, choresReassigned: number }
```

**This is not home status.** Home status is a decaying assertion about right now, lives in Redis with
a TTL, and must stop claiming anything by Thursday. An absence is a dated plan with a start and an
end that the rota reads to decide whose turn it is; it is a database row and it is still true while
nobody is looking at it. Merging them would mean either giving a fortnight in Lisbon an expiry it
does not have or giving "back in an hour" a permanence it must not have, and both are wrong in a way
people only notice when the rota starts assigning bins to somebody on a plane. **Keep them visually
distinct.**

Gated on the `Presence` module, and guild-scoped rather than channel-scoped: being away is a fact
about the house, not about its chore board. `GET` defaults to the last 30 days through the next 90 -
the two questions a client asks are "why does the balance say that" and "who is here next month".

Writes are your own only, with no permission bit, exactly as home status is. **`ManageGuild` can
amend and delete somebody's absence but cannot create one** - somebody has to be able to clear a row
entered by a member who has since stopped answering their phone, but inventing an absence for
somebody else would move their chores off them without their knowing.

Limits: 180 days per absence, 20 upcoming per member, 100-char note. **Overlaps are rejected rather
than merged** (`400 That overlaps an absence you have already declared`); merging looks kinder and is
not, because it silently changes dates the member typed.

### The chore handover, and what it does not undo

Creating an absence hands your unfinished occurrences inside the window to the lightest-loaded other
member and reports how many in `choresReassigned`. Extending one hands over what the extension newly
covers. Each handover releases that occurrence's reminder and nudge stamps, so the new assignee is
actually told it is due rather than inheriting a reminder sent to somebody else, and a nudge aimed at
the previous holder does not spend the new one's twelve-hour window.

**Deleting or shortening an absence does not claw the chores back.** Say so in your confirm copy,
because people will expect otherwise. By the time the dates change the new assignee may already have
done the chore, and an occurrence that changes hands twice - possibly after being completed - is how
the fairness ledger stops being something anybody believes. Whoever comes home early can swap a chore
back in two taps, and that swap is recorded as what it is.

Each handover also sends the new assignee a `chore.reassigned` alert (§19), capped at 25 per absence.
The person going away is not told - a push saying "we took your chores off you" is a phone buzzing
about something the recipient just did.

Realtime: `guild.AbsenceCreated` · `guild.AbsenceUpdated` · `guild.AbsenceDeleted`, all guild-scoped
and carrying `choresReassigned` alongside the row.

---

## 16. Quiet hours & guest access

### Quiet hours

```
GET /api/v1/guild/guilds/{guildId}/quiet-hours     // any member
PUT /api/v1/guild/guilds/{guildId}/quiet-hours     // ManageGuild
```

```ts
interface QuietHours {
  enabled: boolean;
  startMinuteLocal: number;   // 0-1439, minutes past local midnight
  endMinuteLocal: number;
  timeZoneId: string;         // IANA, e.g. "Europe/Zurich"
}
```

The window **wraps midnight** when `start > end` (22:00 → 07:00 is the normal case, not an edge
case). `400` on an out-of-range minute, `start === end`, or an unknown IANA id.

Three things now read this config, and they read it differently:

- **Chore reminders** that would fire inside the window are **deferred** to its end.
- **Bill announcements** are deferred, measured from *now* rather than from the due date - a bill is
  announced days before it is due, and deferring from the due date would hold every heads-up back
  until the morning it was already too late to be one.
- **Cooking reminders** are deferred from their 08:00 local slot.
- **Nudges are rejected**, not deferred, with a `409` (§4).

`timeZoneId` is also what the meals module resolves "this morning" against, so a house that never
sets quiet hours gets its cooking reminders at 08:00 UTC.

### Guest access

```
POST   /api/v1/guild/guilds/{guildId}/members/{userId}/roles/{roleId}/temporary   // { expiresAt }
DELETE /api/v1/guild/guilds/{guildId}/members/{userId}/roles/{roleId}/temporary
```

Needs `ManageGuests`, plus the same role-hierarchy rule as normal role assignment - you can only
hand out roles you could assign by hand. Max one year.

The grant lapses **on its own**: permission resolution ignores expired role memberships from the
exact instant they expire, so the pet sitter's five days end without anybody remembering to revoke
them. Rows are tidied up a week later, so a lapsed grant may still be visible in role listings for
a while - treat `expiresAt` in the past as "no longer granted".

This is also what the house manual (§18) is for: a guest holding `@everyone` can read the wiki, tick
things off, and mark the boiler broken, without inheriting the bins.

---

## 17. Moving out

```
POST /api/v1/guild/guilds/{guildId}/members/{userId}/move-out    // { writeOffBalances?: boolean }
```

A household has no kick. The `Household` preset leaves the Moderation module off, which strips
`KickMembers` and `BanMembers` for everybody including the owner - so this is how somebody who has
moved out is removed. Needs `ManageGuild` plus the usual role-hierarchy rule; the owner cannot be
moved out (transfer ownership first).

**It refuses while they still owe money.** With `writeOffBalances` unset, an unsettled member gives
you `409`:

```ts
{
  error: "This member is not settled up",
  outstanding: { channelId: string; currency: string; netMinor: number }[]
}
```

Render that as a decision, not an error: "Ben owes 240 CHF - settle up first, or write it off".
Setting `writeOffBalances: true` records the settlements that zero them and proceeds. It does not
pretend money moved; it is the house agreeing to stop counting the debt, and it is written to the
audit log as such. Make that clear in the confirm dialog.

On success:

```ts
interface MoveOutSummary {
  userId: string;
  choresReassigned: number;    // unfinished occurrences handed to the next lightest member
  choresDropped: number;       // deleted because the rota had nobody left
  choresPaused: number;        // chores that named them as fixed assignee
  listItemsUnassigned: number;
  balancesWrittenOff: { fromUserId: string; toUserId: string; amountMinor: number }[];
}
```

Their **completed chore history is left alone** - rewriting it would change everyone else's
fairness balance on the day a flatmate leaves. Chores with them as `fixedAssigneeUserId` are paused
rather than reassigned, so the house decides who picks them up; surface those as something to
resolve.

Realtime: `guild.MemberMovedOut` (guild-wide), plus `MemberRemovedForBots` on the bus.

---

## 18. The house manual

A guild created with `kind: "Household"` is seeded with a wiki space holding one category,
**House manual**, and six starter pages:

```
Wifi and devices · Bin day and recycling · Appliances and the boiler
Landlord and emergencies · Meter readings · How this house works   (pinned)
```

`GuildFeatures.Wiki` has been on for households since the preset existed and completely unshaped, so
a new flatmate - or the pet sitter the guest-access module exists for - joined and was told nothing.
The wifi code, the bin day and where the stopcock is are exactly a wiki page, and they are the
difference between guest access being a permission and being useful.

**Every value on these pages is a `[ bracketed ]` blank, not content.** There is not one plausible-
looking placeholder address, phone number, landlord's name or wifi password anywhere in them, on
purpose: a seeded page that reads like real data is how somebody ends up ringing a number that
belongs to a stranger, or trusting a "landlord" line the app made up.

So do not render the brackets as data, do not treat a bracketed line as a filled field, and do not
show these pages as populated. **Treat an unfilled page as an empty state inviting completion** -
that is the behaviour that gets the wifi code written down.

They are ordinary wiki pages after seeding, authored by the owner, and are read and edited through
the normal wiki endpoints with the normal wiki permissions. `ViewWiki` is part of `@everyone`.

---

## 19. Household alerts

Everything above emits `guild.*Created`-style **collaborative broadcasts** - the row that changed,
sent to whoever is currently looking at that channel. They are for keeping two open clients in
sync, and they must never buzz a phone: somebody ticking milk off the list is not worth waking you
up for.

Alerts are the opposite. Few recipients, each of whom needs to know **with the app closed**. They
arrive on one realtime event and, for anyone entitled to a push, as a push carrying the same title,
body and target.

```
guild.HouseholdAlert  →  {
  guildId, channelId, kind, targetId, title, body, data,
  titleLocKey, titleLocArgs, bodyLocKey, bodyLocArgs
}
```

Push data payload: `type: "household"`, plus the same `kind`, `targetId` and localization keys.
Route on `kind`, deep-link with `targetId`, and render `title` / `body` as given - they are written
server-side so a client needs no per-kind copy.

**One event for every kind, deliberately.** Alert kinds keep being added, and a client that forgot
to subscribe to `guild.SomethingNewAlert` would silently stop being told about it.

| `kind` | Who gets it | `targetId` |
|---|---|---|
| `chore.due` | The assignee, once per occurrence, deferred past quiet hours | Occurrence id |
| `chore.nudge` | The assignee only. **The nudger is never named** | Occurrence id |
| `chore.reassigned` | Whoever inherited a chore from somebody going away. Not the person leaving | Occurrence id |
| `ledger.expense` | Everyone with a share, and the payer if somebody else recorded it. Not the person who entered it | Expense id |
| `ledger.settlement` | The counterparty; both parties when a third person recorded it | Settlement id |
| `ledger.bill_due` | Everyone with a share in the bill | Bill occurrence id |
| `ledger.bill_needs_amount` | Only members with `ManageLedger` on that channel | Bill occurrence id |
| `ledger.bill_posted` | Everyone with a share, except whoever posted it | **Expense** id - see below |
| `meals.cooking_today` | The named cook only | Meal plan entry id |
| `maintenance.due` | Everyone who can see that maintenance channel | **Asset** id |
| `maintenance.warranty` | Everyone who can see that maintenance channel | **Asset** id |
| `maintenance.broken` | Everyone who can see it, except whoever marked it | **Asset** id |
| `decision.opened` | Everyone who can see the channel, except the author | Decision id |
| `decision.blocked` | The author and anyone who already voted, except the blocker | Decision id |
| `list.item_added` | Members whose home status is `Out` or `OnMyWay`, plus the assignee if the line names one. Not the person who added it | List item id |
| `list.completed` | Everyone who put a line on that list, except whoever ticked the last box | **Channel** id |
| `pantry.low` | Everyone who can see that pantry, except the actor and anyone already sent `pantry.restock` for the same event | Pantry item id |
| `pantry.restock` | Only members whose home status is `Out` or `OnMyWay` | List item id |
| `pantry.expiring` | Everyone who can see that pantry, batched per pantry | **Channel** id |

### `ledger.bill_posted` points at an expense, not a bill

This is the one trap in the table. Its two siblings, `ledger.bill_due` and
`ledger.bill_needs_amount`, both carry the **bill occurrence** id. `ledger.bill_posted` carries the
**expense** id, because what the recipient wants to open at that moment is the row that now moves
their balance, not the schedule entry that produced it. A router that assumes "every `ledger.bill_*`
kind deep-links to a bill" will send people to a `404`.

By contrast, **all three `maintenance.*` kinds point at the asset.** `maintenance.due`,
`maintenance.warranty` and `maintenance.broken` are three different facts about one machine, and the
asset screen is where all three are answered.

### Things worth knowing before you build against these

- **`ledger.expense` fires on create only.** Not on edit or delete - correcting a split repeatedly
  while you work out who was actually there would otherwise send one push per attempt.
- **`ledger.bill_due` is announced when the bill is generated**, which is up to `leadDays` before it
  is due, and the body says "due today" or "due in 3 days" accordingly. Nothing is sent for a bill
  more than 7 days late; it is stamped and dropped, because otherwise it sits at the head of the
  queue forever.
- **`ledger.bill_needs_amount` goes only to people who can act on it.** Telling anybody else would
  be telling them about a button they do not have.
- **`ledger.bill_posted` fires for hand-posted bills as well as auto-posted ones**, and a repeat post
  sends nothing at all.
- **`chore.nudge` carries no sender anywhere** - not in the copy, not in `data`. Do not try to infer
  one and do not offer "nudged by" UI.
- **`maintenance.broken` fires on the transition into `Broken`**, not on the value.
- **`meals.cooking_today` is at-most-once per entry**, released by a change of date or cook, and
  never sent about a past date.
- **`decision.blocked` fires on the transition into a block.** Rewording the reason does not
  re-alert, so one person cannot buzz the house at will.
- **`list.item_added` is deliberately quiet.** No home-status board and no assignee means nobody is
  told: "someone added milk" buzzing a house that is all sitting in it is how the module gets muted.
  The assignee half needs no Presence module - they were named.
- **`list.completed` fires on the tick that empties the list**, and only for people who contributed
  a line. Every other tick is collaborative state and arrives on `guild.ListItemChecked`.
- **`pantry.low` and `pantry.restock` are one event with two audiences.** Somebody who is out gets
  the request (`pantry.restock`, only when there is a list line to buy against); everybody else gets
  the statement of fact (`pantry.low`). Nobody gets both. At most once per low episode: the stamp
  clears when the quantity climbs back above the threshold.
- **`pantry.restock` needs the Presence module.** Without a home-status board there is no way to
  know who is out, and that half simply does not fire rather than buzzing everyone.
- **Every recipient set is `ViewChannel`-filtered** (except `ledger.bill_needs_amount`, which is
  narrower still). A title carrying an expense description is subject to exactly the permission the
  `GET` is - a notification is not a lesser channel than a `GET`.
- **Muting the guild suppresses the push, not the realtime event.**

### The push collapse key changed

A push's collapse key - Android's `tag`, APNs' `apns-collapse-id` - is now **`kind:targetId`**. It
used to be `targetId` alone.

That was a bug, and this is the fix. A target id is not unique across kinds, and several kinds
deliberately share one: three separate facts about the same appliance (`maintenance.due`,
`maintenance.warranty`, `maintenance.broken`) all carry that asset's id, so keying on the id alone
silently threw two of them away - the warranty warning replaced the service reminder in the tray and
the reader never saw it. The same shape applied to the bill kinds against one occurrence, and to
**`decision.blocked` replacing `decision.opened`**, which is a pre-existing bug this also fixes.

What still collapses, which is the behaviour the key is for: a second `chore.due` about the same
occurrence replaces the first. The key is truncated to 64 characters for APNs, kind first.

Treat this as a **behaviour change you may see in testing**: a device that previously showed one
notification for an asset may now show three. That is correct.

### Localized notifications

`title` and `body` are English. Alongside them, server copy carries a localization key and the
ordered values its placeholders take:

```
bodyLocKey  = "household_pantry_low_listed_body"
bodyLocArgs = ["Shopping"]
body        = "Running low, so it's gone on Shopping."
```

A key is **absent** when the text is something a user typed - a shopping-list line, an expense
description, a pantry item's name, a recipe title, an appliance's name, a channel name - because
that reads the same in every language. So `titleLocKey` is null far more often than `bodyLocKey`.
Arguments are always pre-formatted: money arrives as `"CHF 12.50"`, durations as `"3 days"` or
`"2 weeks"`, counts as `"2"`. No client needs a currency table or a duration formatter.

Render `locKey` if you recognise it, and fall back to `title`/`body` if you do not - a key added
server-side before your release ships is the normal state of things, not an error. Wave two added
nine more keys.

On mobile the keys are resolved by the **OS**, not by the app: Android reads
`res/values*/strings.xml`, iOS reads `*.lproj/Localizable.strings`. That is what makes a household
notification work while the app is dead, which a data-only push does not. The catch is that a key
missing from the bundle does not fall back to the literal text - Android drops the notification's
text and iOS displays the key - so the server only sends keys to a device that declared the
`push.loc.v1` capability at registration. That gate is **per token, not per account**, because two
handsets on one login can be on different releases. Ship the resources and the capability in the same
build, and never remove one without the other.

Household pushes land on the Android `household` notification channel, at high priority so Doze does
not hold a chore reminder until the next morning. A recipient who has turned on hide-push-content
gets the `household_hidden_title` / `household_hidden_body` pair instead, localized like everything
else - hiding the content is not a reason to switch the reader back into English.

---

## 20. The home digest

```
GET /api/v1/guild/guilds/{guildId}/home
```

Everything a home tab, a lock-screen widget or a watch complication needs, in one request. This
replaces ten.

```ts
interface HouseholdDigest {
  guildId: string;
  chores?: {
    mine: ChoreOccurrence[];      // due within a day or already past due, max 10
    mineOverdueCount: number;
    houseOverdueCount: number;    // everyone's, not just yours
  } | null;
  lists?: { channelId: string; channelName: string; openCount: number; preview: ListItem[] }[] | null;
  pantry?: { expiringCount: number; soonest: PantryItem[] } | null;
  ledger?: { channelId: string; channelName: string; currency: string; myNetMinor: number }[] | null;
  decisions?: {
    openCount: number;
    awaitingMyVote: { id: string; channelId: string; title: string; closesAt?: string | null }[];
  } | null;
  homeStatus?: HomeStatus[] | null;

  bills?: {
    dueSoon: { id: string; channelId: string; description: string; dueAt: string;
               amountMinor?: number | null; currency: string;
               myShareMinor?: number | null; status: BillStatus }[];
    overdueCount: number;
    needsAmountCount: number;
  } | null;

  meals?: {
    today: { id: string; channelId: string; slot: MealSlot; title: string; cookUserId?: string | null }[];
    imCookingToday: boolean;
  } | null;

  maintenance?: {
    brokenCount: number;
    serviceOverdueCount: number;
    warrantyExpiringCount: number;
    attention: { id: string; channelId: string; name: string; status: AssetStatus; reason: string }[];
  } | null;

  away?: { userId: string; startAt: string; endAt: string; note?: string | null }[] | null;
}
```

**A null section means "render nothing".** It covers both "the module is off" and "you can see no
channel of that type", and the two are deliberately not distinguished - telling an outsider there
is a ledger they cannot see is a disclosure for no gain. Access is plain guild membership rather
than a feature gate, for the same reason: there is no single feature to check and a non-member must
not learn which modules a guild has.

**Everything is capped** (10 chores, 10 absences, 5 of most things, 3 ledger channels). This is a
glance; the module endpoints above remain the way to read a whole board. `myNetMinor` is your own
position only - positive means the house owes you.

Notes on the wave-two sections:

- **`bills.dueSoon` spans the next fortnight and includes anything already late, at the top.** An
  overdue thing is still a thing that is due, and hiding it to count it separately would leave the
  most urgent row off the widget.
- **`bills.needsAmountCount` is counted apart from `overdueCount`** because the action is different:
  one needs money moved, the other needs somebody to open the post.
- **`myShareMinor` is null when there is no total to divide yet, and also when the template's split
  no longer resolves** - an exact split whose amounts stopped summing after somebody edited the
  total. A wrong share on a widget is worse than a missing one, because it is the number somebody
  transfers.
- **`meals.imCookingToday` is computed over the whole day**, not over the capped `today` list, so a
  busy day cannot quietly answer "no" for somebody who is in fact cooking. `title` is flattened from
  the recipe title or the free text, because a glance renders one line either way.
- **`maintenance.attention[].reason` is a single token**, where the attention board (§13) carries all
  of them. That is the difference between the two surfaces rather than an omission: the board has
  room to say a machine is both broken and out of warranty, a widget line has room for the word that
  decides whether anybody gets up.
- **`away` sits beside `homeStatus` and is deliberately not folded into it.** See §15 for why.

**Conditional requests are supported.** The response carries a strong `ETag` over its own content;
send it back as `If-None-Match` and an unchanged digest returns `304` with no body. A `W/` prefix is
tolerated on the way in, because some HTTP stacks weaken an ETag when they revalidate. Use this for
widget refresh. Note it saves the transfer, not the server work: there is no single timestamp that
moves when any of ten modules changes, so the digest is assembled either way.

The response is `Cache-Control: private, no-cache`. It is per-user - never put it in a shared cache.

---

## 21. Waiting on you

```
GET /api/v1/guild/inbox/tasks?limit=25
```

The other half of the inbox. `/inbox/unread` will never show a household channel, because a list
has no message history and so can never be unread - which left the modules people most want
reminding about with no inbox presence at all.

```ts
interface InboxTask {
  kind: 'ChoreDue' | 'DecisionVote' | 'ListAssignment'
      | 'BillDue' | 'CookingToday' | 'MaintenanceDue';
  targetId: string;               // occurrence / decision / list item / bill / plan entry / asset
  breadcrumb: InboxBreadcrumb;    // same shape the unread tab uses
  title: string;
  subtitle: string;               // empty string, never null, when there is nothing to add
  dueAt?: string | null;
  isOverdue: boolean;
}

interface InboxTaskPage { tasks: InboxTask[]; truncated: boolean }
```

- **Ordering:** anything with a deadline first, soonest at the top; undated items after, oldest
  first.
- **`isOverdue` respects the chore's grace period.** A chore two hours late inside a 24-hour grace
  is not overdue; a decision is overdue the moment it closes.
- **`dueAt` is null** for a list assignment, for a decision left open indefinitely, and for something
  simply marked broken - a broken washing machine has no deadline, it is already late in a way a date
  cannot express.
- **No cursor.** It is a to-do list. `truncated` tells you more were waiting. `limit` defaults to 25
  and caps at 50.
- **Handle an unrecognised `kind` by rendering `title` / `subtitle` and deep-linking on
  `targetId`** - more kinds will be added, and three were added in wave two.

The three new kinds:

- **`BillDue`** - a bill you have a share in, due or about to be (the lead is 5 days). The bill,
  never the balance: "you owe Anna 40 francs" is not a task, because nothing about it changes when
  you look at it, so it would sit in the inbox until somebody settled up and train people to ignore
  the tab. A bill occurrence has a due date and leaves on its own the moment it is posted or skipped.
- **`CookingToday`** - a meal plan entry today or tomorrow with you down as the cook. Tomorrow is
  included because a cook wants the evening they are cooking to appear before the morning of it.
- **`MaintenanceDue`** - something broken or overdue a service.

`GET /inbox/summary` also returns **`taskCount`**, capped at 99 like the others, so the header
badge needs no second request. It counts the same rows this tab shows, so the new kinds move it.

---

## 22. What will bite you otherwise

**1. Household channels have no messages.** No composer, no message history, no `POST /messages`.
Route by `channel.type` before rendering, and treat unknown types as inert rather than as `Text`.
There are seven of them now.

**2. `403` doesn't always mean "you lack permission".** It often means the guild doesn't have that
module. Read `features` first and don't render the UI at all - that's the difference between "your
house doesn't do money" and "you're not allowed to see the money".

**3. The owner is not exempt from the feature gate.** Don't build an admin escape hatch; there
isn't one.

**4. Never send decimal money.** `amountMinor` only. See §6.

**5. A bill is not an expense, and must not be rendered as one.** It is an obligation before it is
an expense, which is the entire reason the module exists. Upcoming bills belong on a "what's coming"
surface, not interleaved into ledger history. See §7.

**6. `ledger.bill_posted` deep-links to an expense.** Its sibling bill kinds deep-link to a bill.
Every `maintenance.*` kind deep-links to the asset. See §19.

**7. Blocks are not downvotes.** An option with 3 support and 1 block does not win. See §11.

**8. Skipped chores are not completed chores.** They don't credit the balance, on purpose - and
presence weighting did not change that. Being away excuses you from your share; skipping does not.
See §4.

**9. Deleting or shortening an absence does not claw back reassigned chores.** Say so in the confirm
copy, because people will expect otherwise. See §15.

**10. Quiet hours reject a nudge and defer a reminder.** Two different behaviours reading one config,
and a client that shows a nudge as "queued" is lying. See §4 and §16.

**11. A service does not clear a `Broken` status, and `Broken` is not `OutOfService`.** A visit is
not proof the thing works, and "we took it out of use" is not "it stopped working". See §13.

**12. Meal plan ingredients are not scaled by servings, and matching is word-boundary containment.**
Both skip reasons come back from the shopping-list call and both must be rendered, or the shopper
cannot tell a working pantry check from a broken button. See §12.

**13. The seeded house manual is blanks, not content.** `[ bracketed ]` prompts are prompts. Do not
render them as data or the house will trust a landlord's number the app made up. See §18.

**14. Payment handles are opaque to the server, so validation is yours.** IBAN mod-97, URI building
and QR generation all moved client-side, `GET` needs a registered `X-Device-Id`, and a member with no
wrap is a state to render, not an error. And the guarantee is against database disclosure, not
against a malicious server. See §10.

**15. Never cache a receipt URL.** It is presigned per request and it expires. See §8.

**16. Everything is realtime, to the people who can see the channel.** All seven channel modules
broadcast every mutation to the online members holding `ViewChannel` on that channel - not to
everyone in the guild, which is what they used to do. If your client is receiving fewer events than
before on a restricted channel, that is the fix, not a regression. Design for concurrent edits: two
people in the same shop is the normal case.

**17. Realtime is not a notification.** Module mutations are state replication and never buzz a
phone - `guild.ListItemChecked` firing for every tick is exactly why. Anything that should reach a
closed phone arrives on `guild.HouseholdAlert` instead, with a push behind it (§19). The two travel
on different events on purpose, and building notifications off the broadcasts would buzz the house
once per keystroke.

**18. A notification key you don't recognise is not an error.** `bodyLocKey` names copy the server
shipped, and it can ship copy before your release does. Fall back to `body` and carry on. See §19.

---

## 23. Compatibility

- **Community guilds are unaffected.** All ten modules are off, so every endpoint above returns
  `403` and no new channel type can be created.
- **New `ChannelType` values** are appended, so an old client's enum parse still works for the
  types it knows. It will encounter unknown types only in household guilds. `Meals` and
  `Maintenance` were appended after the wave-one five.
- **New `ExternalPermission` values** are appended to the contract; services querying permissions
  by name are unaffected.
- **`RoleMember` gained `expiresAt`** (nullable). Existing memberships have `null` and behave
  exactly as before.
- **`ExpenseDto` gained `category`**, never null, defaulting to `Uncategorized`. Ignore it and every
  expense behaves exactly as before.
- **Bots** don't see any of this - the gateway's `GUILD_CREATE` payload carries no household data,
  and there's no Discord equivalent to map it onto.

**Two migrations ship with wave two.** `20260807164659_AddHouseholdWaveTwo` is the schema half.
`20260807164830_BackfillHouseholdWaveTwoEveryonePermissions` is a pure data migration that adds
`PlanMeals` and `LogMaintenance` to every `@everyone` role - so **re-fetch `/@me` permissions after
the deploy**, in every guild, not only households.

### Changes that need client work

| Change | Impact |
|---|---|
| `@everyone` gained `PlanMeals` + `LogMaintenance` | Members can suddenly do things. Re-fetch `/@me` permissions. |
| `Flatmates` gained `ManageMeals` + `ManageMaintenance` | Additive on the seeded role. |
| `ChannelType` gained `Meals` + `Maintenance` | New household guilds have two more channels. Unknown types must stay inert. |
| Household seed gained `# meals` and `# upkeep` | Two extra channels in the starter tree. |
| Household seed gained a **House manual** wiki space | Six pages of `[ bracketed ]` blanks. Never render them as data. |
| **Push collapse key is now `kind:targetId`** | **Behaviour change.** Kinds sharing a target id no longer replace each other in the tray, including `decision.blocked` over `decision.opened`. A device may now show three notifications where it showed one. Correct, but visible. |
| Nine new alert kinds | Route on `kind`; ignore unknown kinds. See §19. |
| `ledger.bill_posted` targets an **expense** | Its sibling bill kinds target a bill. A "bill_* means bill" router 404s. |
| `ExpenseDto.category`, `?category=` on `GET /expenses` | Additive. `400 Unknown category` on an unknown value. |
| `GET /channels/{id}/ledger/summary` | New. Note `clamped`, and that `byPeriod` is not zero-filled. |
| Bills: `/recurring-expenses`, `/bills`, `/bills/{id}/post`, `/bills/{id}/skip` | New. `GET /bills` is bounded at 500, **not paged** - do not build a cursor loop. `skip` returns `204`. |
| Payment handles are **end-to-end encrypted** | **Breaking for anything built against a plaintext handle.** No server-side kind, value, validation or URI building. `GET` requires a registered `X-Device-Id` or `400`s. |
| `POST /expenses/{id}/receipts` | New. Presigned URLs - never cache one. |
| Absence: `/guilds/{id}/absences` | New. `ManageGuild` can amend but not create. Deleting does not claw chores back. |
| `POST /chore-occurrences/{id}/nudge` | New. `409` on cooldown and on quiet hours - both carry a timestamp to render. |
| `ChoreOccurrenceDto` gained `nudgedAt` | Additive, but **only populated on the nudge response and `guild.ChoreOccurrenceNudged`** today. Treat `null` elsewhere as unknown. |
| `ChoreBalanceEntry` gained `presentDays`, balance now presence-weighted | Numbers move for houses that declare absences; identical to before for houses that do not. Render `presentDays` to explain the number. |
| `PantryItemDto` gained `barcode`; scan / consume / restock | Additive. `amount` is optional and defaults to 1. |
| `GET /guilds/{id}/pantry/barcodes` | New. Bare array, max 50, no paging. |
| Meals module: recipes, meal plan, shopping list, cookable | New. Recipes are **paged** (`{ items, nextCursor }`). Ingredients are not scaled by servings. |
| Maintenance module: assets, records, status, attention | New. Records are keyset paged. `attention` reasons include `needs_attention`. |
| `GET /guilds/{id}/home` gained `bills`, `meals`, `maintenance`, `away` | Additive; each is null when the module is off or nothing is visible. |
| `GET /inbox/tasks` gained `BillDue`, `CookingToday`, `MaintenanceDue` | Additive. `taskCount` on `/inbox/summary` moves with them. |
| Nine new notification loc keys | Ship the string resources before the capability. See §19. |

Wave-one changes, still true and still worth checking against an old client:

| Change | Impact |
|---|---|
| `GET /expenses` returns `{ items, nextCursor }` | **Breaking.** A client reading a bare array gets nothing. |
| `guild.ChoreOccurrenceUpdated` always sends `{ occurrence }` | Fixes a crash on skip; drop any `e.skipped` branch. |
| `POST /chore-occurrences/{id}/skip` returns the occurrence | Was an empty `200`. Additive. |
| Household events respect `ViewChannel` | Restricted channels reach fewer clients. Correct, but visible. |
| `POST .../move-out` | Without it a household cannot remove anyone. |
| `GET /pantry/expiring` honours per-pantry `expiryWarningDays` | Result set changes when `days` is omitted. |
| Non-member payer / settlement party rejected | `400` where a typo used to be accepted silently. |
| `PATCH /expenses` payer reassignment needs `ManageLedger` | `403` on a path that used to succeed. |
| `guild.ChoreReminder` replaced by `guild.HouseholdAlert` | **Breaking, if you built against it.** Same content, unified envelope; `occurrenceId` moved to `targetId`. See §19. |
| `guild.HouseholdAlert` carries `titleLocKey` / `bodyLocKey` + args | Additive. Ignore them and you get the English you always got. |
| Household push carries `title_loc_key` / `loc-key` | **Mobile only, and gated** on the `push.loc.v1` capability, per token. Ship the string resources first. |
| Android household push names the `household` notification channel | Previously landed on the app's fallback channel, so silencing "Home" did not silence chores. |
| `GET /guilds/{id}/home` | One call instead of ten; `ETag` / `If-None-Match` supported. See §20. |
| `GET /inbox/tasks`, `taskCount` on `/inbox/summary` | See §21. |
