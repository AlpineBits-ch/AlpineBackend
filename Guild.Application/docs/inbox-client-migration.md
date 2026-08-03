# Inbox rollout — what the client has to change

Shipped 2026-08-03 alongside the Inbox feature. Full endpoint reference:
[`inbox-frontend-guide.md`](./inbox-frontend-guide.md).

**Short version: one thing to fix, everything else is free.**

| | |
|---|---|
| ⚠️ **Fix** | `readState.mentionCount` is always `0` now — read counts from the inbox endpoints |
| ✅ Free | `createdAt` added to two realtime payloads |
| ✅ Free | Mentions capped at 100 and deduped server-side |
| ✅ Free | `@everyone` / `@role` over HTTP now actually notify people |
| ✅ Nothing | Sending and rendering messages is **completely untouched** |

---

## ⚠️ The one thing to fix

`readState.mentionCount` — nested in `MemberDto` and `SelfMemberDto`, so anywhere you read
`/me` or a member list — **is still in the JSON but is now always `0`.**

Same field, same type, so nothing fails to deserialize. It just quietly stops being a number.
That's why it's worth actively grepping for rather than waiting to notice.

```
grep -rn "mentionCount" src/
```

Any hit that came from a member or `/me` payload needs to move to:

| You want | Call |
|---|---|
| The header badge (total) | `GET /api/v1/guild/inbox/summary` → `mentionCount` |
| Per-channel counts | `GET /api/v1/guild/inbox/unread` → `groups[].mentionCount` |

Hits that came from an inbox response are already correct — leave them.

**Why it changed:** it used to be a stored counter incremented per mention. That stopped being
possible: an `@everyone` is now one row rather than one row per member, so there is no per-user
write left to increment. It was also never idempotent — a retried message doubled it, and a deleted
message left it high forever. Counts are computed on read now, and they're exact.

---

## Free wins (no work required)

### `createdAt` on realtime message payloads

`conversation.MessageCreated` and `guild.MessageCreated` now carry the message's **stored**
timestamp.

```jsonc
{ "messageId": "mesg_01J…", "createdAt": "2026-08-03T09:41:02.884Z", /* …unchanged… */ }
```

Purely additive. Use it instead of stamping receipt time locally if you were doing that — the two
drift by however long the message spent on the broker.

### Mentions are capped and deduped

`mentions` and `roleMentions` are truncated to **100 each** (the limit Discord documents on
`allowed_mentions`) and duplicates collapsed.

Over-mentioning **strips the extras, it does not reject the message** — the same shape as the
existing "no `MentionEveryone` permission" behaviour. Only relevant if you echo back exactly what you
sent rather than what came back.

### `@everyone` and `@role` over HTTP now work

They were being dropped between the message endpoint and the guild service, so a role ping notified
nobody. Pre-existing bug, fixed in the same pass. If you have a workaround for it, you can remove it.

---

## Explicitly unchanged

Worth stating so you don't go looking:

- **`POST /api/v1/messaging`** — request body identical, no new fields, none removed.
- **`MessageDto`** — the message response shape is byte-identical.
- Message send, edit, delete, reactions, attachments, MLS: all untouched.

The only reason to touch message code at all is the `mentionCount` grep above.

---

## New surface (optional, build when you're ready)

Six endpoints under `/api/v1/guild/inbox/`, plus `inbox.MentionAdded` and
`inbox.ReadStateChanged` over the existing hub. Nothing else depends on them — the app works exactly
as before if you ship none of it.

See [`inbox-frontend-guide.md`](./inbox-frontend-guide.md).

The one trap worth reading before you start: **an empty page with a non-null `nextCursor` means
"keep paging", not "you're done"** — muting and permission filtering are applied after the page is
taken.
