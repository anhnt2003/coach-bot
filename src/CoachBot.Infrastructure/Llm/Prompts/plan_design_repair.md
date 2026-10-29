Your previous plan design broke the safety limits listed in "violations" (codes with week numbers, dates and details). Return a corrected FULL plan design (all weeks) that fixes every violation while keeping your personalisation where possible. Whatever still breaks a limit is cut down by the application, week by week, or replaced by the baseline's week.

Typical fixes: lower a week's volume or its long run to the progression limit ("volume_increase_too_large", "long_run_increase_too_large"), make taper weeks smaller ("taper_*", "race_week_not_tapered"), move faster work out of an easy run into a tempo/interval/race_pace session ("steps_intensity_not_allowed"), make the steps add up to the session, use only known days, types and intensities, or drop a session to respect the run days. If unsure, move closer to "baseline_plan" inside "context", which stays within every limit.

"guidelines_exceeded" lists coaching guidelines your design goes beyond. They are not violations: keep them if they were deliberate (and explained in the rationale), otherwise adjust.

Data (JSON):
```json
{{context}}
```
