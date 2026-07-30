# Household modules — frontend integration guide

Audience: web/desktop/mobile client engineers.

Everything a shared household needs that a chat server doesn't: the shopping list, the chore rota,
the shared-expense ledger, the pantry, house decisions, who's home, quiet hours, and time-boxed
guest access.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly. Guild endpoints are reached under the `/api/v1/guild/` prefix; the gateway
strips the `guild` segment before forwarding, which is why the paths read
`/api/v1/guild/channels/{channelId}/...`. That doubled-looking segment is correct.

**Prerequisite:** every endpoint here is gated on a `GuildFeatures` module. Read
[guild-kind-and-features-frontend-guide.md](./guild-kind-and-features-frontend-guide.md) first —
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
already receives them — **it just needs to know not to open a message composer for them**. That's
the single biggest integration point: a `List` channel has no message history and no
`POST /messages`.

The other three are guild-scoped, not channels: **home status**, **quiet hours** and **guest
access** (§7–9).

### New channel types are additive

`ChannelType` gained `List`, `Chores`, `Ledger`, `Pantry`, `Decisions` at the end of the enum.
A client that doesn't recognise a type should render it as an inert placeholder rather than
assuming `Text` — that's the failure mode that produces a composer posting into a shopping list.

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

Eleven new values, all gated on their module — a Community guild returns `403` for every one of
them regardless of roles, **including for the guild owner**.

| Permission | Module | Allows |
|---|---|---|
| `ManageLists` | Lists | Clear a list, delete anyone's item |
| `AddListItems` | Lists | Add items; edit/delete your own |
| `CheckOffListItems` | Lists | Tick and untick |
| `ManageChores` | Chores | Create/edit/delete chores, set effort weights |
| `CompleteChores` | Chores | Complete, skip, swap an occurrence |
| `ManageLedger` | Ledger | Edit anyone's expense, record third-party settlements |
| `AddExpenses` | Ledger | Add an expense; edit/delete your own |
| `ManagePantry` | Pantry | Add/edit/delete stock and thresholds |
| `CreateDecisions` | Decisions | Open and close decisions |
| `VoteDecisions` | Decisions | Support / abstain / block |
| `ManageGuests` | GuestAccess | Grant and revoke temporary roles |

They resolve **per channel**, so a channel overwrite granting control of one list doesn't grant
every list. Viewing any module's contents needs only `ViewChannel` on that channel.

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
  quantity?: string | null;      // free text — "2", "2 packs", "a bunch"
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
Ticking is always `CheckOffListItems` — checking things off is the collaborative part.

Caps: 200 chars per `text`, 500 items per list (`400` beyond).

**Reorder** takes a partial list. Ids you omit keep their relative order *after* the ones you sent,
so a drag-and-drop payload of just the moved neighbourhood is fine.

### Realtime

`guild.ListItemCreated` · `guild.ListItemUpdated` · `guild.ListItemChecked` ·
`guild.ListItemDeleted` · `guild.ListItemsReordered` · `guild.ListCleared`

All carry `{ guildId, channelId, ... }`. Apply optimistically then reconcile — the defining use
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
  effortMinutes: number;         // 1-600 — the fairness weight
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

`/swap` reassigns to the lightest-loaded *other* member — the one-tap answer to "I can't do the
bins tonight". `400` if nobody else is in the rotation.

### Balance

```ts
interface ChoreBalanceEntry {
  userId: string;
  completedMinutes: number;
  completedCount: number;
  balanceMinutes: number;   // relative to the household average — negative = behind
}
```

`balanceMinutes` is relative to the house average, not an absolute total, so it reads as
"behind/ahead of your share".

### Two behaviours to render correctly

- **Skipping does not credit the balance.** A skipped chore stays unpaid work, which is exactly
  what makes the rotation land back on the same person. Don't show it as done.
- **`completedByUserId` may differ from `assignedUserId`**, and the balance credits the
  *assignee*. That's deliberate — crediting the doer would let one flatmate farm the ledger by
  doing everyone's easy chores. Show both ("Ben did Anna's washing-up").

Occurrences are generated by the server (on creation, then on schedule, with a 5-minute reconcile
sweep as a backstop). Clients never create them.

Realtime: `guild.ChoreCreated` · `guild.ChoreUpdated` · `guild.ChoreDeleted` ·
`guild.ChoreOccurrenceCreated` · `guild.ChoreOccurrenceUpdated`

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
  quantity: number;              // decimal here — it's compared against the threshold
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
restock list** and stamps `restockedAt`. The created `ListItem` carries `sourcePantryItemId` —
badge it ("added by the pantry") so people know why it appeared.

`restockedAt` is the idempotency guard. It's released when:
- the quantity climbs back above the threshold, **or**
- the list line is deleted or cleared as bought.

So the same item won't be added twice while it's already on the list, and buying it re-arms the
loop for next time. If `restockListChannelId` is null the whole loop is off, whatever individual
thresholds say.

### Expiring

`/pantry/expiring` spans **every pantry in the guild the caller can see**, not one channel —
"what needs eating" is a question about the house. Results are filtered per-channel by
`ViewChannel`, so a guest with access to one pantry can't enumerate a private one.

Realtime: `guild.PantryItemCreated` · `guild.PantryItemUpdated` · `guild.PantryItemDeleted`.
An automatic restock also emits `guild.ListItemCreated` on the **list** channel.

---

## 6. Ledger

```
GET/POST /api/v1/guild/channels/{channelId}/expenses
PATCH    /api/v1/guild/expenses/{expenseId}      DELETE to remove
GET      /api/v1/guild/channels/{channelId}/ledger/balances
GET      /api/v1/guild/channels/{channelId}/ledger/settle-suggestion
POST     /api/v1/guild/channels/{channelId}/ledger/settlements
GET/PUT  /api/v1/guild/channels/{channelId}/ledger/config
```

### Money is integer minor units. Always.

`amountMinor` is a whole number of rappen/cents. **Never send `12.34`** — send `1234`. Every split
and balance is integer arithmetic, which is what guarantees shares sum to the total and balances
sum to exactly zero. Format for display client-side using the channel's `currency`.

One currency per ledger channel (`ledger/config`, ISO-4217). Changing it relabels; it does not
convert existing amounts — worth a confirmation dialog.

```ts
interface Expense {
  id: string; channelId: string;
  payerUserId: string;           // who actually paid
  description: string;
  amountMinor: number;
  currency: string;
  occurredAt: string;
  splitKind: 'Equal' | 'Shares' | 'Exact';
  createdByUserId: string;       // who entered it — often not the payer
  shares: { userId: string; shareValue: number; amountMinor: number }[];
}
```

| `splitKind` | `shareValue` means | Notes |
|---|---|---|
| `Equal` | ignored | **Empty `shares` = everyone in the guild.** The common case (rent, internet) |
| `Shares` | a weight | "Anna counts double, she has the big room" |
| `Exact` | that person's exact `amountMinor` | Must sum to the total, else `400` |

Remainders are distributed server-side, deterministically: 1000 across 3 is 334/333/333. Never
compute shares client-side and send them as `Exact` — you'll disagree with the server on rounding.

### Balances and settling

```ts
interface LedgerBalance { userId: string; netMinor: number }   // + = the house owes them
interface TransferSuggestion { fromUserId: string; toUserId: string; amountMinor: number }
```

Balances always sum to zero and members at zero are omitted — an empty array means the house is
settled. `settle-suggestion` returns at most n−1 transfers (four flatmates settle with two
payments, not six). Recording a settlement doesn't move money; it records that someone paid.

**Permissions:** adding an expense you paid needs `AddExpenses`; recording one on someone else's
behalf, or editing someone else's, needs `ManageLedger`. Same for settlements: your own, or
`ManageLedger` for a third-party one.

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

### This is not a poll — don't build poll UI

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
| `Block` **requires** a `reason` | `400` otherwise — a veto nobody can see the reasoning for is how a house ends up in a silent standoff |
| `Support` requires an `optionId` | `400` otherwise |
| `Block` with `optionId: null` | Objects to the whole decision, not one option |
| One vote per member | Re-voting replaces; `PUT` is the upsert |

Render `blocks` as **objections to resolve**, prominently and with the reason — not as a tally row.
`isBlocked` on an option means it cannot win no matter what `supportCount` says.

**Statuses:** `Blocked` means every option was vetoed. It is deliberately *not* "the least-hated
option wins" — "we couldn't agree" is a result. `Expired` means quorum was never reached
(abstentions don't count toward it). Decisions with a `closesAt` are resolved automatically within
5 minutes of it passing.

Realtime: `guild.DecisionCreated` · `guild.DecisionUpdated` · `guild.DecisionClosed` ·
`guild.DecisionCancelled`

---

## 8. Home status — "who's home"

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

`PUT` body: `{ kind, note?, expiresInMinutes? }` — default 12 hours, capped at 7 days.

**This is not connection presence.** The existing online/offline presence means "their app is
connected"; this means "they're in the flat". Keep them visually distinct or you'll confuse both.

**It decays on purpose.** A status nobody clears stops being asserted rather than claiming someone
is asleep three days later — a stale board is worse than no board. `GET` never returns expired
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

Needs `ManageGuests`, plus the same role-hierarchy rule as normal role assignment — you can only
hand out roles you could assign by hand. Max one year.

The grant lapses **on its own**: permission resolution ignores expired role memberships from the
exact instant they expire, so the pet sitter's five days end without anybody remembering to revoke
them. Rows are tidied up a week later, so a lapsed grant may still be visible in role listings for
a while — treat `expiresAt` in the past as "no longer granted".

---

## 10. What will bite you otherwise

**1. Household channels have no messages.** No composer, no message history, no `POST /messages`.
Route by `channel.type` before rendering, and treat unknown types as inert rather than as `Text`.

**2. `403` doesn't always mean "you lack permission".** It often means the guild doesn't have that
module. Read `features` first and don't render the UI at all — that's the difference between "your
house doesn't do money" and "you're not allowed to see the money".

**3. The owner is not exempt from the feature gate.** Don't build an admin escape hatch; there
isn't one.

**4. Never send decimal money.** `amountMinor` only. See §6.

**5. Blocks are not downvotes.** An option with 3 support and 1 block does not win. See §7.

**6. Skipped chores are not completed chores.** They don't credit the balance, on purpose. See §4.

**7. Everything is realtime.** All five channel modules broadcast every mutation to everyone
present in the guild. Design for concurrent edits — two people in the same shop is the normal case,
not the exception.

---

## 11. Compatibility

Nothing here changes an existing endpoint's behaviour.

- **Community guilds are unaffected.** All eight modules are off, so every endpoint above returns
  `403` and no new channel type can be created. Existing clients need no changes.
- **New `ChannelType` values** are appended, so an old client's enum parse still works for the
  types it knows. It will encounter unknown types only in household guilds.
- **New `ExternalPermission` values** are appended to the contract; services querying permissions
  by name are unaffected.
- **`RoleMember` gained `expiresAt`** (nullable). Existing memberships have `null` and behave
  exactly as before.
- **Bots** don't see any of this — the gateway's `GUILD_CREATE` payload carries no household data,
  and there's no Discord equivalent to map it onto.
