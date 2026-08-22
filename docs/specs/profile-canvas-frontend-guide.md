# Profile canvas - frontend integration guide

The profile canvas is a four-column grid of user-arranged widgets shown on a profile page, plus the
two-widget card preview in the profile popout.

## Base URL and the path rule

```
https://api.venta.gg
```

Social declares its routes without the service segment; the gateway inserts it after `/api/v1`.

| Social declares | You call |
|---|---|
| `/api/v1/profiles/{profileId}/canvas` | `/api/v1/social/profiles/{profileId}/canvas` |
| `/api/v1/profiles/me/canvas` | `/api/v1/social/profiles/me/canvas` |
| `/api/v1/canvas-images/{imageId}` | `/api/v1/social/canvas-images/{imageId}` |

Normal `Authorization: Bearer <token>` throughout, except `GET /canvas-images/{imageId}`, which is
anonymous.

## Routes

```http
GET    /api/v1/social/profiles/{profileId}/canvas          -> ProfileCanvasDto
PUT    /api/v1/social/profiles/me/canvas                   -> ProfileCanvasDto
POST   /api/v1/social/profiles/me/canvas/images            -> CanvasImageDto
DELETE /api/v1/social/profiles/me/canvas/images/{imageId}  -> 204
GET    /api/v1/social/canvas-images/{imageId}              -> 302 to the image
```

`me` resolves from the caller's token. There is no route that writes a canvas by profile id, so a
caller can only ever write their own.

`GET` returns **404** for a profile that has never saved a canvas. That is the normal answer for a
profile with no canvas, not an error; treat it as an empty canvas. A blocked pair also gets 404 in
both directions.

`GET` sets `Cache-Control: private, max-age=0, must-revalidate`: the body is stripped for the
calling viewer specifically and must not be shared between users.

## Wire shapes

```ts
type CanvasVisibility = 'everyone' | 'friends' | 'mutuals';

interface CanvasBackdrop {
    kind: 'gradient' | 'image';
    from?: string;      // gradient stop, ignored when kind is image
    to?: string;        // gradient stop, ignored when kind is image
    imageId?: string;   // canvas image id, ignored when kind is gradient
}

interface CanvasTheme {
    accent: string | null;          // null falls back to the profile's accentColor
    backdrop: CanvasBackdrop | null;
}

interface CanvasWidgetDto {
    id: string;
    type: string;                   // not an enum: an unknown type draws nothing
    x: number;
    y: number;
    w: number;
    h: number;
    visibility: CanvasVisibility;
    card: boolean;                  // drawn in the popout, at most 2 per canvas
    config: unknown;                // opaque, owned by the widget type
}

interface ProfileCanvasDto {
    profileId: string;
    updatedAt: string;              // ISO 8601
    version: number;
    theme: CanvasTheme;
    widgets: CanvasWidgetDto[];
}

interface CanvasWriteDto {          // the PUT body
    theme: CanvasTheme;
    widgets: CanvasWidgetDto[];
}

interface CanvasImageDto {
    imageId: string;
    url: string;
}
```

`config` is stored as JSON and returned byte-identically. The server does not model it, does not
validate its inner shape, and does not strip unknown fields, so a new widget type ships without a
server change.

`profileId`, `updatedAt` and `version` are server-owned. `version` starts at 1 and increments on
every accepted PUT; `updatedAt` is set on every write.

## Limits

A write that breaks any of these is refused with `400` and a plain-text message naming the field,
for example `widgets[3].h must be a finite non-negative integer.` Nothing is silently truncated: the
client caps the same numbers, so a disagreement should be visible rather than cost a widget.

| Limit | Value |
|---|---|
| Columns | 4, so `x + w <= 4` |
| Non-spacer widgets per canvas | 20 |
| Spacers (`type: "spacer"`) per canvas | 20 |
| Widgets with `card: true` | 2 |
| Allowed `(w, h)` footprints | 1x1, 2x1, 2x2, 4x1, 4x2 |
| `x`, `y`, `w`, `h` | finite non-negative integers; NaN and Infinity are refused |
| `visibility` | `everyone`, `friends` or `mutuals` |
| `config` per widget | 8192 bytes of UTF-8 |
| Widget `id` / `type` length | 64 characters |
| Canvas images per profile | 8 |
| Image upload | 8 MB, `image/png`, `image/jpeg`, `image/webp`, `image/gif` |
| Duplicate widget `id` | refused |

`theme.accent`, `theme.backdrop.from` and `theme.backdrop.to` must be hex colours like `#5865F2` or
null. `theme.backdrop.imageId` is required when `kind` is `image`.

## Visibility

`visibility` is enforced on the server. A widget the viewer is not entitled to is **absent from the
response body**, not present and hidden, so the client's preview modes stay a convenience rather
than a boundary.

| `visibility` | Who receives the widget |
|---|---|
| `everyone` | every reader |
| `friends` | the owner, and profiles holding an accepted friendship with them |
| `mutuals` | the owner, and readers who share a guild with them or have a friend in common |

The owner always receives their whole canvas.

Stripping leaves the surviving widgets' coordinates alone, holes included. The client re-packs on
render.

## Images

`POST /profiles/me/canvas/images` takes a multipart body with the field name `file` and answers

```json
{ "imageId": "cnvi_01K...", "url": "https://api.venta.gg/api/v1/social/canvas-images/cnvi_01K..." }
```

`GET /canvas-images/{imageId}` is anonymous and redirects to a presigned URL with
`Cache-Control: public, max-age=<remaining window>`, the same treatment avatars and banners get.

`DELETE /profiles/me/canvas/images/{imageId}` answers `204` whether or not the image was still
there. It removes the blob and, if `theme.backdrop.imageId` named it, clears the backdrop and saves
the canvas, which bumps `version` and emits the realtime event.

## Realtime

Event name `social.ProfileCanvasUpdated`, pushed over the single hub on every accepted PUT:

```ts
{ profileId: string, canvas: ProfileCanvasDto }
```

The canvas in the event is stripped **per recipient**, exactly like a GET by that recipient. The
owner receives their full canvas; each recipient receives their own view.

The event reaches the owner and the owner's accepted friends, capped at 200 friends per save. A
reader who is neither picks the change up on their next GET.
