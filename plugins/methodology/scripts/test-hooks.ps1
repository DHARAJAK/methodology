# Tests the Claude Code hooks (hooks.cs) and the git pre-commit guard in a throwaway repo. Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File <plugin>/scripts/test-hooks.ps1
# Needs the .NET 10 SDK and git (with its sh, as Git for Windows provides). Deletes nothing outside its temp folder.

$hooks = Join-Path $PSScriptRoot 'hooks.cs'
$preCommit = Join-Path (Split-Path $PSScriptRoot) 'templates/pre-commit'
$dir = Join-Path ([IO.Path]::GetTempPath()) ("hooks-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $dir | Out-Null
Push-Location $dir
$env:CLAUDE_PROJECT_DIR = $dir
$bad = 0

git init -q -b main
git config user.email test@example.com
git config user.name test
New-Item -ItemType Directory .factory, specs, .githooks | Out-Null
Copy-Item $preCommit .githooks/pre-commit
git config core.hooksPath .githooks
Set-Content .gitignore ".factory/state.json" -Encoding ascii
Set-Content specs/001-cart.md "# Plan: cart" -Encoding ascii
git add -A; git commit -qm init

function SetBuilding([bool]$building, [string]$task2 = 'pending') {
    $status = if ($building) { 'building' } else { 'finished' }
    Set-Content .factory/state.json "{`n  `"status`": `"$status`",`n  `"tasks`": [`n    { `"id`": `"1`", `"status`": `"committed`" },`n    { `"id`": `"2`", `"status`": `"$task2`" }`n  ]`n}" -Encoding ascii
}
function Hook($name, $json) { $json | & dotnet run $hooks -- $name 2>$null | Out-Null; return $LASTEXITCODE }
function Shell($command) { return "{`"tool_input`": {`"command`": `"$command`"}}" }
function Edit($path) { return "{`"tool_input`": {`"file_path`": `"$($path.Replace('\', '/'))`"}}" }
function Stage { Set-Content ("f-" + [guid]::NewGuid().ToString('N') + '.txt') 'x'; git add -A }
function Expect($name, $actual, $expected) {
    if ($actual -eq $expected) { Write-Host "PASS  $name" -ForegroundColor Green }
    else { Write-Host "FAIL  $name (exit $actual, expected $expected)" -ForegroundColor Red; $script:bad++ }
}

# Not onboarded: every hook stays out of the way.
Expect "no config: hooks do nothing"           (Hook no-raw-commit (Shell 'git commit -m x --no-verify')) 0

Set-Content .factory/config.json '{"check": "exit 0"}' -Encoding ascii
SetBuilding $false
Expect "outside a build: git commit is allowed" (Hook no-raw-commit (Shell 'git commit -m x')) 0
Expect "--no-verify is always blocked"         (Hook no-raw-commit (Shell 'git commit -m x --no-verify')) 2
Expect "-n is always blocked"                  (Hook no-raw-commit (Shell 'git commit -n -m x')) 2
Expect "outside a build: hooksPath setup is ok" (Hook no-raw-commit (Shell 'git config core.hooksPath .githooks')) 0
Expect "outside a build: plan can be edited"   (Hook lock-specs (Edit "$dir/specs/001-cart.md")) 0
Expect "unreadable input: guard fails closed"  (Hook no-raw-commit 'not json') 2
Expect "unreadable input: stop stays open"     (Hook stop 'not json') 0

SetBuilding $true
Expect "build: git commit is blocked"          (Hook no-raw-commit (Shell 'git commit -m x')) 2
Expect "build: git -C . commit is blocked"     (Hook no-raw-commit (Shell 'git -C . commit -m x')) 2
Expect "build: git -c k=v commit is blocked"   (Hook no-raw-commit (Shell 'git -c user.name=x commit -m x')) 2
Expect "build: hooksPath change is blocked"    (Hook no-raw-commit (Shell 'git config core.hooksPath nowhere')) 2
Expect "build: git status is allowed"          (Hook no-raw-commit (Shell 'git status')) 0
Expect "build: plan is locked"                 (Hook lock-specs (Edit "$dir/specs/001-cart.md")) 2
Expect "build: .factory is locked"             (Hook lock-specs (Edit "$dir/.factory/state.json")) 2
Expect "build: source files can be edited"     (Hook lock-specs (Edit "$dir/src/App.cs")) 0
Expect "build: stop refused with open tasks"   (Hook stop '{"stop_hook_active": false}') 2
Expect "build: stop allowed when reminded once" (Hook stop '{"stop_hook_active": true}') 0
SetBuilding $true 'committed'
Expect "build: stop allowed when all committed" (Hook stop '{"stop_hook_active": false}') 0

# Git-level guard: runs however the commit is started.
SetBuilding $true
Stage; git commit -qm raw 2>$null
Expect "pre-commit: raw commit refused in build"     $LASTEXITCODE 1
if ($env:OS -eq 'Windows_NT') {
    Stage; cmd /c "git commit -qm wrapped" 2>$null
    Expect "pre-commit: cmd /c wrapper refused in build" $LASTEXITCODE 1
}
Stage; $env:FACTORY_COMMIT = '1'; git commit -qm referee 2>$null; $code = $LASTEXITCODE; Remove-Item Env:FACTORY_COMMIT
Expect "pre-commit: referee commit allowed in build" $code 0
SetBuilding $false
Stage; git commit -qm normal 2>$null
Expect "pre-commit: normal commit outside a build"   $LASTEXITCODE 0

Pop-Location
Remove-Item Env:CLAUDE_PROJECT_DIR
if ($bad -eq 0) { Write-Host "`nAll checks passed. Test repo left at $dir" -ForegroundColor Green }
else { Write-Host "`n$bad check(s) failed. Test repo left at $dir; send me the output." -ForegroundColor Red }
