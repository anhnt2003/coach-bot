-- Plans are designed from everything the watch knows: COROS running level and race predictions for every distance,
-- the training load status, and each run's detail (training load, training effect, focus, performance).

alter table coach.daily_metrics
    add column running_level                    double precision,
    add column five_k_prediction_seconds        int,
    add column ten_k_prediction_seconds         int,
    add column half_marathon_prediction_seconds int,
    add column load_status                      text;

alter table coach.activities
    add column aerobic_te              double precision,
    add column anaerobic_te            double precision,
    add column training_focus          text,
    add column performance             text,
    add column best_km_pace_sec_per_km int,
    add column avg_power               int,
    add column detail_json             jsonb,
    add column details_fetched_at      timestamptz;
