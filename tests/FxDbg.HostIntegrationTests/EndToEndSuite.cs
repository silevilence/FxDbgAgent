using System.Diagnostics;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;
using Newtonsoft.Json.Linq;

internal static class EndToEndSuite
{
    internal static async Task Run(string root, string configuration)
    {
        string source = Path.Combine(root, "tests", "Debuggees", "Shared", "EndToEndScenarios.cs");
        int Line(string marker) => File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains(marker)).index + 1;
        foreach (string architecture in new[] { "x86", "x64" })
        foreach (string kind in new[] { "Console", "WinForms" })
        foreach (string scenario in new[] { "observe", "late", "exception" })
        {
            string name = "Fx40." + kind + "." + architecture;
            string targetPath = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
            string directory = Path.Combine(root, "artifacts", "stage1-12", configuration, kind + "-" + architecture + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string engineRoot = Path.Combine(root, "src", "FxDbg.Engine", "bin", configuration, "net48");
            using var host = new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
                new EngineProcessPaths(Path.Combine(engineRoot, "FxDbg.Engine.x86.exe"), Path.Combine(engineRoot, "FxDbg.Engine.x64.exe")));
            var id = SessionId.New();
            var target = await host.LaunchAsync(new LaunchRequest(id, targetPath, new[] { "--scenario", scenario, directory }, null, null,
                TargetArchitecture.Auto, true, TimeSpan.FromSeconds(15)), CancellationToken.None);
            using Process process = Process.GetProcessById(target.ProcessId);
            _ = process.Handle;
            async Task<JToken> Call(string method, JObject? parameters = null) => await host.InvokeAsync(id, method, parameters, TimeSpan.FromSeconds(15));
            async Task<JToken> Step(string mode, int thread)
            {
                await Call("step", new JObject { ["kind"] = mode, ["threadId"] = thread });
                JToken stopped = await Call("wait");
                Require((string?)stopped["reason"] == "step", "Expected source step stop.");
                return stopped;
            }
            try
            {
                JToken? breakpoint = null;
                if (scenario == "observe") breakpoint = await Call("break.set", new JObject { ["file"] = source, ["line"] = Line("// E2E_BREAKPOINT") });
                if (scenario == "late")
                {
                    string library = Path.Combine(root, "tests", "Debuggees", "Fx40.LateModule", "bin", configuration, "net40", "Fx40.LateModule.dll");
                    File.Copy(library, Path.Combine(directory, "Fx40.LateModule.dll"));
                    File.Copy(Path.ChangeExtension(library, ".pdb"), Path.Combine(directory, "Fx40.LateModule.pdb"));
                    string lateSource = Path.Combine(root, "tests", "Debuggees", "Fx40.LateModule", "LateCode.cs");
                    int line = File.ReadAllLines(lateSource).Select((text, index) => (text, index)).Single(item => item.text.Contains("// LATE_BREAKPOINT")).index + 1;
                    breakpoint = await Call("break.set", new JObject { ["file"] = lateSource, ["line"] = line });
                    Require((string?)breakpoint["state"] == "pending", "Late module breakpoint must begin pending.");
                }
                await Call("continue");
                JToken stop = await Call("wait");
                int threadId = (int)stop["threadId"]!;
                if (scenario == "exception")
                {
                    JToken error = stop["exception"]!;
                    Require((string?)stop["reason"] == "exception" && (bool)error["isUnhandled"]!, "Unhandled scenario must report an exception.");
                    Require(((string?)error["typeName"])?.EndsWith("ScenarioException") == true && (string?)error["message"] == "e2e-unhandled-message", "Exception type/message must come from target storage.");
                    Require((int?)error["throwLocation"]?["line"] == Line("// E2E_EXCEPTION") && error["stack"]!.Any(), "Exception source and stack must be present.");
                    await Call("terminate");
                }
                else
                {
                    Require((string?)stop["reason"] == "breakpoint" && JToken.DeepEquals(stop["breakpointId"], breakpoint!["breakpointId"]), "Source breakpoint hit identity must match.");
                    if (scenario == "observe")
                    {
                        JToken stack = await Call("stack", new JObject { ["threadId"] = threadId, ["count"] = 32 });
                        Require(stack.Count() >= 14, "Recursive scenario requires at least 14 managed frames.");
                        JToken variables = await Call("variables", new JObject { ["frameId"] = stack[0]!["frameId"]!.DeepClone(), ["maxDepth"] = 1 });
                        File.WriteAllText(Path.Combine(directory, "variables.json"), variables.ToString());
                        JToken Variable(string name) => variables.Single(value => (string?)value["name"] == name);
                        Require((string?)Variable("number")["displayValue"] == "42" && (string?)Variable("message")["displayValue"] == "hello-framework", "Arguments must cross the public Host interface.");
                        Require((string?)Variable("localNumber")["displayValue"] == "84" && (string?)Variable("localText")["displayValue"] == "local-value", "Basic locals must be available at the breakpoint.");
                        JToken node = Variable("node");
                        Require(node["children"]!.Any(child => (string?)child["name"] == "Label" && (string?)child["displayValue"] == "node-label"), "One object field level must be read.");
                        JToken self = node["children"]!.Single(child => (string?)child["name"] == "Self");
                        Require(JToken.DeepEquals(self["referenceId"], node["referenceId"]) && !self["children"]!.Any(), "Cycle must return its original reference without recursion.");
                        JToken largeTexts = node["children"]!.Single(child => (string?)child["name"] == "LargeTexts");
                        var page = new JObject { ["frameId"] = stack[0]!["frameId"]!.DeepClone(), ["referenceId"] = largeTexts["referenceId"]!.DeepClone(),
                            ["count"] = 129, ["maxDepth"] = 0, ["maxStringLength"] = 32768 };
                        try
                        {
                            await Call("variables", page);
                            throw new InvalidOperationException("Oversized variable page must be rejected.");
                        }
                        catch (FxDbgException error)
                        {
                            Require(error.Code == FxDbgErrorCode.InvalidRequest && error.Message.Contains("4 MiB"), "Oversized response needs an actionable size error.");
                        }
                        page["count"] = 1;
                        JToken smallerPage = await Call("variables", page);
                        Require(((string?)smallerPage[0]!["displayValue"])?.Length == 32768, "The same variable reference must work with a smaller page after rejection.");
                        Require((string?)Variable("userCodeCalls")["displayValue"] == "0", "Observation must not execute formatters.");
                        await Call("break.remove", new JObject { ["breakpointId"] = breakpoint!["breakpointId"]!.DeepClone() });
                        JToken atCall = await Step("over", threadId);
                        Require((int?)atCall["location"]?["line"] == Line("// E2E_STEP_CALL"), "Step Over must reach the call source line.");
                        JToken inside = await Step("into", threadId);
                        Require(((string?)inside["methodName"])?.EndsWith("AddOne") == true, "Step Into must enter the helper.");
                        JToken outside = await Step("out", threadId);
                        Require(((string?)outside["methodName"])?.EndsWith("Observe") == true, "Step Out must return to the caller.");
                        await Step("over", threadId);
                    }
                    else
                    {
                        JToken modules = await Call("modules");
                        Require(modules.Any(module => (string?)module["name"] == "Fx40.LateModule.dll" && (string?)module["symbolStatus"] == "loaded"), "Late Windows PDB must be matched and loaded.");
                        await Call("break.remove", new JObject { ["breakpointId"] = breakpoint!["breakpointId"]!.DeepClone() });
                    }
                    await Call("continue");
                    await Call("wait");
                    Require(File.ReadAllText(Path.Combine(directory, "completed")) == "ok", "Scenario must finish without observation side effects.");
                }
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(exitTimeout.Token);
                Require(process.ExitCode == 0, "Controlled scenario must cleanly exit.");
                Require((string?)(await Call("wait"))["reason"] == "processExit", "A wait submitted after process exit must return the terminal observation.");
                try
                {
                    await Call("break.set", new JObject { ["file"] = source, ["line"] = Line("// E2E_BREAKPOINT") });
                    throw new InvalidOperationException("An exited target cannot accept a new breakpoint.");
                }
                catch (FxDbgException error)
                {
                    Require(error.Code == FxDbgErrorCode.InvalidSessionState, "An exited target must report invalid_session_state before touching COM.");
                }
                await Call("detach");
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Console.WriteLine("PASS: " + configuration + " " + kind + " " + architecture + " " + scenario + ".");
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
