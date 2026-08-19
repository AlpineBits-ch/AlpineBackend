# Discord Parity Assessment - venta.gg backend

Snapshot taken 2026-07-30 against `main` (`c1c3f39`). Scope: this repository only (backend).
Client-side behaviour is out of scope except where the backend cannot support it.

Every claim below is anchored to a file. "Missing" means no domain field, no endpoint, and no
handler - not "not exposed yet".

---

## 1. Scorecard

| Area | State |
|---|---|
| Guilds / categories / channels | **On par** |
| Roles & permission overwrites | **On par** (bit-for-bit model; vocabulary is thinner) |
| Members, bans, kicks, timeouts, audit log | **On par** |
| Invites | **On par** |
| Messages (send/edit/delete/reply/react/pin) | **On par** |
| Threads & forums | **On par** for public threads; no private threads or thread membership |
| Announcements / channel follows | **On par** |
| Read state & typing | **On par** |
| Voice channels & 1:1 calls | **On par** for core; missing Discord's voice knobs |
| Onboarding / welcome screen / rules | **On par** |
| Scheduled events, templates, emojis | **Enhance** - shallow versions |
| AutoMod | **Enhance** - 2 rules vs Discord's 6 trigger types |
| Webhooks | **Enhance** - executable, but auth-gated and tokenless so no external integration can call it |
| Search | **Enhance** - single-channel, no filters |
| Bot platform | **Enhance** - commands only, no components |
| Presence / activities | **Missing** - status enum only |
| Notification settings | **Missing** entirely |
| Polls, stickers, soundboard, stage | **Missing** entirely |
| Message components (buttons/menus/modals) | **Missing** entirely |
| Ephemeral messages | **Missing** (accepted, then ignored) |
| E2E encryption, federation, wiki, household | **Beyond Discord** |

---

## 2. On par

These are genuinely equivalent in capability, not just present.

**Guild structure.** `Guild` → `Category` → `Channel` with position ordering and reorder
endpoints (`PATCH /guilds/{id}/channels/reorder`, `.../roles/reorder`). Channel types cover
Text, Voice, Forum, Media, Announcement, Thread (`Guild.Domain/Enums/ChannelType.cs`).

**Permissions.** 64-bit flag enum (`Guild.Domain/Enums/Permissions.cs`) with the same layered
resolution Discord uses: `@everyone` role → role union → guild-level member allow/deny
(`GuildMember.AllowPermissions`/`DenyPermissions`) → category overwrite → channel overwrite.
Role hierarchy is enforced by position comparison with the owner short-circuited to
`int.MaxValue` (`GuildPermissionService.GetHighestRolePositionAsync`, lines 561-614). Results
are cached per guild/channel/user in Redis with targeted invalidation.

**Moderation.** Ban/unban with reason, kick, timeout (`GuildMember.MutedUntil`, which strips
participation permissions centrally rather than at each call site), pending-member listing,
member search. Audit log covers 54 action types (`AuditActionType.cs`) with actor, target and a
JSON metadata blob.

**Invites.** Code (ambiguity-free alphabet), expiry, max uses, use count, exhaustion check,
optional landing channel, redeem + revoke (`GuildInvite.cs`, `InviteEndpoint.cs`). The 2026-08-15
round closed the gaps that made the original "on par" claim optimistic: `InviterId`, `Temporary`
membership (enforced through a scheduled sweep rather than on the raw socket drop -
`TemporaryMembershipSweepService`), invite targets (`InviteTargetType.VoiceChannel` with a redeem
response that can land the joiner in the channel), server-derived expiry on every read path
(`GuildInvite.EffectiveState` - computed, deliberately not swept), `guild.InviteCreated` /
`guild.InviteDeleted` realtime events plus their `InviteCreatedForBots` / `InviteDeletedForBots` bus
contracts, `MANAGE_GUILD`-based permissions on list and revoke, revocation instead of hard delete so
member attribution survives, and a per-caller rate limit on the unauthenticated preview. Vanity
invite URLs are covered separately below. Client contract: `Guild.Application/docs/invites-frontend-guide.md`.

Still thinner than Discord: no invite-target type for an embedded application (`STREAM` /
`EMBEDDED_APPLICATION`), no friend invites, and `INVITE_CREATE`/`INVITE_DELETE` are published on the
bus but not yet dispatched by the bot gateway.

**Messages.** Create/edit/delete, replies (`InReplyTo`), user + role + `@everyone`/`@here`
mention arrays, attachments with server-generated thumbnails, unicode **and** guild-custom-emoji
reactions (`Reaction.EmojiId`), pins, Postgres tsvector full-text search, structured embeds
stored as JSON, and system messages with randomized localized variants
(`MessageType.GuildMemberJoin` + `SystemMessageVariant`).

**Threads & forums.** Threads under Text and Forum parents, threads started from a specific
message (`Channel.StarterMessageId` / `Message.ThreadId`, since ids cannot be shared the way
Discord shares them), archive/lock/pin, tag vocabulary with `RequireTag`, forum config (layout,
sort order, default slowmode), keyset paging, and a periodic auto-archive sweep with a snapshotted
window (`Channel.AutoArchiveMinutes` - the comment there correctly explains why deriving it would
drift).

**Announcements.** Channel follows plus `POST /messaging/{messageId}/publish` - Discord's
crosspost model, implemented.

**Voice.** Cloudflare SFU-backed guild voice with self/server mute, self/server deafen, move
members, screen share (multi-track), camera, speaking state, and heartbeat-based stale-session
cleanup. 1:1 and group calls with full ring/accept/decline/timeout/alone-timeout lifecycle,
multi-device takeover, and CallKit/VoIP + FCM push.

**Onboarding.** Prompts, Channels & Roles selection, welcome screen, rules gating via
`GuildMember.OnboardingCompletedAt` with the same permission-stripping mechanism as timeouts.

**Bot platform basics.** A Discord-wire-compatible gateway (`/api/discord/v10/gateway`), REST
v10 subset, OAuth2 authorize/install, slash command registration (global + guild), and
interaction callback/followup. Real `discord.js` clients connect - the payload classes carry
notes on the exact fields discord.js dereferences unconditionally.

---

## 3. Must be enhanced - exists but is shallow or non-functional

### 3.1 Webhooks are not reachable by external integrations - highest-value gap

There *is* an execute endpoint - `POST /api/v1/webhooks/{webhookId}`
(`Guild.Application/Endpoints/WebhookEndpoint.cs:88`), which posts via `CreateMessageCommand`
with `AuthorIdType.Webhook`. But it is unusable for the case webhooks exist to serve:

- **It sits under the class-level `[Authorize]`**, so it requires a logged-in venta user. GitHub,
  Grafana, CI and every status-page integration have no account and cannot call it.
- **`WebhookConfig` has no token.** The webhook id alone is the only identifier, and it is handed
  out in the management list response - there is no credential to give an external system.
- **The path and body are venta-specific.** Discord integrations post to `/api/webhooks/{id}/{token}`;
  nothing existing points at this shape.
- **Embeds are accepted and dropped.** `WebhookRequestDto.Embeds` is populated by the caller and
  never read - the handler sets `Content` only, so rich payloads arrive blank.
- `AuthorId` is set to the caller-supplied `UserName` string rather than the webhook id, so the
  message author is an unresolvable free-text value.

Also missing: avatar override per execution, webhook types (`Guild.Domain/Enums/WebhookType.cs`
exists but `WebhookConfig` has no `Type` field), and `ManageWebhooks` as a permission (it
currently reuses `ManageChannel`).

### 3.2 Message components - bots cannot build UIs

`InteractionPayload.Type` is hardcoded to `2` (APPLICATION_COMMAND) and the file states
"no components/autocomplete, per the slash commands only v1 scope decision". Missing: buttons,
select menus, modals, autocomplete (type 4), message-component interactions (type 3), modal
submit (type 5), and user/message context-menu command types. Most non-trivial bots are
component-driven; commands-only support caps the ecosystem hard.

### 3.3 Ephemeral responses work, but only for interactions

Flag 64 is honoured. `DiscordInteractionEndpoint.SendEphemeralAsync` pushes the response over the
realtime hub to the invoking user alone and never writes it to the message store, and
`Bots.Tests` asserts that it does not. Component custom ids on an ephemeral response are kept in
`PendingInteractionStore` so the follow-up interaction still resolves.

What that does not give anyone is a per-recipient *message*. An ephemeral response is transient: it
survives no reload, appears in no history, and cannot be revealed later. Anything needing a stored
message that only some readers may see - `MessageFlags` still documents flag 64 as "not
implemented" on the Messaging side - is unbuilt, and `roleplay-guilds.md` §6 covers what it would
cost in Scylla, where `messages` is partitioned on `context_id` with no recipient dimension.

### 3.4 Slowmode is stored but never enforced

`Channel.SlowModeSeconds` is settable, imported from Discord (`RateLimitPerUser`), seeded onto
forum posts from `ForumConfig.DefaultThreadSlowModeSeconds`, and returned in DTOs - but grepping
the whole solution finds no read of it on the send path. `MessagingEndpoints.CreateMessage`
checks permissions and automod, never slowmode. Currently a no-op setting.

### 3.5 `@everyone`/`@here` is ungated

`CreateMessageDto.MentionsEveryone` / `MentionsHere` are taken from the client and written
straight through. There is no `MentionEveryone` permission in the enum and no check anywhere.
Any member of any guild can ping everyone. This is an abuse vector, not just a parity gap.

### 3.6 AutoMod is two rules

`GuildAutoModConfig` = blocked word list + messages-per-interval. Discord has six trigger types
(keyword, regex, spam, keyword-preset, mention-spam, member-profile), multiple actions per rule
(block / timeout / alert-channel), and exempt roles and channels. There is no rule collection at
all here - one config row per guild, no exemptions, so moderators are caught by their own filter.

### 3.7 Message pagination is offset-based

`MessagingController.NormalizePaging` takes `offset`/`limit`. Discord uses `before`/`after`/
`around` message-id cursors. Offset paging over Scylla drifts as messages arrive, and `around`
is what "jump to message" and permalink navigation need - neither is expressible today.

### 3.8 Search is single-scope

`SearchEndpoint` requires exactly one of `channelId`/`conversationId`, caps at 50, and supports
no filters. Discord searches guild-wide with `from:`, `mentions:`, `has:`, `before:`, `in:`, and
paginates. Guild-wide search is the common case and isn't reachable.

### 3.9 Gateway intents are recorded, not applied

`GatewaySession.Intents` and `GatewayConnection` line 125 store the value from IDENTIFY; nothing
reads it back to filter dispatches. Every connected bot receives every event it's installed for,
regardless of what it asked for. Also: no RESUME (session replay) and `shards` is hardcoded to 1.

### 3.10 Thin implementations

- **Scheduled events** - no recurrence, no cover image, no entity-type distinction beyond
  location-vs-voice-channel, RSVP is interested-only (no yes/maybe/no).
- **Emojis** - create/list/delete only, no rename; no role-restricted emoji; no
  `UseExternalEmojis` permission.
- **Bans** - no message purge on ban (Discord's `delete_message_seconds`), no temporary bans.
- **Audit log** - `Metadata` is a free-form JSON blob; there's no structured old/new change set,
  and no reason header propagated from the acting request.
- **Group DMs** - `POST /conversations` creates, `DELETE` removes; there is no add-member,
  remove-member, leave, rename, or icon. Additionally members must all be friends
  (`ConversationEndpoints.cs`, the `befriendedUserIds` check), which is stricter than Discord and
  blocks the normal "add a stranger from the game to the group" flow.

---

## 4. Missing entirely

### 4.1 Notification settings - the largest product-level hole

There is no per-guild or per-channel notification level (all messages / only mentions / nothing),
no mute-with-duration, no suppress-`@everyone`, no suppress-role-mentions, no mobile-push
override, no category-level inheritance, and no system-channel flags to suppress join messages.
`ReadState` tracks `LastReadMessageId` + `MentionCount` and nothing else. Push notifications
(`PushNotifiaction.cs`, `CallPushService.cs`) therefore have no user preference to consult. For a
chat product this is usually the top retention complaint.

### 4.2 Presence and activities

`Profile.OnlineStatus` is a 5-value enum plus `LastSeenAt`. Missing: custom status (text +
emoji + expiry), rich presence / activities ("Playing X", "Listening to Y", with party, assets,
timestamps, buttons), streaming status, per-platform presence (desktop/mobile/web), and
`GuildFeatures.Presence` is currently only a household-module flag, not this.

### 4.3 Message-level features

| Feature | Notes |
|---|---|
| **Polls** | No entity, no endpoint. |
| **Stickers** | Only referenced in `DiscordPermissionMapper` for import mapping. |
| **Soundboard** | Nothing. |
| **Message forwarding / snapshots** | No `MessageReference` concept beyond `InReplyTo`. |
| **Bulk delete** | No `POST /channels/{id}/messages/bulk-delete`. Mods must delete one at a time. |
| **Message flags** | No suppress-embeds, no silent messages, no TTS. |
| **Voice messages** | No waveform/duration on attachments. |
| **Attachment metadata** | No alt text, no spoiler flag. |

### 4.4 Voice features

No voice-channel user limit, bitrate, RTC region, or video quality mode on `Channel`. No **stage
channels** (speakers/audience/request-to-speak). No AFK channel + AFK timeout. No voice-channel
text chat. Permission gaps: `PrioritySpeaker`, `UseVAD` (voice-activity vs push-to-talk),
`RequestToSpeak`, `UseSoundboard`.

### 4.5 Permission vocabulary gaps

Present enum covers the essentials, but Discord additionally has, and this doesn't:

`ManageRoles` (currently folded into `ManagePermissions`), `ManageWebhooks` (folded into
`ManageChannel`), `MentionEveryone`, `ChangeNickname`, `ManageNicknames`, `UseExternalEmojis`,
`UseExternalStickers`, `UseApplicationCommands`, `SendTTSMessages`, `CreatePublicThreads` vs
`CreatePrivateThreads` (only a single `CreateThreads`), `ViewGuildInsights`,
`ManageGuildExpressions`.

Bits 50-62 are free, so this is additive - but each unused bit is a check that currently
resolves to a coarser permission than an admin expects.

### 4.6 Nicknames

`GuildMember.Nickname` is `init`-only and there is no endpoint to change it - not for yourself,
not for a moderator changing someone else's. It's set at join from the profile username and
frozen. Per-guild avatars and banners don't exist either.

### 4.7 Role presentation

No `Hoist` (display separately in member list), no `Mentionable`, no role icon/emoji, no linked
roles / role connections. `Role` is name + description + color + position + permissions.

### 4.8 Private threads and thread membership

Only public threads exist. No `ThreadMember` list, no join/leave/add/remove thread, no thread
member count, no "invitable" flag. `Channel.cs` documents that threads resolve permissions
identically to the parent - correct for public threads, but it means private threads can't be
layered on without a new resolution path.

A thread started from a message links the two by id in both directions rather than by sharing one,
so `MessageFlags.HasThread` is derived from `Message.ThreadId` at projection time and a client
cannot assume `thread.id == message.id` the way Discord's can. Threads are also refused in an
encrypted channel: `Channel.EncryptionState` is `init`-only and `Channel.Create` never sets it, so
the thread would be a plaintext room under an MLS parent.

### 4.9 Guild-level settings and assets

Missing on `Guild`: banner, invite splash, discovery splash, animated icon (only icon +
thumbnail via `GuildIconController`), AFK channel/timeout, default message notification level,
explicit content filter, MFA-required-for-moderation, preferred locale, NSFW level,
rules-channel and public-updates-channel designation, system channel flags.

Vanity URL is no longer missing: `Guild.VanityUrl` (unique, case-insensitive by normalization,
reserved-word checked) with a `ManageGuild` + `guild.vanity_url`-entitled set/clear endpoint, and
resolution that reaches the same invite preview a code does. It degrades rather than being destroyed
when the entitlement is lost, per monetization.md 7.3 and downgrade-2026-08-14.md 9.

### 4.10 Discovery and client organization

No server discovery/browse directory, no guild widget, no guild insights. No guild folders or
client-side guild ordering - conversations have `ReorderConversationsDto`, guilds have no equivalent.
Vanity invite URLs exist as of 2026-08-15; see 4.9.

### 4.11 Member management at scale

No prune / inactive-member cleanup. No member-list gateway streaming (Discord's lazy
`GUILD_MEMBERS` chunking) - `GET /guilds/{id}/members` is a plain paged list, which will not hold
up on a 50k-member guild.

### 4.12 Deliberate non-goals (listed for completeness)

Boosts, Nitro/premium tiers, entitlements/SKUs, monetization, and shops. `InteractionPayload`
already hardcodes `entitlements: []` with a note that the project has no monetization concept.
Not a defect - but note that Discord ties several *functional* limits to boost level (emoji
slots, upload size, audio bitrate), so an equivalent tiering mechanism may be wanted eventually.

---

## 5. Beyond Discord

Worth stating explicitly, because parity work shouldn't erode these.

- **End-to-end encryption** - MLS groups with device key packages, epochs, pending welcomes, and
  per-device key management (`Identity.Domain`, `Conversation.MlsGroupId`/`MlsEpoch`). Discord
  has nothing comparable.
- **Federation** - instance handshake, canonical-ID/shadow-entity materialization, event
  backfill, defederation (`Federation.*`).
- **Wiki** - full page/category/revision model with 9 dedicated permission bits.
- **Guild kinds & feature modules** - `GuildKind` × `GuildFeatures` lets one product serve
  communities, households, teams, study groups and events with per-guild module gating.
- **Household modules** - lists, chores with rotation, shared ledger with settlement suggestions,
  pantry with expiry, consent-based decisions. Modeled as channel types so they inherit
  permissions, ordering and unread state for free.
- **Self-hosting** - AGPL, Docker Compose, no cloud dependency for the core path.
- **Auth breadth** - QR login, Steam OpenID, MFA with recovery codes, multi-device sessions.

---

## 6. Recommended order of work

Ranked by (user-visible impact) × (effort), highest value first.

**Tier 1 - correctness and abuse, do first (small changes, real exposure)**
1. Gate `@everyone`/`@here` behind a new `MentionEveryone` permission (§3.5).
2. Enforce `SlowModeSeconds` on the send path (§3.4).
3. Add `ManageRoles` and `ManageWebhooks` as distinct bits (§4.5).

**Tier 2 - unblocks whole product areas**
4. Notification settings: per-guild/per-channel level + mute, wired into push (§4.1).
5. Webhook tokens + an anonymous, Discord-shaped execute path (§3.1).
6. Nickname endpoints + `ChangeNickname`/`ManageNicknames` (§4.6).
7. Bulk message delete (§4.3).

**Tier 3 - ecosystem and scale**
8. Message components: buttons, select menus, modals, autocomplete (§3.2).
9. Ephemeral message visibility (§3.3).
10. Cursor pagination (`before`/`after`/`around`) for messages (§3.7).
11. Gateway intent filtering + RESUME (§3.9).
12. Guild-wide search with filters (§3.8).

**Tier 4 - feature breadth**
13. AutoMod rule collections with exemptions (§3.6).
14. Presence: custom status, then activities (§4.2).
15. Role hoist/mentionable/icons (§4.7).
16. Voice channel limits/bitrate/region; then stage channels (§4.4).
17. Polls, stickers (§4.3).
18. Private threads + thread membership (§4.8).
19. Group DM member management (§3.10).
20. Guild banner/AFK/notification-level settings; discovery (§4.9, §4.10).
