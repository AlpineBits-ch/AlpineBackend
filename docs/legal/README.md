<!-- LEGAL REVIEW REQUIRED -->

# Legal documents

Every file in this directory is a **placeholder**. None of it is operative legal text, none of it
has been reviewed by counsel, and none of it may be presented to a user as the terms they are
agreeing to.

## What is here and why

T1-12 of `docs/specs/privacy.md` requires that versioned legal documents exist, are served at
stable public URLs, and are content-hashed so a silent edit after publication is detectable. The
*hosting mechanism* is what was built; the *text* deliberately was not.

The spec is explicit about why:

> Write placeholders with an explicit `<!-- LEGAL REVIEW REQUIRED -->` banner and a structural
> outline only. Do not draft operative legal text — that is counsel's job, and a plausible-looking
> generated policy is worse than an obvious placeholder because it will ship.

So each document below is a heading outline with a note under every heading saying what belongs
there. A reader who opens one cannot mistake it for a policy.

## How a document becomes live

1. Counsel writes the text into a **new file** — `privacy-1.0.0.md`, never an edit of a published
   file.
2. Add an entry to `manifest.json` naming the type, the version, the effective date and the file.
3. Deploy. `LegalDocumentCatalog` hashes the file and upserts a `legal_documents` row on startup.
   The row's `Url` points at `GET /api/v1/legal/documents/{documentType}/{version}`, which serves
   these bytes.
4. Every account that consented only to an earlier version now shows that type in the
   `consentRequired` array on `GET /api/v1/users/self`. Existing `user_consents` rows are left
   exactly as they are — a consent is a record of what was accepted at a point in time, and
   rewriting it to point at a newer document would destroy the only evidence of what the user
   actually saw.

## Editing a published file

Don't. `ContentHash` is recomputed on every startup and compared with the stored row; a mismatch is
logged as an error and the stored hash is updated so the drift is visible in the audit trail rather
than silent. If the text has to change, that is a new version.
