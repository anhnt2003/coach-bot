# Safety rules

CoachBot is conservative by design. All rules below are deterministic code in `src/CoachBot.Core/Safety` and
`src/CoachBot.Core/Planning`, configurable via `Safety__*` environment variables (see `SafetyOptions`), and covered by
tests — including grids that generate ~27,000 plans (marathon, half marathon, 10K and 5K × volumes × run days × zone
types × long-run days × race days) and check every one against the validator. **This is training guidance, not medical
advice.**

## Who decides what

The LLM proposes, the rules check. Nothing the LLM returns reaches the athlete's plan without passing the same
validator as rule-based plans, and every load increase needs the athlete's explicit approval.

| Decision | Proposed by | Checked / decided by |
|---|---|---|
| Diagnosis (level, goal realism, limiters, risks, what each phase should develop) | **LLM**, from 8 weeks of runs, fitness data and notes | advice only: shown to the athlete and given to the plan design, run analysis and weekly report; it changes no number by itself |
| Training plan (weeks, sessions, long-run build-up, cutbacks, taper) | **LLM**, starting from a rule-based baseline, its diagnosis and the athlete's notes | `PlanValidator`; on violations the LLM gets **one** repair attempt, then the rule-based plan is used (and the athlete is told) |
| Workout content (e.g. 5 × 1 km at 5K pace, long run with a half-marathon-pace finish, hills, strides) | **LLM**, as structured steps with named intensities, chosen for the goal distance and the athlete's limiters | `PlanValidator`: intensities allowed per session type, time at each intensity per session, steps add up to the distance |
| Paces and heart-rate targets | Rules, from measured fitness (`PaceZoneCalculator`) — never the LLM; the LLM only names the intensity | — |
| Readiness (green/amber/red) | Rules (`ReadinessEvaluator`) | — |
| Easing the plan (reduce, downgrade, rest, swap days) | Rules (`AdaptationEngine`) **and** the LLM | `PlanValidator`; rules win on conflicts; minor → auto-applied with Undo, otherwise approval |
| Progression (longer or harder sessions) | **LLM**, only on green days with ≥ medium confidence, ≤ +5 km per session | `PlanValidator` progression limits; never in taper or cutback weeks; **always** needs approval, sent separately from easing |
| Pace updates after a fitness change | Rules (COROS threshold pace) | always needs approval |
| Minor vs major | Rules (`ChangeClassifier`) | — |
| Red-flag symptoms | Keyword detector **and** LLM triage | see "Red-flag symptoms" |
| Explanations, weekly focus, run analysis, reports | LLM (templates when disabled or failing) | — |

With `Llm__Provider=none`, or when the LLM fails, CoachBot runs entirely on the rules.

## Plan rules (enforced on rule-based and LLM-designed plans)

Limits that depend on the goal distance (`SafetyOptions.ForRace`; the configured values are the marathon's, and shorter
races never get higher ceilings than the configured ones, except the race-week shares: a shorter race tapers less):

| | Marathon | Half marathon | 10K | 5K |
|---|---|---|---|---|
| Long run cap | 32 km / 3 h 15 | 22 km / 2 h 30 | 18 km / 2 h | 16 km / 1 h 45 |
| Peak weekly volume | ≤ 90 km (≥ 50 km floor, 1.8 × base) | ≤ 70 km (40, 1.6×) | ≤ 65 km (35, 1.5×) | ≤ 60 km (30, 1.5×) |
| Taper (share of peak) | 80/60/40% | 80/55% | 85/65% | 70% |
| Race week / week before, at most | 60% / 85% of peak | 60% / 85% | 70% / 90% | 75% / — |
| Minimum weeks (recommended) | 8 (12) | 6 (10) | 4 (8) | 4 (6) |
| Shorter build allowed from | 40 km/week + 21 km long run | 30 + 15 km | 20 + 10 km | 15 + 8 km |
| Low-volume warning below | 20 km/week | 15 | 12 | 10 |
| Race-pace sessions | marathon pace | half-marathon pace | 10K pace | 5K pace |

- Start from the **4-week average** volume, never the peak. No history → 15 km/week with a warning.
- Weekly volume rises at most **min(10%, +8 km)** over the last non-cutback week; **cutback every 4th week** (−25%).
- Peak volume capped at max(base, min(cap, max(floor, multiplier × base))) km (table above).
- Long run: grows by ≤ **3 km/week**, targets ≤ **45%** of the week (40/35/30% for half/10K/5K), hard caps **50%** and
  the distance/duration caps above (at your slowest long-run pace, or 7:00/km when pace is unknown). *Deviation from
  the original plan (30%): with 30%, a typical runner peaking at ~57 km/week never ran longer than ~17 km, which does
  not prepare for a marathon.*
- Hard sessions (tempo, intervals, race pace, long run, race) are never on consecutive days, including across
  weeks; ≤ 1 quality session/week in the base phase, ≤ 2 otherwise; ≥ 75% of volume easy. Structured sessions count
  their steps (steady running half, marathon pace and faster fully); sessions without steps count a typical share
  (tempo 50%, intervals 40%, race pace 60%).
- **Time at intensity per session** (Daniels' guidelines; share of that week's volume, with a minimum so low-volume
  runners can still do a real session): threshold and 10K pace ≤ 10% (at least 4 km, at most 15 km); 5K pace and hills
  ≤ 8% (3 km, 10 km); repetition pace and strides ≤ 5% (1.6 km, 8 km); marathon and half-marathon pace ≤ 20% (6 km,
  29 km).
- **Structured workouts**: each session type allows only certain intensities — easy runs: easy/recovery plus ≤ 12
  strides of ≤ 30 s; long runs: easy, steady, marathon or half-marathon pace (fast finishes), nothing faster; tempo needs
  threshold-type work; intervals need 5K/10K/repetition pace or hills; race-pace sessions need work at the goal race
  pace; recoveries are never faster than steady. Steps must add up to the session distance (±10%, at least 1 km).
- Easy runs shorter than 5 km are dropped by the generator (rest day); no run of any plan may be under 3 km.
- At most `run_days_per_week` run days, one session per day. Limits on long runs apply to the longest run of the week
  whatever it is labelled (a 35 km "easy" run is rejected).
- **Taper:** the table above (marathon: 3 weeks for plans of 12+ weeks, 2 for 8–11); taper weeks only step down and
  stay ≤ 85% of peak; the two days before the race are never hard. For any plan: exactly one race session on race day
  and nothing after it.
- **Feasibility:** race < 1 week away, fewer than the minimum weeks without an experienced base (table above), or
  logged volume < 10 km/week → no plan, with an explanation. Fewer than the recommended weeks or volume under the
  warning level → warnings.
- **Paces** fall back from COROS threshold pace → COROS marathon prediction → recent runs (Riegel, deliberately
  conservative) → heart-rate zones → RPE only. Every intensity is derived from threshold pace: race paces for 5K–marathon
  by Riegel's formula (marathon ≈ 107%, half ≈ threshold / 0.97), easy 118–132%, steady 109–115%, repetition 84–89%;
  hills and strides by effort. A goal more than 5% faster than the predicted race pace is *not* used for training paces.

## Readiness (28-day personal baseline, ≥ 7 data points)

| Signal | Amber | Red |
|---|---|---|
| HRV (7-day average vs baseline) | < 90% | < 80% |
| Resting HR | ≥ baseline + 5 bpm | ≥ + 8 bpm two days in a row |
| Sleep | < 6 h two nights in a row | — |
| COROS recovery | < 50% | < 25% |
| Acute:chronic load ratio | > 1.3 | > 1.5 |
| RPE on an easy run / soreness | ≥ 7 | — |

Three amber signals escalate to red. Confidence (high/medium/low) depends on how many objective signals are
available. **Missing data never counts as "good"**: it is listed in every message and lowers confidence.

## Adaptation

- **Amber:** next hard session within 2 days softened (quality → easy, or long run −20%).
- **Red** (or amber 2 days running): next session → recovery (−30%), or rest if it was already easy.
- **Red two days running:** proposal to cut the rest of the week by 30% and drop intensity (major).
- **Fitness change ≥ 3%** (COROS threshold pace, green day only): proposal to update quality paces (major).
- **LLM proposals** are merged after the rules: easing changes join the rule-based ones; progression is a separate
  proposal that must stay within the weekly progression limits (≤ min(10%, +8 km) over the last full week, long run
  +3 km), keep ≥ 75% easy volume and the per-session intensity limits, and never touch taper or cutback weeks.
- A shorter structured session keeps its structure: warm-up and cool-down shrink first (to 1 km), then repeats are
  dropped. Changing a session's type (e.g. intervals → easy) removes its structure. Pace updates move every step.
- Fatigue can never produce a load increase. Missed sessions are recorded but **never made up**.

## Minor vs major changes

- **Minor → applied automatically, with an Undo button:** exactly one workout, within the next 3 days, not in the
  taper, reduced by ≤ 30% (long run ≤ 20%), downgraded, an easy run turned into rest, or swapped within the same week.
- **Major → needs your approval** (expires after 48 h, plan unchanged): anything that adds load, more than one
  workout, pace changes, taper changes, dropping a key session, plan regeneration.
- Every change (and every undo) is recorded in `coach.plan_changes` with before/after snapshots.

## Red-flag symptoms

Any free-text message, `/feedback`, or `/symptom` is screened twice: by a deterministic keyword detector (English and
Vietnamese, with or without diacritics) and, when an LLM is configured, by an LLM triage that understands negation
and context.

| Category | Examples | Response |
|---|---|---|
| Cardiac | chest pain/tightness, fainting, palpitations, unusual breathlessness, dizziness | stop, urgent medical assessment, emergency number (115 in Vietnam) |
| Injury | sharp/bone pain, swelling, limping, pain getting worse | stop running, see a doctor/physio |
| Illness | fever, flu, vomiting | no training until recovered |

| Keyword detector | LLM triage | Result |
|---|---|---|
| red flag | unavailable/failed, or confirms | **plan paused immediately** |
| red flag | says negated / not current ("không hề đau ngực") | **athlete asked to confirm** with buttons; nothing paused until they answer |
| nothing | finds a current symptom ("đầu gối nhói khi xuống dốc") | **plan paused immediately** |
| nothing | nothing | stored as normal feedback |

The LLM can add a red flag but can never silently clear one: the athlete always decides. When paused, nothing more is
pushed to COROS, and `/resume` asks the athlete to confirm they are symptom-free.
