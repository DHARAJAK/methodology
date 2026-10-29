# Tests the factory referee in a throwaway repo. Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File <plugin>/scripts/test-factory.ps1
# Needs the .NET 10 SDK and git. Deletes nothing outside its temp folder.

$fc = Join-Path $PSScriptRoot 'factory.cs'
$dir = Join-Path ([IO.Path]::GetTempPath()) ("factory-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $dir | Out-Null
Push-Location $dir
$bad = 0

git init -q -b main
git config user.email test@example.com
git config user.name test
New-Item -ItemType Directory .factory, specs | Out-Null
Copy-Item $fc .factory/factory.cs

$onWindows = $env:OS -eq 'Windows_NT'
if ($onWindows) {
    Set-Content check.cmd "@echo off`r`necho old: error in legacy.cs line 99`r`nif exist BAD echo new: error in app.cs`r`nexit /b 1" -Encoding ascii
    # .\ prefix: machines with NoDefaultCurrentDirectoryInExePath=1 do not run programs from the current folder.
    Set-Content .factory/config.json '{"check": ".\\check.cmd"}' -Encoding ascii
} else {
    Set-Content check.sh "echo 'old: error in legacy.cs line 99'`n[ -f BAD ] && echo 'new: error in app.cs'`nexit 1" -Encoding ascii
    Set-Content .factory/config.json '{"check": "sh check.sh"}' -Encoding ascii
}
Set-Content .factory/baseline.txt 'old: error in legacy.cs line 12' -Encoding ascii
Set-Content .gitignore ".factory/state.json`n.factory/last-check.txt" -Encoding ascii
# A plan that passes the plan checks (ADR-016): glossary, differences section, tricky inputs per task.
$good = "Status: Approved 2026-10-23`n## Glossary`n| Term | Meaning |`n|---|---|`n| Cart | items picked, not paid |`n## Differences from existing behaviour`nnone`n## Tasks"
Set-Content specs/001-cart.md "# Plan: cart`n$good`n### Task 1: add button`nTricky inputs: none (test)`n### Task 2: show total`nTricky inputs: none (test)" -Encoding ascii
# A plan with the gaps the checks catch: no tricky inputs, no differences section, a file that does not exist.
Set-Content specs/002-gaps.md "# Plan: gaps`nStatus: Approved 2026-10-23`n## Glossary`n| Term | Meaning |`n|---|---|`n| Cart | items |`n## Tasks`n### Task 1: roadmap`nUpdate ``docs/ROADMAP.md```nOut of scope: moving it to a ``harness/`` branch`n### Task 2: mask passwords`nScenario: Tricky: spaces`n| Input | Output |`n|---|---|`n| a | b |`n## End-to-end check`nGiven a cart When paid Then a receipt" -Encoding ascii
$table = "| Input | Output |`n|---|---|`n| ``SAVE10`` | 10% |`n| ``save10`` | 10% |`n| `` SAVE10 `` | 10% |`n| ``SAVE1O`` | invalid |`n| (empty) | invalid |"
Set-Content specs/004-next.md "# Plan: next`n$good`n### Task 1: tricky`nScenario: Tricky: empty cart`n### Task 2: parse coupon codes`nScenario: Tricky: lower case`n$table`nScenario: End-to-end: pay with a coupon`n## End-to-end check`nGiven a coupon When paid Then 10% off" -Encoding ascii
New-Item -ItemType Directory docs | Out-Null
Copy-Item specs/001-cart.md docs/001-cart.md
git add -A; git commit -qm init

# A referee that does not compile also exits 1, so report it as its own code or it would pass every "refuses" check.
function F {
    $out = & dotnet run .factory/factory.cs -- @args 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host $out.TrimEnd()
    if ($out -match 'The build failed') { return 'build-failed' }
    return $code
}
function ExpectOutput($name, [string[]]$fargs, $pattern) {
    $out = & dotnet run .factory/factory.cs -- @fargs 2>&1 | Out-String
    if ($out -match $pattern) { Write-Host "PASS  $name" -ForegroundColor Green }
    else { Write-Host "FAIL  $name (output did not match '$pattern'):`n$out" -ForegroundColor Red; $script:bad++ }
}
function Expect($name, $actual, $expected) {
    if ($actual -eq $expected) { Write-Host "PASS  $name" -ForegroundColor Green }
    else { Write-Host "FAIL  $name (exit $actual, expected $expected)" -ForegroundColor Red; $script:bad++ }
}

Expect "refuses a plan outside specs folder" (F start docs/001-cart.md) 1
$config = Get-Content .factory/config.json -Raw
Set-Content .factory/config.json ($config.Replace('}', ', "specs": "docs/"}')) -Encoding ascii -NoNewline
Expect "specs folder is configurable"        (F start specs/001-cart.md) 1
git checkout -q -- .factory/config.json
Expect "refuses a plan with gaps"            (F start specs/002-gaps.md) 1
ExpectOutput "names the task without tricky inputs" @('start', 'specs/002-gaps.md') 'Task 1 has no'
ExpectOutput "names the missing file"        @('start', 'specs/002-gaps.md') 'docs/ROADMAP.md'
$gapsOut = & dotnet run .factory/factory.cs -- start specs/002-gaps.md 2>&1 | Out-String
Expect "a branch name is not a missing path"  ($gapsOut -match 'mentions harness') $false
ExpectOutput "asks for the differences section" @('start', 'specs/002-gaps.md') 'Differences from existing behaviour'
ExpectOutput "asks for an input/output table"  @('start', 'specs/002-gaps.md') 'Task 2 parses or transforms text'
ExpectOutput "asks a task to own the end-to-end check" @('start', 'specs/002-gaps.md') 'No task owns the end-to-end check'
Set-Content draft.md 'old notes, not part of the feature'
Expect "start creates the branch (@path too)" (F start '@specs/001-cart.md') 0
Expect "refuses commit before a pass"        (F commit 1) 1
Expect "refuses check before a failing test" (F check 1) 1
Expect "rejects tests that already pass"     (F red 1 --test "exit 0") 1
Expect "records a failing test (red)"        (F red 1 --test "exit 1") 0
Set-Content a.txt 'x'
Expect "ignores errors already in baseline"  (F check 1) 0
Set-Content b.txt 'y'
Expect "refuses commit if files changed"     (F commit 1) 1
Expect "re-check passes"                     (F check 1) 0
Expect "commit after a pass"                 (F commit 1) 0
Expect "old untracked files stay out of commits" ((git show --name-only --format= HEAD) -contains 'draft.md') $false
New-Item BAD -ItemType File | Out-Null
Expect "red can be skipped with a reason"    (F red 2 --skip "no behaviour to test") 0
Expect "new error fails (attempt 1)"         (F check 2) 1
Expect "new error fails (attempt 2)"         (F check 2) 1
Expect "STOP after 3 failures"               (F check 2) 2

Remove-Item BAD
git switch -q main
git branch -q -D feat/001-cart
Remove-Item .factory/state.json
F start specs/001-cart.md | Out-Null
Add-Content specs/001-cart.md 'sneaky edit'
Expect "STOP if the plan is edited"          (F check 1) 2

git checkout -q -- specs/001-cart.md
git switch -q main
git branch -q -D feat/001-cart
Remove-Item .factory/state.json
F start specs/001-cart.md | Out-Null
Set-Content .factory/config.json '{"check": "no-such-check-command"}' -Encoding ascii
F red 1 --test "exit 1" | Out-Null
Expect "a check that cannot run fails"       (F check 1) 1

if ($onWindows) {
    # Prints only a baseline error, so this passes only if the script really ran (a script that cannot start now fails).
    Set-Content check.ps1 "Write-Output 'old: error in legacy.cs line 99'`r`nexit 1" -Encoding ascii
    Set-Content .factory/config.json '{"check": "./check.ps1", "reviewer": "test-reviewer"}' -Encoding ascii
    Expect "a .ps1 check runs"                   (F check 1) 0
    Expect "refuses commit before review"        (F commit 1) 1
    Expect "failed review fails the task"        (F review 1 --fail "event raised under lock") 1
    Expect "re-check after review findings"      (F check 1) 0
    Expect "passed review is recorded"           (F review 1 --pass) 0
    Expect "commit after check and review"       (F commit 1) 0

    F red 2 --skip "docs only" | Out-Null
    Set-Content notes.md 'docs' -Encoding ascii
    F check 2 | Out-Null
    F review 2 --pass | Out-Null
    Expect "last task commits"                   (F commit 2) 0
    Expect "finish records metrics"              (F finish) 0
    Expect "finish commits the metrics line"     ((git status --porcelain -- .factory/metrics.jsonl) -eq $null) $true
    Expect "open-pr refuses an unfilled body"    (F open-pr --dry-run) 1
    (Get-Content .factory/pr-body.md -Raw).Replace('<filled in by /build>', 'Review clean.') | Set-Content .factory/pr-body.md -Encoding ascii
    git remote add origin https://gitlab.example.test/group/project.git
    ExpectOutput "GitLab origin opens an MR into main" @('open-pr', '--dry-run') 'glab mr create --source-branch feat/001-cart --target-branch main'
    Set-Content .factory/config.json '{"check": "./check.ps1", "base": "poc/main"}' -Encoding ascii
    ExpectOutput "base branch is configurable"   @('open-pr', '--dry-run') '--target-branch poc/main'
    git remote set-url origin https://github.com/someone/project.git
    ExpectOutput "GitHub origin opens a PR"      @('open-pr', '--dry-run') 'gh pr create --head feat/001-cart --base poc/main'

    # Improvement loop (ADR-016): finish writes a retro, and the next start waits for its decision.
    $retro = Join-Path (git rev-parse --git-common-dir) 'factory/retros/001-cart.md'
    Expect "finish wrote a retro"                (Test-Path $retro) $true
    Expect "retro keeps the review findings"     ((Get-Content $retro -Raw) -match 'event raised under lock') $true
    Set-Content .factory/config.json '{"check": "./check.ps1"}' -Encoding ascii
    git add -A; git commit -qm "before next feature"
    Expect "start waits for the retro decision"  (F start specs/004-next.md) 1
    Expect "decide needs a known number"         (F decide --bucket spec-gap --change "x" --metric nope) 1
    Expect "decide needs a known bucket"         (F decide --bucket other --change "x" --metric first_pass_rate) 1
    Expect "decide records the change"           (F decide --bucket spec-gap --change "tasks list tricky inputs" --metric first_pass_rate) 0
    ExpectOutput "report shows the change"       @('report') 'tasks list tricky inputs'
    Expect "finish skips unreached tasks"        ((Get-Content (Join-Path (git rev-parse --git-common-dir) 'factory/metrics.jsonl') -Raw) -match '"tasks_reached"') $true
    ExpectOutput "report reads --days"           @('report', '--days', '30') 'Last 30 days'
    $env:METHODOLOGY_NO_OPEN = '1'
    ExpectOutput "report --html writes a page"   @('report', '--days', '30', '--html') 'Report page written'
    Expect "the page lists every build"          ((Get-Content (Join-Path (git rev-parse --git-common-dir) 'factory/report.html') -Raw) -match 'Every build') $true
    Expect "start goes ahead after the decision" (F start specs/004-next.md) 0

    # A follow-up spec builds on top of a stopped build's branch; notes for it go into the retro.
    F stop "test stop" | Out-Null
    ExpectOutput "a note before finish waits for this build" @('note', 'noted before finish') 'Noted for the 004-next retro'
    F finish | Out-Null
    $retros = Join-Path (git rev-parse --git-common-dir) 'factory/retros'
    Expect "early note lands in this build's retro" ((Get-Content (Join-Path $retros '004-next.md') -Raw) -match 'noted before finish') $true
    Expect "early note stays out of the old retro"  ((Get-Content (Join-Path $retros '001-cart.md') -Raw) -match 'noted before finish') $false
    F decide --none "test" | Out-Null
    ExpectOutput "note goes into the retro"      @('note', 'check the + prefix') 'Noted in'
    Expect "note is in the retro"                ((Get-Content (Join-Path (git rev-parse --git-common-dir) 'factory/retros/004-next.md') -Raw) -match 'check the \+ prefix') $true
    Set-Content note.txt 'leaks {\"password\":\"x\"} in escaped JSON' -Encoding ascii
    ExpectOutput "note reads a file"             @('note', '--file', 'note.txt') 'Noted in'
    Expect "quotes survive in a note"            ((Get-Content (Join-Path (git rev-parse --git-common-dir) 'factory/retros/004-next.md') -Raw).Contains('{\"password\":\"x\"}')) $true
    Remove-Item note.txt
    Set-Content .factory/config.json '{"check": "./check.ps1", "base": "main"}' -Encoding ascii
    Set-Content specs/005-follow.md "# Plan: follow`n$good`n### Task 1: fix`nTricky inputs: none (test)" -Encoding ascii
    git add -A; git commit -qm "follow-up plan"
    Expect "follow-up builds on the stopped branch" (F start specs/005-follow.md) 0
    Set-Content specs/006-follow-again.md "# Plan: Follow, after the stopped 005 build`n$good`n### Task 1: fix again`nTricky inputs: none (test)" -Encoding ascii
    git add -A; git commit -qm "same goal again"
    ExpectOutput "warns about a repeated goal"   @('start', 'specs/006-follow-again.md') 'try 2 at the same goal'
    # Builds recorded before v0.1.12 have no try number: report works it out from the plans in the specs folder.
    F stop "test stop" | Out-Null; F finish | Out-Null
    Set-Content specs/003-old.md "# Plan: Follow up fixes" -Encoding ascii
    Set-Content specs/007-x.md "# Plan: Follow up fixes (after the stopped 006 build)" -Encoding ascii
    $loop = Join-Path (git rev-parse --git-common-dir) 'factory/metrics.jsonl'
    Add-Content $loop ('{"feature": "007-x", "date": "' + (Get-Date -Format 'yyyy-MM-dd') + '", "tasks": 1, "spec_to_pr_hours": 1}') -Encoding ascii
    ExpectOutput "report counts older repeat tries" @('report', '--days', '30') 'repeat tries\s+1 of'
}

Pop-Location
if ($bad -eq 0) { Write-Host "`nAll checks passed. Test repo left at $dir" -ForegroundColor Green }
else { Write-Host "`n$bad check(s) failed. Test repo left at $dir; send me the output." -ForegroundColor Red }

# Non-zero exit on failure, so CI (and scripts) can tell.
if ($bad -ne 0) { exit 1 }
