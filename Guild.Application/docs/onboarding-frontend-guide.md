# Guild onboarding (rules screen) — frontend integration guide

Backend support for a "read and accept the rules before participating" gate on new members is
done and live. It reuses the exact same restriction mechanism as a moderator timeout (mute) -
someone with pending onboarding can view every channel they'd normally see, but can't send
messages, react, create threads, or connect to voice until they accept.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** — never call a
microservice directly.

## Configuring onboarding (admin)

```
GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding
PUT https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding
```

Requires `Permissions.ManageGuild`.

```ts
interface OnboardingConfig {
  enabled: boolean;
  rulesText?: string;          // required (400 if missing) when enabled: true
  defaultChannelIds: string[]; // advisory only - see below
}
```

`defaultChannelIds` doesn't grant or change channel visibility by itself - it's purely a hint for
the client to highlight those channels in the onboarding UI ("check out #welcome and #general").
Actual visibility is still governed by the normal role/channel permission system, same as always.

## What a new member sees

`GET https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding/me`

```json
{ "completed": false, "rulesText": "1. Be nice\n2. ...", "defaultChannelIds": ["chan_..."] }
```

Call this right after joining a guild (or on every guild-open, it's cheap) and show a rules/accept
screen when `completed` is `false`. Once `completed` is `true`, never show it again for that
member - there's no "re-accept" flow.

## Accepting

`POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/onboarding/accept` (empty body) - `200`
on success, idempotent (accepting twice is a no-op, not an error). Restrictions lift immediately -
no need to re-fetch permissions or reconnect, the next action just works.

## What "pending" actually restricts

While `completed` is `false`, the member:
- **Can** view every channel they'd normally have access to, read message history, see who's
  online.
- **Cannot** send messages, add reactions, create threads, or connect to voice channels - these
  calls fail the same way they would for a muted member (`403`), because it's the same
  underlying mechanism.

This means the natural UX is: let them browse and read the rules channel (and anywhere else),
with sending disabled and an inline "Accept the rules to start chatting" prompt, rather than a
blocking full-screen modal - though a modal works too if that's your preferred pattern.

## Rendering guidance

- Settings screen: toggle + a text area for rules (markdown-as-plaintext is fine, no special
  formatting is enforced or stripped server-side) + a multi-select of channels for
  `defaultChannelIds`.
- New-member screen: render `rulesText` verbatim (respect whitespace/line breaks, don't try to
  parse it as anything more structured than plain text), a prominent "I understand and agree"
  action wired to the accept endpoint, and (optionally) a short list of `defaultChannelIds` as
  quick links.

## Known limitations (v1)

- Enabling onboarding is not retroactive - existing members are never gated by a rules screen
  turned on after they already joined (matches how verification levels behave too).
- No multi-step onboarding wizard (channel picks, role self-assignment, etc.) - a single
  rules-text-plus-accept step only.
- No moderator visibility into "who hasn't accepted yet" beyond what you could infer from member
  list + this endpoint per-member - no dedicated admin report.
