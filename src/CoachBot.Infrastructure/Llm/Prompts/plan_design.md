Design the athlete's training plan for their goal race, from the first week to race day.

The data:
- "race": name, date, weekday, distance_km and goal_time_seconds (may be null).
- "assessment": your own diagnosis of the athlete (level, prediction, limiters, risks, priorities per phase).
- "athlete": run days per week, preferred long-run day, profile notes and recent notes.
- "fitness": recent training and the latest COROS fitness values, as in the assessment.
- "plan_window": first_monday (week 1 starts that day), weeks (the number of weeks, the last one containing race day) and race_week.

Return:
- "paces": your pace target for each intensity the plan uses, in seconds per km: "pace_fast_sec_per_km" ≤ "pace_slow_sec_per_km". Add "hr_min" and "hr_max" when you want a heart-rate target too (both or neither). One entry per intensity; "race_pace" means the goal distance's own intensity (e.g. half_marathon for a half marathon), so give that one only once. An intensity without an entry is run by effort.
- "weeks": every week from 1 to plan_window.weeks exactly once, each with "phase" (base, build, peak or taper), "cutback" (true for a lighter week), a one-line "focus", and its "sessions".
- Each session: "day" (mon…sun within that week), "type" (recovery, easy, long, tempo, interval, race_pace or race) and "km". At most one session per day; rest days are simply left out. Optional: "intensity" (the effort of a plain run, when it is not the usual one for its type), a short "title", "notes" (a concrete cue for the athlete), and "steps".
- "steps" describe a structured session in order (e.g. warm-up, main set, cool-down). Each step: "repeat" (1 for a single block, at most 30), "work" and an optional "rest", each with an "intensity" and either "km" or "minutes". The session distance is computed from the steps, so make them add up to what you intend. Leave "steps" out for a run at one effort.
- Exactly one session of type "race", on race day. Nothing after it.
- "rationale": 3–6 sentences explaining the plan to the athlete: how it answers their limiters and builds towards race day.

Data (JSON):
```json
{{context}}
```
