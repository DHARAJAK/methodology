---
name: spec
description: Turn a feature idea into an approved plan with Given/When/Then done-whens, by interviewing the user. Use for features with new behavior and several tasks.
disable-model-invocation: true
argument-hint: "<the idea in 2-3 lines>"
---

# /spec

Turn the idea in `$ARGUMENTS` into an approved, committed plan.
**Folders:** plans go in the `specs` folder and ADRs in the `adrs` folder named in `.factory/config.json` (defaults `specs/` and `docs/adr/`). Below they are written `<specs>/` and `<adrs>/`.
**You are read-only until step 5.** Read code freely, but do not create or change any file before the plan is approved.

## 0. Check the size first
If the idea is a tiny change, a single bug, or a medium change with an obvious approach, say so and suggest the lighter path (just ask / failing test first / plan mode). Continue only if the user confirms it is a feature.

## 1. Idea
- Restate the idea in 2-3 lines and ask the user to confirm.
- Use the Explore subagent to read the relevant code, `CLAUDE.md` and `<adrs>/`, so your questions are informed and the plan respects existing decisions.
- If this follows up a stopped build, read its retro (`git rev-parse --git-common-dir`, then `factory/retros/<feature>.md`): the Evidence and "Notes for the next spec" sections are your brief. Every finding there needs a task or a line in Out of scope. If the notes name a git stash with the unfinished attempt, ask the user whether to build on it or start fresh. To build on it, tell them to run `git stash apply` for it and commit it (`git commit -m "wip: <feature> task N"`) before `/build`, and write the tasks to finish that code.

## 2. Interview: the gap checklist
Research on coding agents shows they uncover less than half of a user's hidden requirements when they interview free-form, and they miss behaviour gaps more than simple ones. So work from a fixed list, not from memory.

**a. Sketch the tasks first** (in your head or your reply, not on disk): a few files and one behaviour each.

**b. Fill the gap checklist for every task from the code** (Explore subagent if needed). For each case below, write what happens, or `n/a (reason)`:

| Case | Ask yourself |
|---|---|
| Interrupted | The user cancels, closes the window, signs out, or the call ends while this runs. What is left half-done? |
| Two at once | Double press, two requests in flight, an older answer arriving after a newer one, two windows. Which one wins? |
| Slow or down | The server, network, device or SIP line is slow, refuses, times out or never answers. What does the user see? |
| Old data | Values saved before this change (database rows, settings files, cached tokens) that the new rule or format now rejects or reads differently. |
| Session ends | The login expires, the role changes, or a temporary password is reset mid-flow. Is every piece of session state cleared? |
| Who else uses it | Other code, tests, docs or settings that read, call or reference what this task changes, renames or deletes (search the repo). |
| Typed input | Text the user types or pastes (search, filter, parse, validate). If it applies, it needs the exact input -> output table (step 3). |

**c. Show your answers as assumptions, all at once:** a short list "I assumed: ..." per task, for everything the code answered. The user replies with the numbers that are wrong. This is quick for them and catches wrong guesses before they become code.

**d. Ask only the real decisions, one at a time** (the `AskUserQuestion` tool when available), hardest and most consequential first. Offer 2-4 likely answers plus "other". Ask in terms of what the user will *see* ("the call ends and shows 'Disconnected'"), not code. There is no question limit: stop when no checklist row is blank. Also cover who uses it and why, the main flow, out of scope, and anything needing a new dependency or decision.

**e. Never guess an open question.** If the user says "you pick", pick, and write it under Assumptions as "Claude chose X because Y". If something can't be answered now, write `NEEDS DECISION` next to it: `/build` refuses a plan that still has one, so it is answered or moved to Out of scope before approval.

Build a **glossary** as you go: the customer's words and what each means (for example "Cart = items picked but not paid for"). Use those words exactly in the plan.

## 3. Plan file
- Write the plan in your reply first (not to disk yet), using `plan-template.md` next to this skill.
- Every task is small enough for one fresh subagent: a few files, one behavior.
- Every task has at least one done-when written as **Given / When / Then**, with real example input and a result a user would notice. No vague ones like "login works".
- Name the files and interfaces each task touches, what is out of scope, any new dependency, any decision that needs an ADR, and one end-to-end check for the whole feature.
- Every task has its **Gap checklist** table (the 7 rows from step 2, each answered or `n/a (reason)`), and one `Scenario: Tricky:` per row that applies, so each case gets a test. A docs-only task writes `Gap checklist: none (<reason>)` instead. Every task also gets a `Scenario: Tricky:` for the input most likely to break it, or `Tricky inputs: none (<reason>)`. List differences from existing behaviour (or "none"). `/build` refuses a plan without these (missing rows, `n/a` without a reason, fewer Tricky scenarios than cases that apply, or any `NEEDS DECISION`), and refuses a plan that mentions a file that doesn't exist and isn't in a task's Files line or marked (new).
- **Adversarial pass, for any task that parses, masks, redacts, escapes, normalizes or validates text** (`/build` spots these by those words). Before approval, play the attacker: list at least 5 awkward inputs for the task (other spellings and casing, quotes and escaped quotes, JSON and `key=value` forms, separators such as `:` `=` `=>` `:=`, numbers in names, spaces, empty values, line breaks, very long input, and every case a past retro or review found). Ask the user to accept, change or drop each one, one question at a time. Each accepted input becomes a row of an exact table in the task's Done-when, with the exact output, not a description:
  ```
  | Input | Output |
  |---|---|
  | `password2=hunter2` | `password2=***` |
  ```
  `/build` refuses such a task with fewer than 5 rows, unless it says `Input table: none (<reason>)`. When a decision is the user's (is `nonce` a secret?), ask it here; never leave it for the build.
- The end-to-end check belongs to a task: write it again in that task as `Scenario: End-to-end: ...`, so it gets a test. `/build` refuses a plan whose end-to-end check no task owns (unless the section says `none (<reason>)`).

## 4. Approve
- Before approval, launch a **fresh edge-case reviewer** (a general-purpose subagent, read-only) with only the plan text and the repo: "Find cases in the gap checklist that are wrong or missing for this codebase." Add a Tricky scenario for each real one, or ask the user about it.
- Go through the approval checklist at the end of the template with the user, item by item.
- If anything fails, or the user says "not yet", go back to step 2 for the missing part. The user can also edit the plan directly (Ctrl+G).
- Continue only when the user clearly says the plan is approved.

## 5. Record
Only now may you write files:
- Save the plan to `<specs>/<NNN>-<short-name>.md` (next free number), with `Status: Approved <YYYY-MM-DD>`.
- For each decision the plan lists, write an ADR in `<adrs>/` using the next free number. Follow the format and index in that folder's README if it has one; otherwise create the folder and an index.
- Commit only those files: `spec: <short-name>`. Do not start building.
- Tell the user: "Plan approved and committed. Run `/build <specs>/<file>` when ready." After approval the plan is locked: changing it means running `/spec` again.
