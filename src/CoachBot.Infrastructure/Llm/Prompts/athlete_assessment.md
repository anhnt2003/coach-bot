Assess this athlete as their coach before you design their training plan: where they stand today relative to their goal race, and what most limits their performance at that distance.

The data:
- "race": name, date, distance_km, goal_time_seconds (null when the athlete set none) and weeks_to_race.
- "athlete": run days per week, age, max heart rate, profile notes and recent notes (newest first).
- "fitness", everything the watch knows (any of it can be missing):
  - weekly km and weekly run training load (COROS units) for the last 8 weeks, oldest first; the 4-week average km; the longest run of the last 4 weeks; weeks of history;
  - "recent_runs" of the last 8 weeks, newest first: date, km, minutes, average and best-kilometre pace, average and max heart rate, elevation gain, COROS training load, aerobic and anaerobic training effect (0–5), training focus, and "performance" (COROS's reading of the run against the athlete's current ability, e.g. "Best");
  - "watch": the latest COROS fitness assessment (VO2max, running level, threshold pace, race predictions for 5K, 10K, half and marathon, and goal_distance_prediction_seconds), and "watch_four_weeks_ago", the same about four weeks earlier;
  - "training_load": COROS short-term (acute) and long-term (base) load, their ratio and COROS's status of it, the long-term load four weeks ago, and the last two weeks day by day;
  - "recovery": the latest COROS recovery percentage and 7-day averages of resting heart rate, HRV, sleep hours and stress.

Ground the diagnosis in all of it. The watch's running level, threshold pace and race predictions (above all for the goal distance) say what the athlete can do now, and the comparison with four weeks ago says where they are heading. The recent runs show what they have actually done: their paces and heart rates, training effect, focus and performance. The training load status, ratio and base load say how much training they absorb now. When the watch's prediction and the recent runs disagree, say which you trust and why. predicted_race_seconds and the goal verdict must follow from this evidence and the weeks left.

Write:
- "level", "summary" (2–4 sentences).
- "predicted_race_seconds": your prediction of the goal race time from the athlete's current fitness, or 0 when the data is too thin to predict.
- "goal_verdict" (use "no_goal_time" when no goal time is set) and "goal_comment" (1–2 sentences: the goal compared with your prediction and the weeks available).
- "strengths": what the data shows they already do well, each a short phrase (may be empty).
- "limiters": at most 3, most important first, each with the evidence from the data in one sentence.
- "risks": what the plan must be careful about, each a short phrase (may be empty).
- "priorities": one entry per phase (base, build, peak, taper) saying what to develop and with which kinds of sessions.
Be honest: if the data is too thin to judge something, say so instead of guessing.

Data (JSON):
```json
{{context}}
```
