# methodology (Claude Code plugin)

Spec-first agentic coding with Claude Code, for .NET repos: `/onboard`, `/spec`, `/build`, a referee script that counts attempts and refuses failing commits, hooks Claude cannot skip, and quality metrics. Design and decisions: ADR-001 to ADR-015.

## Install (once per laptop)
In Claude Code:
```
/plugin marketplace add <github-user>/methodology
/plugin install methodology@methodology
```
Restart Claude Code. Update later with `/plugin marketplace update methodology`.

Needs: git, the .NET 10 SDK (one that matches the repo's `global.json`, because the hooks and referee run with `dotnet run` inside the repo), `glab` for GitLab or `gh` for GitHub (signed in; the referee picks one from the origin URL), gitleaks (recommended). Hooks run without a shell, so Git Bash is not required for them.

## Use in a repo
1. Make sure the repo has a `CLAUDE.md`.
2. `/onboard` once. Review and merge the `chore/onboard` branch. It adds `.factory/` (check command, baseline, metrics), `.claude/settings.json` (safety rules) and `.githooks/pre-commit` (secrets scan).
3. Size each piece of work:
   - **Tiny:** just ask, run the check, commit.
   - **Bug:** failing test first, fix, check, PR.
   - **Medium:** plan mode, Ctrl+G to edit, build, check, PR.
   - **Feature:** `/spec <idea>`, approve, `/clear`, `/build specs/<file>` (or the `specs` folder set in `.factory/config.json`). The build runs to the end without asking you anything; you get one message (and a Windows notification) when the MR is ready or the build needs you. Then `/verify`, merge the MR yourself, and run `/retro`.
4. After every build, `/retro` turns the worst problem into ONE change (`factory decide`). The next `/build` waits until that decision is recorded. `factory report` shows whether each change moved its number. Label bugs found after merge `escaped`.

If a skill name clashes with another plugin, use the full name, e.g. `/methodology:spec`.

## What's inside
| Path | What it does |
|---|---|
| `plugins/methodology/skills/onboard` | `/onboard`: check command, baseline, safety rules, secrets scan |
| `plugins/methodology/skills/spec` | `/spec`: interview, glossary, plan with Given/When/Then, approval checklist |
| `plugins/methodology/skills/build` | `/build`: one fresh subagent per task, referee, per-task project review, Code Evaluation, PR; one uninterrupted run |
| `plugins/methodology/skills/retro` | `/retro`: sort what went wrong into a bucket, pick ONE change, record it with `factory decide` |
| `plugins/methodology/agents/implementer.md` | Builds one task, test first (proves the test fails with `factory red`) |
| `plugins/methodology/scripts/factory.cs` | The referee: start (with plan checks and the retro gate), red, check, review, commit, status, stop, finish (writes the retro), open-pr, decide, report |
| `plugins/methodology/scripts/hooks.cs` | Lock the plan, no raw commit, format `.cs`, no finishing with open tasks, Windows notification when a build ends or Claude waits |
| `plugins/methodology/hooks/hooks.json` | When each hook runs (only in onboarded repos) |
| `plugins/methodology/templates/` | Safety settings and pre-commit hook that `/onboard` copies into a repo |
| `plugins/methodology/scripts/test-factory.ps1` | Tests the referee in a throwaway repo |
| `plugins/methodology/scripts/test-hooks.ps1` | Tests the hooks and the git commit guard |

Status: on Windows with the .NET 10 SDK, the referee passes `test-factory.ps1` and the hooks and git commit guard pass `test-hooks.ps1` (v0.1.5). Both run automatically on a Windows machine for every PR and push to `main` (`.github/workflows/test.yml`); a red tick means a guard rail has a hole. The `/spec`, `/build` and `/onboard` flows, and real `glab`/`gh` calls, are not yet tested end to end.

## How test-first is enforced
Prompts guide; hooks and gates enforce. The implementer is told to write tests first, the referee refuses `check` until `factory red` saw those tests fail, and only `factory commit` may commit during `/build`. That last rule is held by two layers: a Claude Code hook that blocks `git commit` (including `git -C . commit` and `--no-verify`), and a git `pre-commit` guard that refuses any commit during a build without the referee's marker, however it was started. Guard hooks fail closed: if they cannot read their input, they block. Remaining limit: something that rewrites `.factory/state.json` from a shell could forge the referee's records; the per-task reviewer and the merge request review are the backstop.

## How the workflow improves itself (ADR-016)
After every build, passed or stopped, `factory finish` writes a retro: each task's attempts, whether test first ran, the reviewer findings text, the check failures, dependencies not in the plan, and a docs warning. It's kept in the shared git folder (`.git/factory/retros/`), so it survives a stop on an unmerged branch, all worktrees see it, and it's never pushed. `/retro` sorts each problem into spec gap, harness gap, rule gap or model slip, and you pick ONE change, preferably a check in code. `factory decide` records it with its number before; `factory start` refuses the next build until that's done; `factory report` shows the number after. When a build stops, `/build` saves what the follow-up spec needs with `factory note` into the same retro (never a temp folder), `/spec` reads it, and the follow-up may start on the stopped branch so both land in one merge request.

The first round of changes (from the first real build, which stopped on log masking) is built in: `factory start` refuses a plan where a task has no `Scenario: Tricky:` (or `Tricky inputs: none (reason)`), where the "Differences from existing behaviour" section or the glossary is missing, or which mentions a file that doesn't exist, isn't in a task's Files line, and isn't marked (new).

The second round (from build 002, which stopped on the same log masking) adds three things. A task that parses, masks, redacts, escapes, normalizes or validates text needs an exact `| Input | Output |` table of at least 5 rows; `/spec` builds it with an adversarial pass you approve row by row. The end-to-end check must be owned by a task (`Scenario: End-to-end:`), so it gets a test. Files that were untracked before the build stay out of task commits. `factory note --file` keeps findings with quotes intact.
