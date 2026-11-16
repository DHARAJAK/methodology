---
name: wrap-up
description: At the end of a session or a /build, propose changes to the files that steer Claude (CLAUDE.md, .claude/rules/, the spec template, the roadmap), each with evidence, for the user to accept later. Never edits those files.
---

# /wrap-up

Keeps Claude's working context true, so the next session does not start from stale facts. You **propose**; the user decides later in `/retro` (or when they ask). `/build` runs these steps itself after `factory finish`; the user types `/wrap-up` at the end of any other session.

The referee is `dotnet run "${CLAUDE_PLUGIN_ROOT}/scripts/factory.cs" -- <command>`, written below as `factory <command>`.

## 1. Compare what happened with what the files say
- Look at what changed in this session or build: `git log` and `git diff` since it started, the retro notes (for a build), decisions the user stated in the conversation.
- Read the files that steer Claude: `CLAUDE.md`, `.claude/rules/*.md`, the spec template, and the roadmap or status doc if the repo has one (for example `docs/ROADMAP.md`).
- Find only real mismatches: a status that is now wrong, a decision the user made that no file records, a new project, folder or pattern that is undocumented, a rule that caused a mistake or a wasted attempt.

## 2. Write at most 3 proposals, most important first
For each, write a file `.factory/proposal.txt` with exactly these lines, then run `factory propose --file .factory/proposal.txt`:
```
File: <the file to change>
Change: <what to add, change or REMOVE, in one or two sentences>
Why: <the mistake or confusion it prevents>
Evidence: <a commit, a test, a file and line, or the user's own words with the date>
Size: minor | significant
```
- **Evidence is required.** Your own belief is not evidence. "Worked live" needs the user's words; "implemented" needs a commit.
- **minor** = wording or facts. **significant** = adds, changes or removes a rule.
- Prefer removing or replacing old text over adding more: a longer `CLAUDE.md` is followed less well. Changing facts (what works today, what is next) belong in the roadmap, not in `CLAUDE.md`.
- Never propose loosening a rule, a test or a check to make a failure go away. Report that to the user instead.
- If `factory propose` says too many are waiting, stop and tell the user.
- If nothing is out of date, propose nothing. That is a good result.

## 3. Do not apply them
Do not edit those files now, even if the change looks small. Tell the user in one line: "N context proposals saved for your review (`/retro`)." The next session start reminds them.
