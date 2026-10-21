-- Structured workouts (warm-up, repeats, recoveries) designed by the LLM, and race-pace sessions for any goal
-- distance (5K to marathon) instead of marathon pace only.
alter table coach.planned_workouts add column steps jsonb;

update coach.planned_workouts set type = 'RacePace' where type = 'MarathonPace';

-- Audit trail and proposals store workout types as snake_case JSON.
update coach.recommendations
   set changes_json = replace(changes_json::text, '"marathon_pace"', '"race_pace"')::jsonb
 where changes_json::text like '%"marathon_pace"%';
update coach.plan_changes
   set before_json = replace(before_json::text, '"marathon_pace"', '"race_pace"')::jsonb,
       after_json  = replace(after_json::text, '"marathon_pace"', '"race_pace"')::jsonb
 where before_json::text like '%"marathon_pace"%' or after_json::text like '%"marathon_pace"%';
