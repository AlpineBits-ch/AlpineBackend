# Moderation and support: client guide

What the venta clients (Alpine desktop/web, venta-mobile) have to build so that reporting, being
banned, and getting help all work. The server side is in
[`moderation-and-support.md`](./moderation-and-support.md); this is only the part that lives in the
client.

Three pieces of work, in the order they matter:

1. **Blocked sign-in.** A banned account currently gets a bare 403 from `/connect/token`. That is the
   one screen every banned user sees, and today it says nothing.
2. **Reporting.** A report flow on messages, users and guilds.
3. **Report status.** A small list in settings showing what you reported and what became of it.

---

## 0. The two hostnames

Both derive from the instance the client is already pointed at, so nothing new needs configuring -
**replace the first label** of the API host, do not prepend:

```ts
/** api.venta.gg -> support.venta.gg;  venta.gg -> support.venta.gg */
function siteHost(apiUrl: string, label: 'support' | 'docs' | 'admin'): string {
  const { hostname, protocol } = new URL(apiUrl);
  const labels = hostname.split('.');

  // A bare domain or a single label gets a prefix; a subdomain has its first label replaced.
  const host = labels.length < 3 || /^[\d.]+$/.test(hostname)
    ? `${label}.${hostname}`
    : [label, ...labels.slice(1)].join('.');

  return `${protocol}//${host}`;
}
```

Prepending is the bug the server side already shipped once: an instance on `api.venta.gg` would get
`support.api.venta.gg`, a name with no DNS, and every link in the client would go nowhere.

Self-hosted instances can override the server's copy of this with `SUPPORT_DOMAIN`. The client has
no way to read that, so **if the derived host is wrong for a deployment, the client's links are
wrong**. That is acceptable for now - the same derivation is the default on both sides - but it is
the reason a future `/api/v1/configuration` field should carry the real support URL. File that
rather than hardcoding `venta.gg` anywhere.

---

## 1. Blocked sign-in - the screen that matters most

### What the server sends

`POST /connect/token` with `grant_type=password` answers **403** with the plain-text body
`User is not allowed to sign in` when `ApplicationUser.IsSigninAllowed()` is false. That covers
`Banned`, `Inactive`, `PendingDeletion`, `PurgeInProgress` and `Deleted` - the client cannot tell
them apart from the response, and it must not guess.

> **Server change needed before this can be built properly.** The 403 body does not say which state
> the account is in, and it does not carry the ban's reference code. Until it does, the client shows
> the generic screen below with a link to the support site and no reference. Getting the reference in
> there is a follow-up on the server (`ConnectController`'s `IsSigninAllowed` branches), and this
> guide is written so the client can be built now and improved when it lands.

### The screen

Not a toast. Not a red line under the password field. A full replacement of the sign-in form, because
the user cannot proceed and retrying is not the answer.

```
┌──────────────────────────────────────────────┐
│  ⛔  You can't sign in to this account       │
│                                              │
│  Your account has been restricted by the     │
│  moderation team.                            │
│                                              │
│  We emailed the address on this account with │
│  what happened, why, and a reference code.   │
│                                              │
│  If you think this is wrong, you can appeal  │
│  once.                                       │
│                                              │
│  [ Appeal this decision ]   [ Contact us ]   │
│                                              │
│  Try a different account                     │
└──────────────────────────────────────────────┘
```

* **Appeal this decision** → `{supportUrl}/appeal`
* **Contact us** → `{supportUrl}`
* **Try a different account** returns to sign-in with the fields cleared.

### The appeal lifecycle, and what the user must be told

```
suspended / banned  ──►  appeal (once)  ──►  accepted ──► moderator lifts it ──► can sign in
                                         └─►  declined ──► final, no further appeal
```

Three things the client must not contradict:

1. **One appeal per decision.** `POST /api/v1/support/appeals` answers **409** on a second attempt.
   The body carries `status` and `final`.
2. **A declined appeal is final, and the user is told so** - in the decision email, in the 409 body,
   and on the support site's status check. If the client ever renders appeal state, render `final`
   from the response; do not derive it yourself.
3. **An accepted appeal is not instant.** Granting records the decision; a moderator then issues the
   unban as a separate step. The email says "you will get a second email once that has gone through",
   so the client must not tell someone to try signing in immediately.

Staff can still lift a restriction after a declined appeal, at their own discretion. **Do not surface
that as an action the user can take** - no "request another review" button. It is a thing that may
happen to them, not a thing they can ask for, and presenting it as the latter guarantees a stream of
requests that go nowhere.

Copy rules, because this screen is read by someone who is angry:

* Say what happened in the first line. Do not open with policy.
* Do not apologise, and do not editorialise about what they did.
* Never say "contact support" without a link that works while signed out. The support site is
  anonymous precisely so this link is reachable.
* Do not offer "retry" or "reset password". Neither helps and both read as the client not
  understanding its own state.

### Do not clear local data

A banned session must **not** wipe the local database, the MLS key material, or the stored recovery
state. Bans get lifted; an appeal that succeeds should return the account to a working client rather
than an empty one. Treat it exactly like an expired token: keep everything, refuse to sync.

---

## 2. Reporting

### `POST /api/v1/reports`

Authenticated. Body:

```jsonc
{
  "targetUserId": "user_...",          // required, always the account being reported
  "subjectKind": "Message",            // User | Message | Channel | Guild
  "subjectId": "mesg_...",             // required unless subjectKind is User
  "reason": "Harassment",              // see the list below
  "details": "free text, 4000 max",
  "evidence": { /* see below */ }      // optional, 16 KB serialised
}
```

Answers `200 { id, status, merged }`. **`merged: true` means the report folded into one this user
already filed against the same subject within 24 hours** - show "Thanks, we've added this to your
earlier report" rather than a second "Report submitted".

Refusals worth handling by `code`: `self_report`, `subject_id_required`, `evidence_too_large`,
`reason_invalid`. Everything else is a generic failure.

### Reasons, in the user's words

Show these labels, send the value on the left. Do not show the enum names.

| Value | Label |
|---|---|
| `Spam` | Spam or unsolicited advertising |
| `Harassment` | Harassment or targeted abuse |
| `HateSpeech` | Hateful conduct |
| `ViolentThreats` | Threats of violence |
| `SelfHarm` | Content promoting self-harm |
| `SexualContent` | Unwanted sexual content |
| `ChildSafety` | Content endangering a minor |
| `Impersonation` | Impersonation |
| `Malware` | Malware or malicious links |
| `IllegalContent` | Illegal content |
| `Other` | Something else |

`ChildSafety`, `SelfHarm` and `ViolentThreats` are triaged Critical server-side. The client should
not say "this will be prioritised" - it should not make promises about response time - but it is
worth putting those three where they are easy to find rather than buried under "Other".

### Evidence - read this part carefully

**Direct messages are end-to-end encrypted. The server holds ciphertext and cannot read a reported
DM.** So for an encrypted conversation, the only thing a moderator will ever see is what the
reporting client attaches. If the client sends nothing, the moderator is deciding on the reporter's
free-text description alone.

Send a snapshot of the decrypted view around the reported message:

```jsonc
{
  "capturedAt": "2026-08-05T10:14:22Z",
  "conversationId": "conv_...",
  "encrypted": true,                    // was this from an E2EE conversation
  "messages": [
    {
      "id": "mesg_...",
      "authorId": "user_...",
      "sentAt": "2026-08-05T10:12:01Z",
      "content": "…",                   // plaintext as this client rendered it
      "reported": true                  // the one being reported
    }
  ]
}
```

Rules:

* **Include context, not the whole conversation.** Roughly 10 messages before and 3 after the
  reported one. The cap is 16 KB serialised and the server refuses anything larger - truncate from
  the oldest end and drop attachments to metadata (`{ "attachment": "image/png, 2.1 MB" }`), never
  base64.
* **Never send key material, and never send another conversation.** This blob is read by staff.
* **Set `encrypted` honestly.** The console renders it as unverifiable either way, but the flag is
  what lets it say *why*.
* For unencrypted guild channels the server re-reads the message live, so evidence is optional
  there - send it anyway, since a message deleted before review is otherwise gone.

Tell the user what is being attached. A one-line "The last 10 messages in this chat will be included
so a moderator can see the context" with a way to review it. Silently uploading a slice of someone's
private conversation, even their own, is not something to do without saying so.

### Where the entry points go

| Surface | Entry point |
|---|---|
| Message | Context menu → Report message |
| DM / user profile | Overflow → Report user (also offer **Block**, above it) |
| Guild member list | Member context menu → Report member |
| Guild | Guild settings → Report this server |

**Always offer Block above Report**, and make blocking the visually primary action for harassment.
Blocking works immediately and needs nobody; reporting is a queue. A UI that only offers the queue
leaves someone waiting on us while they are still being messaged.

After submitting: a quiet confirmation, no modal celebration. Offer **Block them too** if they are
not already blocked - this is the moment it is most useful.

---

## 3. Report status

`GET /api/v1/reports/mine?limit=25` returns the reports this account filed:

```jsonc
[
  { "id": "rprt_...", "subjectKind": "Message", "reason": "Harassment",
    "createdAt": "…", "status": "UnderReview", "resolved": null }
]
```

`status` is deliberately only `UnderReview`, `ActionTaken` or `Closed`. The server collapses its
internal states, and it never returns the moderator's note or who handled it - in a harassment case,
telling the reporter what was decided is a channel straight back to the person they reported. Render
exactly those three:

* `UnderReview` → "Being reviewed"
* `ActionTaken` → "We took action"
* `Closed` → "Reviewed - no action taken"

Put this under **Settings → Privacy & Safety → Reports you've filed**. It is a low-traffic screen
whose purpose is to answer "did anything happen to that thing I reported six weeks ago", so it does
not need to be findable from anywhere else.

---

## 4. Emails the user will get

The client does not send these, but it should not contradict them.

| Trigger | Subject | Contains |
|---|---|---|
| Warning / suspension / ban | "Your account has been…" | reason, duration, reference code, appeal link |
| Ban or suspension lifted | "Your account has been restored" | - |
| Appeal decided | "Your appeal was accepted / not accepted" | the decision note |
| Support ticket opened | `[VNT-XXXXXXXX] <subject>` | reference **and the one copy of the access key** |
| Staff replies to a ticket | `Re: [VNT-…]` | the reply text |

The reference code is `VNT-` plus 8 characters from a Crockford-style alphabet with no `I`, `L`, `O`
or `U`. If the client ever renders one, use a monospace face and do not lowercase it. Any input that
accepts one should be case-insensitive and tolerant of spaces and hyphens - the server normalises,
and the client should not be stricter than the server.

---

## 5. What not to build

* **A moderation UI in the client.** The console is a separate host with its own auth; do not add a
  staff mode to the product client.
* **A ban countdown timer.** The client is not told when a suspension ends - the expiry lives in the
  moderation record, not in any response the client can see. Do not compute one from the email.
* **Auto-appeal, or a "request review" button that files a second appeal.** One appeal per decision,
  enforced server-side with a unique index; a second submission gets a 409 and the user learns
  nothing new.
* **Retrying a 403 sign-in.** It will not start working. Retrying just makes the rate limiter the
  thing they hit next.
