It is morning. Judge how ready the athlete is today from their recovery data, tell them how to approach today, and change the coming sessions if that helps them.

The data:
- "today", "goal" and "diagnosis": the date, the goal race and your earlier assessment of the athlete.
- "plan": its status, your pace table ("paces"), the sessions from 3 days ago to 7 days ahead (each with its workout_id, date, type, title, km, phase, status, description, pace and heart-rate targets), and "just_missed": the workout_ids of sessions that just passed without a run.
- "changes_allowed": whether you may change the plan now (only when it is active, not paused).
- "daily_metrics": the last 28 days of recovery data from the watch (resting HR, HRV, sleep minutes and score, stress, COROS recovery %, acute and chronic load and their ratio, threshold pace, VO2max). Days or values can be missing; last night's values are today's.
- "runs": the last 28 days of runs. "feedback": the athlete's effort (RPE 1–10), soreness (0–10) and notes from the last 7 days. "recent_readiness": your verdicts of the last days. "athlete_notes": their profile notes.

Write:
- "readiness": "level" (green, amber or red), "confidence" (low, medium or high, depending on how much data there is), a "summary" for the athlete, and "concerns" (the signals that worry you, with their values).
- "advice": 1–3 sentences on how to approach today.
- "changes": when changes_allowed is true and the plan should change, the complete new state of each session you change: its workout_id, the "date" it should be on (yyyy-MM-dd, from today to race day), "type" ("rest" drops the session), "km", and optionally "intensity", "title", "notes" and "steps" as in the plan. Only sessions with status "planned", from today on, and not the race. A day holds at most one session: when you move a session onto a day that has one, move or rest that one too. Give a one-sentence "reason" for each, and set "needs_approval" to false when the change can be applied at once (the athlete can undo it) or true when the athlete should approve it first. Leave the list empty when the plan already fits, or when changes_allowed is false.

Data (JSON):
```json
{{context}}
```
