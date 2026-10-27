---
name: onboard
description: Prepare this repo for the methodology, once per repo. Checks CLAUDE.md, sets up one check command, saves a baseline of existing failures, adds safety settings and a secrets scan.
disable-model-invocation: true
---

# /onboard

Run once per repo, before the first `/spec`. The plugin's files are in `${CLAUDE_PLUGIN_ROOT}`. Work through the steps in order.
Tell the user what you found at each step in one or two plain sentences.
**Never fix existing failures here.** Onboarding only records how things are today.

## 1. Start safely
- Confirm this is a git repo and the working tree is clean. If not, stop and ask the user to commit or stash.
- Create a branch: `chore/onboard`.

## 2. CLAUDE.md
- Check that `CLAUDE.md` exists at the repo root.
- If it is missing, stop and tell the user to add it from their CLAUDE.md project. Only if they ask, run `/init` to draft one instead.
- Do not rewrite their CLAUDE.md. You may only add the "Check command" line in step 3, after the user agrees.

## 3. One check command
The methodology needs ONE command that runs every standard check and exits non-zero on failure.
- If CLAUDE.md already names it, use that.
- Otherwise, detect the stack from the files (for example `*.sln`/`*.csproj`, `package.json`, `pyproject.toml`, `go.mod`) and find the real commands for: build, tests, lint or analyzers, format check, type check.
- Propose one wrapper and ask the user to confirm:
  - `make check` if `make` is available, as a `check` target in the `Makefile`
  - otherwise a script: `./check.sh` (macOS/Linux) or `./check.ps1` (Windows)
- Example for .NET: `dotnet build -warnaserror` (only if the repo already builds cleanly), `dotnet test`, `dotnet format --verify-no-changes`.
- Once agreed, add one line to CLAUDE.md under a heading "Check command", and write `.factory/config.json`:
  ```json
  { "check": "make check", "onboarded": "<YYYY-MM-DD>" }
  ```
- If the repo has its own review agent in `.claude/agents/` (one that knows the project's rules), add `"reviewer": "<agent name>"`. `/build` then runs it on every task before committing, and the referee refuses to commit a task without a passing review. Without one, only the whole-branch `/code-review` at the end runs.

## 4. Baseline of existing failures
- Run the check command once. Do not fix anything, even if it fails.
- Save the full output to `.factory/baseline.txt`.
- Save a summary to `.factory/baseline.json`:
  ```json
  {
    "date": "<YYYY-MM-DD>",
    "commit": "<git sha>",
    "exit_code": 1,
    "failing_tests": ["<test names>"],
    "lint_problems": 0,
    "warnings": 0
  }
  ```
- Later checks fail only on problems that are NOT in this baseline.
- If the check cannot run at all (missing SDK, broken build), stop and tell the user what is missing.

## 5. Safety settings
- Copy `${CLAUDE_PLUGIN_ROOT}/templates/settings.json` (from the methodology plugin) to `.claude/settings.json` in the repo.
- If the repo already has one, merge: keep every existing rule and add the template's `deny` and `ask` rules. Never remove a rule.
- The template has every command rule twice, as `Bash(...)` and `PowerShell(...)`, because on Windows Claude Code may run commands through PowerShell. Keep both.
- Show the user the final `deny` and `ask` lists.

## 6. Secrets scan at pre-commit
- Copy `${CLAUDE_PLUGIN_ROOT}/templates/pre-commit` to `.githooks/pre-commit` in the repo, make it executable, and run `git config core.hooksPath .githooks`.
- Add the line `.githooks/* text eol=lf` to `.gitattributes` (create it if missing), so Windows line endings never break the hook script.
- Check that `gitleaks` is installed (`gitleaks version`). If it is not, do NOT install it yourself: tell the user how (for example `winget install gitleaks`, `brew install gitleaks`), and note it in the summary.

## 7. Ignore and commit
- Add to `.gitignore`: `.factory/state.json`, `.factory/last-check.txt`, `.factory/pr-body.md` and `.factory/baseline.txt` (the raw output holds local paths and the user name). Commit `.factory/config.json`, `.factory/baseline.json`, `.gitattributes` and later `.factory/metrics.jsonl` (numbers only, no code or prompts).
- For typed languages (C#, TypeScript, Java, Go...), suggest the user check `/plugin` for a code-intelligence plugin for their language.
- Commit everything on `chore/onboard` with the message `chore: onboard repo for the methodology`. Do not push.

## 8. Summary for the user
End with a short summary:
- Check command: `<command>`
- Baseline: `<N failing tests, N lint problems, N warnings>` (or "clean")
- Safety rules added: `<count>`
- Secrets scan: on / needs gitleaks installed
- Next step: review the branch, merge it, then run `/spec` for your first feature.
