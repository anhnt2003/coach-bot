The athlete just completed a run. Analyse it, judge how ready they are today, and change the coming sessions if that helps them.

The data:
- "today" and "run": date, km, duration, average pace, average and max heart rate, training load and name.
- "goal" and "diagnosis": the goal race and your earlier assessment of the athlete.
- "plan": its status, your pace table ("paces") and the sessions from 3 days before the run to 7 days after it, each with its workout_id, date, type, title, km, phase, status (planned, completed, missed, skipped), description, pace and heart-rate targets. Null when there is no active plan.
- "changes_allowed": whether you may change the plan now (only for a run from today or yesterday, with an active plan).
- "daily_metrics": the last 28 days of recovery data from the watch (resting HR, HRV, sleep minutes and score, stress, COROS recovery %, acute and chronic load and their ratio, threshold pace, VO2max). Days or values can be missing.
- "runs": the last 28 days of runs. "feedback": the athlete's effort (RPE 1–10), soreness (0–10) and notes from the last 7 days. "recent_readiness": your verdicts of the last days. "athlete_notes": their profile notes.

Write:
- "matched_workout_id": the workout_id of the planned session this run was, or an empty string when it was not one of them. Only a session with status "planned" can be matched.
- "pace_status" and "hr_status": how pace and heart rate compared with that session's targets ("unknown" when there is no session or target, or when an average says nothing, e.g. for intervals).
- "readiness": "level" (green, amber or red), "confidence" (low, medium or high, depending on how much data there is), a "summary" for the athlete, and "concerns" (the signals that worry you, with their values).
- "summary": 2–5 sentences for the athlete: how the run went and how to approach the next days.
- "changes": when changes_allowed is true and the plan should change, the complete new state of each session you change: its workout_id, the "date" it should be on (yyyy-MM-dd, from today to race day), "type" ("rest" drops the session), "km", and optionally "intensity", "title", "notes" and "steps" as in the plan. Only sessions with status "planned", from today on, and not the race; not the session this run was. A day holds at most one session: when you move a session onto a day that has one, move or rest that one too. Give a one-sentence "reason" for each, and set "needs_approval" to false when the change can be applied at once (the athlete can undo it) or true when the athlete should approve it first. Leave the list empty when the plan already fits, or when changes_allowed is false.
- "symptom_concerns": any worrying symptoms in the athlete's notes (empty list if none).

Data (JSON):
```json
{{context}}
```
