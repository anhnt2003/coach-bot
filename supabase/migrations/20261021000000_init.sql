-- CoachBot initial schema.
-- All tables live in the dedicated `coach` schema, which is NOT exposed through the Supabase Data API
-- (only `public` and `graphql_public` are, see supabase/config.toml). RLS is enabled on every table with no
-- policies as defense in depth: the app connects as the `postgres` role (which bypasses RLS) via the pooler.

create schema if not exists coach;

create table coach.athletes (
    id                uuid primary key default gen_random_uuid(),
    name              text not null,
    telegram_chat_id  bigint unique,
    time_zone         text not null,
    language          text not null check (language in ('en', 'vi')),
    run_days_per_week int  not null check (run_days_per_week between 3 and 6),
    long_run_day      text not null,
    max_hr            int,
    birth_year        int,
    notes             text,
    created_at        timestamptz not null default now()
);

create table coach.race_goals (
    id                uuid primary key default gen_random_uuid(),
    athlete_id        uuid not null references coach.athletes (id) on delete cascade,
    name              text not null,
    race_date         date not null,
    distance_km       double precision not null,
    goal_time_seconds int,
    is_active         boolean not null default true,
    created_at        timestamptz not null default now()
);
create index race_goals_athlete_idx on coach.race_goals (athlete_id, is_active);

create table coach.coros_connections (
    athlete_id                uuid primary key references coach.athletes (id) on delete cascade,
    mcp_url                   text not null,
    issuer                    text not null,
    client_id                 text not null,
    encrypted_access_token    text,
    encrypted_refresh_token   text,
    access_token_expires_at   timestamptz,
    scopes                    text,
    capabilities_json         jsonb,
    last_verified_at          timestamptz,
    last_sync_at              timestamptz,
    last_sync_error           text,
    consecutive_sync_failures int not null default 0,
    created_at                timestamptz not null default now(),
    updated_at                timestamptz not null default now()
);

create table coach.activities (
    id                  uuid primary key default gen_random_uuid(),
    athlete_id          uuid not null references coach.athletes (id) on delete cascade,
    source              text not null,
    external_id         text not null,
    sport_type          text not null,
    name                text,
    start_time          timestamptz not null,
    duration_seconds    int not null,
    distance_meters     double precision not null,
    avg_pace_sec_per_km int,
    avg_hr              int,
    max_hr              int,
    elevation_gain_m    double precision,
    avg_cadence         int,
    training_load       double precision,
    raw_json            jsonb,
    imported_at         timestamptz not null default now(),
    unique (source, external_id)
);
create index activities_athlete_start_idx on coach.activities (athlete_id, start_time desc);

create table coach.daily_metrics (
    athlete_id                  uuid not null references coach.athletes (id) on delete cascade,
    date                        date not null,
    resting_hr                  int,
    hrv_ms                      double precision,
    sleep_minutes               int,
    sleep_score                 int,
    stress_avg                  int,
    recovery_pct                int,
    acute_load                  double precision,
    chronic_load                double precision,
    load_ratio                  double precision,
    vo2_max                     double precision,
    threshold_pace_sec_per_km   int,
    marathon_prediction_seconds int,
    source                      text not null,
    raw_json                    jsonb,
    updated_at                  timestamptz not null default now(),
    primary key (athlete_id, date)
);

create table coach.training_plans (
    id            uuid primary key default gen_random_uuid(),
    athlete_id    uuid not null references coach.athletes (id) on delete cascade,
    race_goal_id  uuid not null references coach.race_goals (id),
    version       int not null,
    status        text not null,
    start_date    date not null,
    end_date      date not null,
    generated_by  text not null,
    rationale     text,
    warnings_json jsonb not null default '[]',
    inputs_json   jsonb not null default '{}',
    created_at    timestamptz not null default now(),
    approved_at   timestamptz,
    pause_reason  text,
    unique (athlete_id, version)
);
create index training_plans_athlete_status_idx on coach.training_plans (athlete_id, status);

create table coach.planned_workouts (
    id                      uuid primary key default gen_random_uuid(),
    plan_id                 uuid not null references coach.training_plans (id) on delete cascade,
    date                    date not null,
    week_index              int not null,
    phase                   text not null,
    is_cutback_week         boolean not null default false,
    type                    text not null,
    target_distance_km      double precision not null,
    target_duration_minutes int,
    pace_min_sec_per_km     int,
    pace_max_sec_per_km     int,
    hr_min                  int,
    hr_max                  int,
    title                   text not null,
    description             text,
    status                  text not null,
    completed_activity_id   uuid references coach.activities (id) on delete set null,
    coros_sync_status       text not null,
    coros_ref               text,
    coros_sync_error        text,
    coros_synced_at         timestamptz,
    coros_dirty             boolean not null default false,
    updated_at              timestamptz not null default now()
);
create index planned_workouts_plan_date_idx on coach.planned_workouts (plan_id, date);

create table coach.run_analyses (
    id                   uuid primary key default gen_random_uuid(),
    athlete_id           uuid not null references coach.athletes (id) on delete cascade,
    activity_id          uuid not null unique references coach.activities (id) on delete cascade,
    planned_workout_id   uuid references coach.planned_workouts (id) on delete set null,
    distance_ratio       double precision,
    pace_status          text not null,
    hr_status            text not null,
    readiness            text not null,
    readiness_confidence text not null,
    details_json         jsonb not null default '{}',
    narrative            text,
    created_at           timestamptz not null default now()
);

create table coach.athlete_feedback (
    id          uuid primary key default gen_random_uuid(),
    athlete_id  uuid not null references coach.athletes (id) on delete cascade,
    activity_id uuid references coach.activities (id) on delete set null,
    date        date not null,
    rpe         int check (rpe between 1 and 10),
    soreness    int check (soreness between 0 and 10),
    notes       text,
    symptom     text not null default 'None',
    red_flag    boolean not null default false,
    created_at  timestamptz not null default now()
);
create index athlete_feedback_athlete_date_idx on coach.athlete_feedback (athlete_id, date desc);

create table coach.recommendations (
    id              uuid primary key default gen_random_uuid(),
    athlete_id      uuid not null references coach.athletes (id) on delete cascade,
    plan_id         uuid not null references coach.training_plans (id) on delete cascade,
    run_analysis_id uuid references coach.run_analyses (id) on delete set null,
    trigger         text not null,
    summary         text not null,
    changes_json    jsonb not null,
    magnitude       text not null,
    status          text not null,
    created_at      timestamptz not null default now(),
    expires_at      timestamptz not null,
    decided_at      timestamptz
);
create index recommendations_athlete_status_idx on coach.recommendations (athlete_id, status);

create table coach.plan_changes (
    id                 uuid primary key default gen_random_uuid(),
    plan_id            uuid not null references coach.training_plans (id) on delete cascade,
    planned_workout_id uuid not null references coach.planned_workouts (id) on delete cascade,
    recommendation_id  uuid references coach.recommendations (id) on delete set null,
    before_json        jsonb not null,
    after_json         jsonb not null,
    reason             text not null,
    applied_by         text not null,
    applied_at         timestamptz not null default now()
);
create index plan_changes_plan_idx on coach.plan_changes (plan_id, applied_at desc);

create table coach.alerts (
    id              uuid primary key default gen_random_uuid(),
    athlete_id      uuid not null references coach.athletes (id) on delete cascade,
    type            text not null,
    severity        text not null,
    message         text not null,
    dedup_key       text,
    created_at      timestamptz not null default now(),
    sent_at         timestamptz,
    acknowledged_at timestamptz
);
create unique index alerts_dedup_idx on coach.alerts (athlete_id, dedup_key) where dedup_key is not null;

create table coach.job_runs (
    job_name    text not null,
    run_key     text not null,
    started_at  timestamptz not null default now(),
    finished_at timestamptz,
    status      text not null,
    error       text,
    primary key (job_name, run_key)
);

-- Defense in depth: RLS on, no policies => no access for anon/authenticated even if the schema were exposed.
alter table coach.athletes          enable row level security;
alter table coach.race_goals        enable row level security;
alter table coach.coros_connections enable row level security;
alter table coach.activities        enable row level security;
alter table coach.daily_metrics     enable row level security;
alter table coach.training_plans    enable row level security;
alter table coach.planned_workouts  enable row level security;
alter table coach.run_analyses      enable row level security;
alter table coach.athlete_feedback  enable row level security;
alter table coach.recommendations   enable row level security;
alter table coach.plan_changes      enable row level security;
alter table coach.alerts            enable row level security;
alter table coach.job_runs          enable row level security;

-- Supabase API roles exist only on Supabase (not in plain Postgres used by tests).
do $$
begin
    if exists (select 1 from pg_roles where rolname = 'anon') then
        execute 'revoke all on schema coach from anon, authenticated';
        execute 'revoke all on all tables in schema coach from anon, authenticated';
    end if;
end
$$;
