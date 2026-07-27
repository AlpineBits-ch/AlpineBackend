# Gateway compat layer - real discord.js smoke test

Opt-in, manually-triggered check that an unmodified `discord.js` bot can point at venta's
Discord-compat surface with nothing but a base-URL change. This is the actual acceptance bar for
the Gateway work (`Bots.Application/Gateway/*`) - `Bots.Tests/Gateway/GatewayLiveE2ETests.cs` is a
hand-rolled `ClientWebSocket` equivalent of the same check, useful for CI-adjacent runs since it
doesn't need Node, but this script is what actually proves real-library compatibility.

Not part of `dotnet test` or any CI pipeline - it's a separate Node script you run by hand.

## Setup

```
npm install
```

## Run

Either export credentials:

```
BOTS_E2E_CLIENT_ID=user_xxx BOTS_E2E_CLIENT_SECRET=xxx node smoke-test.mjs
```

...or populate `../.e2e-credentials.local.json` (git-ignored, shared with `GatewayLiveE2ETests`):

```json
{
  "clientId": "user_xxx",
  "clientSecret": "xxx",
  "baseUrl": "https://api.venta.gg"
}
```

```
node smoke-test.mjs
```

## What it checks

- `client.login()` completes (HELLO -> IDENTIFY -> READY over the real Gateway wire protocol).
- `client.user`/`client.guilds` populate correctly from our READY + GUILD_CREATE dispatch.
- If a message is sent in a channel the bot can see within the 20s listen window after READY,
  `messageCreate` fires with the right channel/author/content - confirming MESSAGE_CREATE dispatch.

A bot that isn't installed in any guild yet still passes (0 guilds is a valid state) - the point
is the handshake itself, not guild membership.
