# CoachBot — personal AI running coach

A self-hosted running coach for one athlete preparing for a goal race — 5K, 10K, half marathon or marathon. It syncs
training and recovery data from COROS, diagnoses where you stand against your goal, designs a periodized plan with
workouts chosen for that distance and your limiters, adapts it after every run, pushes approved workouts back to the
COROS calendar, and talks to you through Telegram (English or Vietnamese).

**Stack:** C# / .NET 10 · Supabase (Postgres 17) · any OpenAI-compatible LLM (optional) · Telegram · Docker

> **The LLM coaches, the rules check.** The AI coach diagnoses your fitness against the goal, designs the plan and
> its workouts (intervals, tempos, race-pace blocks, hills, strides — as structured steps), and proposes adjustments
> after each run. Deterministic safety limits tuned per race distance cover data integrity and injury ceilings only: a
> plan week that exceeds them is cut down to them, while coaching heuristics (session mix, 80/20, spacing) are
> guidelines the AI may go beyond with a reason. Load increases always
> wait for your approval, paces always come from measured fitness, and symptom screening combines keywords with LLM
> triage. Without an LLM, CoachBot runs entirely on the rules. Not medical advice.

## Running locally

Prerequisites: .NET 10 SDK, Docker (Docker Desktop/OrbStack), [Supabase CLI](https://supabase.com/docs/guides/cli),
a COROS account and a Telegram bot. CoachBot only coaches from your real COROS data: there is no demo mode. Until
COROS is connected, every command except `/start`, `/help`, `/language` and `/connect` (buttons and free text too)
only asks you to connect, and the scheduled jobs are skipped.

```bash
# 1. Database: local Supabase (applies supabase/migrations automatically)
supabase start

# 2. Configuration
cp .env.example .env
#   - Secrets__EncryptionKey:  openssl rand -base64 32
#   - Telegram__BotToken:      create a bot with @BotFather
#   - Coros__McpUrl:           only if not global: https://mcpeu.coros.com/mcp (or mcpus / mcpcn)
#   - LLM (optional):          see "LLM" below

# 3. Run
dotnet run --project src/CoachBot.Host
#   The first /start in Telegram replies with your chat id → set Telegram__AllowedChatId, restart, /start again.
```

Then in Telegram:

```text
/connect            # required: open the link, approve in COROS → imports the last 8 weeks
/goal 2027-03-07 3:59 Hanoi Marathon      # or: /goal 2026-12-20 half 1:45 · /goal 2026-11-29 5k 22:30
/profile days=5 long=sun maxhr=188 lang=vi
/plan               # review → ✅ Approve plan
/today  /week  /status
```

New runs are picked up by the hourly sync (or `/sync` right away), analysed, and may lead to proposed adjustments.

## LLM

| `Llm__Provider` | Behaviour |
| --- | --- |
| `none` (default) | Plans, adaptations, analysis and reports come from the rule-based engine and templates |
| `openai_compatible` | Any OpenAI-compatible chat completions API (OpenAI, OpenRouter, Ollama, …) |

For `openai_compatible` set `Llm__BaseUrl` (e.g. `https://api.openai.com/v1`, `http://localhost:11434/v1`),
`Llm__Model` and, if the API needs one, `Llm__ApiKey`. The model must support JSON-schema structured output
(`response_format: json_schema`). Answers are streamed, so a reasoning model can think for minutes without a proxy
cutting the request. `Llm__TimeoutSeconds` bounds the wait for an answer to start and `Llm__TotalTimeoutSeconds`
(default 900) the whole call; failed requests are retried twice (a proxy gateway timeout is not), and any failure
falls back to the rules.

## Connecting COROS

CoachBot uses the **official COROS MCP server** (`https://mcp.coros.com/mcp`, regional `mcpeu` / `mcpus` / `mcpcn`).
It registers itself as an OAuth client and you approve access in the browser; no COROS password is stored.

Set `Coros__McpUrl` for a regional endpoint, then connect from a terminal:

```bash
dotnet run --project src/CoachBot.Host -- coros login                  # approval link; waits for the redirect on localhost:8080
dotnet run --project src/CoachBot.Host -- coros status                 # connection, token expiry, last sync
dotnet run --project src/CoachBot.Host -- coros sync full              # imports 8 weeks of runs + 5 weeks of metrics (read-only)
dotnet run --project src/CoachBot.Host -- coros discover               # saves raw tool schemas/outputs to .local/coros-fixtures/ (gitignored, personal data)
dotnet run --project src/CoachBot.Host -- coros test-write 2026-11-05  # creates one test workout on that date
```

or from Telegram: `/connect`, open the link, approve, and paste the address you were redirected to back into the
chat (on a server it points at `localhost` and does not load — that is expected).

Pushing approved workouts to the COROS calendar is off by default. Use `coros test-write` to see how a workout
appears in the COROS app (delete it there afterwards), then set `Coros__EnableWorkoutPush=true`. Workouts are pushed
for the next `Coach__PushWindowDays` (default 7) days, because the COROS MCP server cannot delete or reschedule them.

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
| `/help` | Command list |

Feedback, symptom reports and free-text messages are screened for red-flag symptoms (English and Vietnamese).

## Scheduled jobs

Interpreted in `Coach__TimeZone` (default `Asia/Ho_Chi_Minh`):

| Job | Default (`Jobs__*Cron`) |
| --- | --- |
| COROS sync, post-run analysis and adaptations | hourly |
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
| `Coros__*` | MCP endpoint, workout push |
| `Llm__*` | Provider, base URL, model, API key, timeouts |
| `Telegram__*` | Bot token, allowed chat id |
| `Jobs__*` | Cron schedules |

## Project layout

```text
src/CoachBot.Core            domain, safety rules, planning, analysis, application services (no I/O)
src/CoachBot.Infrastructure  EF Core/Supabase, COROS MCP adapter, OAuth, LLM client, Telegram, scheduler, i18n
src/CoachBot.Host            ASP.NET Core host (/health, OAuth callback), CLI verbs (coros …, health)
supabase/                    Supabase CLI project; migrations/*.sql are the schema source of truth
tests/                       Core unit tests (incl. the plan-safety grid), infrastructure tests, integration tests
```

## Tests

```bash
dotnet test
```

Integration tests need a Postgres 17: they use `COACHBOT_TEST_DATABASE_URL` if set, else the local Supabase database
(`supabase start`), else a Testcontainers `postgres:17-alpine`. Each test runs in its own throwaway database. They
replace COROS with a synthetic test double (`tests/CoachBot.IntegrationTests/FakeCorosGateway.cs`), so they run
offline; the app itself has no such mode.

## Deployment

One Docker container plus a hosted Supabase project; Telegram uses long polling, so no public URL is needed.
