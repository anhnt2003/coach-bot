The athlete just completed a run. Analyse it using only the data below and propose adjustments to the upcoming workouts if they would help. "diagnosis" is your earlier assessment of the athlete (limiters and phase priorities for their goal distance); relate the run to it when relevant. For structured sessions ("planned.structure"), the average pace includes warm-up and recoveries, so judge the effort, not the average.

Write:
- "summary": 2–5 sentences: how the run compared with the plan, what the recovery signals say (including which are missing), and how to approach the next days. If rule_based_changes is non-empty, explain them in plain words — they are applied by the safety rules and take precedence.
- "suggestions": optional changes to workouts in "upcoming" (use their workout_id; do not touch workouts already in rule_based_changes). Allowed actions:
  - reduce_distance / downgrade (easier type) / convert_to_rest — any time recovery or the athlete's notes call for it. A shorter structured session keeps its structure with fewer repeats.
  - swap_days (with "new_date" yyyy-MM-dd within the next 14 days) — to fit the athlete's schedule or space hard days better.
  - increase_distance (at most +5 km on one session) / upgrade (harder type) — ONLY if readiness level is green with medium or high confidence, the plan is not in taper, and the recent runs show the athlete is handling the load well. Increases are always sent to the athlete for approval and must respect the weekly progression rules.
  Fewer, well-justified changes are better than many. Leave the list empty when the plan already fits.
- "symptom_concerns": any worrying symptoms in athlete_notes (empty list if none).

Data (JSON):
```json
{{context}}
```
