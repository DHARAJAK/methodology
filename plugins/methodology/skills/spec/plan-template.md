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

## Design
- **Files and interfaces:** <files to add or change; public functions, endpoints, tables>
- **New dependencies:** <package and why, or "none">. Nothing else may be installed.
- **Decisions needing an ADR:** <choice and short reason, or "none">

## Tasks
### Task 1: <name>
- **Files:** <paths>
- **Done when:**
  ```
  Scenario: <short name>
  Given <starting situation, with real example data>
  When <the user's action>
  Then <result the user would notice>
  ```

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
- [ ] There is one end-to-end check.
- [ ] Optional: the customer has read the scenarios.
