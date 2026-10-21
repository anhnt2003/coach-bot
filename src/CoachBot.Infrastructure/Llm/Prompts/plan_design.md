Design a personalised training plan for this athlete's goal race (race.distance_km), like a good coach would for this distance and this person.

You get the race, your own diagnosis of the athlete ("assessment": limiters, risks, phase priorities — may be null if unavailable), their availability, recent training and notes, the safety rules, and "baseline_plan": a plain plan built by the rule engine that is known to satisfy every rule. Use the baseline's weekly volumes and long-run progression as a safe frame, and make the content yours: choose the sessions that develop this athlete's limiters for this distance, vary them across the weeks, and progress them towards race-specific work. Keep weekly volumes close to the baseline unless there is a clear reason; you must never exceed the rules.

Workout ideas by goal (choose and adapt — do not copy blindly):
- Marathon: long runs with the last 3–10 km at marathon pace in build/peak; marathon-pace blocks (e.g. 2 × 5 km at marathon pace); threshold cruise intervals (e.g. 4 × 2 km at threshold, 2 min jog); progression long runs (easy into steady); some 5K-pace intervals early on.
- Half marathon: threshold cruise intervals and continuous tempos; half-marathon-pace blocks (e.g. 3 × 3 km at half_marathon, 3 min jog); long runs of 16–22 km, some finishing at steady or half-marathon pace; VO2max intervals (e.g. 5 × 1 km at five_k).
- 10K: VO2max intervals (e.g. 6 × 800 m at five_k, 400 m jog); 10K-pace reps (e.g. 5 × 1 km at ten_k); threshold tempos; hill repeats and strides in the base phase.
- 5K: VO2max reps (e.g. 8 × 400 m at five_k, 12 × 200 m at repetition); hill repeats; strides after easy runs; threshold runs for endurance.
- Any goal: strides (6–10 × 20 s) after an easy run are a light way to add speed; cutback weeks keep one short quality session; the taper keeps a little race-pace work and cuts volume.

Requirements:
- Return every week from 1 to plan_window.weeks exactly once. Week 1 starts on plan_window.first_monday; "day" is the weekday within that week.
- Only list run sessions (rest days are implied). One session per day, at most run_days_per_week sessions per week.
- Session types: recovery, easy, long, tempo (threshold-type work), interval (5K pace, 10K pace, repetition or hills), race_pace (work at the goal race pace), race. Exactly one "race", on race day, in the last week.
- "title": a short, specific name for every non-easy session, e.g. "5 × 1 km at 5K pace" or "Long run, last 5 km at half-marathon pace".
- "steps": give the structure of every tempo, interval and race_pace session and of long runs with faster parts (strides on easy runs too). Each step: "repeat" (1 for a single block), "work" and optional "rest", each with an "intensity" and either "km" or "minutes". Start with an easy warm-up (usually 2–3 km) and end with an easy cool-down. Use "race_pace" as the intensity for the goal race pace. The session distance is computed from the steps, so make them add up to what you intend. Leave "steps" out for plain easy, recovery and long runs.
- The intensity limits per session ("rules.intensity_per_session") and the intensities allowed per type ("rules.session_intensities") are checked; respect them.
- Use "notes" for a short, concrete cue per session (purpose, how it should feel, fuelling) when useful.
- "focus": one short line per week. "rationale": 3–6 sentences explaining the plan to the athlete — how it addresses their limiters and builds towards race day — mentioning any warnings honestly.

Data (JSON):
```json
{{context}}
```
