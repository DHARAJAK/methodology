---
name: build
description: Build an approved plan task by task with fresh subagents, the factory referee and hooks, then evaluate the whole feature and open a pull request (GitHub) or merge request (GitLab).
disable-model-invocation: true
argument-hint: "specs/<NNN>-<name>.md"
---

# /build

Build the approved plan `$ARGUMENTS`. You are a thin coordinator: subagents write the code, the factory script decides pass, fail or stop. Keep your own context small: do not read large parts of the code yourself.

The referee is `dotnet run "${CLAUDE_PLUGIN_ROOT}/scripts/factory.cs" -- <command>`, written below as `factory <command>`. The path is filled in by Claude Code and works in Bash and PowerShell alike.

## 1. Start
- Switch to the base branch first (`base` in `.factory/config.json`; if none, the repo's `main` or `master`) and pull. Then run `factory start $ARGUMENTS`. If it refuses, tell the user why and stop.
- It creates the branch `feat/<plan>` and lists the tasks. If it says "Resuming", continue with the first task that is not committed (`factory status`).

## 2. For each task, in order
1. Launch a **fresh `implementer` subagent** with only: the plan path, the task number, and the referee path `${CLAUDE_PLUGIN_ROOT}/scripts/factory.cs`.
2. When it reports:
   - **PASS**, and `.factory/config.json` names a `reviewer` agent: launch that agent (fresh, read-only) on the task's uncommitted changes (`git diff HEAD` plus new files) with the plan path and task number. It checks the project's own rules. Then record its verdict:
     - no real problems: `factory review <task> --pass`, then `factory commit <task>`.
     - real problems: `factory review <task> --fail "<one-line summary>"`, and send the findings to a **new** implementer for the same task. If `factory` exits with code 2, go to step 5.
   - **PASS** and no `reviewer` configured: run `factory commit <task>`.
   - If the commit is refused (files changed, secrets found), send the problem to a new implementer for the same task.
   - **STOP**, or `factory` exits with code 2: go to step 5 (Needs you). Do not try to fix it yourself.
3. Tell the user one line per task: "Task 2 of 5 done (attempt 1)."

Never edit code yourself, never commit with git directly, never change the plan. The hooks block these anyway.

## 3. Code Evaluation (whole feature, once)
When `factory commit` says "All tasks done":
1. **Plan coverage:** every task in the plan is committed and every Given/When/Then has a test. The end-to-end check in the plan: run it if it can be run automatically, otherwise list it for the user's `/verify`.
2. **Review:** run the `/code-review` skill on this branch against the base branch (`base` in `.factory/config.json`, otherwise `base` in `.factory/state.json`). Only correctness problems and requirement gaps count, not style.
3. **ADR gaps:** list new dependencies or new patterns in the diff that no ADR in `docs/adr/` covers. For each, write a short ADR with `Status: Proposed` in `docs/adr/` and commit them: `git commit -m "docs: proposed ADRs"`. The user accepts or rejects them in the PR.
4. If the review found a real correctness problem or a requirement gap: run `factory stop "<one-line reason>"`, then go to step 5 (Needs you) with the findings. Do not fix it in this run.

## 4. Pull request (Done)
1. Run `factory finish`. It records the 13 metrics in `.factory/metrics.jsonl` and writes `.factory/pr-body.md`.
2. Replace "<filled in by /build>" in `.factory/pr-body.md` with: the review result, the ADR gaps found, and the end-to-end check for `/verify`.
3. Push the branch: `git push -u origin feat/<plan>` (the user approves the prompt).
4. Open the pull request (GitHub) or merge request (GitLab): `factory open-pr`. The referee picks the host from the origin URL (or `host` in `.factory/config.json`) and targets the `base` branch. If it says the CLI is missing or not signed in, tell the user the exact command it printed (`glab auth login` or `gh auth login`) and stop; the branch is already pushed.
5. Tell the user: the PR or MR link, the numbers in one line, and "Run `/verify` on the running app, read the diff, and merge it yourself."

## 5. Needs you (STOP)
- Run `factory finish` so the stop is counted in the metrics (no PR is opened).
- Tell the user, in plain words: which task or check stopped, the reason (from `factory status` and `.factory/last-check.txt`), and that the work is kept on the branch.
- Suggest the next step: usually the plan was unclear, so rerun `/spec` for this feature with what was learned.
