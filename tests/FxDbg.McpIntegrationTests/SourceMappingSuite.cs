using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class SourceMappingSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        foreach (string architecture in new[] { "x86", "x64" })
        foreach (bool late in new[] { false, true })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string directory = Path.GetFullPath(Path.Combine(root, "artifacts/stage3-validation", "source space " + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            string source = Path.GetFullPath(Path.Combine(root, late ? "tests/Debuggees/Fx40.LateModule/LateCode.cs" : "tests/Debuggees/Shared/EndToEndScenarios.cs"));
            string marker = late ? "// LATE_BREAKPOINT" : "// PAGING_BREAKPOINT";
            int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains(marker)).index + 1;
            string local = Path.Combine(directory, Path.GetFileName(source));
            File.Copy(source, local);
            string exe = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            string moduleName = late ? "Fx40.LateModule.dll" : Path.GetFileName(exe);
            if (late)
                foreach (string extension in new[] { ".dll", ".pdb" })
                    File.Copy(Path.Combine(root, $"tests/Debuggees/Fx40.LateModule/bin/{configuration}/net40/Fx40.LateModule{extension}"), Path.Combine(directory, "Fx40.LateModule" + extension));
            var mappings = new object[]
            {
                new { buildRoot = Path.GetDirectoryName(source), localRoot = Path.Combine(directory, "unused-global") },
                new { buildRoot = Path.GetDirectoryName(source), localRoot = directory, module = moduleName }
            };
            await using var connection = await McpTestConnection.Create(bundle, timeout.Token);
            await ObservationSuite.Call(connection.Client, "launch", new() { ["exe"] = exe, ["sourceMappings"] = new object[]
            {
                new { buildRoot = @"C:\build", localRoot = @"D:\one" }, new { buildRoot = @"C:\build", localRoot = @"D:\two" }
            } }, timeout.Token, "invalid_request");
            var created = await ObservationSuite.Call(connection.Client, "launch", new()
            {
                ["exe"] = exe, ["args"] = new[] { "--scenario", late ? "late" : "paging", directory },
                ["stopAtEntry"] = true, ["sourceMappings"] = mappings
            }, timeout.Token);
            string session = (string)created["sessionId"]!;
            using var target = Process.GetProcessById((int)created["result"]!["processId"]!);
            _ = target.Handle;
            async Task<JsonNode> Call(string name, Dictionary<string, object?>? args = null)
            {
                args ??= new(); args["sessionId"] = session;
                return (await ObservationSuite.Call(connection.Client, name, args, timeout.Token))["result"]!;
            }
            try
            {
                var breakpoint = await Call("set_breakpoint", new() { ["file"] = local.ToUpperInvariant(), ["line"] = line });
                if (late) ObservationSuite.Require((string?)breakpoint["state"] == "pending", "Unloaded mapped module is pending.");
                await Call("continue");
                var status = await Call("status");
                var stop = status["stop"]!;
                ObservationSuite.Require((string?)stop["reason"] == "breakpoint", "Mapped breakpoint hit.");
                var stack = (await Call("stack", new() { ["threadId"] = (int)stop["threadId"]! })).AsArray();
                var location = stack[0]!["sourceLocation"]!;
                ObservationSuite.Require(string.Equals((string?)location["filePath"], local, StringComparison.OrdinalIgnoreCase) && (int)location["line"]! == line, "Stack displays mapped source and correct line. Expected " + local + ":" + line + "; actual " + location);
                ObservationSuite.Require(string.Equals((string?)location["originalFilePath"], source, StringComparison.OrdinalIgnoreCase), "Original PDB path preserved.");
                var bound = status["breakpoints"]![0]!;
                ObservationSuite.Require((string?)bound["state"] == "verified" && string.Equals((string?)bound["boundLocation"]!["filePath"], local, StringComparison.OrdinalIgnoreCase), "Binding reports mapped path.");
                await Call("detach");
                await target.WaitForExitAsync(timeout.Token);
                ObservationSuite.Require(target.ExitCode == 0, "Mapped fixture exits cleanly.");
                Console.WriteLine($"Source mappings {architecture} {configuration}: {(late ? "pending late module" : "entry module")}, module priority, case, spaces, original path passed.");
            }
            finally
            {
                await connection.DisposeAsync();
                if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(); }
            }
        }
    }
}
