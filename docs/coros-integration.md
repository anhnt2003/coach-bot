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

## Verified live (2026-10-22, with a real account)

| Check | Result |
|---|---|
| Token exchange + refresh (offline_access) | ✅ browser login (PKCE) stores tokens; region `mcpus` |
| `tools/list` with a real token | ✅ 34 tools; **no tool publishes an output schema** |
| Read tool output | ✅ **every read tool answers with human-readable text** (e.g. `Duration: 45:16 \| Distance: 7.02 km`), not JSON. `CorosText` parses it; the source text is stored with every row |
| Read tool arguments | ✅ dates `yyyyMMdd`. `querySportRecords` lists every filter as required but describes them as "Optional": they are sent as `null`. `querySleepHrv` needs `startDate`, `endDate` and `days` (≤ 7 per call); 35-day `days` ranges are accepted |
| Full sync (`coros sync full`) | ✅ 8 weeks of activities (runs, walks, swims with sport codes), 35 days of stress and training load (short/long-term load, load ratio), current recovery, VO2max, threshold pace, race predictions |

Seen only as "no data" so far (the account had none): sleep score/duration, sleep HRV, resting HR. Those readers
accept the obvious labelled forms (`Sleep Score: 81`, `Main Sleep: 7h 12min`, `Average HRV: 45 ms`,
`2026-10-21: 51 bpm`) and never guess unlabelled numbers; check them with `coros discover` once such data exists.

## Not yet verified

| Item | How it gets verified |
|---|---|
| Input schema of the workout `course` object and write behaviour | `coros test-write <date>` creates one standalone workout; check it in the COROS app and delete it there (MCP tools cannot delete) |

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
dotnet run --project src/CoachBot.Host -- coros discover        # records raw tool output to .local/ (read-only)
dotnet run --project src/CoachBot.Host -- coros sync full       # imports 8 weeks of data, same as /sync full (read-only)
dotnet run --project src/CoachBot.Host -- coros test-write 2026-11-05   # pick a date within 90 days
# if the workout shows up correctly in the COROS app: delete it there, then set Coros__EnableWorkoutPush=true
```

Files in `.local/coros-fixtures/` contain personal health data — never commit them. Test fixtures copy the format
with made-up values (`CorosTextTests`).

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
