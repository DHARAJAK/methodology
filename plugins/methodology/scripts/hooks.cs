#:property TreatWarningsAsErrors=false
// Claude Code hooks for the methodology (ADR-006, ADR-010).
// Claude Code runs these automatically; Claude cannot skip them.
//
//   lock-specs     before an edit: during /build, the plan (specs/) and .factory/ are read-only
//   no-raw-commit  before a git commit: during /build, only "factory commit" may commit;
//                  --no-verify (skipping the secrets scan) is never allowed
//   format         after an edit: formats the changed .cs file (whitespace only, fast)
//   stop           when Claude wants to finish: refuses while /build has uncommitted tasks
//
// Exit 2 = block; the message on stderr is shown to Claude so it can correct course.

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();
Directory.SetCurrentDirectory(root);

var raw = Console.In.ReadToEnd();
var input = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw)!;
var state = File.Exists(".factory/state.json") ? JsonNode.Parse(File.ReadAllText(".factory/state.json")) : null;
var building = state?["status"]?.GetValue<string>() == "building";

switch (args.Length > 0 ? args[0] : "")
{
    case "lock-specs":
    {
        var rel = RelativePath(input["tool_input"]?["file_path"]?.GetValue<string>());
        if (building && rel.StartsWith("specs/"))
            Block("The approved plan is locked during /build. If the plan is wrong, stop and tell the user to rerun /spec.");
        if (building && rel.StartsWith(".factory/"))
            Block("Files in .factory/ belong to the factory script. Do not edit them; use the factory commands.");
        break;
    }
    case "no-raw-commit":
    {
        var cmd = input["tool_input"]?["command"]?.GetValue<string>() ?? "";
        var isCommit = Regex.IsMatch(cmd, @"\bgit\s+commit\b");
        if (isCommit && Regex.IsMatch(cmd, @"--no-verify\b|\s-n(\s|$)"))
            Block("Never skip the pre-commit secrets scan (--no-verify / -n).");
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
    default:
        Console.Error.WriteLine("usage: hooks.cs lock-specs | no-raw-commit | format | stop");
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
