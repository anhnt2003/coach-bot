Assess this athlete as their coach before you design their training plan: where they stand today relative to their goal race, and what most limits their performance at that distance.

The data:
- "race": name, date, distance_km, goal_time_seconds (null when the athlete set none) and weeks_to_race.
- "athlete": run days per week, age, max heart rate, profile notes and recent notes (newest first).
- "fitness": weekly km for the last 8 weeks (oldest first), the 4-week average, the longest run of the last 4 weeks, weeks of history, up to 20 recent runs (date, km, minutes, average pace, average heart rate, name), and the latest COROS fitness values (threshold pace in seconds per km, marathon prediction in seconds, VO2max) with their date. Any of these can be missing.

Write:
- "level", "summary" (2–4 sentences).
- "predicted_race_seconds": your prediction of the goal race time from the athlete's current fitness, or 0 when the data is too thin to predict.
- "goal_verdict" (use "no_goal_time" when no goal time is set) and "goal_comment" (the goal compared with your prediction and the weeks available).
- "strengths": what the data shows they already do well (may be empty).
- "limiters": at most 3, most important first, each with the evidence from the data.
- "risks": what the plan must be careful about (may be empty).
- "priorities": one entry per phase (base, build, peak, taper) saying what to develop and with which kinds of sessions.
Be honest: if the data is too thin to judge something, say so instead of guessing.

Data (JSON):
```json
{{context}}
```
