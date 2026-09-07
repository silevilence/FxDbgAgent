using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;

internal static class ServiceIisPermissionsSuite
{
    internal static async Task Run(string bundle, string root, string configuration, string manifest, bool restricted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        string resultPath = Path.Combine(root, "artifacts/stage3-4-validation", $"permissions-{(restricted ? "restricted" : "admin")}-{configuration}.json");
        var outcomes = new JsonArray();
        uint? original = RestrictedTokenProcess.DebugAttributes(Environment.ProcessId);
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var http = new HttpClient { Timeout=TimeSpan.FromSeconds(10) };
        try
        {
            ObservationSuite.Require(restricted ? !elevated && original is null : elevated && original.HasValue, "Required real token state is present (group membership alone is not debug permission).");
            foreach (bool alreadyEnabled in restricted ? new[] { false } : new[] { false, true })
            {
                if (!restricted) RestrictedTokenProcess.SetDebugEnabled(alreadyEnabled);
                await using var connection = await McpTestConnection.Create(bundle, timeout.Token);
                uint? hostBefore = RestrictedTokenProcess.DebugAttributes(connection.ProcessId);
                foreach (var resource in JsonNode.Parse(File.ReadAllText(manifest))!["resources"]!.AsArray())
                {
                    string architecture = (string)resource!["architecture"]!;
                    foreach (string kind in new[] { "service", "iis" })
                    {
                        JsonNode targetHealth = kind == "service"
                            ? JsonNode.Parse(await File.ReadAllTextAsync((string)resource["heartbeat"]!, timeout.Token))!
                            : JsonNode.Parse(await http.GetStringAsync((string)resource["url"]!, timeout.Token))!;
                        int targetPid = (int)targetHealth["pid"]!;
                        var previousEngines = ProcessInventory.Children(connection.ProcessId).Select(item => item.Id).ToHashSet();
                        var attached = await ObservationSuite.Call(connection.Client, "attach",
                            new() { ["pid"]=targetPid, ["timeoutMs"]=20000 }, timeout.Token, restricted ? "access_denied" : null);
                        if (restricted)
                        {
                            string message = (string)attached["error"]!["message"]!;
                            ObservationSuite.Require(message.Contains("Host") || message.Contains("Engine"), "Permission failure identifies its stage.");
                        }
                        else
                        {
                            string session = (string)attached["sessionId"]!;
                            ObservationSuite.Require((string?)attached["result"]!["architecture"] == architecture, "Actual target architecture routed correctly.");
                            int enginePid = ProcessInventory.Children(connection.ProcessId).Single(item =>
                                !previousEngines.Contains(item.Id) && item.Name == $"FxDbg.Engine.{architecture}.exe").Id;
                            ObservationSuite.Require(RestrictedTokenProcess.DebugAttributes(enginePid) == hostBefore, "Engine token restored/preserved after actual CLR attach.");
                            await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=targetPid},timeout.Token,"already_debugged");
                            await ObservationSuite.Call(connection.Client,"detach",new(){["sessionId"]=session},timeout.Token);
                        }
                        {
                            await Task.Delay(1200, timeout.Token);
                            JsonNode after = kind == "service"
                                ? JsonNode.Parse(await File.ReadAllTextAsync((string)resource["heartbeat"]!, timeout.Token))!
                                : JsonNode.Parse(await http.GetStringAsync((string)resource["url"]!, timeout.Token))!;
                            ObservationSuite.Require((int)after["pid"]! == targetPid && (kind != "service" || (int)after["sequence"]! > (int)targetHealth["sequence"]!), "Target keeps serving work after attach/detach or permission denial.");
                        }
                        uint? hostAfter = RestrictedTokenProcess.DebugAttributes(connection.ProcessId);
                        ObservationSuite.Require(hostAfter == hostBefore, $"Host token restored/preserved after attach: before={hostBefore}, after={hostAfter}.");
                        outcomes.Add(new JsonObject { ["architecture"]=architecture,["kind"]=kind,["targetPid"]=targetPid,
                            ["alreadyEnabled"]=alreadyEnabled,["expected"]=restricted ? "access_denied" : "attached-detached" });
                    }
                }
                // A missing SeDebugPrivilege must not disable ordinary same-user debugging.
                string exe = Path.Combine(root,$"tests/Debuggees/Fx40.Console.x86/bin/{configuration}/net40/Fx40.Console.x86.exe");
                var launched = await ObservationSuite.Call(connection.Client,"launch",new(){["exe"]=exe,["stopAtEntry"]=true},timeout.Token);
                await ObservationSuite.Call(connection.Client,"terminate",new(){["sessionId"]=(string)launched["sessionId"]!},timeout.Token);
                using (var sameUser = Process.Start(new ProcessStartInfo(exe)
                { UseShellExecute=false, CreateNoWindow=true, ArgumentList={"--wait-milliseconds","30000"} })!)
                {
                    try
                    {
                        await Task.Delay(700, timeout.Token);
                        var attached = await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=sameUser.Id},timeout.Token);
                        await ObservationSuite.Call(connection.Client,"detach",new(){["sessionId"]=(string)attached["sessionId"]!},timeout.Token);
                        ObservationSuite.Require(!sameUser.HasExited, "Same-user attach works without SeDebugPrivilege.");
                    }
                    finally { if (!sameUser.HasExited) { sameUser.Kill(); await sameUser.WaitForExitAsync(timeout.Token); } }
                    await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=sameUser.Id},timeout.Token,"target_not_found");
                }
                if (!restricted)
                {
                    var pids = JsonNode.Parse(File.ReadAllText(manifest))!["resources"]!.AsArray()
                        .Select(resource => (int)JsonNode.Parse(File.ReadAllText((string)resource!["heartbeat"]!))!["pid"]!).ToArray();
                    var concurrent = await Task.WhenAll(pids.Select(pid => ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=pid},timeout.Token)));
                    ObservationSuite.Require(RestrictedTokenProcess.DebugAttributes(connection.ProcessId) == hostBefore, "Concurrent sessions preserve Host privileges.");
                    await Task.WhenAll(concurrent.Select(attached => ObservationSuite.Call(connection.Client,"detach",new(){["sessionId"]=(string)attached["sessionId"]!},timeout.Token)));
                }
                await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=int.MaxValue},timeout.Token,"target_not_found");
            }
            if (!restricted)
            {
                await RestrictedTokenProcess.Run(new[] { typeof(ServiceIisPermissionsSuite).Assembly.Location,bundle,"permissions-restricted",root,configuration,manifest },root,timeout.Token);
                ObservationSuite.Require(JsonNode.Parse(File.ReadAllText(Path.Combine(root,"artifacts/stage3-4-validation",$"permissions-restricted-{configuration}.json")))!["passed"]!.GetValue<bool>(), "Restricted child wrote successful evidence.");
            }
            File.WriteAllText(resultPath,new JsonObject { ["passed"]=true,["elevated"]=elevated,["outcomes"]=outcomes }.ToJsonString());
            Console.WriteLine($"Permissions {configuration} {(restricted ? "restricted" : "admin")}: {outcomes.Count} real Service/IIS cases passed.");
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath,new JsonObject { ["passed"]=false,["error"]=error.ToString(),["outcomes"]=outcomes }.ToJsonString());
            throw;
        }
        finally { if (!restricted) RestrictedTokenProcess.SetDebugEnabled((original!.Value & 2) != 0); }
    }
}
