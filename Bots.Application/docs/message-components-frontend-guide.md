# Message components & ephemeral replies - frontend integration guide

Buttons, select menus, modals, autocomplete, and ephemeral bot replies. Backend work is done -
this is what the client needs to build against it.

Read the [slash commands guide](./slash-commands-frontend-guide.md) first if you haven't; this is
the same model extended, and the core insight is identical.

## The core thing to understand

**venta's own client plays the part Discord's client plays.** A bot attaches components to a
message; our client renders them and, when the user clicks one, calls straight into our backend,
which turns that into the `MESSAGE_COMPONENT` interaction the bot's library expects. There is no
Discord client anywhere in the loop.

As with commands, the *result* of a click is not returned from the call. The bot receives the
interaction on its own connection, does its work, and responds - which shows up either as a normal
message, an in-place edit of the existing message, an ephemeral message, or a modal. All four are
covered below.

## Base URL

```
https://api.venta.gg/api/v1/bots/...
```

Normal `Authorization: Bearer <token>`.

---

## 1. Rendering components

Messages may now carry `componentsJson` - a JSON array in Discord's exact component shape, the
same opaque-storage convention `embedsJson` already uses. Parse and render it.

```json
[
  {
    "type": 1,
    "components": [
      { "type": 2, "style": 1, "label": "Confirm", "custom_id": "confirm_42" },
      { "type": 2, "style": 4, "label": "Cancel",  "custom_id": "cancel_42" },
      { "type": 2, "style": 5, "label": "Docs",    "url": "https://example.test" }
    ]
  }
]
```

Component types:

| `type` | What |
|---|---|
| 1 | Action row - a container, always the top level; render children in a horizontal row |
| 2 | Button |
| 3 | String select (dropdown with `options`) |
| 4 | Text input - **modals only**, never appears on a message |
| 5-8 | User / Role / Mentionable / Channel select |

Button `style`: 1 primary, 2 secondary, 3 success, 4 danger, 5 link.

Things to get right:

- **A style-5 button has a `url` and no `custom_id`.** It navigates; it must not call the
  interaction endpoint.
- `disabled: true` renders greyed and non-interactive.
- `emoji` is `{ id, name, animated }` - `id` set means a guild custom emoji, otherwise `name` is a
  unicode emoji.
- Selects carry `options`, `placeholder`, `min_values`, `max_values`.
- **Render unknown `type` values as an inert placeholder rather than throwing.** The server carries
  components opaquely and does not validate them, precisely so new Discord component types don't
  need a backend change first. Your renderer will see types it doesn't know.

## 2. Sending a component interaction

When the user clicks a button or confirms a select:

```http
POST /api/v1/bots/api/v1/guilds/{guildId}/channels/{channelId}/messages/{messageId}/component-interactions

{ "customId": "confirm_42", "componentType": 2, "values": [] }
```

`values` carries the selected option values for a select; empty for a button.

Returns `202 Accepted` - the bot's response arrives asynchronously (see §4).

| Status | Meaning |
|---|---|
| `202` | Dispatched |
| `400` | `customId` missing, or not present on that message |
| `403` | You lack `SendMessages` in that channel |
| `404` | No such message, message is in another channel, or the bot isn't installed |

Requires `SendMessages` - pressing a button is participation in the channel, so a muted or
read-only member can't drive a bot's flow.

The `customId` is validated against the components the message actually carries. Don't invent one.

## 3. Ephemeral messages - read this carefully

A bot can reply so that **only the invoking user sees it**. These arrive over the realtime
connection and are **never stored**:

```js
connection.on("guild.EphemeralMessageCreated", (msg) => {
  // { id: "ephm_...", guildId, channelId, content, embeds, components, authorId, createdAt }
})
```

Properties that matter for your implementation:

- **Not in history.** They will never come back from any `GET .../messages` call.
- **Gone on reload.** If the user refreshes, it's gone. That is correct and matches Discord.
- Render them inline in the channel, visually distinct, ideally labelled "Only you can see this".
- Keep them in local state only. Do not write them into your message cache as if they were real
  messages, or they'll appear to vanish confusingly on the next history fetch.
- The id is prefixed `ephm_` - use that to branch rendering and cleanup.

Ephemeral messages **can carry components**, and clicking them uses the same endpoint as §2 with
the `ephm_` id as `{messageId}`. That works for ~15 minutes, after which the interaction expires
and you'll get a `404` - treat that as "this prompt has expired" and disable the controls.

## 4. What arrives after an interaction

Four possible outcomes. Handle all of them:

| Bot responds with | You see |
|---|---|
| A normal message | `guild.MessageCreated` as usual |
| An in-place edit (UPDATE_MESSAGE) | `guild.MessageUpdated` - **re-render `componentsJson`**, it has probably changed |
| An ephemeral reply | `guild.EphemeralMessageCreated` (§3) |
| A modal | `guild.ModalOpen` (§5) |

The in-place edit is the common "confirm" pattern: the bot replaces the message text and clears or
disables the buttons. **Your `MessageUpdated` handler must re-read `componentsJson`** - if it only
updates `content`, buttons stay clickable after the flow has completed and users will double-submit.

A bot may also defer, in which case nothing arrives immediately. Show a pending state on the
clicked control; the real response follows within ~15 minutes (usually instantly).

## 5. Modals

```js
connection.on("guild.ModalOpen", ({ guildId, channelId, botUserId, customId, title, components }) => {
  // components: action rows wrapping type-4 text inputs
})
```

Render a form from `components`. Each text input has `custom_id`, `label`, `style` (1 = short,
2 = paragraph), `placeholder`, `required`, `min_length`, `max_length`, `value` (prefill).

On submit, echo the same structure back with `value` filled in:

```http
POST /api/v1/bots/api/v1/guilds/{guildId}/channels/{channelId}/modal-submit

{
  "botUserId": "usr_bot1",
  "customId": "feedback_modal",
  "components": [
    { "type": 1, "components": [{ "type": 4, "custom_id": "body", "value": "it works" }] }
  ]
}
```

Returns `202`. The bot's response arrives via one of the §4 paths.

Keep the action-row nesting - bot libraries read it back in that shape.

## 6. Autocomplete

For slash command options that suggest as the user types. Unlike everything else here, this one is
**synchronous**:

```http
POST /api/v1/bots/api/v1/guilds/{guildId}/channels/{channelId}/autocomplete

{
  "botUserId": "usr_bot1",
  "commandName": "weather",
  "options": [{ "name": "city", "type": 3, "value": "ber" }]
}
```

Returns the choices directly:

```json
[{ "name": "Berlin", "value": "berlin" }, { "name": "Bern", "value": "bern" }]
```

- Send **all** options typed so far, including the partial one - the bot needs the full set to
  suggest sensibly. An option the user hasn't filled in can omit `value`.
- The request blocks up to **3 seconds** waiting for the bot. A bot that doesn't answer in time
  yields `[]`, which should render as "no suggestions" - not an error.
- Debounce keystrokes (~250ms) and cancel in-flight requests. Every call parks a server request
  for up to 3s.

---

## Summary of client work

1. Render `componentsJson` on messages; degrade unknown types gracefully rather than throwing.
2. Wire clicks and select confirmations to the component-interactions endpoint.
3. Handle `guild.EphemeralMessageCreated` - render inline, keep out of the message cache, label as
   private.
4. **Re-render components in your `guild.MessageUpdated` handler**, not just content.
5. Handle `guild.ModalOpen`, render the form, post back to `modal-submit` preserving the nesting.
6. Debounced autocomplete with a 3s ceiling and an empty-result path.
7. Expire ephemeral component controls after ~15 minutes, and handle `404` as "prompt expired".
