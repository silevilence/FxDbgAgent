using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class ServiceSuite
{
    internal static async Task Run(string bundle, string root, string configuration, string manifest)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        string source = Path.GetFullPath(Path.Combine(root, "tests/Debuggees/Environment.Shared/EnvironmentService.cs"));
        int Line(string marker) => File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(row=>row.text.Contains("// " + marker)).index + 1;
        var evidence = new JsonObject { ["configuration"]=configuration, ["passed"]=false, ["cases"]=new JsonArray() };
        var cases = evidence["cases"]!.AsArray();
        try
        {
            foreach (var resource in JsonNode.Parse(File.ReadAllText(manifest))!["resources"]!.AsArray())
            {
                string service = (string)resource!["service"]!, heartbeat = (string)resource["heartbeat"]!;
                string architecture = (string)resource["architecture"]!;
                ObservationSuite.Require(service.StartsWith("FxDbgStage34-services-", StringComparison.Ordinal), "Only the isolated service acceptance fixture can be controlled.");
                var health = await ReadHeartbeat(heartbeat, token);
                int pid = (int)health["pid"]!;
                await AssertRunning(service, pid, token);
                await using (var connection = await McpTestConnection.Create(bundle, token))
                {
                    var attached = await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=pid},token);
                    string session = (string)attached["sessionId"]!;
                    async Task<JsonNode> Call(string method, Dictionary<string,object?>? args=null, string? error=null)
                    { args ??=new(); args["sessionId"]=session; return await ObservationSuite.Call(connection.Client,method,args,token,error); }
                    await Call("pause");
                    var bp=(await Call("set_breakpoint",new(){["file"]=source,["line"]=Line("ENV_SERVICE_ENTRY")}))["result"]!;
                    var stop=(await Call("continue"))["result"]!["stop"]!;
                    ObservationSuite.Require((string?)stop["reason"]=="breakpoint", "Service business breakpoint hit.");
                    int thread=(int)stop["threadId"]!;
                    var stack=(await Call("stack",new(){["threadId"]=thread}))["result"]!.AsArray();
                    ObservationSuite.Require(stack.Count>=2 && (int)stack[0]!["sourceLocation"]!["line"]! == Line("ENV_SERVICE_ENTRY"), "Service stack contains exact business source line.");
                    ObservationSuite.Require(string.Equals((string?)stack[0]!["sourceLocation"]!["filePath"],source,StringComparison.OrdinalIgnoreCase),"Service stack uses the matching PDB source document: " + stack[0]!["sourceLocation"]);
                    string oldFrame=(string)stack[0]!["frameId"]!;
                    var vars=(await Call("variables",new(){["frameId"]=oldFrame}))["result"]!.AsArray();
                    int input=int.Parse((string)vars.Single(value=>(string?)value!["name"]=="input")!["displayValue"]!);
                    foreach (string kind in new[]{"over","into","out"})
                    {
                        stop=(await Call("step",new(){["threadId"]=thread,["kind"]=kind}))["result"]!["stop"]!;
                        ObservationSuite.Require((string?)stop["reason"]=="step", "Service step " + kind);
                        thread=(int)stop["threadId"]!;
                        stack=(await Call("stack",new(){["threadId"]=thread}))["result"]!.AsArray();
                        if(kind=="over")
                        {
                            ObservationSuite.Require((int)stack[0]!["sourceLocation"]!["line"]! == Line("ENV_SERVICE_CALL"), "Step over advances to call.");
                            vars=(await Call("variables",new(){["frameId"]=(string)stack[0]!["frameId"]!}))["result"]!.AsArray();
                            ObservationSuite.Require((string?)vars.Single(value=>(string?)value!["name"]=="doubled")!["displayValue"] == (input*2).ToString(), "Service local variable matches parameter.");
                        }
                        if(kind=="into") ObservationSuite.Require(((string)stack[0]!["methodName"]!).Contains("AddOne"), "Step into enters service helper.");
                        if(kind=="out") ObservationSuite.Require(((string)stack[0]!["methodName"]!).Contains("DoWork"), "Step out returns to service work.");
                    }
                    await Call("variables",new(){["frameId"]=oldFrame},"frame_not_found");
                    await Call("terminate",error:"invalid_request");
                    await Call("remove_breakpoint",new(){["breakpointId"]=(string)bp["breakpointId"]!});
                    await Call("continue",new(){["waitForStop"]=false});
                    await Call("pause");
                    await Call("detach");
                    await AssertHealthy(service,heartbeat,pid,token);
                    cases.Add(new JsonObject{["architecture"]=architecture,["mode"]="mcp-debug-detach",["pid"]=pid,["input"]=input,["line"]=Line("ENV_SERVICE_ENTRY")});
                }

                foreach(string mode in new[]{"eof-running","eof-stopped","kill-running","kill-stopped"})
                {
                    health=await ReadHeartbeat(heartbeat,token);
                    await using var connection=await McpTestConnection.Create(bundle,token);
                    var attached=await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=pid},token);
                    if(mode.EndsWith("stopped")) await ObservationSuite.Call(connection.Client,"pause",new(){["sessionId"]=(string)attached["sessionId"]!},token);
                    using var engine=Process.GetProcessById(ProcessInventory.Children(connection.ProcessId).Single(item=>item.Name.StartsWith("FxDbg.Engine.")).Id);
                    var elapsed=Stopwatch.StartNew();
                    using(var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        if(mode.StartsWith("eof")) connection.CloseInput(); else connection.KillHost();
                        await engine.WaitForExitAsync(cleanup.Token);
                        await connection.WaitExit(cleanup.Token);
                    }
                    elapsed.Stop();
                    await AssertHealthy(service,heartbeat,pid,token);
                    await using var reconnect=await McpTestConnection.Create(bundle,token);
                    var reattached=await ObservationSuite.Call(reconnect.Client,"attach",new(){["pid"]=pid},token);
                    await ObservationSuite.Call(reconnect.Client,"detach",new(){["sessionId"]=(string)reattached["sessionId"]!},token);
                    cases.Add(new JsonObject{["architecture"]=architecture,["mode"]=mode,["pid"]=pid,["cleanupMs"]=elapsed.ElapsedMilliseconds});
                }

                await using(var connection=await McpTestConnection.Create(bundle,token))
                {
                    var attached=await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=pid},token);
                    string session=(string)attached["sessionId"]!;
                    await ObservationSuite.Call(connection.Client,"pause",new(){["sessionId"]=session},token);
                    var threads=(await ObservationSuite.Call(connection.Client,"threads",new(){["sessionId"]=session},token))["result"]!.AsArray();
                    string? oldFrame=null;
                    foreach(var thread in threads)
                    {
                        var frames=(await ObservationSuite.Call(connection.Client,"stack",new(){["sessionId"]=session,["threadId"]=(int)thread!["threadId"]!},token))["result"]!.AsArray();
                        if(frames.Count>0) { oldFrame=(string)frames[0]!["frameId"]!; break; }
                    }
                    ObservationSuite.Require(oldFrame is not null,"Stopped service has managed frames.");
                    await ObservationSuite.Call(connection.Client,"continue",new(){["sessionId"]=session,["waitForStop"]=false},token);
                    await Command("sc.exe",new[]{"stop",service},root,token);
                    JsonNode state;
                    do { state=(await ObservationSuite.Call(connection.Client,"status",new(){["sessionId"]=session},token))["result"]!; if((string?)state["target"]?["sessionState"]!="terminated") await Task.Delay(50,token); }
                    while((string?)state["target"]?["sessionState"]!="terminated");
                    await ObservationSuite.Call(connection.Client,"variables",new(){["sessionId"]=session,["frameId"]=oldFrame},token,"invalid_session_state");
                    while(!Regex.IsMatch(await Command("sc.exe",new[]{"query",service},root,token),@"STATE\s*:\s*1\b")) await Task.Delay(50,token);
                    await Command("sc.exe",new[]{"start",service},root,token);
                    do { health=await ReadHeartbeat(heartbeat,token); if((int)health["pid"]! == pid) await Task.Delay(100,token); } while((int)health["pid"]! == pid);
                    int previousPid=pid; pid=(int)health["pid"]!;
                    await AssertRunning(service,pid,token);
                    var fresh=await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=pid},token);
                    ObservationSuite.Require((string)fresh["sessionId"]! != session,"Restart requires a new explicit session.");
                    await ObservationSuite.Call(connection.Client,"detach",new(){["sessionId"]=(string)fresh["sessionId"]!},token);
                    await AssertHealthy(service,heartbeat,pid,token);
                    cases.Add(new JsonObject{["architecture"]=architecture,["mode"]="scm-restart",["oldPid"]=previousPid,["newPid"]=pid});
                }
                await CliSmoke(root,configuration,pid,source,Line("ENV_SERVICE_CALL"),token);
                health=await ReadHeartbeat(heartbeat,token);
                await AssertHealthy(service,heartbeat,pid,token);
                cases.Add(new JsonObject{["architecture"]=architecture,["mode"]="cli-breakpoint-detach",["pid"]=pid});
            }
            ObservationSuite.Require(cases.Count==14,"All Service MCP/CLI scenarios completed for both architectures.");
            evidence["passed"]=true;
        }
        catch(Exception error) { evidence["error"]=error.ToString(); throw; }
        finally { evidence["atUtc"]=DateTimeOffset.UtcNow.ToString("o"); File.WriteAllText(Path.Combine(root,$"artifacts/stage3-4-validation/services-{configuration}.json"),evidence.ToJsonString()); }
    }

    internal static async Task<JsonNode> ReadHeartbeat(string path,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            try { return JsonNode.Parse(await File.ReadAllTextAsync(path,token))!; }
            catch(Exception error) when(error is IOException or System.Text.Json.JsonException) { await Task.Delay(20,token); }
        }
    }
    private static async Task AssertHealthy(string service,string heartbeat,int pid,CancellationToken token)
    {
        // Establish the baseline after detach/cleanup, so progress made before it
        // cannot accidentally satisfy the post-cleanup business liveness assertion.
        JsonNode before=await ReadHeartbeat(heartbeat,token);
        ObservationSuite.Require((int)before["pid"]! == pid,"Cleanup preserved the selected service instance.");
        JsonNode after;
        do { await Task.Delay(100,token); after=await ReadHeartbeat(heartbeat,token); } while((int)after["sequence"]! <= (int)before["sequence"]!);
        ObservationSuite.Require((int)after["pid"]! == pid && (int)after["result"]! == (int)after["sequence"]!*2+1,"Same service instance continues its business heartbeat.");
        await AssertRunning(service,pid,token);
    }
    private static async Task AssertRunning(string service,int pid,CancellationToken token)
    {
        string status=await Command("sc.exe",new[]{"queryex",service},Environment.CurrentDirectory,token);
        ObservationSuite.Require(Regex.IsMatch(status,@"STATE\s*:\s*4\b") && Regex.IsMatch(status,@"PID\s*:\s*"+pid+@"\b"),"SCM confirms RUNNING and the exact process instance.");
    }
    internal static async Task<string> Command(string exe,IEnumerable<string> args,string directory,CancellationToken token)
    {
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};
        foreach(string arg in args) start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync(token); var error=process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        finally { if(!process.HasExited) process.Kill(); }
        ObservationSuite.Require(process.ExitCode==0,$"Fixture command {Path.GetFileName(exe)} failed ({process.ExitCode}): {await output} {await error}");
        return await output;
    }
    private static async Task CliSmoke(string root,string configuration,int pid,string source,int line,CancellationToken token)
    {
        string cli=Path.Combine(root,$"src/FxDbg.Cli/bin/{configuration}/net10.0-windows/fxdbg.dll");
        async Task<JsonNode> Call(params string[] args) => JsonNode.Parse(await Command("dotnet",new[]{cli}.Concat(args),root,token))!;
        var attached=await Call("attach","--pid",pid.ToString()); string session=(string)attached["sessionId"]!;
        try
        {
            await Call("pause","--session",session);
            await Call("break","--session",session,"--file",source,"--line",line.ToString());
            await Call("continue","--session",session);
            var stop=(await Call("wait","--session",session))["result"]!;
            ObservationSuite.Require((string?)stop["reason"]=="breakpoint","CLI service breakpoint.");
            var stack=(await Call("stack","--session",session,"--thread",((int)stop["threadId"]!).ToString()))["result"]!.AsArray();
            ObservationSuite.Require((int)stack[0]!["sourceLocation"]!["line"]! == line,"CLI service source location.");
        }
        finally
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Command("dotnet",new[]{cli,"detach","--session",session},root,cleanup.Token);
        }
    }
}
