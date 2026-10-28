#:property TreatWarningsAsErrors=false
// The factory script: the referee for /build (ADR-006).
//
// Claude calls it; it decides. It counts attempts, stops after too many
// failures, and refuses to commit work that has not passed the check.
//
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- start  specs/001-cart.md
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- red    1 --test "dotnet test --filter AddBook" (must FAIL: test first)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- red    1 --skip "docs only"           (task needs no test)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- check  1 --test "dotnet test --filter AddBook"
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- review 1 --pass | --fail "findings" (when config names a reviewer)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- commit 1 -m "add-to-cart button"
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- status
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- stop "reason" (Code Evaluation failed: hand back to you)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- finish        (end of feature: metrics + PR body)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- open-pr [--dry-run] (GitHub PR or GitLab MR into the base branch)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- decide --bucket spec-gap --change "..." --metric first_pass_rate [--where plugin]
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- decide --none "reason"   (the retro decision; start waits for it)
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- note "finding" | note --file f (adds to the newest retro, for the next /spec)
//
// .factory/config.json: "check" (required), "reviewer" (agent name), "host" ("gitlab" | "github", default
// from the origin URL), "base" (branch PRs/MRs target, default "main"), "specs" (plan folder, default
// "specs"), "adrs" (ADR folder for /spec and /build, default "docs/adr").
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- report [days] (summary for the 2-weekly review)
//
// Improvement loop (ADR-016): finish writes a retro to <git common dir>/factory/retros/, shared by all
// worktrees and never pushed. The next start refuses until 'decide' records what changes because of it.
//
// Exit codes: 0 = ok, 1 = failed (rework allowed), 2 = STOP (needs you).
// Needs the .NET 10 SDK and git. Stores numbers and IDs only, never code or prompts.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const int MaxAttempts = 3; // first try + 2 reworks (ADR-004)
const string Factory = ".factory";
string StatePath = Path.Combine(Factory, "state.json");
string ConfigPath = Path.Combine(Factory, "config.json");
string BaselinePath = Path.Combine(Factory, "baseline.txt");
string LastCheckPath = Path.Combine(Factory, "last-check.txt");
string MetricsPath = Path.Combine(Factory, "metrics.jsonl");
string PrBodyPath = Path.Combine(Factory, "pr-body.md");

// In a worktree .git is a file, not a folder.
if (!Directory.Exists(".git") && !File.Exists(".git")) Die("run this from the repo root.");
Directory.CreateDirectory(Factory);
// The improvement loop's files live in the shared .git folder: they survive a STOP on an unmerged branch,
// every worktree sees the same ones, and they are never pushed.
string LoopDir = Path.Combine(Path.GetFullPath(Git("rev-parse", "--git-common-dir")), "factory");
string RetroDir = Path.Combine(LoopDir, "retros");
const string TextTaskWords = @"\b(pars(e|es|ed|er|ing)|redact\w*|mask\w*|saniti[sz]\w*|escap\w*|normali[sz]\w*|validat\w*|regex\w*|deserializ\w*)\b";
const int MinInputRows = 5;
string LoopMetricsPath = Path.Combine(LoopDir, "metrics.jsonl");
string ImprovementsPath = Path.Combine(LoopDir, "improvements.jsonl");
// Metrics where higher is better; for every other number lower is better.
string[] HigherIsBetter = { "first_pass_rate", "done_when_coverage", "test_first_rate" };
string[] Buckets = { "spec-gap", "harness-gap", "rule-gap", "model-slip" };

if (args.Length == 0) Usage();
switch (args[0])
{
    case "start": if (args.Length < 2) Usage(); Start(args[1]); break;
    case "red": if (args.Length < 2) Usage(); Red(args[1], Option("--test"), Option("--skip")); break;
    case "check": if (args.Length < 2) Usage(); Check(args[1], Option("--test")); break;
    case "review": if (args.Length < 2) Usage(); Review(args[1], args.Contains("--pass"), Option("--fail")); break;
    case "commit": if (args.Length < 2) Usage(); Commit(args[1], Option("-m")); break;
    case "status": Status(); break;
    case "finish": Finish(); break;
    case "open-pr": OpenPr(args.Contains("--dry-run")); break;
    case "note": if (args.Length < 2) Usage(); Note(NoteText()); break;
    case "decide": Decide(Option("--bucket"), Option("--change"), Option("--metric"), Option("--where"), Option("--none"), Option("--feature")); break;
    case "stop": StopCommand(args.Length > 1 ? args[1] : "stopped by /build"); break;
    case "report": Report(args.Length > 1 && int.TryParse(args[1], out var d) ? d : 14); break;
    default: Usage(); break;
}
return 0;

// ---------- commands ----------

void Start(string plan)
{
    plan = plan.TrimStart('@'); // "@docs/specs/x.md" is how Claude Code mentions a file
    if (!File.Exists(plan)) Die($"plan {plan} not found.");
    var text = File.ReadAllText(plan);
    if (!Regex.IsMatch(text, @"^Status:\s*Approved", RegexOptions.Multiline))
        Die("plan is not approved. Run /spec and approve it first.");
    if (Git("ls-files", plan) == "" || Git("status", "--porcelain", "--", plan) != "")
        Die("plan has uncommitted changes. An approved plan must be committed as-is.");
    var specs = Folder(LoadJson(ConfigPath, "Run /onboard first."), "specs", "specs");
    if (!RepoPath(plan).StartsWith(specs + "/"))
        Die($"plans live in {specs}/ (\"specs\" in {ConfigPath}), where the hooks lock them during /build. Move the plan there.");

    if (File.Exists(StatePath))
    {
        var old = LoadJson(StatePath, "");
        if (Str(old, "plan") == plan && Str(old, "status") == "building")
        {
            if (!PlanUnchanged(old)) Stop(old, "the plan changed since the build started. Rerun /spec.");
            Git("switch", Str(old, "branch"));
            Console.WriteLine($"Resuming {Str(old, "feature")} on {Str(old, "branch")}.");
            Status();
            return;
        }
        if (Str(old, "status") == "building")
            Die($"another feature is still building ({Str(old, "plan")}). Finish or stop it first.");
    }

    // Improvement loop gate: the last build's retro needs a decision before the next build starts.
    var undecided = UndecidedRetros();
    if (undecided.Count > 0)
        Die($"the last build ({string.Join(", ", undecided)}) has no retro decision. Run /retro, or: factory decide --none \"<reason>\"");

    var planProblems = PlanProblems(text);
    if (planProblems.Count > 0)
        Die("the plan has gaps the build would trip over. Fix them with /spec and approve again:\n  - " + string.Join("\n  - ", planProblems));

    if (Git("status", "--porcelain", "--untracked-files=no") != "")
        Die("working tree has uncommitted changes. Commit or stash them first.");
    // Untracked files that are already lying around (drafts, notes, an uncommitted metrics.jsonl) are not part of
    // this feature: remember them so "factory commit" never sweeps them into a task commit.
    var leftOut = new JsonArray();
    foreach (var f in Git("ls-files", "--others", "--exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        leftOut.Add(JsonValue.Create(f.Trim()));

    var tasks = new JsonArray();
    foreach (Match m in Regex.Matches(text, @"^###\s*Task\s+(\d+)\s*:\s*(.+?)\s*$", RegexOptions.Multiline))
        tasks.Add(new JsonObject
        {
            ["id"] = m.Groups[1].Value,
            ["name"] = m.Groups[2].Value,
            ["status"] = "pending",
            ["attempts"] = 0,
            ["started"] = null,
            ["ended"] = null,
        });
    if (tasks.Count == 0) Die("no tasks found. Tasks must be headed '### Task 1: <name>'.");

    var feature = Path.GetFileNameWithoutExtension(plan);
    var branch = $"feat/{feature}";
    // The feature must start from the configured base branch, or the PR/MR would carry unrelated commits.
    var current = Git("branch", "--show-current");
    var configBase = Str(LoadJson(ConfigPath, "Run /onboard first."), "base");
    // A follow-up spec may build on top of a stopped build's branch: its commits then go into this MR too.
    var previous = File.Exists(StatePath) ? LoadJson(StatePath, "") : null;
    var stackedOn = previous != null && Str(previous, "status") == "stopped-recorded" && Str(previous, "branch") == current ? previous : null;
    if (configBase != "" && current != configBase && stackedOn == null)
        Die($"you are on '{current}'. Switch to the base branch '{configBase}' (and pull) first, or to the branch of the stopped build this plan follows up.");
    // main, master, develop...: whatever the feature starts from, or what the stopped build was heading for.
    var baseBranch = configBase != "" ? configBase : stackedOn != null ? BaseOf(new JsonObject(), stackedOn) : current;
    if (Git("branch", "--list", branch) != "") Die($"branch {branch} already exists. Delete it or rename the plan.");
    Git("switch", "-c", branch);

    var state = new JsonObject
    {
        ["feature"] = feature,
        ["plan"] = plan,
        ["plan_hash"] = FileHash(plan),
        ["plan_commit"] = Git("log", "-1", "--format=%H", "--", plan),
        ["branch"] = branch,
        ["base"] = baseBranch,
        ["stacked_on"] = stackedOn != null ? Str(stackedOn, "feature") : null,
        ["status"] = "building",
        ["started"] = Now(),
        ["ended"] = null,
        ["tasks"] = tasks,
        ["left_out"] = leftOut,
    };
    Save(state);
    Console.WriteLine($"Started {feature} on branch {branch} with {tasks.Count} tasks.");
    if (stackedOn != null) Console.WriteLine($"Built on top of the stopped build {Str(stackedOn, "feature")}: its commits go into the same merge request, into {baseBranch}.");
    foreach (var t in tasks) Console.WriteLine($"  Task {Str(t!, "id")}: {Str(t!, "name")}");
    if (leftOut.Count > 0)
        Console.WriteLine($"Note: {leftOut.Count} untracked file(s) were already here (for example {leftOut[0]}). They stay out of every task commit.");
}

// Test first: the task's new tests must fail before any code is written, or they do not test the new behaviour.
void Red(string taskId, string? testCmd, string? skipReason)
{
    var state = LoadJson(StatePath, "Run 'factory start <plan>' first.");
    if (Str(state, "status") != "building") Die($"feature is {Str(state, "status")}, not building.", 2);
    if (!PlanUnchanged(state)) Stop(state, "the plan changed during the build. Rerun /spec.");
    var task = FindTask(state, taskId);
    if (Str(task, "status") == "committed") Die($"task {taskId} is already committed.");

    if (skipReason != null)
    {
        task["red"] = new JsonObject { ["at"] = Now(), ["skipped"] = skipReason };
        Save(state);
        Console.WriteLine($"Task {taskId}: no test, recorded as skipped ({skipReason}). Now run: factory check {taskId}");
        return;
    }
    if (testCmd == null) Die("red needs --test \"<command for the new tests>\", or --skip \"<reason>\" for a task with no test.");

    var (code, _) = Shell(testCmd);
    if (code == 0) Die("the tests passed before the code was written, so they do not test the new behaviour. Write a test that fails first.");
    task["red"] = new JsonObject { ["at"] = Now(), ["test"] = testCmd };
    Save(state);
    Console.WriteLine($"RED: task {taskId} has a failing test. Write the code, then run: factory check {taskId} --test \"{testCmd}\"");
}

void Check(string taskId, string? testCmd)
{
    var state = LoadJson(StatePath, "Run 'factory start <plan>' first.");
    if (Str(state, "status") != "building") Die($"feature is {Str(state, "status")}, not building.", 2);
    if (!PlanUnchanged(state)) Stop(state, "the plan changed during the build. Rerun /spec.");
    var task = FindTask(state, taskId);
    if (Str(task, "status") == "committed") Die($"task {taskId} is already committed.");
    if (task["red"] == null)
        Die($"task {taskId} has no failing test on record. Test first: run 'factory red {taskId} --test \"<cmd>\"' before writing code.");
    task["review"] = null;

    var attempts = task["attempts"]!.GetValue<int>() + 1;
    task["attempts"] = attempts;
    task["started"] ??= Now();
    var config = LoadJson(ConfigPath, "Run /onboard first.");

    var results = new List<(string Name, bool Ok, string Output)>();
    var newProblemLines = new List<string>();
    if (testCmd != null)
    {
        var (tc, tout) = Shell(testCmd);
        results.Add(("done-when test", tc == 0, tout));
        task["has_test"] = true;
    }
    var (code, output) = Shell(Str(config, "check"));
    if (code == 0)
        results.Add(("check", true, output));
    else
    {
        var baseline = File.Exists(BaselinePath) ? ProblemLines(File.ReadAllText(BaselinePath)) : new HashSet<string>();
        var problems = ProblemLines(output);
        var newProblems = problems.Except(baseline).OrderBy(x => x).ToList();
        newProblemLines = newProblems;
        if (newProblems.Count > 0)
            output += "\n--- NEW problems (not in baseline) ---\n" + string.Join("\n", newProblems);
        // A check that fails without printing any recognisable problem (for example, it could not start)
        // must never count as a pass, or a broken check command would let every task through.
        var unrecognisedFailure = problems.Count == 0;
        if (unrecognisedFailure)
            output += $"\n--- The check exited with code {code} but printed no recognisable problem. Treated as a failure. ---";
        results.Add(("check vs baseline", newProblems.Count == 0 && !unrecognisedFailure, output));
    }

    File.WriteAllText(LastCheckPath, string.Join("\n\n", results.Select(r => $"===== {r.Name} =====\n{r.Output}")));
    var passed = results.All(r => r.Ok);
    task["last_check"] = new JsonObject { ["at"] = Now(), ["passed"] = passed };

    if (passed)
    {
        task["status"] = "passed";
        task["fingerprint"] = TreeFingerprint();
        Save(state);
        Console.WriteLine($"PASS: task {taskId} on attempt {attempts}. Now run: factory commit {taskId}");
        return;
    }
    task["status"] = "failed";
    var failed = string.Join(", ", results.Where(r => !r.Ok).Select(r => r.Name));
    // Evidence for the retro: what failed on which attempt (the first few new problem lines, not the whole log).
    Append(task, "check_log", new JsonObject
    {
        ["attempt"] = attempts,
        ["failed"] = failed,
        ["problems"] = new JsonArray(newProblemLines.Take(5).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
    });
    if (attempts >= MaxAttempts) Stop(state, $"task {taskId} failed {attempts} times ({failed}). See {LastCheckPath}.");
    Save(state);
    Console.WriteLine($"FAIL: task {taskId}, attempt {attempts} of {MaxAttempts} ({failed}).");
    Console.WriteLine($"Details in {LastCheckPath}. Fix the problem and run the check again.");
    Environment.Exit(1);
}

// Records the project reviewer's verdict on a task that passed the check. A failed review counts as an attempt.
void Review(string taskId, bool pass, string? findings)
{
    var state = LoadJson(StatePath, "Run 'factory start <plan>' first.");
    var task = FindTask(state, taskId);
    if (Str(task, "status") != "passed") Die($"task {taskId} has not passed the check (status: {Str(task, "status")}). Review comes after a passing check.");
    if (!pass && findings == null) Die("review needs --pass, or --fail \"<findings>\".");

    if (pass)
    {
        task["review"] = new JsonObject { ["result"] = "pass", ["at"] = Now(), ["fingerprint"] = TreeFingerprint() };
        Save(state);
        Console.WriteLine($"Review passed for task {taskId}. Now run: factory commit {taskId}");
        return;
    }

    task["review_findings"] = (task["review_findings"]?.GetValue<int>() ?? 0) + 1;
    task["status"] = "failed";
    var attempts = task["attempts"]!.GetValue<int>();
    Append(task, "review_log", new JsonObject { ["attempt"] = attempts, ["findings"] = findings });
    if (attempts >= MaxAttempts) Stop(state, $"task {taskId} failed review on attempt {attempts}: {findings}");
    Save(state);
    Console.WriteLine($"REVIEW FAILED: task {taskId}, attempt {attempts} of {MaxAttempts}. Send the findings to a new implementer:");
    Console.WriteLine(findings);
    Environment.Exit(1);
}

void Commit(string taskId, string? message)
{
    var state = LoadJson(StatePath, "Run 'factory start <plan>' first.");
    var task = FindTask(state, taskId);
    if (Str(task, "status") != "passed")
        Die($"task {taskId} has not passed the check (status: {Str(task, "status")}). Refusing to commit.");
    var reviewer = Str(LoadJson(ConfigPath, "Run /onboard first."), "reviewer");
    if (reviewer != "")
    {
        var review = task["review"];
        if (review == null || Str(review, "result") != "pass")
            Die($"task {taskId} has not passed review by '{reviewer}'. Run the reviewer, then 'factory review {taskId} --pass'.");
        if (Str(review, "fingerprint") != TreeFingerprint())
            Die("files changed after the review. Run the check and the review again.");
    }
    if (TreeFingerprint() != Str(task, "fingerprint"))
    {
        task["status"] = "failed";
        Save(state);
        Die("files changed after the check passed. Run the check again.");
    }
    if (!PlanUnchanged(state)) Stop(state, "the plan changed during the build. Rerun /spec.");

    Git("add", "-A");
    // Keep files that were untracked before the build out of the commit (see Start).
    var keepOut = (state["left_out"]?.AsArray() ?? new JsonArray()).Select(n => n!.GetValue<string>()).Where(File.Exists).ToList();
    if (keepOut.Count > 0) Git(new[] { "reset", "-q", "--" }.Concat(keepOut).ToArray());
    var msg = $"task {taskId}: {message ?? Str(task, "name")}";
    // FACTORY_COMMIT lets the "no raw commit" hook allow this commit.
    var (rc, rout) = Run("git", new[] { "commit", "-m", msg }, new() { ["FACTORY_COMMIT"] = "1" });
    if (rc != 0)
    {
        if (Regex.IsMatch(rout, "leaks? found", RegexOptions.IgnoreCase))
        {
            state["secrets_caught"] = (state["secrets_caught"]?.GetValue<int>() ?? 0) + 1;
            task["status"] = "failed";
            Save(state);
            Die($"the secrets scan blocked this commit. Remove the secret, then check again:\n{rout}");
        }
        Die($"git commit failed (pre-commit hook or nothing to commit):\n{rout}");
    }

    task["status"] = "committed";
    task["ended"] = Now();
    task["commit"] = Git("rev-parse", "HEAD");
    var tasks = state["tasks"]!.AsArray();
    if (tasks.All(t => Str(t!, "status") == "committed")) state["status"] = "tasks-done";
    Save(state);
    Console.WriteLine($"Committed task {taskId}: {msg}");
    var left = tasks.Where(t => Str(t!, "status") != "committed").Select(t => Str(t!, "id")).ToList();
    Console.WriteLine(left.Count > 0 ? $"Tasks left: {string.Join(", ", left)}" : "All tasks done. Next: Code Evaluation.");
}

void StopCommand(string reason)
{
    var state = LoadJson(StatePath, "No build in progress.");
    Stop(state, reason);
}

void Finish()
{
    var state = LoadJson(StatePath, "No build to finish.");
    var status = Str(state, "status");
    if (status != "tasks-done" && status != "stopped") Die($"feature is {status}; finish runs after all tasks are committed, or after a STOP.");
    var tasks = state["tasks"]!.AsArray().Select(t => t!).ToList();
    var plan = Str(state, "plan");
    var planText = File.Exists(plan) ? File.ReadAllText(plan) : "";
    var config = LoadJson(ConfigPath, "Run /onboard first.");

    // Final full check: new problems and complexity warnings compared to the baseline.
    var (code, output) = Shell(Str(config, "check"));
    var baseline = File.Exists(BaselinePath) ? ProblemLines(File.ReadAllText(BaselinePath)) : new HashSet<string>();
    var newProblems = code == 0 ? 0 : ProblemLines(output).Except(baseline).Count();
    var complexity = Regex.Matches(output, @"CA1502|CA1505|S3776|S1541|cyclomatic|cognitive complexity", RegexOptions.IgnoreCase).Count;

    // Dependencies added on this branch that the plan does not name.
    var depDiff = Git("diff", $"{BaseOf(config, state)}...HEAD", "--", "*.csproj", "package.json", "Directory.Packages.props");
    var added = Regex.Matches(depDiff, @"^\+.*(?:PackageReference|PackageVersion)\s+(?:Include|Update)=""([^""]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
        .Concat(Regex.Matches(depDiff, @"^\+\s*""([@\w./-]+)""\s*:\s*""[\^~]?\d", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
        .Distinct().ToList();
    // Packages the repo already used on the base branch (for example xUnit in another test project) are not new.
    var baseRef = BaseOf(config, state);
    var unapproved = added
        .Where(dep => !planText.Contains(dep, StringComparison.OrdinalIgnoreCase))
        .Where(dep => Run("git", new[] { "grep", "-q", "-F", dep, baseRef, "--", "*.csproj", "package.json", "Directory.Packages.props" }).Item1 != 0)
        .ToList();

    // Docs check: code changed on the branch, but no Markdown outside the plan folder did.
    var specsFolder = Folder(config, "specs", "specs");
    var changed = Git("diff", "--name-only", $"{baseRef}...HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(f => !f.StartsWith(".factory/") && !f.StartsWith(specsFolder + "/")).ToList();
    var docsWarning = changed.Any(f => !f.EndsWith(".md")) && !changed.Any(f => f.EndsWith(".md"))
        ? "Code changed but no docs did: is CLAUDE.md (and any README or roadmap) still right?" : null;

    var planCommits = Git("log", "--format=%cI", "--", plan).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var specStart = planCommits.Length > 0 ? DateTimeOffset.Parse(planCommits[^1]) : DateTimeOffset.Parse(Str(state, "started"));
    var (tokensTotal, tokensOutput) = Tokens(DateTimeOffset.Parse(Str(state, "started")));
    var committed = tasks.Where(t => Str(t, "status") == "committed").ToList();

    var metrics = new JsonObject
    {
        ["feature"] = Str(state, "feature"),
        ["date"] = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
        ["tasks"] = tasks.Count,
        ["first_pass_rate"] = Ratio(committed.Count(t => t["attempts"]!.GetValue<int>() == 1), tasks.Count),
        ["attempts_per_task"] = Ratio(tasks.Sum(t => t["attempts"]!.GetValue<int>()), tasks.Count),
        ["needs_you"] = status == "stopped" ? 1 : 0,
        ["spec_bounces"] = Math.Max(0, planCommits.Length - 1),
        ["spec_to_pr_hours"] = Math.Round((DateTimeOffset.Now - specStart).TotalHours, 1),
        ["new_problems"] = newProblems,
        ["complexity_warnings"] = complexity,
        ["done_when_coverage"] = Ratio(committed.Count(t => t["has_test"]?.GetValue<bool>() == true), tasks.Count),
        ["test_first_rate"] = Ratio(committed.Count(t => t["red"]?["test"] != null), tasks.Count),
        ["review_findings"] = tasks.Sum(t => t["review_findings"]?.GetValue<int>() ?? 0),
        ["secrets_caught"] = state["secrets_caught"]?.GetValue<int>() ?? 0,
        ["unapproved_deps"] = unapproved.Count,
        ["tokens_total"] = tokensTotal,
        ["tokens_output"] = tokensOutput,
    };
    File.AppendAllText(MetricsPath, metrics.ToJsonString() + "\n");
    Directory.CreateDirectory(LoopDir);
    File.AppendAllText(LoopMetricsPath, metrics.ToJsonString() + "\n");
    var retroPath = WriteRetro(state, metrics, status == "stopped", unapproved, docsWarning);

    // PR body: goal, scenarios with ticks, the numbers.
    var goal = Regex.Match(planText, @"^## Goal\s*\n(.*?)(?=^## )", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value.Trim();
    var body = new StringBuilder();
    body.AppendLine($"## {Str(state, "feature")}").AppendLine().AppendLine(goal).AppendLine();
    body.AppendLine($"Plan: `{plan}`").AppendLine().AppendLine("### Scenarios");
    foreach (var t in tasks)
        body.AppendLine($"- [{(Str(t, "status") == "committed" ? "x" : " ")}] Task {Str(t, "id")}: {Str(t, "name")} (attempts: {t["attempts"]})");
    body.AppendLine().AppendLine("### Numbers").AppendLine().AppendLine("| Metric | Value |").AppendLine("|---|---|");
    foreach (var (k, v) in metrics) if (k != "feature" && k != "date") body.AppendLine($"| {k} | {v?.ToJsonString() ?? "n/a"} |");
    if (unapproved.Count > 0) body.AppendLine().AppendLine($"**Dependencies not in the plan:** {string.Join(", ", unapproved)}");
    if (docsWarning != null) body.AppendLine().AppendLine($"**Docs:** {docsWarning}");
    body.AppendLine().AppendLine("### Code Evaluation").AppendLine().AppendLine("<filled in by /build>");
    File.WriteAllText(PrBodyPath, body.ToString());

    state["ended"] = Now();
    state["status"] = status == "stopped" ? "stopped-recorded" : "finished";
    Save(state);
    // Commit the metrics line, or the next "factory start" finds an uncommitted change after every build.
    if (Run("git", new[] { "check-ignore", "-q", MetricsPath }).Item1 != 0)
    {
        Git("add", "--", MetricsPath);
        var (mrc, mout) = Run("git", new[] { "commit", "-q", "-m", $"metrics: {Str(state, "feature")}", "--", MetricsPath }, new() { ["FACTORY_COMMIT"] = "1" });
        if (mrc != 0) Console.WriteLine($"Could not commit {MetricsPath}: {mout.Trim()}");
    }
    Console.WriteLine(metrics.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Metrics appended to {MetricsPath} and committed. PR body written to {PrBodyPath}.");
    Console.WriteLine($"Retro written to {retroPath}. Run /retro when you review the merge request; the next build waits for its decision.");
}

// Opens a GitHub pull request or GitLab merge request from the feature branch into the base branch.
// The CLI gets its arguments as a list, so the multi-line body needs no shell quoting.
void OpenPr(bool dryRun)
{
    var state = LoadJson(StatePath, "No build to open.");
    if (Str(state, "status") != "finished") Die($"feature is {Str(state, "status")}; open-pr runs after 'factory finish' on a completed build.");
    if (!File.Exists(PrBodyPath)) Die($"{PrBodyPath} not found. Run 'factory finish' first.");
    var body = File.ReadAllText(PrBodyPath);
    if (body.Contains("<filled in by /build>")) Die($"fill in the Code Evaluation section of {PrBodyPath} first.");

    var config = LoadJson(ConfigPath, "Run /onboard first.");
    var host = HostOf(config);
    var baseBranch = BaseOf(config, state);
    var branch = Str(state, "branch");
    var planPath = Str(state, "plan");
    var planTitle = File.Exists(planPath) ? Regex.Match(File.ReadAllText(planPath), @"^#\s*Plan:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value : "";
    var title = planTitle != "" ? planTitle : Str(state, "feature");

    var (cli, cliArgs) = host == "gitlab"
        ? ("glab", new[] { "mr", "create", "--source-branch", branch, "--target-branch", baseBranch, "--title", title, "--description", body, "--yes" })
        : ("gh", new[] { "pr", "create", "--head", branch, "--base", baseBranch, "--title", title, "--body-file", PrBodyPath });
    Console.WriteLine($"{host}: {cli} {string.Join(' ', cliArgs.Select(a => a == body ? "<pr-body.md>" : a.Contains(' ') ? $"\"{a}\"" : a))}");
    if (dryRun) return;

    try
    {
        var (code, output) = Run(cli, cliArgs);
        Console.WriteLine(output.Trim());
        if (code != 0) Die($"{cli} failed. Is it signed in (`{cli} auth login`) and is the branch pushed?");
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Die($"{cli} is not installed. Install it, sign in with `{cli} auth login`, then run open-pr again.");
    }
}

void Report(int days)
{
    // The shared copy also holds stopped builds whose branch was never merged.
    var metricsFile = File.Exists(LoopMetricsPath) ? LoopMetricsPath : MetricsPath;
    if (!File.Exists(metricsFile)) Die("no metrics yet. They appear after the first 'factory finish'.");
    var since = DateTimeOffset.Now.AddDays(-days).ToString("yyyy-MM-dd");
    var rows = File.ReadAllLines(metricsFile).Where(l => l.Trim() != "").Select(l => JsonNode.Parse(l)!)
        .Where(r => string.CompareOrdinal(Str(r, "date"), since) >= 0).ToList();
    if (rows.Count == 0) Die($"no features finished in the last {days} days.");
    double Avg(string k) => rows.Where(r => r[k] != null).Select(r => r[k]!.GetValue<double>()).DefaultIfEmpty(0).Average();
    double Sum(string k) => rows.Where(r => r[k] != null).Select(r => r[k]!.GetValue<double>()).Sum();
    var medianHours = rows.Select(r => r["spec_to_pr_hours"]!.GetValue<double>()).OrderBy(x => x).ElementAt(rows.Count / 2);
    var config = LoadJson(ConfigPath, "Run /onboard first.");
    var escaped = EscapedBugs(config, since);
    var reverts = Git("log", $"--since={since}", "--grep=^Revert", "--oneline").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    Console.WriteLine($"Last {days} days · {rows.Count} features · {Sum("tasks")} tasks");
    Console.WriteLine($"first-pass rate      {Avg("first_pass_rate"):P0}");
    Console.WriteLine($"attempts per task    {Avg("attempts_per_task"):0.0}");
    Console.WriteLine($"needs-you            {Sum("needs_you")} of {rows.Count}");
    Console.WriteLine($"spec bounces         {Sum("spec_bounces")}");
    Console.WriteLine($"spec-to-PR (median)  {medianHours:0.0}h");
    Console.WriteLine($"new problems         {Sum("new_problems")}");
    Console.WriteLine($"complexity warnings  {Sum("complexity_warnings")}");
    Console.WriteLine($"done-when coverage   {Avg("done_when_coverage"):P0}");
    Console.WriteLine($"test-first rate      {Avg("test_first_rate"):P0}");
    Console.WriteLine($"review findings      {Sum("review_findings")}");
    Console.WriteLine($"escaped bugs         {escaped?.ToString() ?? $"n/a (needs {(HostOf(config) == "gitlab" ? "glab" : "gh")}, signed in)"}");
    Console.WriteLine($"reverts              {reverts}");
    Console.WriteLine($"secrets caught       {Sum("secrets_caught")}");
    Console.WriteLine($"unapproved deps      {Sum("unapproved_deps")}");
    Console.WriteLine($"tokens per feature   {Avg("tokens_total"):N0} (output {Avg("tokens_output"):N0})");
    ImprovementsReport(metricsFile);
}

// For each retro decision: its number on the build that caused it, and the average over the builds since.
void ImprovementsReport(string metricsFile)
{
    if (!File.Exists(ImprovementsPath)) return;
    var all = File.ReadAllLines(metricsFile).Where(l => l.Trim() != "").Select(l => JsonNode.Parse(l)!).ToList();
    var changes = File.ReadAllLines(ImprovementsPath).Where(l => l.Trim() != "").Select(l => JsonNode.Parse(l)!)
        .Where(c => Str(c, "bucket") != "none").ToList();
    if (changes.Count == 0) return;
    Console.WriteLine();
    Console.WriteLine("Changes from retros (before = the build that caused it, after = average of builds since):");
    foreach (var c in changes)
    {
        var metric = Str(c, "metric");
        var causedAt = all.FindLastIndex(r => Str(r, "feature") == Str(c, "feature"));
        var since = all.Skip(causedAt + 1).Where(r => r[metric] != null).Select(r => r[metric]!.GetValue<double>()).ToList();
        var before = c["before"]?.GetValue<double>();
        var after = since.Count > 0 ? since.Average() : (double?)null;
        var verdict = before == null || after == null ? "too early"
            : after == before ? "no change"
            : (after > before) == HigherIsBetter.Contains(metric) ? "better" : "WORSE: revert or replace it";
        Console.WriteLine($"  {Str(c, "change")}");
        Console.WriteLine($"    {metric}: {before?.ToString("0.##") ?? "n/a"} -> {after?.ToString("0.##") ?? "n/a"} over {since.Count} build(s): {verdict}");
    }
}

// Adds a finding to the newest retro, the one permanent place for what the next /spec must know.
// The finding as typed, from a file (--file path) or standard input (-): shells cut quoted text in arguments.
string NoteText() =>
    args[1] == "--file" && args.Length > 2 ? File.ReadAllText(args[2]).Trim()
    : args[1] == "-" ? Console.In.ReadToEnd().Trim()
    : string.Join(' ', args.Skip(1));

void Note(string text)
{
    // A note belongs to the current build, whatever order /build runs note and finish in: before finish it waits
    // in state.json and finish writes it into the retro; after finish it goes straight into that build's retro.
    var state = File.Exists(StatePath) ? LoadJson(StatePath, "") : null;
    var recorded = state != null && Str(state, "status") is "finished" or "stopped-recorded";
    if (state != null && !recorded)
    {
        var notes = state["notes"]?.AsArray() ?? new JsonArray();
        notes.Add(JsonValue.Create(text));
        state["notes"] = notes;
        Save(state);
        Console.WriteLine($"Noted for the {Str(state, "feature")} retro (written by 'factory finish').");
        return;
    }
    var own = state != null ? Path.Combine(RetroDir, Str(state, "feature") + ".md") : null;
    var latest = own != null && File.Exists(own) ? own
        : Directory.Exists(RetroDir) ? Directory.GetFiles(RetroDir, "*.md").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault() : null;
    if (latest == null) Die("no retro yet. Notes go into the retro that 'factory finish' writes.");
    var md = File.ReadAllText(latest);
    const string heading = "## Notes for the next spec";
    if (!md.Contains(heading)) md = md.Replace("## Buckets", heading + "\n\n## Buckets");
    var at = md.IndexOf("## Buckets");
    md = md[..at].TrimEnd() + "\n- " + text.Replace("\r", "").Replace("\n", "\n  ") + "\n\n" + md[at..];
    File.WriteAllText(latest, md);
    Console.WriteLine($"Noted in {latest}");
}

// Records the retro decision: one change (bucket, what, which number should move) or none with a reason.
void Decide(string? bucket, string? change, string? metric, string? place, string? none, string? feature)
{
    var targets = UndecidedRetros();
    if (feature != null) targets = targets.Where(f => f == feature).ToList();
    if (targets.Count == 0) Die(feature != null ? $"no undecided retro for {feature}." : "no retro is waiting for a decision.");
    var target = targets[^1];
    var path = Path.Combine(RetroDir, target + ".md");

    string line;
    var entry = new JsonObject { ["date"] = DateTimeOffset.Now.ToString("yyyy-MM-dd"), ["feature"] = target };
    if (none != null)
    {
        line = $"none: {none}";
        entry["bucket"] = "none";
        entry["change"] = none;
    }
    else
    {
        if (bucket == null || !Buckets.Contains(bucket)) Die($"decide needs --bucket {string.Join(" | ", Buckets)} (or --none \"<reason>\").");
        if (string.IsNullOrWhiteSpace(change)) Die("decide needs --change \"<the one change>\".");
        var row = File.Exists(LoopMetricsPath)
            ? File.ReadAllLines(LoopMetricsPath).Where(l => l.Trim() != "").Select(l => JsonNode.Parse(l)!).LastOrDefault(r => Str(r, "feature") == target)
            : null;
        var numeric = row?.AsObject().Where(kv => kv.Value is JsonValue v && v.TryGetValue<double>(out _)).Select(kv => kv.Key).ToList() ?? new List<string>();
        if (metric == null || !numeric.Contains(metric))
            Die($"decide needs --metric, the number this change should move. One of: {string.Join(", ", numeric)}");
        line = $"{bucket}: {change} (moves {metric}, lives in {place ?? "plugin"})";
        entry["bucket"] = bucket;
        entry["change"] = change;
        entry["metric"] = metric;
        entry["where"] = place ?? "plugin";
        entry["before"] = row![metric]!.GetValue<double>();
    }
    var text = File.ReadAllText(path);
    File.WriteAllText(path, Regex.Replace(text, @"^## Decision:.*$", $"## Decision: {line}", RegexOptions.Multiline));
    File.AppendAllText(ImprovementsPath, entry.ToJsonString() + "\n");
    Console.WriteLine($"Decision recorded for {target}: {line}");
    Console.WriteLine("The next build can start. 'factory report' will show whether the number moved.");
}

void Status()
{
    var state = LoadJson(StatePath, "No build in progress.");
    Console.WriteLine($"{Str(state, "feature")} on {Str(state, "branch")}: {Str(state, "status")}");
    foreach (var t in state["tasks"]!.AsArray())
        Console.WriteLine($"  Task {Str(t!, "id")}: {Str(t!, "status"),-10} attempts {t!["attempts"]}  {Str(t!, "name")}");
    if (state["stop_reason"] != null) Console.WriteLine($"Stopped because: {Str(state, "stop_reason")}");
}

// ---------- helpers ----------

double Ratio(int part, int whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 2);

string HostOf(JsonNode config)
{
    var host = Str(config, "host");
    if (host != "") return host;
    var (code, url) = Run("git", new[] { "remote", "get-url", "origin" });
    return code == 0 && url.Contains("gitlab", StringComparison.OrdinalIgnoreCase) ? "gitlab" : "github";
}

// The branch PRs/MRs target: "base" in config, else the branch the feature started from, else main.
string BaseOf(JsonNode config, JsonNode state) =>
    Str(config, "base") is { Length: > 0 } fromConfig ? fromConfig
    : Str(state, "base") is { Length: > 0 } fromState ? fromState
    : "main";

// A repo folder named in config, such as "docs/specs/", as "docs/specs".
string Folder(JsonNode config, string key, string fallback) =>
    Str(config, key).Replace('\\', '/').Trim('/') is { Length: > 0 } folder ? folder : fallback;

// A path relative to the repo root with forward slashes, however it was typed.
string RepoPath(string path) => Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path)).Replace('\\', '/');

// "group/sub/project" from an https or ssh origin URL, for GitLab API calls.
string? ProjectPath()
{
    var (code, output) = Run("git", new[] { "remote", "get-url", "origin" });
    if (code != 0) return null;
    var url = output.Trim();
    var path = url.StartsWith("git@") ? url[(url.IndexOf(':') + 1)..] : new Uri(url).AbsolutePath.TrimStart('/');
    return path.EndsWith(".git") ? path[..^4] : path;
}

// Issues labelled "escaped" (bugs found after merge) created since the date, or null if the host CLI is unavailable.
int? EscapedBugs(JsonNode config, string since)
{
    try
    {
        if (HostOf(config) == "gitlab")
        {
            var project = ProjectPath();
            if (project == null) return null;
            var (code, output) = Run("glab", new[] { "api", $"projects/{Uri.EscapeDataString(project)}/issues?labels=escaped&scope=all&created_after={since}T00:00:00Z&per_page=100" });
            return code == 0 ? JsonNode.Parse(output)?.AsArray().Count : null;
        }
        var (ghCode, ghOutput) = Run("gh", new[] { "issue", "list", "--label", "escaped", "--state", "all", "--search", $"created:>={since}", "--json", "number", "--jq", "length" });
        return ghCode == 0 && int.TryParse(ghOutput.Trim(), out var count) ? count : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

// Sums token usage from Claude Code's local session logs for this repo since the build started.
// Logs live in ~/.claude/projects/<repo path with non-alphanumerics as '-'>/**/*.jsonl.
// Returns (null, null) if they cannot be found; the format is Claude Code's, so check it after updates.
(long?, long?) Tokens(DateTimeOffset since)
{
    var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    var dir = Path.Combine(home, "projects", Regex.Replace(Directory.GetCurrentDirectory(), "[^A-Za-z0-9]", "-"));
    if (!Directory.Exists(dir)) return (null, null);
    long total = 0, output = 0;
    foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
    {
        if (File.GetLastWriteTime(file) < since.LocalDateTime) continue;
        foreach (var line in File.ReadLines(file))
        {
            if (!line.Contains("\"usage\"")) continue;
            JsonNode? n;
            try { n = JsonNode.Parse(line); } catch { continue; }
            var ts = n?["timestamp"]?.GetValue<string>();
            if (ts == null || DateTimeOffset.Parse(ts) < since) continue;
            var u = n?["message"]?["usage"];
            if (u == null) continue;
            long Get(string k) => u[k]?.GetValue<long>() ?? 0;
            output += Get("output_tokens");
            total += Get("input_tokens") + Get("output_tokens") + Get("cache_creation_input_tokens") + Get("cache_read_input_tokens");
        }
    }
    return (total, output);
}

// Adds an entry to a list field of a JSON object, creating the list if needed.
void Append(JsonNode node, string key, JsonNode entry)
{
    if (node[key] is not JsonArray list) node[key] = list = new JsonArray();
    list.Add(entry);
}

// Retros whose "## Decision:" line is still empty, oldest first.
List<string> UndecidedRetros() =>
    !Directory.Exists(RetroDir) ? new List<string>()
    : Directory.GetFiles(RetroDir, "*.md").OrderBy(File.GetLastWriteTimeUtc)
        .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^## Decision:\s*$", RegexOptions.Multiline))
        .Select(f => Path.GetFileNameWithoutExtension(f)).ToList();

// Writes the retro: the facts of this build, for the /retro conversation and the start gate.
string WriteRetro(JsonNode state, JsonObject metrics, bool stopped, List<string> unapproved, string? docsWarning)
{
    Directory.CreateDirectory(RetroDir);
    var feature = Str(state, "feature");
    var tasks = state["tasks"]!.AsArray().Select(t => t!).ToList();
    var md = new StringBuilder();
    md.AppendLine($"# Retro: {feature}").AppendLine();
    md.AppendLine($"Outcome: {(stopped ? $"STOPPED: {Str(state, "stop_reason")}" : "finished, merge request ready")}");
    md.AppendLine($"Date: {DateTimeOffset.Now:yyyy-MM-dd} · Plan: `{Str(state, "plan")}` · Branch: `{Str(state, "branch")}`");
    md.AppendLine($"Numbers: first try {metrics["first_pass_rate"]} · attempts per task {metrics["attempts_per_task"]} · review findings {metrics["review_findings"]} · new problems {metrics["new_problems"]}");
    md.AppendLine().AppendLine("## Tasks").AppendLine().AppendLine("| Task | Status | Attempts | Test first |").AppendLine("|---|---|---|---|");
    foreach (var t in tasks)
    {
        var red = t["red"] == null ? "no" : t["red"]!["skipped"] != null ? $"skipped ({Str(t["red"]!, "skipped")})" : "yes";
        md.AppendLine($"| {Str(t, "id")}: {Str(t, "name")} | {Str(t, "status")} | {t["attempts"]} | {red} |");
    }
    md.AppendLine().AppendLine("## Evidence").AppendLine();
    var evidence = 0;
    foreach (var t in tasks)
    {
        foreach (var r in t["review_log"]?.AsArray() ?? new JsonArray())
        {
            md.AppendLine($"- Task {Str(t, "id")} review failed (attempt {r!["attempt"]}): {Str(r, "findings")}");
            evidence++;
        }
        foreach (var c in t["check_log"]?.AsArray() ?? new JsonArray())
        {
            var problems = c!["problems"]?.AsArray().Select(p => p!.GetValue<string>()).ToList() ?? new List<string>();
            md.AppendLine($"- Task {Str(t, "id")} check failed (attempt {c["attempt"]}, {Str(c, "failed")}){(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            evidence++;
        }
    }
    if (unapproved.Count > 0) { md.AppendLine($"- Dependencies not in the plan: {string.Join(", ", unapproved)}"); evidence++; }
    if (docsWarning != null) { md.AppendLine($"- {docsWarning}"); evidence++; }
    if (evidence == 0) md.AppendLine("- Nothing failed.");
    var notes = state["notes"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
    if (notes.Count > 0)
    {
        md.AppendLine().AppendLine("## Notes for the next spec").AppendLine();
        foreach (var n in notes) md.AppendLine("- " + n.Replace("\r", "").Replace("\n", "\n  "));
    }
    md.AppendLine().AppendLine("## Buckets").AppendLine();
    md.AppendLine("<one line per problem: spec-gap | harness-gap | rule-gap | model-slip, and why (filled in with /retro)>");
    md.AppendLine().AppendLine("## Decision:");
    var path = Path.Combine(RetroDir, feature + ".md");
    File.WriteAllText(path, md.ToString());
    return path;
}

// Checks on the approved plan that catch the gaps which cost attempts later (ADR-016). Plain text rules, no AI.
List<string> PlanProblems(string text)
{
    var problems = new List<string>();
    var lines = text.Replace("\r\n", "\n").Split('\n');

    // 1. Every task names its tricky inputs as scenarios, or says why it has none.
    var taskHeads = Regex.Matches(text, @"^###\s*Task\s+(\d+)\s*:.*$", RegexOptions.Multiline).ToList();
    for (var i = 0; i < taskHeads.Count; i++)
    {
        var start = taskHeads[i].Index;
        var nextTask = i + 1 < taskHeads.Count ? taskHeads[i + 1].Index : text.Length;
        var bodyStart = start + taskHeads[i].Length;
        var nextSection = Regex.Match(text[bodyStart..], @"^## ", RegexOptions.Multiline);
        var end = nextSection.Success ? Math.Min(nextTask, bodyStart + nextSection.Index) : nextTask;
        var section = text[start..end];
        if (!Regex.IsMatch(section, @"Scenario:\s*Tricky\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(section, @"Tricky inputs:\s*none\s*\(.+\)", RegexOptions.IgnoreCase))
            problems.Add($"Task {taskHeads[i].Groups[1].Value} has no \"Scenario: Tricky ...\" and no \"Tricky inputs: none (reason)\" line.");
        // A task that parses, masks or checks text gets an exact input -> output table: a few loose scenarios let
        // reviewers find the next leak one attempt at a time (retro 002, log masking).
        if (Regex.IsMatch(section, TextTaskWords, RegexOptions.IgnoreCase)
            && InputTableRows(section) < MinInputRows
            && !Regex.IsMatch(section, @"Input table:\s*none\s*\(.+\)", RegexOptions.IgnoreCase))
            problems.Add($"Task {taskHeads[i].Groups[1].Value} parses or transforms text but has no input -> output table with at least {MinInputRows} rows (| Input | Output |), and no \"Input table: none (reason)\" line.");
    }

    // 1b. The end-to-end check belongs to a task, so it gets a test instead of being left to /verify.
    var e2e = Regex.Match(text, @"^##\s*End-to-end check\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
    if (e2e.Success && !Regex.IsMatch(e2e.Groups[1].Value.Trim(), @"^none\b", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(text, @"Scenario:\s*End-to-end\b", RegexOptions.IgnoreCase))
        problems.Add("No task owns the end-to-end check. Add it to a task as \"Scenario: End-to-end: ...\" so it gets a test (or write \"none (reason)\" under the section).");

    // 2. The plan says how it differs from what exists today (or that it does not).
    if (!Regex.IsMatch(text, @"^##\s*Differences from existing behaviou?r", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        problems.Add("No \"## Differences from existing behaviour\" section (write \"none\" if there are none).");

    // 3. A glossary with at least one term, so the plan's words mean one thing.
    var glossary = Regex.Match(text, @"^##\s*Glossary\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
    if (!glossary.Success || !Regex.IsMatch(glossary.Groups[1].Value, @"^\|(?!\s*-)(?!\s*Term\s*\|).+\|.+\|", RegexOptions.Multiline))
        problems.Add("No \"## Glossary\" table with at least one term.");

    // 4. Every file (name.ext) or folder (ending in /) the plan mentions exists, is listed in a task's Files line (the build makes it), or is marked (new).
    var taskFiles = lines.Where(l => Regex.IsMatch(l, @"\*\*Files:\*\*|^\s*-?\s*Files:", RegexOptions.IgnoreCase))
        .SelectMany(l => Regex.Matches(l, "`([^`]+)`").Select(m => m.Groups[1].Value)).ToHashSet();
    var pathLike = new Regex(@"`([^`\s<>*:]+/|[^`\s<>*:]*\.(?:cs|csproj|sln|slnx|props|targets|md|json|ps1|cmd|sh|xml|xaml|axaml|yml|yaml|config|txt|ts|js|py|go|html|css))`", RegexOptions.IgnoreCase);
    var missing = new List<string>();
    foreach (var line in lines)
    {
        if (line.Contains("(new)", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (Match m in pathLike.Matches(line))
        {
            // A lone `name/` is as often a branch (`harness/`, `feat/`) as a folder: only paths with a folder in them
            // (`docs/drafts/`) or a file extension are checked.
            if (m.Groups[1].Value.EndsWith('/') && m.Groups[1].Value.TrimEnd('/').IndexOf('/') < 0) continue;
            var p = m.Groups[1].Value.TrimEnd('/', '.', ',');
            if (taskFiles.Contains(m.Groups[1].Value) || File.Exists(p) || Directory.Exists(p) || missing.Contains(p)) continue;
            missing.Add(p);
        }
    }
    foreach (var p in missing)
        problems.Add($"The plan mentions {p}, which does not exist. Create it, add it to a task's Files line, or mark the line (new).");
    return problems;
}

// Rows of a markdown table whose header has an Input and an Output column (the separator row is not counted).
int InputTableRows(string section)
{
    var rows = 0;
    var inTable = false;
    foreach (var raw in section.Replace("\r\n", "\n").Split('\n'))
    {
        var line = raw.Trim();
        if (!line.StartsWith('|')) { inTable = false; continue; }
        if (Regex.IsMatch(line, @"^\|\s*Input\b.*\|\s*(Expected\s+)?Output\b", RegexOptions.IgnoreCase)) { inTable = true; continue; }
        if (inTable && !Regex.IsMatch(line, @"^\|[\s:|-]+\|$")) rows++;
    }
    return rows;
}

string Now() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

string Str(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Usage()
{
    Console.Error.WriteLine("usage: factory start <plan> | red <task> --test \"cmd\" | red <task> --skip \"reason\" | check <task> [--test \"cmd\"] | review <task> --pass | review <task> --fail \"findings\" | commit <task> [-m \"msg\"] | status | stop \"reason\" | finish | open-pr [--dry-run] | decide --bucket <b> --change \"what\" --metric <m> [--where w] | decide --none \"reason\" | note \"finding for the next spec\" | note --file <path> | note - (stdin) | report [days]");
    Environment.Exit(1);
}

void Die(string msg, int code = 1)
{
    Console.Error.WriteLine($"factory: {msg}");
    Environment.Exit(code);
}

void Stop(JsonNode state, string msg)
{
    state["status"] = "stopped";
    state["stop_reason"] = msg;
    Save(state);
    Console.WriteLine($"STOP: {msg}");
    Console.WriteLine("Hand this back to the user. Work is kept on the branch.");
    Environment.Exit(2);
}

JsonNode LoadJson(string path, string hint)
{
    if (!File.Exists(path)) Die($"{path} not found. {hint}");
    return JsonNode.Parse(File.ReadAllText(path))!;
}

void Save(JsonNode state) =>
    File.WriteAllText(StatePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

string FileHash(string path) => Hash(File.ReadAllBytes(path));

bool PlanUnchanged(JsonNode state) =>
    File.Exists(Str(state, "plan")) && FileHash(Str(state, "plan")) == Str(state, "plan_hash");

JsonNode FindTask(JsonNode state, string id)
{
    var tasks = state["tasks"]!.AsArray();
    var task = tasks.FirstOrDefault(t => Str(t!, "id") == id);
    if (task == null) Die($"no task {id} in the plan. Tasks: {string.Join(", ", tasks.Select(t => Str(t!, "id")))}");
    return task!;
}

// Hash of every uncommitted change, so commit can tell if files moved after check.
string TreeFingerprint()
{
    using var ms = new MemoryStream();
    void Add(byte[] b) => ms.Write(b, 0, b.Length);
    Add(Encoding.UTF8.GetBytes(Git("diff", "HEAD", "--binary")));
    var untracked = Git("ls-files", "--others", "--exclude-standard")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(f => f, StringComparer.Ordinal);
    foreach (var f in untracked)
    {
        if (f.StartsWith(".factory/")) continue;
        Add(Encoding.UTF8.GetBytes(f));
        if (File.Exists(f)) Add(File.ReadAllBytes(f));
    }
    return Hash(ms.ToArray());
}

// Lines that look like errors or failures, with numbers replaced by N.
// v1 heuristic for "new compared to the baseline": a line is new if it is not in the baseline.
HashSet<string> ProblemLines(string text)
{
    var pattern = new Regex("(error|fail|failed|failure|exception)", RegexOptions.IgnoreCase);
    return text.Split('\n')
        .Where(l => pattern.IsMatch(l))
        .Select(l => Regex.Replace(l.Trim(), @"\d+", "N"))
        .ToHashSet();
}

string Git(params string[] gitArgs)
{
    var (code, output) = Run("git", gitArgs);
    if (code != 0 && gitArgs[0] is not ("diff" or "ls-files"))
        Die($"git {string.Join(' ', gitArgs)} failed: {output.Trim()}");
    return output.Trim();
}

(int, string) Shell(string command)
{
    if (!OperatingSystem.IsWindows()) return Run("/bin/sh", new[] { "-c", command });

    // cmd.exe cannot run a PowerShell script such as "./scripts/verify.ps1", so run .ps1 checks with PowerShell.
    var script = Regex.Match(command, @"^\s*""?(?<path>[^""\s]+\.ps1)""?(?<rest>.*)$", RegexOptions.IgnoreCase);
    if (!script.Success) return Run("cmd.exe", new[] { "/c", command });

    var psArgs = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script.Groups["path"].Value };
    psArgs.AddRange(script.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    return Run("powershell.exe", psArgs);
}

(int, string) Run(string file, IEnumerable<string> argList, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var a in argList) psi.ArgumentList.Add(a);
    if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result + stderr.Result);
}
