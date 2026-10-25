# CoachBot — personal AI running coach

A self-hosted running coach for one athlete preparing for a goal race — 5K, 10K, half marathon or marathon. It syncs
training and recovery data from COROS, diagnoses where you stand against your goal, designs a periodized plan with
workouts chosen for that distance and your limiters, adapts it after every run, pushes approved workouts back to the
COROS calendar, and talks to you through Telegram (English or Vietnamese).

**Stack:** C# / .NET 10 · Supabase (Postgres 17) · any OpenAI-compatible LLM (optional) · Telegram · Docker

> **The LLM coaches, the rules check.** The AI coach diagnoses your fitness against the goal, designs the plan and
> its workouts (intervals, tempos, race-pace blocks, hills, strides — as structured steps), and proposes adjustments
> after each run. Every proposal must pass deterministic safety rules tuned per race distance
> ([docs/safety.md](docs/safety.md)) or it is discarded and the rule-based plan is used instead. Load increases always
> wait for your approval, paces always come from measured fitness, and symptom screening combines keywords with LLM
> triage. Without an LLM, CoachBot runs entirely on the rules. Not medical advice.

## Running locally

Prerequisites: .NET 10 SDK, Docker (Docker Desktop/OrbStack), [Supabase CLI](https://supabase.com/docs/guides/cli).

```bash
# 1. Database: local Supabase (applies supabase/migrations automatically)
supabase start

# 2. Configuration
cp .env.example .env
#   - Secrets__EncryptionKey:  openssl rand -base64 32
#   - Telegram__BotToken:      create a bot with @BotFather
#   - LLM (optional):          see "LLM" below

# 3. Run
dotnet run --project src/CoachBot.Host
#   The first /start in Telegram replies with your chat id → set Telegram__AllowedChatId, restart, /start again.
```

COROS runs in mock mode by default (`Coros__Mode=mock`): synthetic history and recovery data, no account needed.
Mock scenarios: `Coros__MockScenario=normal | fatigued | missing_hrv | sparse_history` (restart after changing).

Then in Telegram:

```text
/goal 2027-03-07 3:59 Hanoi Marathon      # or: /goal 2026-12-20 half 1:45 · /goal 2026-11-29 5k 22:30
/profile days=5 long=sun maxhr=188 lang=vi
/sync full          # loads the last 8 weeks
/plan               # review → ✅ Approve plan
/today  /week  /status
/mock_run 10        # mock mode only: simulates a finished 10 km run → analysis, RPE buttons, adaptations
```

## LLM

| `Llm__Provider` | Behaviour |
| --- | --- |
| `none` (default) | Plans, adaptations, analysis and reports come from the rule-based engine and templates |
| `openai_compatible` | Any OpenAI-compatible chat completions API (OpenAI, OpenRouter, Ollama, …) |

For `openai_compatible` set `Llm__BaseUrl` (e.g. `https://api.openai.com/v1`, `http://localhost:11434/v1`),
`Llm__Model` and, if the API needs one, `Llm__ApiKey`. The model must support JSON-schema structured output
(`response_format: json_schema`). Requests are retried twice with `Llm__TimeoutSeconds` per attempt; any failure
falls back to the rules.

## Connecting COROS

CoachBot uses the **official COROS MCP server** (`https://mcp.coros.com/mcp`, regional `mcpeu` / `mcpus` / `mcpcn`).
It registers itself as an OAuth client and you approve access in the browser; no COROS password is stored.

Set `Coros__Mode=mcp` (and `Coros__McpUrl` for a regional endpoint), then connect from a terminal:

```bash
dotnet run --project src/CoachBot.Host -- coros login                  # approval link; waits for the redirect on localhost:8080
dotnet run --project src/CoachBot.Host -- coros status                 # connection, token expiry, last sync
dotnet run --project src/CoachBot.Host -- coros discover               # saves raw tool schemas/outputs to .local/coros-fixtures/ (gitignored, personal data)
dotnet run --project src/CoachBot.Host -- coros test-write 2026-11-05  # creates one test workout on that date
```

or from Telegram: `/connect`, open the link, approve, and paste the address you were redirected to back into the
chat (on a server it points at `localhost` and does not load — that is expected).

Pushing approved workouts to the COROS calendar is off by default. Use `coros test-write` to see how a workout
appears in the COROS app (delete it there afterwards), then set `Coros__EnableWorkoutPush=true`. Workouts are pushed
for the next `Coach__PushWindowDays` (default 7) days, because the COROS MCP server cannot delete or reschedule them.
Details and COROS MCP limits: [docs/coros-integration.md](docs/coros-integration.md).

## Telegram commands

| Command | |
| --- | --- |
| `/goal YYYY-MM-DD [distance] [time] [name]` | Goal race; distance `5k`, `10k`, `half`, `marathon` (default) or e.g. `15k` |
| `/profile days= long= maxhr= born= lang=` | Training days per week, long-run day (`mon`…`sun` or `t2`…`cn`), max HR, birth year, language |
| `/language en\|vi` | Message language |
| `/plan` | Generate a plan for approval |
| `/today` `/week` `/daily` `/status` `/report` | Today's workout, this week, readiness check, current status, weekly report |
| `/sync [full]` | Pull COROS data now (`full` = last 8 weeks) |
| `/push` | Push upcoming approved workouts to COROS |
| `/connect` `/disconnect` | Connect or disconnect COROS |
| `/feedback <text>` `/soreness 0-10` `/symptom [text]` | How you feel; any free-text message is also stored as feedback |
| `/pause` `/resume` | Pause or resume the plan |
| `/mock_run [km]` | Mock mode only: simulate a finished run |
| `/help` | Command list |

Feedback, symptom reports and free-text messages are screened for red-flag symptoms (English and Vietnamese); see
[docs/safety.md](docs/safety.md).

## Scheduled jobs

Interpreted in `Coach__TimeZone` (default `Asia/Ho_Chi_Minh`):

| Job | Default (`Jobs__*Cron`) |
| --- | --- |
| COROS sync, post-run analysis and adaptations | every 3 hours |
| Daily readiness check | 06:30 |
| Weekly report | Sunday 19:00 |
| Push to COROS (when `Coros__EnableWorkoutPush=true`) | 20:00 |
| Expire unanswered proposals | hourly |

## Configuration

All settings are environment variables, documented in [.env.example](.env.example). A `.env` file is loaded for local
runs and never committed; real environment variables override it.

| Group | Purpose |
| --- | --- |
| `ConnectionStrings__Postgres` | Supabase Postgres connection string |
| `Secrets__EncryptionKey` | AES-256-GCM key for COROS tokens at rest |
| `Coach__*` | Time zone, default language, public base URL (COROS redirect), push window |
| `Coros__*` | `mock` or `mcp`, MCP endpoint, mock scenario, workout push |
| `Llm__*` | Provider, base URL, model, API key, timeout |
| `Telegram__*` | Bot token, allowed chat id |
| `Jobs__*` | Cron schedules |

## Project layout

```text
src/CoachBot.Core            domain, safety rules, planning, analysis, application services (no I/O)
src/CoachBot.Infrastructure  EF Core/Supabase, COROS MCP + mock adapters, OAuth, LLM client, Telegram, scheduler, i18n
src/CoachBot.Host            ASP.NET Core host (/health, OAuth callback), CLI verbs (coros …, health)
supabase/                    Supabase CLI project; migrations/*.sql are the schema source of truth
tests/                       Core unit tests (incl. the plan-safety grid), infrastructure tests, integration tests
docs/                        coros-integration.md · safety.md · deployment.md
```

## Tests

```bash
dotnet test
```

Integration tests need a Postgres 17: they use `COACHBOT_TEST_DATABASE_URL` if set, else the local Supabase database
(`supabase start`), else a Testcontainers `postgres:17-alpine`. Each test runs in its own throwaway database.

## Deployment

One Docker container plus a hosted Supabase project; Telegram uses long polling, so no public URL is needed. See
[docs/deployment.md](docs/deployment.md).
