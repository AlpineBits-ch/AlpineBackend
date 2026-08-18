# Server-rolled dice - frontend integration guide

Dice are rolled on the server and recorded, so a result is not the roller's to influence. A roll is
an ordinary message with `type: "DiceRoll"` plus a side record in Guild, which means a client that
knows nothing about dice still renders the result as text.

All URLs are public, through the gateway (`https://api.venta.gg`), under `/api/v1/guild/**`. The
paths below are service-internal, so `/api/v1/guilds/{guildId}/channels/{channelId}/rolls` is
reached as `/api/v1/guild/api/v1/guilds/{guildId}/channels/{channelId}/rolls`.

## Roll

```
POST /api/v1/guilds/{guildId}/channels/{channelId}/rolls
{ "expression": "4d6kh3+2", "personaId": null, "reason": "Perception", "visibility": "Public" }
```

Requires the `Dice` module, the `RollDice` module permission, and `SendMessages` on the channel,
because a roll is a post. `personaId` and `reason` are optional; `visibility` defaults to `Public`
and is the only value accepted (see below).

**200** with the recorded roll:

```json
{
  "id": "roll_...",
  "messageId": "mesg_...",
  "channelId": "chan_...",
  "rollerUserId": "user_...",
  "personaId": "pers_...",
  "expression": "4d6kh3 + 2",
  "reason": "Perception",
  "total": 16,
  "visibility": "Public",
  "breakdown": "4d6kh3 (6, 5, 3, ~1) + 2",
  "terms": [
    { "notation": "4d6kh3", "sign": 1, "constant": null,
      "dice": [6, 5, 3, 1], "kept": [6, 5, 3], "subtotal": 14 },
    { "notation": "2", "sign": 1, "constant": 2, "dice": [], "kept": [], "subtotal": 2 }
  ],
  "createdAt": "2026-08-18T12:00:00Z"
}
```

`expression` is the server's normalization, not the text that was sent. Render that rather than the
user's input, so what is shown is what was rolled.

| Status | What happened |
|---|---|
| `400` | The expression is not valid notation, or breaks a bound. The body is a one-sentence reason - show it. |
| `400` | `visibility` was not `Public`. |
| `403` | No `Dice` module, no `RollDice`, no `SendMessages`, or a persona the caller may not speak as. |
| `404` | No such channel in this guild. |

## The notation

Case-insensitive, whitespace ignored.

```
expression := term (('+' | '-') term)*
term       := constant | pool
pool       := [count] 'd' sides modifier*
modifier   := ('kh' | 'kl' | 'dh' | 'dl') [n] | '!' | 'adv' | 'dis'
```

| Form | Means |
|---|---|
| `2d6` | Roll two six-sided dice. |
| `d20` | Count defaults to one. |
| `3d6+2d4-1` | Arithmetic between terms; a leading `-` applies to the first term. |
| `4d6kh3` | Keep the highest three. `kl` keeps the lowest. |
| `4d6dl1` | Drop the lowest one. `dh` drops the highest. The count defaults to one, so `4d6dl` is the same thing. |
| `1d10!` | Exploding: a die showing its maximum face is rolled again and added into that same die. |
| `1d20adv` | Advantage: rolls two and keeps the highest. `dis` keeps the lowest. |

`adv` on a pool of one raises it to two, since keeping the highest of one die would change nothing.
`2d20adv` is therefore the classic advantage roll. Only one keep or drop mode per term, and `adv`
counts as one of them.

Explosions resolve into the die that caused them, so a keep or drop mode compares whole dice. In
`terms[].dice` each entry is one die with whatever it exploded into already added.

`breakdown` marks a die that did not count with a leading `~`. Beyond forty dice it ends in `...`.

### Bounds

Every one of these is refused before anything is rolled.

| Limit | Value |
|---|---|
| Expression length | 128 characters |
| Terms | 16 |
| Dice in one term | 100 |
| Dice in the whole expression | 100 |
| Die sides | 2 to 1000 |
| Constant | 1,000,000 |
| Explosion chain per die | 10 |

A `d1` is refused rather than special-cased: it always shows its maximum face, so an exploding one
would never terminate.

## The message

The roll is posted as a message with `type: "DiceRoll"`, authored by the real account. `content` is
the plain-text line, for example:

```
Perception: 4d6kh3 (6, 5, 3, ~1) + 2 = 16
```

`embedsJson` carries one Discord-shaped embed so an existing embed renderer shows the same thing,
with the structured roll on an extra `dice` member that such a renderer ignores:

```json
[{ "type": "rich", "title": "Perception", "description": "4d6kh3 (6, 5, 3, ~1) + 2",
   "fields": [{ "name": "Expression", "value": "4d6kh3 + 2", "inline": true },
              { "name": "Total", "value": "16", "inline": true }],
   "dice": { "expression": "4d6kh3 + 2", "total": 16, "breakdown": "...", "terms": [] } }]
```

Read `dice` if you render rolls richly; fall back to `content` if you do not. Never re-derive the
total from `terms` - the server's `total` is the recorded one.

## Rolling in character

`personaId` resolves the same way the send path does: an explicit id wins, otherwise the channel's
autoproxy state decides. A dice expression is notation rather than prose, so no proxy prefix is
matched in it - a character who should roll must be named explicitly or be the channel's autoproxy
persona.

The message carries `authorDisplayName`, `authorAvatarUrl` and `personaId`; `authorId` stays the
real account, so blocking, moderation and reply-pings are unaffected.

## Known limitations (v1)

- **Public rolls only.** `visibility` of `GameMasterOnly` or `Blind` is refused with a `400` rather
  than accepted and shown to everybody anyway. Do not offer the option until the server takes it.
- **No sheet-linked rolls.** `@sheet.perception` reading a character page's infobox is not in the
  notation yet.
- **No read route.** The roll is returned by the POST and carried on the message; there is no
  endpoint to fetch one back by id.
- **No roll history.** The record is stored, but nothing lists it.
