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
//
// .factory/config.json: "check" (required), "reviewer" (agent name), "host" ("gitlab" | "github", default
// from the origin URL), "base" (branch PRs/MRs target, default "main").
//   dotnet run "$METHODOLOGY_ROOT/scripts/factory.cs" -- report [days] (summary for the 2-weekly review)
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

if (!Directory.Exists(".git")) Die("run this from the repo root.");
Directory.CreateDirectory(Factory);

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
    case "stop": StopCommand(args.Length > 1 ? args[1] : "stopped by /build"); break;
    case "report": Report(args.Length > 1 && int.TryParse(args[1], out var d) ? d : 14); break;
    default: Usage(); break;
}
return 0;

// ---------- commands ----------

void Start(string plan)
{
    if (!File.Exists(plan)) Die($"plan {plan} not found.");
    var text = File.ReadAllText(plan);
    if (!Regex.IsMatch(text, @"^Status:\s*Approved", RegexOptions.Multiline))
        Die("plan is not approved. Run /spec and approve it first.");
    if (Git("ls-files", plan) == "" || Git("status", "--porcelain", "--", plan) != "")
        Die("plan has uncommitted changes. An approved plan must be committed as-is.");
    LoadJson(ConfigPath, "Run /onboard first.");

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

    if (Git("status", "--porcelain", "--untracked-files=no") != "")
        Die("working tree has uncommitted changes. Commit or stash them first.");

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
    if (Git("branch", "--list", branch) != "") Die($"branch {branch} already exists. Delete it or rename the plan.");
    Git("switch", "-c", branch);

    var state = new JsonObject
    {
        ["feature"] = feature,
        ["plan"] = plan,
        ["plan_hash"] = FileHash(plan),
        ["plan_commit"] = Git("log", "-1", "--format=%H", "--", plan),
        ["branch"] = branch,
        ["status"] = "building",
        ["started"] = Now(),
        ["ended"] = null,
        ["tasks"] = tasks,
    };
    Save(state);
    Console.WriteLine($"Started {feature} on branch {branch} with {tasks.Count} tasks.");
    foreach (var t in tasks) Console.WriteLine($"  Task {Str(t!, "id")}: {Str(t!, "name")}");
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
    var depDiff = Git("diff", $"{BaseOf(config)}...HEAD", "--", "*.csproj", "package.json", "Directory.Packages.props");
    var added = Regex.Matches(depDiff, @"^\+.*(?:PackageReference|PackageVersion)\s+(?:Include|Update)=""([^""]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
        .Concat(Regex.Matches(depDiff, @"^\+\s*""([@\w./-]+)""\s*:\s*""[\^~]?\d", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
        .Distinct().ToList();
    var unapproved = added.Where(dep => !planText.Contains(dep, StringComparison.OrdinalIgnoreCase)).ToList();

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
    body.AppendLine().AppendLine("### Code Evaluation").AppendLine().AppendLine("<filled in by /build>");
    File.WriteAllText(PrBodyPath, body.ToString());

    state["ended"] = Now();
    state["status"] = status == "stopped" ? "stopped-recorded" : "finished";
    Save(state);
    Console.WriteLine(metrics.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Metrics appended to {MetricsPath}. PR body written to {PrBodyPath}.");
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
    var baseBranch = BaseOf(config);
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
    if (!File.Exists(MetricsPath)) Die("no metrics yet. They appear after the first 'factory finish'.");
    var since = DateTimeOffset.Now.AddDays(-days).ToString("yyyy-MM-dd");
    var rows = File.ReadAllLines(MetricsPath).Where(l => l.Trim() != "").Select(l => JsonNode.Parse(l)!)
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
    Console.WriteLine();
    Console.WriteLine("Review: pick the worst number, choose ONE process change, record it as an ADR.");
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

string BaseOf(JsonNode config) => Str(config, "base") is { Length: > 0 } baseBranch ? baseBranch : "main";

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

string Now() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

string Str(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Usage()
{
    Console.Error.WriteLine("usage: factory start <plan> | red <task> --test \"cmd\" | red <task> --skip \"reason\" | check <task> [--test \"cmd\"] | review <task> --pass | review <task> --fail \"findings\" | commit <task> [-m \"msg\"] | status | stop \"reason\" | finish | open-pr [--dry-run] | report [days]");
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
