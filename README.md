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
   - **Feature:** `/spec <idea>`, approve, `/clear`, `/build specs/<file>`, then `/verify` and merge the PR yourself.
4. Every two weeks: `dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- report`, pick the worst number, make ONE process change, record it as an ADR. Label bugs found after merge `escaped`.

If a skill name clashes with another plugin, use the full name, e.g. `/methodology:spec`.

## What's inside
| Path | What it does |
|---|---|
| `plugins/methodology/skills/onboard` | `/onboard`: check command, baseline, safety rules, secrets scan |
| `plugins/methodology/skills/spec` | `/spec`: interview, glossary, plan with Given/When/Then, approval checklist |
| `plugins/methodology/skills/build` | `/build`: one fresh subagent per task, referee, per-task project review, Code Evaluation, PR |
| `plugins/methodology/agents/implementer.md` | Builds one task, test first (proves the test fails with `factory red`) |
| `plugins/methodology/scripts/factory.cs` | The referee: start, red, check, review, commit, status, stop, finish, report |
| `plugins/methodology/scripts/hooks.cs` | Lock the plan, no raw commit, format `.cs`, no finishing with open tasks |
| `plugins/methodology/hooks/hooks.json` | When each hook runs (only in onboarded repos) |
| `plugins/methodology/templates/` | Safety settings and pre-commit hook that `/onboard` copies into a repo |
| `plugins/methodology/scripts/test-factory.ps1` | Tests the referee in a throwaway repo |

Status: the referee passes `test-factory.ps1` (21 scenarios) on Windows with the .NET 10 SDK. `hooks.cs` and the `/spec`, `/build` and `/onboard` flows are not yet tested end to end.
