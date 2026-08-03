# Docs.Generator

Produces the two documents behind **docs.venta.gg** by statically analysing the solution with
Roslyn. Nothing here talks to a running service.

```bash
dotnet run --project Docs.Generator -- Echo.sln
# or, to write straight into the gateway's wwwroot:
dotnet run --project Docs.Generator -- Echo.sln --out Echo/wwwroot/docs
```

## What it emits

| File | Consumed by | Contents |
|---|---|---|
| `asyncapi.json` | the docs page, and any AsyncAPI tooling | AsyncAPI 3 document for the realtime hub |
| `openapi-responses.json` | `Echo/Docs/ResponseOverlay.cs` | response schemas per HTTP endpoint |
| `realtime-inventory.json` | humans, diffs, CI checks | every send call site with file:line |

## Why it exists

**Realtime.** The 166 server pushes are `hub.Clients.X.SendAsync("name", payload)` string literals
scattered across eight services, and 124 of them pass an anonymous object. There is no registry to
reflect over. Anonymous types are still real types, though, so Roslyn's semantic model yields their
members exactly as it would for a named DTO.

**HTTP.** 267 endpoints return `Task<IResult>` and the solution contains no `Produces<T>` or
`[ProducesResponseType]` at all, so ASP.NET's OpenAPI generator emits every operation with empty
responses. Rather than migrate 267 signatures to typed results by hand, the generator reads the
`Results.Ok(...)` calls already in each body. That recovers a 200 body for 191 of them.

## The two things that make it correct rather than approximate

**Bind on symbols, not text.** Outbound sends are matched against
`Microsoft.AspNetCore.SignalR.ClientProxyExtensions.SendAsync`. This solution also contains
`bus.SendAsync` (Wolverine), `http.SendAsync` and the bot gateway's raw `_socket.SendAsync`; all
three match a textual search for `SendAsync` and none of them are hub sends.

**Follow indirection.** Some sends take the event name as a parameter of a fan-out helper, and the
Social handlers nest two levels deep, so resolution iterates to a fixpoint. Some endpoints are
one-line forwarders to a shared private helper (`PermissionOverwriteEndpoint` has eight). Skipping
either would silently drop 40 events and 20 endpoints — silently, because the output would still
look complete.

## Reading the output

The run reports `unresolved` counts and lists them. **A non-zero count is a hole in the published
documentation, not noise.** As of the last run: 0 unresolved realtime sends, 1 endpoint caveat
(`FederationHandshakeEndpoint` uses `Results.StatusCode` with a computed code, recorded as 200).

It also reports **shape conflicts** — one event name sent with two different payload shapes. These
are client-facing bugs rather than documentation problems: a typed client cannot deserialise both.
There are currently 6.

## Wire names

Field names in the output are **wire names, not C# names**. SignalR's JSON protocol serialises with
a camelCase policy and none of the four `AddJsonProtocol` registrations override it, so `ChannelId`
ships as `channelId`.

One caveat the generator flags but cannot fix: enums serialise as strings wherever a
`JsonStringEnumConverter` is registered — Guild, Isle, Messaging and Social — but the gateway's own
`AddSignalR()` has no converter, so an enum's representation depends on which process serialised the
message. That is a real inconsistency in the product, not just in the docs.
