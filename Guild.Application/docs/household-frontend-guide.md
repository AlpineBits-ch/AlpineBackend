# Household modules - frontend integration guide

Audience: web/desktop/mobile client engineers.

Everything a shared household needs that a chat server doesn't: the shopping list, the chore rota,
the shared-expense ledger, the pantry, house decisions, who's home, quiet hours, and time-boxed
guest access.

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

Five of the eight modules are **channel types**. A household guild's sidebar contains channels
whose contents are structured rows instead of messages:

| `channel.type` | Module | Holds |
|---|---|---|
| `List` | `Lists` | Shopping / todo items |
| `Chores` | `Chores` | Chore definitions and their generated occurrences |
| `Ledger` | `Ledger` | Expenses, shares, settlements |
| `Pantry` | `Pantry` | Stock for one location (fridge, freezer, cellar) |
| `Decisions` | `Decisions` | House decisions and votes |

They come back from the normal channel endpoints alongside `Text` and `Voice`, so your sidebar
already receives them - **it just needs to know not to open a message composer for them**. That's
the single biggest integration point: a `List` channel has no message history and no
`POST /messages`.

The other three are guild-scoped, not channels: **home status**, **quiet hours** and **guest
access** (§7-9).

### New channel types are additive

`ChannelType` gained `List`, `Chores`, `Ledger`, `Pantry`, `Decisions` at the end of the enum.
A client that doesn't recognise a type should render it as an inert placeholder rather than
assuming `Text` - that's the failure mode that produces a composer posting into a shopping list.

### Creating them

Ordinary `POST /api/v1/guild/guilds/{guildId}/channels` with the new `type`. `400` if the guild
doesn't have that module:

```
Channel type 'Ledger' is not enabled for this guild.
```

### Household guilds are seeded

Creating a guild with `kind: "Household"` (see the features guide) now provisions a starter tree
instead of the usual Text/Voice pair:

```
Home     # general (Text) · # groceries (List) · # chores (Chores)
House    # pantry (Pantry) · # ledger (Ledger) · # decisions (Decisions)
Voice    # house (Voice)
```

One channel per module, so nothing is hidden behind a settings tour. `systemChannelId` still points
at `# general`.

---

## 2. Permissions

Eleven values, all gated on their module - a Community guild returns `403` for every one of them
regardless of roles, **including for the guild owner**.

| Permission | Module | Allows | In `@everyone` |
|---|---|---|---|
| `AddListItems` | Lists | Add items; edit/delete your own | yes |
| `CheckOffListItems` | Lists | Tick and untick | yes |
| `ManageLists` | Lists | Clear a list, delete anyone's item | Flatmates |
| `CompleteChores` | Chores | Complete, skip, swap an occurrence | yes |
| `ManageChores` | Chores | Create/edit/delete chores, set effort weights | Flatmates |
| `AddExpenses` | Ledger | Add an expense; edit/delete your own | yes |
| `ManageLedger` | Ledger | Edit anyone's expense, record third-party settlements | Flatmates |
| `ManagePantry` | Pantry | Add/edit/delete stock and thresholds | yes |
| `CreateDecisions` | Decisions | Open and close decisions | yes |
| `VoteDecisions` | Decisions | Support / abstain / block | yes |
| `ManageGuests` | GuestAccess | Grant and revoke temporary roles | Flatmates |

They resolve **per channel**, so a channel overwrite granting control of one list doesn't grant
every list. Viewing any module's contents needs only `ViewChannel` on that channel.

### `@everyone` holds the participation bits

The seven marked "yes" are part of `Role.DefaultEveryonePermissions` and are back-filled onto every
existing guild. An ordinary member can shop, tick, complete a chore, add an expense they paid, put
things in the pantry, and vote - with no role setup at all.

They are granted in **every** guild, not only households, because the feature gate does the real
work: in a Community guild all seven are clamped out of the resolved mask, and they light up the
moment somebody enables the module. So `GET /guilds/{id}/@me` in a Community guild will not list
them.

`ManagePantry` is in that set despite the name. There is no separate "add stock" bit, so without it
nobody could put anything in the fridge.

### The Flatmates role

A guild created with `kind: "Household"` is now seeded with a second role, **`Flatmates`**,
position 1, holding the owner. It carries the four manage bits above, and its membership is the
**default chore rotation pool**.

Adding someone to Flatmates is the product's way of saying "this person lives here". It is a
deliberate action and it is the one that distinguishes a flatmate from a guest who joined by
invite - a guest holds `@everyone` and is therefore never assigned the bins. Surface it in your
member UI as something more meaningful than a role chip.

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

### Balance

```ts
interface ChoreBalanceEntry {
  userId: string;
  completedMinutes: number;
  completedCount: number;
  balanceMinutes: number;   // relative to the household average - negative = behind
}
```

`balanceMinutes` is relative to the house average, not an absolute total, so it reads as
"behind/ahead of your share".

### Two behaviours to render correctly

- **Skipping does not credit the balance.** A skipped chore stays unpaid work, which is exactly
  what makes the rotation land back on the same person. Don't show it as done.
- **`completedByUserId` may differ from `assignedUserId`**, and the balance credits the
  *assignee*. That's deliberate - crediting the doer would let one flatmate farm the ledger by
  doing everyone's easy chores. Show both ("Ben did Anna's washing-up").

Occurrences are generated by the server (on creation, then on schedule, with a 5-minute reconcile
sweep as a backstop). Clients never create them.

A chore whose `anchorAt` is months in the past generates **one** occurrence for the current period,
not one per missed slot. Don't build UI that expects historic occurrences to be backfilled.

Realtime: `guild.ChoreCreated` · `guild.ChoreUpdated` · `guild.ChoreDeleted` ·
`guild.ChoreOccurrenceCreated` · `guild.ChoreOccurrenceUpdated`

**`guild.ChoreOccurrenceUpdated` always carries `{ guildId, channelId, occurrence }`.** Skipping
used to send `{ occurrenceId, skipped: true }` instead; it no longer does. `POST /skip` also
returns the updated occurrence rather than an empty `200`.

### Reminders

The assignee is notified when an occurrence falls due - realtime **and** push, so a closed app
still gets it. This arrives as a **household alert** (§10), with `kind: "chore.due"` and
`targetId` set to the occurrence id.

Sent to the assignee only, at most once per occurrence.

Two behaviours to expect:

- **Quiet hours actually apply now.** A reminder that would fire inside the guild's window is held
  until the window ends (§9). Nothing is emitted in the meantime - don't show a pending state.
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
}

interface PantryConfig {
  channelId: string;
  restockListChannelId?: string | null;   // must be a List channel in this guild
  expiryWarningDays: number;              // 1-90
}
```

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

### Expiring

`/pantry/expiring` spans **every pantry in the guild the caller can see**, not one channel -
"what needs eating" is a question about the house. Results are filtered per-channel by
`ViewChannel`, so a guest with access to one pantry can't enumerate a private one.

**`days` is now optional and per-pantry.** Omit it and each pantry uses its own
`expiryWarningDays`, so a freezer set to 14 days and a fridge set to 2 both behave correctly in one
response. Pass `days` only to override every pantry at once (for a "what goes off this month" view).
This previously ignored the config entirely and used a flat three days for everything.

### The expiry sweep

A background sweep now warns each pantry about stock inside **its own** `expiryWarningDays`,
without anyone opening the board. This arrives as a household alert (§10) with
`kind: "pantry.expiring"` and `targetId` set to the **pantry channel id**.

- **One alert per pantry, not per item.** The body reads "Milk, Yoghurt and 2 more are about to go
  off"; the full list is in `data.items`.
- **At most once per item.** Editing `expiresAt` releases the stamp, so a corrected date warns
  again on the new date. Clearing it stops the warning entirely.
- **Nothing arrives for something more than 7 days past its date.** That is compost, not news, and
  it is what stops a guild returning from an outage announcing a year of leftovers.

Realtime: `guild.PantryItemCreated` · `guild.PantryItemUpdated` · `guild.PantryItemDeleted`.
An automatic restock also emits `guild.ListItemCreated` on the **list** channel.

---

## 6. Ledger

```
GET      /api/v1/guild/channels/{channelId}/expenses?limit=50&cursor=
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
  createdByUserId: string;       // who entered it - often not the payer
  shares: { userId: string; shareValue: number; amountMinor: number }[];
}
```

| `splitKind` | `shareValue` means | Notes |
|---|---|---|
| `Equal` | ignored | **Empty `shares` = everyone in the guild.** The common case (rent, internet) |
| `Shares` | a weight | "Anna counts double, she has the big room" |
| `Exact` | that person's exact `amountMinor` | Must sum to the total, else `400` |

Remainders are distributed server-side, deterministically: 1000 across 3 is 334/333/333. Never
compute shares client-side and send them as `Exact` - you'll disagree with the server on rounding.

### Balances and settling

```ts
interface LedgerBalance { userId: string; netMinor: number }   // + = the house owes them
interface TransferSuggestion { fromUserId: string; toUserId: string; amountMinor: number }
```

Balances always sum to zero and members at zero are omitted - an empty array means the house is
settled. `settle-suggestion` returns at most n−1 transfers (four flatmates settle with two
payments, not six). Recording a settlement doesn't move money; it records that someone paid.

### Listing is paged (breaking)

`GET /expenses` no longer returns a bare array. It returns:

```ts
{ items: Expense[]; nextCursor: string | null }
```

`limit` defaults to 50, caps at 200. Pass `nextCursor` back as `cursor` for the next page; `null`
means you have reached the end. A malformed cursor is a `400`, not a silent first page.

The old shape was a hard `Take(200)` with no way to ask for more, so any ledger past 200 expenses
was quietly truncated.

**Permissions:** adding an expense you paid needs `AddExpenses`; recording one on someone else's
behalf, or editing someone else's, needs `ManageLedger`. Same for settlements: your own, or
`ManageLedger` for a third-party one.

Two things that now `400` where they used to be accepted:

- A `payerUserId`, or either party to a settlement, who is **not a member of the guild**. A
  non-member in the money graph is a balance nobody can ever clear.
- Reassigning `payerUserId` to someone else via `PATCH` without `ManageLedger`. Create already
  required it; the patch path did not, so create-then-patch walked around the check.

Every expense and settlement mutation, and every currency change, is written to the guild audit
log.

Realtime: `guild.ExpenseCreated` · `guild.ExpenseUpdated` · `guild.ExpenseDeleted` ·
`guild.SettlementRecorded`. Re-fetch balances after any of them.

---

## 7. Decisions

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

## 8. Home status - "who's home"

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

## 9. Quiet hours & guest access

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
case). `400` on an out-of-range minute, `start === end`, or an unknown IANA id. Chore reminders
that would fire inside the window are deferred to its end.

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

---

## 9a. Moving out

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

## 10. Household alerts

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
| `ledger.expense` | Everyone with a share, and the payer if somebody else recorded it. Not the person who entered it | Expense id |
| `ledger.settlement` | The counterparty; both parties when a third person recorded it | Settlement id |
| `decision.opened` | Everyone who can see the channel, except the author | Decision id |
| `decision.blocked` | The author and anyone who already voted, except the blocker | Decision id |
| `list.item_added` | Members whose home status is `Out` or `OnMyWay`, plus the assignee if the line names one. Not the person who added it | List item id |
| `list.completed` | Everyone who put a line on that list, except whoever ticked the last box | **Channel** id |
| `pantry.low` | Everyone who can see that pantry, except the actor and anyone already sent `pantry.restock` for the same event | Pantry item id |
| `pantry.restock` | Only members whose home status is `Out` or `OnMyWay` | List item id |
| `pantry.expiring` | Everyone who can see that pantry, batched per pantry | **Channel** id |

Things worth knowing before you build against these:

- **`ledger.expense` fires on create only.** Not on edit or delete - correcting a split repeatedly
  while you work out who was actually there would otherwise send one push per attempt.
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
- **`pantry.low` fires with no restock list configured**, which `pantry.restock` cannot - those
  houses previously heard nothing at all when something ran out.
- **`pantry.restock` needs the Presence module.** Without a home-status board there is no way to
  know who is out, and that half simply does not fire rather than buzzing everyone.
- **Every recipient set is `ViewChannel`-filtered.** A title carrying an expense description is
  subject to exactly the permission the `GET` is.
- **Muting the guild suppresses the push, not the realtime event.**

### Localized notifications

`title` and `body` are English. Alongside them, server copy carries a localization key and the
ordered values its placeholders take:

```
bodyLocKey  = "household_pantry_low_listed_body"
bodyLocArgs = ["Shopping"]
body        = "Running low, so it's gone on Shopping."
```

A key is **absent** when the text is something a user typed - a shopping-list line, an expense
description, a pantry item's name, a channel name - because that reads the same in every language.
So `titleLocKey` is null far more often than `bodyLocKey`. Arguments are always pre-formatted:
money arrives as `"CHF 12.50"`, counts as `"2"`. No client needs a currency table.

Render `locKey` if you recognise it, and fall back to `title`/`body` if you do not - a key added
server-side before your release ships is the normal state of things, not an error.

On mobile the keys are resolved by the **OS**, not by the app: Android reads
`res/values*/strings.xml`, iOS reads `*.lproj/Localizable.strings`. That is what makes a household
notification work while the app is dead, which a data-only push does not. The catch is that a key
missing from the bundle does not fall back to the literal text - Android drops the notification's
text and iOS displays the key - so the server only sends keys to a device that declared the
`push.loc.v1` capability at registration. Ship the resources and the capability in the same build,
and never remove one without the other.

---

## 11. The home digest

```
GET /api/v1/guild/guilds/{guildId}/home
```

Everything a home tab, a lock-screen widget or a watch complication needs, in one request. This
replaces six.

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
}
```

**A null section means "render nothing".** It covers both "the module is off" and "you can see no
channel of that type", and the two are deliberately not distinguished - telling an outsider there
is a ledger they cannot see is a disclosure for no gain.

**Everything is capped.** This is a glance; the module endpoints in §3-§8 remain the way to read a
whole board. `myNetMinor` is your own position only - positive means the house owes you.

**Conditional requests are supported.** The response carries a strong `ETag`; send it back as
`If-None-Match` and an unchanged digest returns `304` with no body. Use this for widget refresh.
Note it saves the transfer, not the server work: there is no single timestamp that moves when any
of six modules changes, so the digest is assembled either way.

The response is `Cache-Control: private, no-cache`. It is per-user - never put it in a shared cache.

---

## 12. Waiting on you

```
GET /api/v1/guild/inbox/tasks?limit=25
```

The other half of the inbox. `/inbox/unread` will never show a household channel, because a list
has no message history and so can never be unread - which left the modules people most want
reminding about with no inbox presence at all.

```ts
interface InboxTask {
  kind: 'ChoreDue' | 'DecisionVote' | 'ListAssignment';
  targetId: string;               // occurrence / decision / list item
  breadcrumb: InboxBreadcrumb;    // same shape the unread tab uses
  title: string;
  subtitle: string;
  dueAt?: string | null;          // null for a list assignment
  isOverdue: boolean;
}

interface InboxTaskPage { tasks: InboxTask[]; truncated: boolean }
```

- **Ordering:** anything with a deadline first, soonest at the top; undated items after, oldest
  first.
- **`isOverdue` respects the chore's grace period.** A chore two hours late inside a 24-hour grace
  is not overdue; a decision is overdue the moment it closes.
- **No cursor.** It is a to-do list. `truncated` tells you more were waiting.
- **Handle an unrecognised `kind` by rendering `title` / `subtitle` and deep-linking on
  `targetId`** - more kinds will be added.

`GET /inbox/summary` now also returns **`taskCount`**, capped at 99 like the others, so the header
badge needs no second request.

---

## 13. What will bite you otherwise

**1. Household channels have no messages.** No composer, no message history, no `POST /messages`.
Route by `channel.type` before rendering, and treat unknown types as inert rather than as `Text`.

**2. `403` doesn't always mean "you lack permission".** It often means the guild doesn't have that
module. Read `features` first and don't render the UI at all - that's the difference between "your
house doesn't do money" and "you're not allowed to see the money".

**3. The owner is not exempt from the feature gate.** Don't build an admin escape hatch; there
isn't one.

**4. Never send decimal money.** `amountMinor` only. See §6.

**5. Blocks are not downvotes.** An option with 3 support and 1 block does not win. See §7.

**6. Skipped chores are not completed chores.** They don't credit the balance, on purpose. See §4.

**7. Everything is realtime, to the people who can see the channel.** All five channel modules
broadcast every mutation to the online members holding `ViewChannel` on that channel - not to
everyone in the guild, which is what they used to do. If your client is receiving fewer events than
before on a restricted channel, that is the fix, not a regression. Design for concurrent edits: two
people in the same shop is the normal case.

**8. Realtime is not a notification.** Module mutations are state replication and never buzz a
phone - `guild.ListItemChecked` firing for every tick is exactly why. Anything that should reach a
closed phone arrives on `guild.HouseholdAlert` instead, with a push behind it (§10). The two travel
on different events on purpose, and building notifications off the broadcasts would buzz the house
once per keystroke.

**9. A notification key you don't recognise is not an error.** `bodyLocKey` names copy the server
shipped, and it can ship copy before your release does. Fall back to `body` and carry on. See §10.

---

## 14. Compatibility

- **Community guilds are unaffected.** All eight modules are off, so every endpoint above returns
  `403` and no new channel type can be created.
- **New `ChannelType` values** are appended, so an old client's enum parse still works for the
  types it knows. It will encounter unknown types only in household guilds.
- **New `ExternalPermission` values** are appended to the contract; services querying permissions
  by name are unaffected.
- **`RoleMember` gained `expiresAt`** (nullable). Existing memberships have `null` and behave
  exactly as before.
- **Bots** don't see any of this - the gateway's `GUILD_CREATE` payload carries no household data,
  and there's no Discord equivalent to map it onto.

### Changes that need client work

| Change | Impact |
|---|---|
| `GET /expenses` returns `{ items, nextCursor }` | **Breaking.** A client reading a bare array gets nothing. |
| `guild.ChoreOccurrenceUpdated` always sends `{ occurrence }` | Fixes a crash on skip; drop any `e.skipped` branch. |
| `POST /chore-occurrences/{id}/skip` returns the occurrence | Was an empty `200`. Additive. |
| Household events now respect `ViewChannel` | Restricted channels reach fewer clients. Correct, but visible. |
| `@everyone` gained seven household bits | Members can suddenly do things. Re-fetch `/@me` permissions. |
| `Flatmates` role seeded on new households | One extra role in the list; default `rotationRoleId`. |
| `POST .../move-out` | New. Without it a household cannot remove anyone. |
| `GET /pantry/expiring` honours per-pantry `expiryWarningDays` | Result set changes when `days` is omitted. |
| Non-member payer / settlement party rejected | `400` where a typo used to be accepted silently. |
| `PATCH /expenses` payer reassignment needs `ManageLedger` | `403` on a path that used to succeed. |
| `guild.ChoreReminder` replaced by `guild.HouseholdAlert` | **Breaking, if you built against it.** Same content, unified envelope; `occurrenceId` moved to `targetId`, `choreId` / `dueAt` into `data`. See §10. |
| Six more alert kinds + push | New. Route on `kind`; ignore unknown kinds. |
| `list.item_added`, `list.completed`, `pantry.low` | New kinds. Additive - see the table in §10 for who gets each. |
| `guild.HouseholdAlert` gained `titleLocKey` / `bodyLocKey` + args | Additive. Ignore them and you get the English you always got. See §10. |
| Household push carries `title_loc_key` / `loc-key` | **Mobile only, and gated.** Sent only to a device declaring the `push.loc.v1` capability, because a key absent from the app bundle costs the notification its text. Ship the string resources first. |
| Android household push now names the `household` notification channel | Previously landed on the app's fallback channel, so silencing "Home" did not silence chores. |
| `GET /guilds/{id}/home` | New. One call instead of six; `ETag` / `If-None-Match` supported. See §11. |
| `GET /inbox/tasks` | New. Household items waiting on the caller. See §12. |
| `GET /inbox/summary` gained `taskCount` | Additive. Fold into the header badge. |
