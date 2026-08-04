<!-- LEGAL REVIEW REQUIRED -->

# Cookie and Local Storage Notice — PLACEHOLDER, NOT IN FORCE

**This document is a structural outline. It is not a cookie notice, it has not been reviewed by
counsel, and it makes no representation to any user.**

Version: `0.1.0-placeholder`

---

## 1. What this covers

*To be drafted: cookies, local storage, session storage, and any device-local identifiers used by
the web client and the desktop/mobile clients.*

## 2. Strictly necessary storage

*To be drafted. Must be enumerated from the actual client, not assumed — at minimum the auth
token/refresh token storage and the client-owned UI state blob (`GET`/`PUT
/api/v1/users/self/settings`), which is server-stored rather than a cookie and carries no server
semantics.*

## 3. Functional storage

*To be drafted.*

## 4. Analytics and measurement

*To be drafted. There is no analytics pipeline today. If one is added it is gated on
`AllowPersonalization` (T0-4) and must appear here before it ships.*

## 5. Third-party storage

*To be drafted: anything set by an embedded third party, including the error reporter.*

## 6. How to control it

*To be drafted: the in-product control, and the browser-level control.*

## 7. Changes

*To be drafted, matching the versioned-consent mechanism.*
