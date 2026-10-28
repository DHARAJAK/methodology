# Plan: <feature name>
Status: Draft
Spec number: <NNN>

## Goal
<2-3 lines: who gets what, and why>

## Glossary
| Term | Meaning |
|---|---|
| <customer's word> | <what it means here> |

## Scope
- **In:** <what this feature does>
- **Out of scope:** <what it deliberately does not do>

## Differences from existing behaviour
<where this deliberately differs from today's app, the POC or the docs, and why; or "none">

## Design
- **Files and interfaces:** <files to add or change; public functions, endpoints, tables>. Mark a file the build will create with (new).
- **New dependencies:** <exact package IDs and why, or "none">. Nothing else may be installed.
- **Decisions needing an ADR:** <choice and short reason, or "none">

## Tasks
### Task 1: <name>
- **Files:** `<path>`, `<path>`
- **Done when:**
  ```
  Scenario: <short name>
  Given <starting situation, with real example data>
  When <the user's action>
  Then <result the user would notice>

  Scenario: Tricky: <the input most likely to break it>
  Given <the awkward case: empty, spaces, quotes, separators, very long, wrong order, camelCase name...>
  When <the same action>
  Then <what must happen>
  ```
  (Only for a task with no awkward input at all, such as docs only: replace the Tricky scenario with a line "Tricky inputs: none" followed by the reason in brackets.)

### Task 2: <name>
...

## End-to-end check
<one Given/When/Then scenario for the whole feature, run at the end>

## Assumptions
<anything still assumed after the interview, or "none">

## Approval checklist
- [ ] The goal is clear in 2-3 lines.
- [ ] Glossary terms are used consistently in the plan.
- [ ] Out of scope is listed.
- [ ] Files and interfaces are named for every task.
- [ ] Every task is small: a few files, one behavior.
- [ ] Every task has at least one Given/When/Then with real example data and a user-visible result.
- [ ] New dependencies are listed (or "none").
- [ ] Decisions needing an ADR are listed (or "none").
- [ ] Every task has a `Scenario: Tricky:` for its most awkward input, or `Tricky inputs: none (reason)`.
- [ ] Every file the plan mentions exists, is in a task's Files line, or is marked (new).
- [ ] Differences from existing behaviour are listed (or "none").
- [ ] If behaviour changes, a task updates `CLAUDE.md` and the docs that describe it.
- [ ] There is one end-to-end check.
- [ ] Optional: the customer has read the scenarios.
