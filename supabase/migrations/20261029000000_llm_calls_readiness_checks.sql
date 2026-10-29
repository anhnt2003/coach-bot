-- The LLM now makes every coaching decision and there is no rule-based fallback. Every call is kept for debugging,
-- because the same data can produce a different answer, and the LLM's daily readiness verdict replaces the rule-based
-- readiness evaluation.

create table coach.llm_calls (
    id          uuid primary key default gen_random_uuid(),
    athlete_id  uuid references coach.athletes (id) on delete cascade,
    purpose     text not null,
    model       text not null,
    context     jsonb not null default '{}',
    response    text,
    status      text not null check (status in ('ok', 'timeout', 'http', 'empty', 'schema', 'integrity')),
    error       text,
    duration_ms int not null default 0,
    created_at  timestamptz not null default now()
);

create index llm_calls_athlete_created_idx on coach.llm_calls (athlete_id, created_at desc);
create index llm_calls_status_created_idx on coach.llm_calls (status, created_at desc);

create table coach.readiness_checks (
    athlete_id uuid not null references coach.athletes (id) on delete cascade,
    date       date not null,
    level      text not null,
    confidence text not null,
    summary    text not null,
    concerns   jsonb not null default '[]',
    source     text not null,
    created_at timestamptz not null default now(),
    primary key (athlete_id, date)
);

alter table coach.llm_calls        enable row level security;
alter table coach.readiness_checks enable row level security;

-- Supabase API roles exist only on Supabase (not in the plain Postgres used by tests).
do $$
begin
    if exists (select 1 from pg_roles where rolname = 'anon') then
        execute 'revoke all on table coach.llm_calls, coach.readiness_checks from anon, authenticated';
    end if;
end
$$;
