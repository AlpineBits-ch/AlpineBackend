# WhatsApp bridge - feasibility report

**Question asked:** can an Echo user read and write WhatsApp conversations from an Echo client, with
messages flowing both directions against their *own* WhatsApp account?

**Short answer:** yes, but only via the unofficial multi-device protocol (the route
Beeper/mautrix-whatsapp take). The official Cloud API structurally cannot do it, and the new
EU-mandated interoperability route can, but is gated behind a contract with Meta and is EU-only.
Echo-side integration is unusually cheap because the `Import.*` service is already this exact shape;
the cost and the risk both sit almost entirely in the WhatsApp session layer.

Estimated effort: **~4-5 weeks to a working 1:1 text+media MVP, ~3 months to something operable**,
one developer. Details in [Effort](#effort-and-phasing).

---

## 1. The three access routes

There are exactly three ways to get messages in and out of WhatsApp. Only one of them matches what
was asked.

### Route A - WhatsApp Business Cloud API (official)

**Does not satisfy the requirement.** Not "hard", not "expensive" - structurally the wrong product.

| Constraint | Consequence for a bridge |
|---|---|
| Number must be registered to a WhatsApp Business Account | It stops working in the normal WhatsApp app. It is not *your* WhatsApp account any more. |
| You cannot initiate a conversation with an arbitrary contact | You can only reply to people who message *your* business number first. |
| 24-hour service window | Outside it, only pre-approved template messages - and those are billed per delivered message (marketing templates run ~$0.025 US / ~$0.12 DE / ~$0.0094 IN). Free-form replies inside the window are free. |
| No group chats | Roughly half of real WhatsApp usage is invisible. |
| Templates need Meta approval | Every canned outbound string is a review cycle. |

Cloud API is the right tool if the product goal were "Echo as a customer-support inbox for a
business's WhatsApp number." It is the wrong tool for "my WhatsApp, in my client."

### Route B - Unofficial multi-device protocol (whatsmeow / Baileys)

**This is the only route that delivers the asked-for behaviour today.**

The user links Echo as a *companion device* - the same mechanism as WhatsApp Web/Desktop - by
scanning a QR code or entering a pairing code. From there the bridge is a full participant: 1:1 and
group chats, media, replies, reactions, read receipts, presence, edits, deletions. This is what
[mautrix-whatsapp](https://github.com/mautrix/whatsapp) and Beeper run in production, on top of
[whatsmeow](https://github.com/tulir/whatsmeow).

The costs are real and non-negotiable:

- **It violates WhatsApp's ToS.** Ban risk is real but empirically moderate for ordinary usage;
  community guidance is that the bridge alone rarely triggers a ban, and bans cluster around
  correlated signals - VoIP numbers, brand-new accounts, emulators, cold-DMing non-contacts. You
  cannot promise users their account is safe, and a ban takes their real WhatsApp with it, not just
  the bridge.
- **Protocol churn.** WhatsApp changes the wire format without notice. You are pinned to whatever
  cadence upstream whatsmeow ships at, and a stale bridge just stops working.
- **No .NET implementation.** whatsmeow is Go, Baileys is Node. There is no viable C# port and
  writing one is not a realistic line item. **This forces a sidecar process** - see
  [§3](#3-the-sidecar-is-not-optional).

### Route C - DMA third-party interoperability (official, EU-only)

Meta shipped this in **November 2025**; BirdyChat and Haiket are the first two live third-party
services. This is the legitimate long-term answer and it is worth designing towards, but it does not
solve the problem now:

- Requires formally accepting Meta's published **reference offer** - a real contract with
  technical, security and data-protection obligations, signed by a legal entity operating a
  messaging service for EU end users.
- **European Region only.** Users outside it get nothing.
- **Both sides must opt in.** The WhatsApp user has to enable third-party chats in Settings, and
  they land in a separate section of their inbox, not their normal chat list.
- **Year one is 1:1 only** - text, images, voice, video, files. Groups arrive "once partners are
  ready"; calls are not in scope.
- You must implement **E2EE at parity with WhatsApp** (Signal protocol), which means real crypto
  work, not just an API client.

### Recommendation on route

Build **Route B**, but put the WhatsApp session behind a narrow internal port (`IWhatsAppSession`:
link, unlink, send, receive-stream, media fetch) so that Route C can be swapped in underneath for EU
users later without touching a line of Echo-side code. That interface decision costs nothing now and
is the difference between "we can go legitimate in a quarter" and "we rewrite the feature."

---

## 2. Echo-side fit - this part is genuinely easy

The `Import.*` service is a working precedent for almost every piece of this. It already does:
persistent outbound WebSocket to a foreign platform with backoff and resume
(`Import.Application/Gateway/DiscordGatewayClient.cs`), a durable link entity
(`Import.Domain/Entity/GuildLink.cs`), foreign-ID↔Echo-ID mapping (`ImportEntityMapping`), Redis
job/cursor state (`DiscordImportStateStore`), and a periodic reconciliation service. A
`Bridge.WhatsApp.*` service is the same skeleton with a different foreign platform.

**Proposed shape** - mirroring the existing four-project convention
(`Application`/`Contracts`/`Domain`/`Infrastructure`), plus one new container in `deploy/compose.yaml`
alongside `import:`.

### What maps cleanly onto existing primitives

| Bridge need | Existing mechanism | Work |
|---|---|---|
| WA chat → Echo conversation | `Conversation` + `ConversationMember` | Reuse as-is |
| WA contact → Echo author | `Message.AuthorDisplayName` / `AuthorAvatarUrl` per-message overrides (added for webhooks) | Reuse, or add `AuthorIdType.Bridge` alongside `User`/`Bot`/`Webhook` |
| Foreign participants in a local conversation | Federation's shadow-member pattern - `ConversationMember.FederatedServerId`, empty `PublicKey`, `CachedUserName` fallback (`ConversationMaterializationHandlers.cs`) | Direct precedent, copy the approach |
| Media | `FileService` → S3/MinIO, `ProcessAttachmentHandler`, `MinimalAttachment` + thumbnails | Sidecar decrypts WA blob, hands bytes to the existing pipeline |
| Delivery to clients | `MessageCreated` → `MessageCreatedHandler` → SignalR `EchoRealtimeHub` + `MessagePushService` | Zero change - inbound bridge messages ride the normal path |
| Echo → WA outbound | New Wolverine subscriber on `MessageCreated`, filtered to mapped conversations | Small |

The per-message display-name/avatar override is the single most useful thing already in the schema:
it means a WhatsApp contact can render correctly in a group without minting a real `ApplicationUser`
per contact.

### The two genuinely hard Echo-side problems

**(a) It collides head-on with MLS.** Echo conversations can be `ChannelEncryptionState.Encrypted`,
with per-generation MLS groups and client-held keys - the server cannot read them by design
(`Conversation.MlsGroupId`/`MlsEpoch`, `MlsGeneration` on every message). A server-side bridge cannot
join an MLS group; it has no device, no key package, and manufacturing one would quietly break the
encryption guarantee for everyone in the conversation.

The only sane resolution: **bridged conversations are `Plain`, and the client says so explicitly.**
That is defensible - the WhatsApp leg is E2EE either way, and terminating it server-side is exactly
what every bridge including Beeper does - but it must be a visible product decision, not an
implementation detail. Users of an E2EE-branded app will notice.

The alternative, running the bridge *in the client* as a real MLS device, preserves the property but
means a Go runtime inside every mobile client, per-device sessions, and no delivery while the app is
closed. Not recommended.

**(b) One WebSocket per linked user, not one per service.** This is the operational difference from
`Import`, and it is the one that bites. Discord's gateway client is a single socket for the whole
instance. WhatsApp is a persistent authenticated session **per linked account**: 1,000 users = 1,000
live sockets plus 1,000 encrypted session blobs to store, restore and keep warm. That forces:

- Session credentials in an encrypted-at-rest column - this is bearer access to a user's WhatsApp.
- Ownership leasing in Redis so exactly one pod holds a given user's socket. The service cannot be
  scaled by naively adding replicas.
- Real reconnect/backoff/logged-out-detection per session, and a UI state for "your WhatsApp
  session died, re-scan."

**(c) Loop suppression.** Every message needs an origin tag plus a WA-msg-id↔Echo-msg-id mapping row,
checked on both edges. The federation materialization handlers already flag echo risk as a known
hazard in this codebase; the same discipline applies, with the extra wrinkle that WhatsApp echoes
your own sent messages back to your companion devices.

---

## 3. The sidecar is not optional

whatsmeow is Go. Echo is .NET. The bridge therefore looks like:

```
Echo client ──► Messaging.Application ──► RabbitMQ/Wolverine ──► Bridge.WhatsApp.Application (.NET)
                                                                          │  gRPC / unix socket
                                                                          ▼
                                                              whatsapp-sidecar (Go + whatsmeow)
                                                                          │  WhatsApp multi-device WS
                                                                          ▼
                                                                     WhatsApp
```

The .NET service owns links, mappings, Echo-side writes and the bus. The Go sidecar owns *only* the
WhatsApp session and speaks a thin internal protocol. Keeping that boundary narrow is what makes
Route C substitutable later, and what keeps whatsmeow's upgrade churn from leaking into your domain
code.

Cost: one more container image, one more language in the build, a `whatsapp:` service in
`deploy/compose.yaml`, and Go in CI. Given `deploy/` already orchestrates ten services, this is
incremental, not structural.

---

## 4. Effort and phasing

One developer, assuming familiarity with the codebase.

| Phase | Scope | Estimate |
|---|---|---|
| 0 - Spike | whatsmeow sidecar, QR pair one account, receive one message, send one message. Hardcoded, no persistence. **De-risks everything before real budget is committed.** | ~1 week |
| 1 - MVP | Link/unlink flow + endpoints, session persistence, `WhatsAppLink` + `BridgeMessageMapping` entities, 1:1 text both directions, loop suppression, puppet rendering via display-name override | 3-4 weeks |
| 2 - Parity | Media both ways through `FileService`, replies, reactions, read receipts, typing, edits/deletes, contact + avatar sync | 2-3 weeks |
| 3 - Groups | Group chats, membership sync, history backfill on link | 3-4 weeks |
| 4 - Operable | Session HA and Redis ownership leasing, reconnect/ban detection, per-user isolation, metrics, alerting, credential encryption review | 2-3 weeks |

**Demo-able in ~5 weeks. Something you'd let strangers use in ~3 months.** Phase 4 is the one that
gets cut under pressure and shouldn't be - an unattended bridge that silently stops delivering
messages is worse than no bridge.

---

## 5. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| User's WhatsApp account banned | **High** - it's their real account, not a service account | Prominent consent screen at link time; never auto-DM; no bulk sends; document it honestly |
| Protocol change breaks the bridge | **High** - recurring, not one-off | Pin + track upstream whatsmeow; alert on session-failure rate; budget ongoing maintenance, this is not fire-and-forget |
| Meta legal action / ToS enforcement | Medium | Route C as the escape hatch; consider shipping this self-hosted-only or opt-in first |
| GDPR: you now store decrypted messages from third parties who never agreed to your ToS | **High, and under-appreciated** | Retention policy, encryption at rest, DPIA, delete-on-unlink. Get advice before EU launch. |
| App store policy | Medium | Beeper ships bridges on both stores, so precedent exists - but review it before submission |
| E2EE brand damage (bridged chats are server-readable) | Medium | Explicit per-conversation UI labelling; do not let it look like the MLS conversations |
| Session-credential compromise | **High** - a leak is full WhatsApp account takeover for every linked user | Encrypted column + KMS, no plaintext logging, tight blast radius on the sidecar container |

---

## 6. Verdict

Technically feasible and architecturally a good fit - the `Import.*` service already proves the
pattern, and the messaging domain already has the primitives (per-message author overrides, shadow
members, an attachment pipeline, realtime fan-out) that usually make bridges expensive.

The engineering isn't the hard part. The hard parts are:

1. **Deciding that bridged conversations are not E2EE**, and saying so in the UI.
2. **Accepting an unofficial protocol** with ban risk borne by the user and a permanent maintenance
   tail.
3. **The per-user socket model**, which changes how this service scales and deploys.

If those three are acceptable, do Phase 0 first. A one-week spike answers more than any further
analysis will, and the interface it forces you to define is the same one Route C would plug into.

---

### Sources

- [Pricing on the WhatsApp Business Platform - Meta for Developers](https://developers.facebook.com/documentation/business-messaging/whatsapp/pricing)
- [WhatsApp Business API Pricing 2026: Conversation Categories, Costs, and What Changed](https://blueticks.co/blog/whatsapp-business-api-pricing-2026)
- [Webhooks overview - Meta for Developers](https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/overview)
- [Messaging Interoperability: WhatsApp enables third-party chats for users in Europe - Meta (Nov 2025)](https://about.fb.com/news/2025/11/messaging-interoperability-whatsapp-enables-third-party-chats-for-users-in-europe/)
- [Making messaging interoperability with third parties safe for users in Europe - Engineering at Meta](https://engineering.fb.com/2024/03/06/security/whatsapp-messenger-messaging-interoperability-eu/)
- [BEREC opinion on Meta's reference offers under Article 7 DMA](https://www.berec.europa.eu/en/all-documents/berec/opinions/berec-opinion-on-metas-reference-offers-to-facilitate-messenger-and-whatsapp-interoperability-under-article-7-of-the-digital-markets-act)
- [tulir/whatsmeow - Go library for the WhatsApp web multidevice API](https://github.com/tulir/whatsmeow)
- [mautrix/whatsapp - Matrix-WhatsApp puppeting bridge](https://github.com/mautrix/whatsapp)
- [mautrix-whatsapp authentication docs](https://docs.mau.fi/bridges/go/whatsapp/authentication.html)
- [Baileys - WhatsApp Web API](https://whiskeysockets-baileys-94.mintlify.app/introduction)
