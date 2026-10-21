# Deployment

CoachBot is one container plus a Supabase project. Telegram uses outbound long polling, so the server needs **no
public URL or open inbound port**.

## 1. Supabase (database)

1. Create a project at [supabase.com](https://supabase.com) — region **Southeast Asia (Singapore)** is closest to Vietnam.
2. Apply the schema from this repo:
   ```bash
   supabase login
   supabase link --project-ref <project-ref>
   supabase db push            # applies supabase/migrations/*.sql
   ```
3. Connection string: *Project Settings → Database → Connection pooling*, **Session mode, port 5432**:
   ```
   Host=aws-0-<region>.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<project-ref>;Password=<db-password>;SSL Mode=Require
   ```
   - Do not use the transaction pooler (port 6543): it breaks session state and prepared statements.
   - The direct host `db.<ref>.supabase.co` is IPv6-only unless you buy the IPv4 add-on; most VPS/Docker setups need the pooler.
4. All tables live in the `coach` schema, which the Supabase Data API does not expose; RLS is enabled on every table
   with no policies. Do not add `coach` to *API → Exposed schemas*.

## 2. Server (Docker)

Any small VPS (1 vCPU / 1 GB) with Docker:

```bash
git clone <your repo> coachbot && cd coachbot
cp .env.example .env    # fill in: ConnectionStrings__Postgres (pooler), Secrets__EncryptionKey, Telegram__*, Llm__*, Coros__*
docker compose up -d --build
docker compose logs -f coachbot
docker compose exec coachbot dotnet CoachBot.Host.dll health && echo healthy
```

- The image is multi-stage, runs as the non-root `app` user, logs JSON to stdout, and has a built-in healthcheck.
- Port 8080 is bound to `127.0.0.1` only (used for `/health` and the optional OAuth callback).
- Connect COROS from Telegram with `/connect`: approve in your browser, then paste the address you were redirected to
  (it points at `localhost` and will not load — that's expected) back into the chat.
- Back up the `.env` file: losing `Secrets__EncryptionKey` only means reconnecting COROS, but keep it secret.

## 3. Updates and schema changes

```bash
git pull
supabase db push                 # only when supabase/migrations changed
docker compose up -d --build
```

New schema changes = a new timestamped SQL file in `supabase/migrations/` (never edit applied files); the EF Core
mapping in `CoachDbContext` must match, which `dotnet test` verifies against a real database.

## Operations

- **Scheduled jobs** (time zone `Coach__TimeZone`): sync every 3 h, daily check 06:30, weekly report Sunday 19:00,
  COROS push 20:00, proposal expiry hourly. Each run is recorded in `coach.job_runs`, so a restart never runs a slot
  twice; missed slots within 6 h are caught up at startup.
- **Alerts:** repeated sync failures, expired/revoked COROS access, fatigue and symptoms are stored in `coach.alerts`
  and sent to Telegram.
- **Data deletion:** deleting the row in `coach.athletes` cascades to all of that athlete's data.
- **Supabase Free tier:** projects can be paused after a period of inactivity and have no point-in-time recovery;
  the Pro plan is recommended for real use.
