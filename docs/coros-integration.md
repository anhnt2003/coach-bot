# COROS integration: verified status and what is still needed

CoachBot uses only the **official COROS MCP server** ([coroslab/COROS-MCP](https://github.com/coroslab/COROS-MCP)).
No unofficial APIs, scraping or stored COROS passwords. Until the real integration is verified with your account,
the default is `Coros__Mode=mock`, and all mock data is stored with `source = 'mock'` so it is never confused with
real data (history queries only read rows from the active source).

## Verified live (2026-10-21, against production COROS endpoints)

| Check | Result |
|---|---|
| Protected-resource metadata (`/.well-known/oauth-protected-resource/mcp`) | ✅ resource `https://mcpus.coros.com/mcp`, auth server `https://mcpus.coros.com` (the global URL routes to the US region) |
| Authorization-server metadata | ✅ Spring-style endpoints: `/oauth2/authorize`, `/oauth2/token`, `/oauth2/device_authorization`, `/connect/register`; PKCE S256; scopes `openid mcp.tools offline_access` |
| Unauthenticated `/mcp` | ✅ `401` with `WWW-Authenticate: Bearer resource_metadata=…` |
| Dynamic client registration (RFC 7591) | ✅ succeeds. **The server registers every client as public** (`token_endpoint_auth_method: none`, no secret) **with only `authorization_code` + `refresh_token`**, even when the device grant or a secret is requested |
| Device authorization grant | ❌ `401` for dynamically registered clients (advertised in metadata, but not granted to DCR clients) → CoachBot uses **browser login with PKCE** instead |
| Authorization request (PKCE S256 + RFC 8707 `resource`, localhost redirect URI) | ✅ accepted: `302` to the COROS account sign-in (`openus.coros.com`). Approval itself not completed yet |

## Not yet verified (needs you to approve access once)

| Item | How it gets verified |
|---|---|
| Token exchange + refresh (offline_access) | `coros login` → approve in browser → "Authorized" |
| `tools/list` with a real token (34 tools documented) | printed by `coros login` |
| **Output field mapping** of read tools (`querySportRecords`, `queryDailyHealthData`, `querySleepHrv`, `querySleepOverview`, `queryRecoveryStatus`, `queryTrainingLoadAssessment`, `queryFitnessAssessmentOverview`) | `coros discover` saves raw responses to `.local/coros-fixtures/<timestamp>/`; the tolerant mappers in `src/CoachBot.Infrastructure/Coros/CorosMcpGateway.Data.cs` are then confirmed or adjusted |
| Input schema of the workout `course` object and write behaviour | `coros test-write <date>` creates one standalone workout; check it in the COROS app and delete it there (MCP tools cannot delete) |

The read mappings are written defensively: arguments are built from the **live tool schemas** (names, required
fields, date format), several candidate field names are accepted, items that lack required fields are skipped
(never guessed), raw payloads are stored with every row, and a warning with the payload *shape* (no values) is
logged if a non-empty response maps to nothing.

**Automatic pushing to COROS stays off** (`Coros__EnableWorkoutPush=false`) until `coros test-write` has produced a
workout you could see in the app. If the `course` schema requires fields CoachBot cannot fill, the push fails with
a message naming those fields instead of sending a guess.

Structured workouts (warm-up, repeats, recoveries) are pushed as **one workout with the steps written in its
description** (title, total distance and duration as structured fields). Native COROS step structures would let the
watch guide each repeat, but the `course` step schema is part of what `coros discover` has to reveal first.

## Steps to finish the verification

```bash
# .env: Coros__Mode=mcp  (and Coros__McpUrl=https://mcpeu.coros.com/mcp etc. if your account is in another region)
dotnet run --project src/CoachBot.Host -- coros login
dotnet run --project src/CoachBot.Host -- coros discover
dotnet run --project src/CoachBot.Host -- coros test-write 2026-11-05   # pick a date within 90 days
# if the workout shows up correctly in the COROS app: delete it there, then set Coros__EnableWorkoutPush=true
```

Share the *structure* of the files in `.local/coros-fixtures/` (they contain your personal health data — do not
commit them) to finalize the mappings, then convert them into anonymized test fixtures.

## Known COROS MCP limits (from the official docs)

- No webhooks → CoachBot polls every 3 hours (`Jobs__SyncCron`).
- Writes: running/trail/cycling only; scheduled dates must be today … today+90; plans 4–16 weeks.
- **No delete or reschedule tools**: CoachBot therefore pushes only a rolling 7-day window (`Coach__PushWindowDays`)
  and asks you to remove/edit items in the app when a pushed workout later becomes a rest day or the plan is replaced.
- `querySleepHrv` ≤ 7 days per call; `queryRecoveryStatus` is current-only; FIT downloads 50 files/day (not used).
- Summary-level data only (no per-second streams, laps unless separately queried, power or running dynamics).

## Security notes

- OAuth tokens (and a client secret, if COROS ever issues one) are encrypted with AES-256-GCM
  (`Secrets__EncryptionKey`) in `coach.coros_connections`; never logged.
- Token requests are never retried automatically (refresh tokens may rotate); write tool calls are never retried.
- `/disconnect` deletes stored tokens; revoke the app in your COROS account settings as well.
- Each `coros login` reuses the registered client for the same issuer, so repeated logins do not keep registering new clients.
