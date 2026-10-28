#:property TreatWarningsAsErrors=false
// Claude Code hooks for the methodology (ADR-006, ADR-010).
// Claude Code runs these automatically; Claude cannot skip them.
//
//   lock-specs     before an edit: during /build, the plan folder ("specs" in .factory/config.json,
//                  default specs/), the plan being built and .factory/ are read-only
//   no-raw-commit  before a git commit: during /build, only "factory commit" may commit;
//                  --no-verify (skipping the secrets scan) is never allowed
//   format         after an edit: formats the changed .cs file (whitespace only, fast)
//   stop           when Claude wants to finish: refuses while /build has uncommitted tasks;
//                  once a build has stopped or finished, tells you with a Windows notification (once)
//   notify         Claude Code is waiting for you (Notification event): Windows notification
//   session-start  sets METHODOLOGY_ROOT for the session
//
// Exit 2 = block; the message on stderr is shown to Claude so it can correct course.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();
Directory.SetCurrentDirectory(root);

// session-start: tell later shell commands where the plugin lives (args[1] = plugin root).
if (args.Length > 1 && args[0] == "session-start")
{
    var envFile = Environment.GetEnvironmentVariable("CLAUDE_ENV_FILE");
    if (!string.IsNullOrEmpty(envFile))
        File.AppendAllText(envFile, $"export METHODOLOGY_ROOT=\"{args[1].Replace('\\', '/')}\"\n");
    return 0;
}

// Every other hook only acts in repos set up with /onboard.
if (!File.Exists(".factory/config.json")) return 0;

var hook = args.Length > 0 ? args[0] : "";
JsonNode input;
JsonNode? state;
string specs;
try
{
    // The plan folder from config ("specs", default "specs"), as "docs/specs" however it was written.
    var configured = JsonNode.Parse(File.ReadAllText(".factory/config.json"))?["specs"]?.GetValue<string>();
    specs = string.IsNullOrWhiteSpace(configured) ? "specs" : configured.Replace('\\', '/').Trim('/');
    // Read stdin as UTF-8: the console's default code page garbles a byte-order mark and non-ASCII paths.
    using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var raw = stdin.ReadToEnd();
    input = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw)!;
    state = File.Exists(".factory/state.json") ? JsonNode.Parse(File.ReadAllText(".factory/state.json")) : null;
}
catch (Exception e)
{
    // Claude Code treats a crashed hook as a non-blocking error, so a guard that crashed would silently allow everything.
    if (hook is "lock-specs" or "no-raw-commit")
        Block($"The methodology hook could not read its input ({e.GetType().Name}: {e.Message}). Blocked to be safe; tell the user.");
    return 0;
}
var building = state?["status"]?.GetValue<string>() == "building";

switch (hook)
{
    case "lock-specs":
        {
            var rel = RelativePath(input["tool_input"]?["file_path"]?.GetValue<string>());
            if (building && (rel.StartsWith(specs + "/") || rel != "" && rel == RelativePath(state?["plan"]?.GetValue<string>())))
                Block("The approved plan is locked during /build. If the plan is wrong, stop and tell the user to rerun /spec.");
            if (building && rel.StartsWith(".factory/"))
                Block("Files in .factory/ belong to the factory script. Do not edit them; use the factory commands.");
            break;
        }
    case "no-raw-commit":
        {
            var cmd = input["tool_input"]?["command"]?.GetValue<string>() ?? "";
            // "git commit", and forms with options before the subcommand such as "git -C . commit" or "git -c x=y commit".
            var isCommit = Regex.IsMatch(cmd, @"\bgit\b[^|;&\n]*?\scommit\b");
            if (isCommit && Regex.IsMatch(cmd, @"--no-verify\b|\s-n(\s|$)"))
                Block("Never skip the pre-commit hook (--no-verify / -n): it runs the secrets scan and the /build commit guard.");
            // Pointing git at other hooks would switch off the pre-commit guard; onboarding sets core.hooksPath, so only block it mid-build.
            if (building && Regex.IsMatch(cmd, @"\bgit\b") && Regex.IsMatch(cmd, @"core\.hooksPath", RegexOptions.IgnoreCase))
                Block("Do not change core.hooksPath during /build: the pre-commit hook guards the build.");
            if (building && isCommit)
                Block("During /build, commit only through the referee: factory commit <task> (see the /build skill).");
            break;
        }
    case "format":
        {
            var rel = RelativePath(input["tool_input"]?["file_path"]?.GetValue<string>());
            if (rel.EndsWith(".cs") && File.Exists(rel))
                Run("dotnet", "format", "whitespace", "--folder", "--include", rel);
            break;
        }
    case "stop":
        {
            var status = state?["status"]?.GetValue<string>() ?? "";
            // One run, one message: when a build ends (stopped or finished), say so once, so you need not watch it.
            if (status is "stopped" or "stopped-recorded" or "finished" && FirstTimeFor($"{state?["feature"]}:{(status == "finished" ? "finished" : "stopped")}"))
            {
                if (status == "finished") Toast("Build finished", $"{state?["feature"]}: the merge request is ready for /verify and review.");
                else Toast("Build needs you", $"{state?["feature"]} stopped: {state?["stop_reason"]}");
            }
            if (input["stop_hook_active"]?.GetValue<bool>() == true) break; // already reminded once; don't loop
            if (!building) break;
            var open = state!["tasks"]!.AsArray()
                .Where(t => t!["status"]?.GetValue<string>() != "committed")
                .Select(t => t!["id"]!.GetValue<string>()).ToList();
            if (open.Count > 0)
                Block($"/build is not finished: task(s) {string.Join(", ", open)} not committed. " +
                      "Run the factory check and commit for them. If the factory printed STOP, report that to the user instead.");
            break;
        }
    case "notify":
        Toast("Claude Code needs you", input["message"]?.GetValue<string>() ?? "Claude is waiting for your answer.");
        break;
    default:
        Console.Error.WriteLine("usage: hooks.cs lock-specs | no-raw-commit | format | stop | notify");
        break;
}
return 0;

string RelativePath(string? path) =>
    string.IsNullOrEmpty(path) ? "" : Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');

void Block(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(2);
}

// True the first time a key is seen; keys are kept in the shared .git folder, so nothing shows up in git status.
bool FirstTimeFor(string key)
{
    try
    {
        var psi = new ProcessStartInfo("git", "rev-parse --git-common-dir") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var dir = Path.Combine(Path.GetFullPath(p.StandardOutput.ReadToEnd().Trim()), "factory");
        p.WaitForExit();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notified.txt");
        if (File.Exists(file) && File.ReadAllLines(file).Contains(key)) return false;
        File.AppendAllText(file, key + "\n");
        return true;
    }
    catch { return false; }
}

// A Windows notification (tray balloon). Needs no admin rights and no modules; does nothing elsewhere,
// or when METHODOLOGY_NO_TOAST is set (the tests set it). Never waits for it and never fails a hook.
void Toast(string title, string text)
{
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("METHODOLOGY_NO_TOAST") != null) return;
    string Quote(string v) => "'" + v.Replace("'", "''") + "'";
    var script = "Add-Type -AssemblyName System.Windows.Forms, System.Drawing; " +
                 "$n = New-Object System.Windows.Forms.NotifyIcon; $n.Icon = [System.Drawing.SystemIcons]::Information; $n.Visible = $true; " +
                 $"$n.ShowBalloonTip(10000, {Quote(title)}, {Quote(text.Length > 200 ? text[..200] : text)}, 'Info'); Start-Sleep 10; $n.Dispose()";
    try
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
    catch { /* a missed notification must never break the hook */ }
}

void Run(string file, params string[] argList)
{
    var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in argList) psi.ArgumentList.Add(a);
    try
    {
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
    catch { /* formatting is best-effort; never block an edit over it */ }
}
