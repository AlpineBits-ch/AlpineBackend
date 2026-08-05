# Server templates - frontend integration guide

Backend support for snapshotting a guild's category/channel/role structure into a reusable
template, and creating a new guild from one, is done and live. **Permission overwrites, member
data, and messages are never captured** - a template is structure only (names, types,
descriptions, positions, and each role's permission bitmask), matching Discord's own template
scope.

All URLs below are **public, through the gateway (`https://api.venta.gg`)** - never call a
microservice directly.

## Creating a template from an existing guild

```
POST https://api.venta.gg/api/v1/guild/guilds/{guildId}/templates
{ "name": "Gaming community starter", "description": "Categories + roles for a game server" }
```

Requires `Permissions.ManageGuild`. Returns `{ id, name, description, createdAt }`. The template
is a fully independent snapshot from this point on - renaming, restructuring, or even deleting the
source guild afterward has no effect on it.

## Viewing a template

`GET https://api.venta.gg/api/v1/guild/templates/{templateId}` - no permission check beyond being
logged in (templates are meant to be shareable by id/link, same spirit as Discord's template
codes). Returns the full structure:

```ts
interface GuildTemplate {
  id: string;
  name: string;
  description?: string;
  creatorUserId: string;
  createdAt: string;
  usageCount: number;
  snapshot: {
    roles: { name: string; color: string; position: number; permissions: number }[];
    categories: { name: string; position: number; channels: TemplateChannel[] }[];
    uncategorizedChannels: TemplateChannel[];
  };
}
// TemplateChannel: { name: string; type: "Text" | "Voice" | "Forum" | "Announcement"; description?: string; position: number }
```

Use this to render a preview ("this template creates: 3 categories, 8 channels, 4 roles") before
someone commits to using it.

## Creating a guild from a template

```
POST https://api.venta.gg/api/v1/guild/templates/{templateId}/use
{ "name": "My New Server", "description": "..." }
```

Creates a brand-new guild, owned by the requesting user, with the template's categories/channels/
roles replayed on top (instead of the usual two-default-categories new-guild setup). Returns
`{ id, name }` for the new guild - treat this exactly like the response from the normal
"create guild" endpoint (same next steps: navigate into it, it's immediately usable).

Every use increments the template's `usageCount` - useful if you want to show "used 42 times" on
a template picker/gallery UI, though there's no ranking/discovery endpoint server-side (see below).

## Rendering guidance

- "Create from template" as an alternate path alongside your existing "create a new server" flow,
  taking a template id/link as input.
- A template preview screen (channel/category tree, role list with color swatches) before the
  final "Create" tap - people should see what they're about to get.
- "Save as template" as an action in server settings, calling the from-guild endpoint.

## Known limitations (v1)

- **No template directory/discovery endpoint.** There's no "browse public templates" list - a
  template is only reachable by its id, which you're expected to share as a link/code, same UX
  pattern as an invite link.
- No permission overwrites captured - a channel that was private in the source guild is public
  (default `@everyone` permissions) in a guild created from its template. Only role-level
  permission bitmasks travel with the template.
- Role ordering isn't preserved precisely - roles from a template are created in snapshot order
  but without guaranteed exact position values; re-order them after creation if it matters.
- No template versioning/update - a template is immutable once created; "updating" one means
  creating a new template.
