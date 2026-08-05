# Specs

Cross-cutting design documents and the client guides that go with them.

**Where a document lives.** A feature owned by one service keeps its client guide next to that
service - `Guild.Application/docs/`, `Bots.Application/docs/`. Anything spanning services, or owned
by the gateway, is here. If you are looking for a guide and it is not in this list, try
`*/*.Application/docs/`.

## Client and frontend guides

What the venta clients (Alpine desktop/web, venta-mobile) have to build.

| Guide | Covers |
|---|---|
| [moderation-frontend-guide.md](./moderation-frontend-guide.md) | **Banned / suspended sign-in screen**, the in-client report flow, evidence snapshots, report status |
| [privacy-frontend-guide.md](./privacy-frontend-guide.md) | Privacy settings, consent prompts, data export and deletion |
| [message-previews-frontend-guide.md](./message-previews-frontend-guide.md) | Rendering link embeds |
| [device-identity-consolidation-client-guide.md](./device-identity-consolidation-client-guide.md) | The single device identity, `X-Device-Id` |
| [registration-contract-change.md](./registration-contract-change.md) | Migrating off the old signup payload |
| [multi-device-voice-and-calls.md](./multi-device-voice-and-calls.md) | Calls and voice channels across a user's devices |
| [social-friend-realtime.md](./social-friend-realtime.md) | `social.*` websocket events for friend requests |

## Design and implementation

| Spec | Covers |
|---|---|
| [moderation-and-support.md](./moderation-and-support.md) | Moderation console, support site, reports, bans, appeals, tickets |
| [privacy.md](./privacy.md) | Settings, consent records, data-subject rights |
| [message-previews.md](./message-previews.md) | The Unfurl service |
| [inbox.md](./inbox.md) | Inbox and mention fan-out |
| [guild-onboarding-parity.md](./guild-onboarding-parity.md) | Onboarding prompts, Channels & Roles, welcome screen |
| [discord-parity.md](./discord-parity.md) | Standing assessment of what is and is not implemented |

## MLS and encryption

Encryption is **per conversation and opt-in** - `Conversation.EncryptionState` starts at `Plain`.
These describe how it works when it is on.

| Spec | Covers |
|---|---|
| [mls-hardening-plan.md](./mls-hardening-plan.md) | The hardening work and its rationale |
| [mls-hardening-contract.md](./mls-hardening-contract.md) | Cross-repo API contract clients implement against |
| [mls-security-findings.md](./mls-security-findings.md) | Consolidated review findings |
| [mls-remaining-work.md](./mls-remaining-work.md) | What is still open |

## Feasibility reports

Written to decide whether to build something. Neither is built.

| Report | Question |
|---|---|
| [global-search-feasibility.md](./global-search-feasibility.md) | Can we search messages across an instance? |
| [whatsapp-bridge-feasibility.md](./whatsapp-bridge-feasibility.md) | Can we bridge WhatsApp? |
