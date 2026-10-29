---
name: retro
description: After a /build (finished or stopped), turn what went wrong into ONE lasting change, and record it so the next build can start.
disable-model-invocation: true
---

# /retro

Ten minutes after each `/build`, usually while the user reviews the merge request. The referee wrote the facts; you help the user turn them into **one** change. The next `factory start` refuses until the decision is recorded, so this step can't be skipped.

The referee is `dotnet run "${CLAUDE_PLUGIN_ROOT}/scripts/factory.cs" -- <command>`, written below as `factory <command>`.

## 1. Read the retro
- `git rev-parse --git-common-dir` gives the shared git folder. The retro is in `<that folder>/factory/retros/<feature>.md` (the newest one with an empty `## Decision:` line).
- Read it and the plan it names. Read code only if a finding can't be understood without it.

## 2. Sort each problem into ONE bucket
List every problem from the Evidence section, one line each, with the bucket you suggest and why:
- **spec-gap:** the plan didn't say something it needed to (an edge case, an order of steps, a term, a doc to update). Fix it in the plan template, or better, in a referee plan check.
- **harness-gap:** the referee, a hook or a setting let something through, or blocked something wrongly. Fix it in the plugin.
- **rule-gap:** `CLAUDE.md` or `.claude/rules` is wrong or out of date. Fix it in this repo.
- **model-slip:** the rules were clear and were ignored. Turn the rule into a check (hook or referee), never into a louder sentence.

Ask the user to confirm or change the buckets, one question at a time.

## 3. Pick ONE change
- Suggest the single change that would have prevented the most attempts or the stop. Prefer a change that is **code** (a referee or hook check) over a change that is prompt text.
- Name the number it should move, from the retro's metrics (for example `first_pass_rate`, `attempts_per_task`, `review_findings`, `needs_you`).
- The user decides. If nothing is worth changing (for example, a clean first-try build), that's a valid decision too.

## 4. Record it
- `factory decide --bucket <spec-gap|harness-gap|rule-gap|model-slip> --change "<the change in one line>" --metric <number> --where <plugin|template|repo>`
- or `factory decide --none "<why nothing changes>"`
- Fill in the retro's `## Buckets` section with the lines from step 2.
- Tell the user where the change will be made. A plugin change is a pull request to the methodology repo; a repo change is a small commit or `/spec` in this repo. Don't make the change in this conversation unless the user asks.
- `factory report` later shows whether the number moved (`factory report --html` opens the same numbers as charts across builds). If it got worse after a few builds, the next retro reverts or replaces the change.
