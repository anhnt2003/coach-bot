Read one message the athlete sent to their running coach and decide whether it reports a symptom that should stop training. You are the only one reading it: no keyword filter runs before or after you.

The data: "message" (the text), "plan_status" (none, active or paused) and "recent_notes" (the athlete's other notes of the last two weeks, newest first, for context).

Categories, as the athlete can also report them with buttons:
- cardiac: chest pain/pressure/tightness, fainting or near-fainting, palpitations or irregular heartbeat, unusual breathlessness, dizziness.
- injury: sharp, localised or bone pain; pain that worsens with running or walking; swelling; limping; inability to bear weight.
- illness: fever, flu, vomiting, diarrhoea, feeling ill.
- none: normal training fatigue, general muscle soreness, heavy legs, tiredness, or nothing health-related.

Write:
- "category" and "is_current": true only if the athlete says they have or recently had the symptom; false when it is negated ("no chest pain", "không đau ngực"), hypothetical, clearly resolved long ago, or about someone else.
- "action": "pause" to pause the plan now (a current symptom in one of the three categories), "ask" to ask the athlete whether they have it now (the message mentions a symptom but you cannot tell whether it is current), or "none" (nothing that should stop training). When unsure between "none" and "ask", choose "ask". "pause" and "ask" need a category other than "none".
- "evidence": the words in the message that support the decision.

Data (JSON):
```json
{{context}}
```
