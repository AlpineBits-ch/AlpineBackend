# Bot install links (`venta://`)

How third-party bot providers link users into installing a bot on venta.gg, and what the client
needs to build to handle that link. Modeled on Discord's "Add to Server" button.

## The idea

A bot provider puts a button on their own website:

```html
<a href="venta://install-bot?client_id=user_2c9f...&permissions=513">Add to venta.gg</a>
```

Clicking it should:

1. Open the venta.gg app (via the OS-registered `venta://` protocol handler).
2. The app shows an **"Install bot"** modal listing the servers (guilds) the logged-in user has
   `ManageGuild` in - same list Discord shows when you click its own install button - each marked
   as either **Install** or **Installed** (if the bot is already a member of that guild).
3. User picks a server → app shows a consent screen (bot name/icon/description + the permissions
   being requested, clamped to what that user is actually allowed to grant in that guild).
4. User confirms → app calls the install API → modal closes, done.

No browser round-trip, no redirect_uri dance required for the common case - the whole thing
happens inside the app, same as Discord's native client flow.

## URL scheme

```
venta://install-bot?client_id={clientId}&permissions={permissions}[&guild_id={guildId}][&redirect_uri={redirectUri}][&state={state}]
```

| Param | Required | Meaning |
|---|---|---|
| `client_id` | yes | The bot's application id (`BotApplication.BotUserId`, same string as the bot's Discord-compat client id). |
| `permissions` | no (default `0`) | Requested permission bitmask - same bit layout as `Guild.Domain.Enums.Permissions`. Bit values are listed below. |
| `guild_id` | no | If present, skip the server picker and jump straight to the consent screen for that guild. Still validated server-side - the user must have `ManageGuild` there or the install is rejected. |
| `redirect_uri` | no | If present, the app navigates here after a successful install, with `?guild_id=...&permissions=...` appended (mirrors Discord's OAuth2 redirect). Only meaningful for provider-hosted flows that need to know when install finished. |
| `state` | no | Opaque value the provider wants echoed back. Pass it through unchanged if you add `redirect_uri` support; not otherwise interpreted. |

Everything after `client_id` is optional - a provider can hand out a link with just a `client_id`
and let the user pick permissions... except **permissions are chosen by the bot developer, not the
end user** (same as Discord), so in practice a provider always includes `permissions` too.

## What the frontend needs to build

The backend REST endpoints already exist and don't need changes - this is purely a client-side
deep-link handler plus a modal:

1. **Register `venta://` as a custom URL scheme** for the app (desktop: OS protocol registration;
   mobile: `intent-filter` / `CFBundleURLTypes`; web: this only works for the installed app, not
   the browser tab - a provider linking from a plain web page should expect the OS to hand the
   `venta://` link to the installed app, and to do nothing if the app isn't installed. That
   "nothing happens" case is an accepted gap for v1, same call as not building a web fallback page.)

2. **On receiving a `venta://install-bot?...` link**, parse the query params and open the
   **Install bot modal**:
   - Fetch the user's manageable guilds: `GET /api/v1/bots/guilds/manageable` (bearer auth). This
     returns the guilds where the logged-in user has `ManageGuild` - the only guilds they're
     allowed to install a bot into.
   - Cross-reference against the bot's current installs if you have them (see below) to badge
     each row `Install` vs `Installed`.
   - If `guild_id` was present in the link, skip straight to step 3 for that guild instead of
     showing the picker.

3. **On guild selection**, fetch the consent screen contents:
   `GET /api/v1/bots/oauth2/authorize?clientId={client_id}&permissions={permissions}&guildId={guildId}`
   → returns `{ applicationId, name, iconUrl, description, guildId, requestedPermissions, grantablePermissions }`.
   Render the bot's name/icon/description and the **grantable** permission list (not the raw
   requested one - the server has already clamped it to what this user can actually grant in this
   guild, same escalation-guard math as the human "assign a role" flow).

4. **On confirm**, finalize the install:
   `POST /api/v1/bots/oauth2/authorize` with body `{ clientId, guildId, permissions, redirectUri? }`
   → `200 { guildId, grantedPermissions }` on success, or a redirect if `redirectUri` was supplied.
   Close the modal, show a success toast, done.

5. **Auth**: all of the above are normal bearer-token calls, same as every other authenticated
   endpoint in the app - if the deep link arrives while the user is logged out, show the normal
   login flow first and resume the modal afterward.

### Permission bitmask reference

Bit positions (value = `2^bit`), grouped as shown in the bot dev portal's picker - copy this table
if you need to render human-readable permission names anywhere in the modal:

```
0  ViewChannel            16 CreateThreads          32 KickMembers
1  SendMessages           17 SendMessagesInThreads  33 BanMembers
2  EditOwnMessages        18 ManageOwnThreads       34 ModerateMembers
3  EditAnyMessage         19 ManageAnyThread        35 ManageGuild
4  DeleteOwnMessages      20 ManageChannel          36 ViewAuditLog
5  DeleteAnyMessage       21 ManagePermissions      37 ManageEmojis
6  PinMessages            22 CreateInvite           38 ManageEvents
7  AttachFiles            23 ReadMessageHistory     39 PrioritySpeaker
8  EmbedLinks             24 SendVoiceMessages      40 RequestToSpeak
9  AddReactions           25 SendPolls              41 UseVoiceActivity
10 Connect                26 UseExternalEmojis      50 MentionEveryone
11 Speak                  27 UseExternalStickers    51 ManageRoles
12 Stream                 28 CreatePrivateThreads   52 ManageWebhooks
13 MuteMembers            29 UseApplicationCommands 53 ChangeNickname
14 DeafenMembers          30 CreateExpressions      54 ManageNicknames
15 MoveMembers            31 ManageExpressions
```

(Bit 63 is a guild-owner-only superadmin flag - never offer it in an install picker; the server
clamps it away for anyone but the owner anyway.)

**Bits 23-31 and 39-41 changed meaning.** They used to be the wiki permission block. Those
permissions, along with every household-module one, moved to a separate `ModulePermissions` mask
when the core mask ran out of room, and the migration that moved them cleared the old bits from
every stored value in the same transaction. An install link carries a single 64-bit core mask and
cannot request a module permission at all, so any link minted before that change which set one of
these bits will now request a different permission than it did - re-mint any stored install URLs.

## Testing without a real provider site

The bot developer portal (`/bots-portal`) has a **"Copy install link"** / **"Test install link"**
button on each bot you own, which builds the exact `venta://install-bot?...` URL above using that
bot's `client_id` and `defaultPermissions`, and either copies it or navigates to it directly -
useful for confirming the OS protocol handler is wired up correctly before asking a real bot
provider to put the button on their site.
