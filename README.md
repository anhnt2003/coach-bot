# CoachBot — personal AI running coach

A self-hosted running coach for one athlete preparing for a goal race — 5K, 10K, half marathon or marathon. It syncs
training and recovery data from COROS, diagnoses where you stand against your goal, designs a periodized plan with
workouts chosen for that distance and your limiters, adapts it after every run, pushes approved workouts back to the
COROS calendar, and talks to you through Telegram (English or Vietnamese).

**Stack:** C# / .NET 10 · Supabase (Postgres 17) · Gemini (or any OpenAI-compatible LLM, or none) · Telegram · Docker

> **The LLM coaches, the rules check.** The AI coach diagnoses your fitness against the goal, designs the plan and
> its workouts (intervals, tempos, race-pace blocks, hills, strides — as structured steps), and proposes adjustments
> after each run. Deterministic, unit-tested safety limits tuned per race distance ([docs/safety.md](docs/safety.md))
> cover data integrity and injury ceilings only: a plan week that exceeds them is cut down to them, while coaching
> heuristics (session mix, 80/20, spacing) are guidelines the AI may go beyond with a reason. Load increases always
> wait for your approval, paces always come from measured fitness, and symptom screening combines keywords with LLM
> triage. Not medical advice.

## What works today

| Area | Status |
|---|---|
| Goal races from 5K to marathon, with distance-specific safety limits | ✅ tested (plan grids for every distance) |
| LLM diagnosis + LLM-designed plans with structured workouts, validation, one repair round, clamping to the limits, rule-based fallback | ✅ implemented, tested with a scripted LLM |
| Plan validation, adaptation (rules + LLM easing/progression), approvals, undo, audit trail | ✅ implemented, unit + integration tested |
| Readiness (HRV, resting HR, sleep, COROS recovery, load ratio, RPE/soreness) with missing-data handling | ✅ tested |
| Red-flag symptoms: keywords (en + vi) + LLM triage (negation → confirm, LLM-only → pause) | ✅ tested |
| Telegram bot (commands, buttons, daily check, weekly report, alerts) | ✅ implemented; needs your bot token for a live run |
| Supabase schema (migrations, RLS, `coach` schema not exposed by the Data API) | ✅ verified on local Supabase |
| Mock COROS adapter (4 scenarios) | ✅ default mode |
| **Real COROS MCP integration** | ✅ login + reading data verified with a real account · ⚠️ pushing workouts not yet verified (off) — see [docs/coros-integration.md](docs/coros-integration.md) |
| Gemini (plan design, run analysis, triage, reports) | ⚠️ request/response format unit-tested against the documented API; **not yet called with a real key**, so plan-design quality is unproven |

## Quick start (local, mock data)

Prerequisites: .NET 10 SDK, Docker (Docker Desktop/OrbStack), [Supabase CLI](https://supabase.com/docs/guides/cli).

```bash
# 1. Database: local Supabase (applies supabase/migrations automatically)
supabase start

# 2. Configuration
cp .env.example .env
#   - set Secrets__EncryptionKey:  openssl rand -base64 32
#   - optional: Llm__Provider=gemini + Llm__ApiKey=<key from https://aistudio.google.com/apikey>
#   - Telegram: create a bot with @BotFather, set Telegram__BotToken

# 3. Run
dotnet run --project src/CoachBot.Host
#   First /start in Telegram replies with your chat id → set Telegram__AllowedChatId, restart, /start again.
```

Then in Telegram:

```
/goal 2027-03-07 3:59 Hanoi Marathon      # or: /goal 2026-12-20 half 1:45 · /goal 2026-11-29 5k 22:30
/profile days=5 long=sun maxhr=188 lang=vi
/sync full          # mock mode: loads 8 weeks of synthetic history
/plan               # review → ✅ Approve plan
/today  /week  /status
/mock_run 10        # mock mode only: simulates a finished 10 km run → analysis, RPE buttons, adaptations
```

Try other mock scenarios with `Coros__MockScenario=fatigued | missing_hrv | sparse_history` (restart after changing).

## Connecting your real COROS account

COROS exposes an **official MCP server** (`https://mcp.coros.com/mcp`, regional `mcpeu`/`mcpus`/`mcpcn`). CoachBot
registers itself as an OAuth client and you approve access in the browser:

```bash
# in .env: Coros__Mode=mcp
dotnet run --project src/CoachBot.Host -- coros login      # opens a COROS approval link, listens on localhost:8080
dotnet run --project src/CoachBot.Host -- coros discover   # records real tool schemas/outputs to .local/ (gitignored)
dotnet run --project src/CoachBot.Host -- coros status
dotnet run --project src/CoachBot.Host -- coros sync full     # imports 8 weeks of runs + 5 weeks of metrics (read-only)
dotnet run --project src/CoachBot.Host -- coros test-write 2026-11-05   # creates ONE test workout; delete it in the app
```

Or from Telegram: `/connect`, open the link, approve, and (if the bot runs on a server) paste the address you were
redirected to back into the chat. Read [docs/coros-integration.md](docs/coros-integration.md) first: it lists what
has been verified live and what still needs your account.

## Commands

`/goal` `/profile` `/language` `/plan` `/today` `/week` `/status` `/report` `/sync [full]` `/push` `/connect`
`/disconnect` `/feedback` `/soreness` `/symptom` `/pause` `/resume` `/help` — and any free-text message is stored
as feedback and screened for red-flag symptoms.

## Project layout

```
src/CoachBot.Core            domain, safety rules, planning, analysis, application services (no I/O)
src/CoachBot.Infrastructure  EF Core/Supabase, COROS MCP + mock adapters, OAuth, LLM clients, Telegram, scheduler, i18n
src/CoachBot.Host            ASP.NET Core host (/health, OAuth callback), CLI verbs (coros …, health)
supabase/                    Supabase CLI project; migrations/*.sql are the schema source of truth
tests/                       Core unit tests (incl. ~430-case plan-safety grid), infrastructure tests, integration tests
docs/                        coros-integration.md · safety.md · deployment.md
```

## Tests

```bash
dotnet test
```

Integration tests need a Postgres 17: they use `COACHBOT_TEST_DATABASE_URL` if set, else the local Supabase database
(`supabase start`), else a Testcontainers `postgres:17-alpine`. Each test runs in its own throwaway database.

## Configuration

All settings are environment variables (see [.env.example](.env.example)); `.env` is loaded for local runs and never
committed. COROS tokens are stored AES-256-GCM encrypted with `Secrets__EncryptionKey`.

## Deployment

See [docs/deployment.md](docs/deployment.md) (Docker on a VPS + hosted Supabase).
