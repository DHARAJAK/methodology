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
- If this follows up a stopped build, read its retro (`git rev-parse --git-common-dir`, then `factory/retros/<feature>.md`): the Evidence and "Notes for the next spec" sections are your brief. Every finding there needs a task or a line in Out of scope.

## 2. Interview (about 10 questions max)
- Ask ONE question at a time. Offer 2-4 likely answers plus "other" when you can.
- Ask about: who uses it and why, the main flow, edge cases and errors, what is out of scope, data and interfaces, anything that would need a new dependency or a new decision.
- Build a **glossary** as you go: the customer's words and what each means (for example "Cart = items picked but not paid for"). Use those words exactly in the plan.
- Stop when you could write every done-when without guessing, or after about 10 questions. List any assumptions you are still making.

## 3. Plan file
- Write the plan in your reply first (not to disk yet), using `plan-template.md` next to this skill.
- Every task is small enough for one fresh subagent: a few files, one behavior.
- Every task has at least one done-when written as **Given / When / Then**, with real example input and a result a user would notice. No vague ones like "login works".
- Name the files and interfaces each task touches, what is out of scope, any new dependency, any decision that needs an ADR, and one end-to-end check for the whole feature.
- Every task gets a `Scenario: Tricky:` for the input most likely to break it (ask about these in the interview), or `Tricky inputs: none (<reason>)`. List differences from existing behaviour (or "none"). `/build` refuses a plan without these, and refuses a plan that mentions a file that doesn't exist and isn't in a task's Files line or marked (new).

## 4. Approve
- Go through the approval checklist at the end of the template with the user, item by item.
- If anything fails, or the user says "not yet", go back to step 2 for the missing part. The user can also edit the plan directly (Ctrl+G).
- Continue only when the user clearly says the plan is approved.

## 5. Record
Only now may you write files:
- Save the plan to `<specs>/<NNN>-<short-name>.md` (next free number), with `Status: Approved <YYYY-MM-DD>`.
- For each decision the plan lists, write an ADR in `<adrs>/` using the next free number. Follow the format and index in that folder's README if it has one; otherwise create the folder and an index.
- Commit only those files: `spec: <short-name>`. Do not start building.
- Tell the user: "Plan approved and committed. Run `/build <specs>/<file>` when ready." After approval the plan is locked: changing it means running `/spec` again.
