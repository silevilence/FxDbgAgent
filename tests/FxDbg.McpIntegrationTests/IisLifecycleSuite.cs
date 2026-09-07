using System.Text.Json.Nodes;
using System.Xml.Linq;

internal static class IisLifecycleSuite
{
    internal static async Task Run(string bundle,string root,string configuration,string manifest)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var token=deadline.Token;
        using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(90) };
        var cases=new JsonArray(); bool passed=false;
        string source=Path.GetFullPath(Path.Combine(root,"tests/Debuggees/Fx40.Environment.Late/LateWork.cs"));
        string page=Path.GetFullPath(Path.Combine(root,"tests/Debuggees/Fx40.Environment.Web/late.aspx"));
        int line=File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(row=>row.text.Contains("// ENV_LATE_BREAKPOINT")).index+1;
        string appcmd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32/inetsrv/appcmd.exe");
        try
        {
            foreach(var resource in JsonNode.Parse(File.ReadAllText(manifest))!["resources"]!.AsArray())
            {
                string pool=(string)resource!["pool"]!,url=(string)resource["url"]!,webRoot=(string)resource["webRoot"]!;
                ObservationSuite.Require(pool.StartsWith("FxDbgStage34-lifecycle-",StringComparison.Ordinal),"Lifecycle controls require an isolated owned pool.");
                async Task<string> Command(params string[] args)=>await ServiceSuite.Command(appcmd,args,root,token);
                async Task<JsonNode> Request(string action="")
                {
                    Console.WriteLine($"IIS {resource["architecture"]}: HTTP {action}");
                    return JsonNode.Parse(await http.GetStringAsync(url+"?action="+action,token))!;
                }
                var initial=await Request(); int pid=(int)initial["pid"]!;
                ObservationSuite.Require(!(bool)initial["lateLoaded"]!,"Fresh worker has not loaded the late assembly.");
                string release=Path.Combine(webRoot,"release.request");
                var baseline=new Dictionary<string,string>();
                foreach(string setting in new[]{"processModel.pingingEnabled","processModel.idleTimeout","recycling.periodicRestart.time"})
                    baseline.Add(setting,(await Command("list","apppool",pool,"/text:"+setting)).Trim());
                var snapshots=new JsonArray();
                Task<string>? pending=null;
                await using var connection=await McpTestConnection.Create(bundle,token);
                string session="";
                async Task Attach(int target)
                {
                    Console.WriteLine($"IIS {resource["architecture"]}: attach {target}");
                    var result=await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=target,["sourceMappings"]=new[]{new{buildRoot=webRoot,localRoot=Path.GetDirectoryName(page)}}},token);
                    session=(string)result["sessionId"]!;
                    ObservationSuite.Require((string?)result["result"]!["architecture"]==(string?)resource["architecture"],"Lifecycle target uses matching Engine architecture.");
                }
                async Task<JsonNode> Call(string method,Dictionary<string,object?>? args=null,string? error=null)
                { args??=new();args["sessionId"]=session;return (await ObservationSuite.Call(connection.Client,method,args,token,error))[error is null?"result":"error"]!; }
                async Task<JsonNode> Wait(Func<JsonNode,bool> condition,string label,int seconds=20)
                {
                    var limit=DateTimeOffset.UtcNow.AddSeconds(seconds); JsonNode? state=null;
                    do
                    {
                        state=await Call("status");
                        if(condition(state))
                        {
                            snapshots.Add(new JsonObject{["label"]=label,["state"]=state.DeepClone()});
                            Console.WriteLine($"IIS {resource["architecture"]}: {label} ({state["stop"]?["reason"]})");
                            File.WriteAllText(Path.Combine(root,$"artifacts/stage3-4-validation/lifecycle-progress-{configuration}-{resource["architecture"]}.json"),snapshots.ToJsonString());
                            return state;
                        }
                        await Task.Delay(30,token);
                    } while(DateTimeOffset.UtcNow<limit);
                    throw new InvalidOperationException(label+" did not converge: "+state);
                }
                async Task<JsonNode> Stopped()
                {
                    var state=await Wait(value=>(string?)value["target"]?["sessionState"]=="stopped","stopped");
                    ObservationSuite.Require((string?)state["stop"]?["reason"]=="breakpoint","Lifecycle HTTP request stops at a source breakpoint.");
                    return state;
                }
                async Task<JsonNode> Trigger(string endpoint)
                {
                    pending=http.GetStringAsync(endpoint,token);
                    return await Stopped();
                }
                async Task<JsonNode> Resume()
                {
                    Console.WriteLine($"IIS {resource["architecture"]}: continue HTTP");
                    await Call("continue",new(){["waitForStop"]=false});
                    var response=JsonNode.Parse(await pending!)!;pending=null;
                    Console.WriteLine($"IIS {resource["architecture"]}: HTTP resumed");
                    ObservationSuite.Require((int)response["pid"]! == pid && (int)response["result"]! ==85,"HTTP completed in the attached worker.");
                    return response;
                }
                string Domain(JsonNode state,int runtimeId)=>(string)state["appDomains"]!.AsArray().Single(domain=>(int)domain!["runtimeId"]! ==runtimeId)!["appDomainId"]!;
                try
                {
                    await Command("set","apppool",pool,"/processModel.pingingEnabled:false","/processModel.idleTimeout:00:00:00","/recycling.periodicRestart.time:00:00:00");
                    // Process-model changes recycle asynchronously. Finish that fixture
                    // transition before attaching; the actual overlap test is below.
                    await Command("stop","apppool","/apppool.name:"+pool);
                    var setupLimit=DateTimeOffset.UtcNow.AddSeconds(20);
                    while(XDocument.Parse(await Command("list","wp","/xml")).Descendants("WP").Any(wp=>(string?)wp.Attribute("APPPOOL.NAME")==pool))
                    { if(DateTimeOffset.UtcNow>=setupLimit) throw new InvalidOperationException("Configured fixture worker did not exit."); await Task.Delay(50,token); }
                    await Command("start","apppool","/apppool.name:"+pool);
                    initial=await Request(); pid=(int)initial["pid"]!;
                    ObservationSuite.Require(!(bool)initial["lateLoaded"]!,"Configured worker starts without the late module.");
                    await Attach(pid);
                    var late=await Call("set_breakpoint",new(){["file"]=source,["line"]=line});
                    ObservationSuite.Require((string?)late["state"]=="pending","Unloaded disk module starts pending.");
                    var state=await Trigger(url+"?action=late");
                    ObservationSuite.Require(state["breakpoints"]!.AsArray().Any(bp=>(string?)bp!["breakpointId"]==(string?)late["breakpointId"] && (string?)bp["state"] is "verified" or "moved"),"Module load rebinds pending disk breakpoint.");
                    var response=await Resume(); ObservationSuite.Require((int)response["lateResult"]! ==85,"Late disk code executed.");
                    await Call("remove_breakpoint",new(){["breakpointId"]=(string)late["breakpointId"]!});

                    var pageBp=await Call("set_breakpoint",new(){["file"]=page,["line"]=2});
                    ObservationSuite.Require((string?)pageBp["state"]=="pending","Unrequested page starts pending; batch compilation is disabled.");
                    state=await Trigger(new Uri(new Uri(url),"late.aspx").ToString());
                    var pageStack=await Call("stack",new(){["threadId"]=(int)state["stop"]!["threadId"]!});
                    ObservationSuite.Require(string.Equals((string?)pageStack[0]!["sourceLocation"]?["filePath"],page,StringComparison.OrdinalIgnoreCase) && (int?)pageStack[0]!["sourceLocation"]?["line"]==2,"First dynamic compilation binds exact local page source.");
                    // One ASPX source line can contain several sequence points. Remove
                    // this proven one-shot breakpoint before draining the whole request.
                    await Call("remove_breakpoint",new(){["breakpointId"]=(string)pageBp["breakpointId"]!}); await Resume();

                    var domains=await Request("domains-create");
                    state=await Wait(value=>value["appDomains"]!.AsArray().Count(domain=>(string?)domain!["name"]=="FxDbgFixtureDomain")==2,"two child domains");
                    string first=Domain(state,(int)domains["domains"]!["a"]!),second=Domain(state,(int)domains["domains"]!["b"]!);
                    var scoped=await Call("set_breakpoint",new(){["file"]=source,["line"]=line,["appDomainId"]=first});
                    ObservationSuite.Require(scoped["boundAppDomainIds"]!.AsArray().Count==1 && (string?)scoped["boundAppDomainIds"]![0]==first,"Scoped breakpoint binds only first child domain.");
                    await Request("run-b"); ObservationSuite.Require((string?)(await Call("status"))["target"]?["sessionState"]=="running","Second child does not hit first child's breakpoint.");
                    state=await Trigger(url+"?action=run-a");
                    ObservationSuite.Require((string?)state["stop"]?["appDomainId"]==first,"Stop belongs to selected child domain.");
                    var stack=await Call("stack",new(){["threadId"]=(int)state["stop"]!["threadId"]!});
                    string frame=(string)stack[0]!["frameId"]!;
                    var variables=(await Call("variables",new(){["frameId"]=frame,["maxDepth"]=0})).AsArray();
                    ObservationSuite.Require((string?)stack[0]!["appDomainId"]==first && variables.All(value=>(string?)value!["appDomainId"]==first),"Stack and variables retain child domain identity.");
                    string reference=(string)variables.Single(value=>(string?)value!["name"]=="values")!["referenceId"]!;
                    var oldModules=state["modules"]!.AsArray().Where(module=>(string?)module!["appDomainId"]==first).Select(module=>(string)module!["moduleId"]!).ToArray();
                    await Resume();
                    var global=await Call("set_breakpoint",new(){["file"]=source,["line"]=line});
                    var reset=await Request("reset-a");
                    state=await Wait(value=>value["appDomains"]!.AsArray().All(domain=>(string?)domain!["appDomainId"]!=first),"old domain unloaded");
                    string replacement=Domain(state,(int)reset["domains"]!["a"]!);
                    ObservationSuite.Require(replacement!=first && replacement!=second && state["modules"]!.AsArray().All(module=>!oldModules.Contains((string)module!["moduleId"]!)),"Domain recreation invalidates old domain and modules.");
                    ObservationSuite.Require((string?)state["breakpoints"]!.AsArray().Single(bp=>(string?)bp!["breakpointId"]==(string?)scoped["breakpointId"])!["state"]=="pending","Old scoped breakpoint stays pending.");
                    await Call("status",new(){["appDomainId"]=first},"invalid_request");
                    state=await Trigger(url+"?action=run-a");
                    ObservationSuite.Require((string?)state["stop"]?["appDomainId"]==replacement,"Unscoped breakpoint rebinds and hits rebuilt domain.");
                    stack=await Call("stack",new(){["threadId"]=(int)state["stop"]!["threadId"]!});
                    await Call("variables",new(){["frameId"]=frame},"frame_not_found");
                    await Call("variables",new(){["frameId"]=(string)stack[0]!["frameId"]!,["referenceId"]=reference},"value_unavailable");
                    await Resume();
                    await Call("remove_breakpoint",new(){["breakpointId"]=(string)global["breakpointId"]!});
                    await Call("remove_breakpoint",new(){["breakpointId"]=(string)scoped["breakpointId"]!});
                    await Request("domains-clear");
                    await Request("memory");
                    state=await Wait(value=>value["modules"]!.AsArray().Any(module=>((string?)module!["name"])?.Contains("FxDbg.Stage34.Memory")==true),"in-memory module enumerated");
                    var memory=state["modules"]!.AsArray().Single(module=>((string?)module!["name"])?.Contains("FxDbg.Stage34.Memory")==true)!;
                    ObservationSuite.Require((string?)memory["symbolStatus"]!="loaded" && !string.IsNullOrWhiteSpace((string?)memory["diagnostic"]),"Pure-memory module explicitly lacks disk symbols without crashing session.");

                    // Keep one actual request in the old worker so normal overlapped recycling is observable.
                    pending=http.GetStringAsync(url+"?action=hold",token);
                    var activeLimit=DateTimeOffset.UtcNow.AddSeconds(10);
                    JsonNode active;
                    while((int)(active=await Request())["activeHolds"]! <1 || (int)active["pid"]! !=pid)
                    { if(DateTimeOffset.UtcNow>=activeLimit) throw new InvalidOperationException("Hold request was not active in the old worker."); await Task.Delay(50,token); }
                    await Command("recycle","apppool","/apppool.name:"+pool);
                    JsonNode fresh; var recycleLimit=DateTimeOffset.UtcNow.AddSeconds(15);
                    do { fresh=await Request(); if((int)fresh["pid"]! !=pid) break;await Task.Delay(50,token); } while(DateTimeOffset.UtcNow<recycleLimit);
                    int nextPid=(int)fresh["pid"]!; ObservationSuite.Require(nextPid!=pid,"Normal recycle starts a new worker.");
                    int[] workers=XDocument.Parse(await Command("list","wp","/apppool.name:"+pool,"/xml")).Descendants("WP").Select(wp=>int.Parse(wp.Attribute("WP.NAME")!.Value)).ToArray();
                    ObservationSuite.Require(workers.Contains(pid) && workers.Contains(nextPid),"Old and new pool workers coexist while an old request drains.");
                    state=await Call("status"); ObservationSuite.Require((int)state["target"]!["processId"]! ==pid,"Existing session never transfers to the new PID.");
                    File.WriteAllText(release,"release");
                    var drained=JsonNode.Parse(await pending)!;pending=null;
                    ObservationSuite.Require((int)drained["pid"]! ==pid && (int)drained["lifecycleResult"]! ==85,"Old worker completes its held request before exit.");
                    await Wait(value=>(string?)value["target"]?["sessionState"]=="terminated","old worker exited",35);
                    await Call("threads",null,"invalid_session_state");
                    string previousSession=session; await Attach(nextPid);
                    ObservationSuite.Require(session!=previousSession,"Recycled worker requires an explicit fresh session.");
                    await Call("pause"); await Call("detach");
                    ObservationSuite.Require((int)(await Request())["pid"]! ==nextPid,"New worker serves requests after safe detach.");
                    cases.Add(new JsonObject{["architecture"]=(string)resource["architecture"]!,["oldPid"]=pid,["newPid"]=nextPid,["overlapCandidates"]=new JsonArray(workers.Select(value=>(JsonNode?)JsonValue.Create(value)).ToArray()),["settingsBefore"]=System.Text.Json.JsonSerializer.SerializeToNode(baseline),["snapshots"]=snapshots});
                }
                finally
                {
                    try
                    {
                        File.WriteAllText(release,"cleanup");
                        await connection.DisposeAsync();
                        if(pending is not null) { try { await pending; } catch { /* Failed request is not cleanup evidence. */ } }
                    }
                    finally
                    {
                        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await ServiceSuite.Command(appcmd,new[]{"set","apppool",pool}.Concat(baseline.Select(pair=>"/"+pair.Key+":"+pair.Value)).ToArray(),root,cleanup.Token);
                        foreach(var pair in baseline)
                            ObservationSuite.Require((await ServiceSuite.Command(appcmd,new[]{"list","apppool",pool,"/text:"+pair.Key},root,cleanup.Token)).Trim()==pair.Value,"Owned pool setting restored: "+pair.Key);
                        File.Delete(release);
                    }
                }
            }
            passed=true;
        }
        finally { File.WriteAllText(Path.Combine(root,$"artifacts/stage3-4-validation/iis-lifecycle-{configuration}.json"),new JsonObject{["passed"]=passed,["cases"]=cases,["atUtc"]=DateTimeOffset.UtcNow.ToString("o")}.ToJsonString()); }
    }
}
