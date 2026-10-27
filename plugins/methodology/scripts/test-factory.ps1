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
Set-Content specs/001-cart.md "# Plan: cart`nStatus: Approved 2026-10-23`n## Tasks`n### Task 1: add button`n### Task 2: show total" -Encoding ascii
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
Expect "start creates the branch"            (F start specs/001-cart.md) 0
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
    Expect "open-pr refuses an unfilled body"    (F open-pr --dry-run) 1
    (Get-Content .factory/pr-body.md -Raw).Replace('<filled in by /build>', 'Review clean.') | Set-Content .factory/pr-body.md -Encoding ascii
    git remote add origin https://gitlab.example.test/group/project.git
    ExpectOutput "GitLab origin opens an MR into main" @('open-pr', '--dry-run') 'glab mr create --source-branch feat/001-cart --target-branch main'
    Set-Content .factory/config.json '{"check": "./check.ps1", "base": "poc/main"}' -Encoding ascii
    ExpectOutput "base branch is configurable"   @('open-pr', '--dry-run') '--target-branch poc/main'
    git remote set-url origin https://github.com/someone/project.git
    ExpectOutput "GitHub origin opens a PR"      @('open-pr', '--dry-run') 'gh pr create --head feat/001-cart --base poc/main'
}

Pop-Location
if ($bad -eq 0) { Write-Host "`nAll checks passed. Test repo left at $dir" -ForegroundColor Green }
else { Write-Host "`n$bad check(s) failed. Test repo left at $dir; send me the output." -ForegroundColor Red }
