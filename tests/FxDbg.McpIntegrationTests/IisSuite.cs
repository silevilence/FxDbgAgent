using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Xml.Linq;

internal static class IisSuite
{
    internal static async Task Run(string bundle,string root,string configuration,string manifest)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token=deadline.Token;
        using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(90) };
        var evidence=new JsonObject { ["passed"]=false,["configuration"]=configuration,["cases"]=new JsonArray() };
        var cases=evidence["cases"]!.AsArray();
        string source=Path.GetFullPath(Path.Combine(root,"tests/Debuggees/Fx40.Environment.Web/Health.cs"));
        string page=Path.GetFullPath(Path.Combine(root,"tests/Debuggees/Fx40.Environment.Web/health.aspx"));
        int line=File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(row=>row.text.Contains("// ENV_WEB_CALL")).index+1;
        try
        {
            foreach(var resource in JsonNode.Parse(File.ReadAllText(manifest))!["resources"]!.AsArray())
            {
                string pool=(string)resource!["pool"]!, url=(string)resource["url"]!, webRoot=(string)resource["webRoot"]!;
                ObservationSuite.Require(pool.StartsWith("FxDbgStage34-iis-",StringComparison.Ordinal),"Only isolated IIS fixtures may be changed.");
                var health=JsonNode.Parse(await http.GetStringAsync(url,token))!;
                int pid=(int)health["pid"]!;
                string loadedPath=(string)health["assemblyPath"]!;
                ObservationSuite.Require((bool)health["shadowCopy"]! && !loadedPath.StartsWith(webRoot,StringComparison.OrdinalIgnoreCase),"Actual IIS worker uses a shadow copy.");
                string appcmd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32/inetsrv/appcmd.exe");
                string workers=await ServiceSuite.Command(appcmd,new[]{"list","wp","/apppool.name:"+pool,"/xml"},root,token);
                int[] candidates=XDocument.Parse(workers).Descendants("WP").Select(worker=>int.Parse(worker.Attribute("WP.NAME")!.Value)).ToArray();
                ObservationSuite.Require(candidates.Contains(pid),"Prewarmed request PID belongs to the selected application pool.");
                object[] mappings={
                    new {buildRoot=webRoot,localRoot=Path.GetDirectoryName(page)},
                    // ASP.NET pages were compiled in deployment, but the business DLL
                    // retains its repository PDB paths. Its exact module mapping wins.
                    new {buildRoot=Path.GetDirectoryName(source),localRoot=Path.GetDirectoryName(source),module="Fx40.Environment.Web.dll"}
                };
                async Task<McpTestConnection> Connect() => await McpTestConnection.Create(bundle,token);
                async Task<string> Attach(McpTestConnection connection)
                {
                    var attached=await ObservationSuite.Call(connection.Client,"attach",new(){["pid"]=pid,["sourceMappings"]=mappings},token);
                    ObservationSuite.Require((string?)attached["result"]!["architecture"]==(string?)resource["architecture"],"Worker architecture routes to the matching Engine.");
                    return (string)attached["sessionId"]!;
                }
                async Task<JsonNode> DebugRequest(string file,int targetLine,bool inspectVariables)
                {
                    await using var connection=await Connect(); string session=await Attach(connection);
                    async Task<JsonNode> Call(string method,Dictionary<string,object?>? args=null)
                    {args??=new();args["sessionId"]=session;return (await ObservationSuite.Call(connection.Client,method,args,token))["result"]!;}
                    var bp=await Call("set_breakpoint",new(){["file"]=file,["line"]=targetLine});
                    File.WriteAllText(Path.Combine(root,$"artifacts/stage3-4-validation/iis-last-breakpoint-{resource["architecture"]}.json"),bp.ToJsonString());
                    Task<string> request=http.GetStringAsync(url,token);
                    try
                    {
                        JsonNode state;
                        do
                        {
                            state=await Call("status");
                            if((string?)state["target"]?["sessionState"]!="stopped")
                            {
                                if(request.IsCompleted)
                                {
                                    await request;
                                    File.WriteAllText(Path.Combine(root,$"artifacts/stage3-4-validation/iis-unhit-{resource["architecture"]}.json"),state.ToJsonString());
                                    throw new InvalidOperationException("HTTP request completed without stopping at " + file + ":" + targetLine + "; inspect iis-unhit evidence.");
                                }
                                await Task.Delay(20,token);
                            }
                        }
                        while((string?)state["target"]?["sessionState"]!="stopped");
                        ObservationSuite.Require((string?)state["stop"]?["reason"]=="breakpoint","HTTP work hit the requested source breakpoint.");
                        var stack=(await Call("stack",new(){["threadId"]=(int)state["stop"]!["threadId"]!})).AsArray();
                        var location=stack[0]!["sourceLocation"]!;
                        ObservationSuite.Require((int)location["line"]! == targetLine && string.Equals((string?)location["filePath"],file,StringComparison.OrdinalIgnoreCase),"IIS stack maps to the exact local source document: "+location);
                        if(inspectVariables)
                        {
                            var vars=(await Call("variables",new(){["frameId"]=(string)stack[0]!["frameId"]!})).AsArray();
                            ObservationSuite.Require((string?)vars.Single(value=>(string?)value!["name"]=="input")!["displayValue"]=="42","IIS parameter is 42.");
                            ObservationSuite.Require((string?)vars.Single(value=>(string?)value!["name"]=="doubled")!["displayValue"]=="84","IIS local is 84.");
                        }
                        string moduleName=(string)stack[0]!["moduleName"]!;
                        var module=state["modules"]!.AsArray().Single(item=>string.Equals((string?)item!["path"],moduleName,StringComparison.OrdinalIgnoreCase) && (string?)item["appDomainId"]==(string?)stack[0]!["appDomainId"]);
                        ObservationSuite.Require((string?)module!["symbolStatus"]=="loaded","Loaded Windows PDB is associated with the stopped module.");
                        if(inspectVariables)
                        {
                            await Call("continue",new(){["waitForStop"]=false});
                            await request;
                        }
                        await Call("detach");
                        var response=JsonNode.Parse(await request)!;
                        ObservationSuite.Require((int)response["pid"]! == pid && (int)response["result"]! == 85,"Detaching resumes the paused HTTP request in the same worker.");
                        return new JsonObject { ["source"]=file,["line"]=targetLine,["module"]=module.DeepClone(),["breakpoint"]=bp.DeepClone() };
                    }
                    finally
                    {
                        // Dispose sends EOF and detaches before awaiting an unfinished HTTP request.
                        await connection.DisposeAsync();
                        try { await request; } catch when(token.IsCancellationRequested) { }
                    }
                }
                var business=await DebugRequest(source,line,true);
                var dynamicPage=await DebugRequest(page,2,false);
                string pdbPath=Path.ChangeExtension(loadedPath,".pdb");
                string expectedAssembly=Path.Combine(webRoot,"bin/Fx40.Environment.Web.dll");
                ObservationSuite.Require(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loadedPath)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(expectedAssembly))),"Only the verified fixture's actual shadow-copy symbol file may be changed.");
                byte[] originalPdb=File.ReadAllBytes(pdbPath);
                DateTime originalTimestamp=File.GetLastWriteTimeUtc(pdbPath);
                var pdbFile=new FileInfo(pdbPath);
                string originalAcl=pdbFile.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                void RestoreAcl()
                {
                    var security=new FileSecurity();
                    security.SetSecurityDescriptorSddlForm(originalAcl,AccessControlSections.Access);
                    pdbFile.SetAccessControl(security);
                }
                var transitions=new JsonArray();
                try
                {
                    foreach(bool denied in new[]{false,true})
                    {
                        if(denied)
                        {
                            var security=pdbFile.GetAccessControl();
                            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.Read,AccessControlType.Deny));
                            pdbFile.SetAccessControl(security);
                        }
                        else File.Delete(pdbPath); // Exact fixture PDB, restored below; no directory deletion.
                        await using var connection=await Connect(); string session=await Attach(connection);
                        async Task<JsonNode> Call(string method,Dictionary<string,object?>? args=null)
                        {args??=new();args["sessionId"]=session;return (await ObservationSuite.Call(connection.Client,method,args,token))["result"]!;}
                        var pending=await Call("set_breakpoint",new(){["file"]=source,["line"]=line});
                        async Task<JsonNode> SymbolState(string expected)
                        {
                            var limit=DateTimeOffset.UtcNow.AddSeconds(15);
                            JsonNode? last=null;
                            do
                            {
                                var state=await Call("status");
                                last=state["modules"]!.AsArray().SingleOrDefault(item=>string.Equals((string?)item!["path"],loadedPath,StringComparison.OrdinalIgnoreCase));
                                if((string?)last?["symbolStatus"]==expected)
                                {
                                    ObservationSuite.Require(string.Equals((string?)last["pdbPath"],pdbPath,StringComparison.OrdinalIgnoreCase),"Symbol candidate is bounded to the actual loaded module's adjacent PDB.");
                                    transitions.Add(last.DeepClone());
                                    return state;
                                }
                                await Task.Delay(50,token);
                            } while(DateTimeOffset.UtcNow<limit);
                            throw new InvalidOperationException("Expected automatic symbol state "+expected+"; actual "+last);
                        }
                        await SymbolState(denied?"readFailed":"missing");
                        ObservationSuite.Require((string?)pending["state"]=="pending","Unavailable PDB cannot verify a breakpoint.");
                        if(denied)
                        {
                            RestoreAcl();
                            ObservationSuite.Require(File.GetLastWriteTimeUtc(pdbPath)==originalTimestamp,"ACL recovery does not change PDB metadata.");
                        }
                        else
                        {
                            File.Copy(Path.Combine(root,$"tests/Debuggees/Fx40.Environment.Late/bin/{configuration}/net40/Fx40.Environment.Late.pdb"),pdbPath);
                            await SymbolState("mismatch");
                            File.WriteAllBytes(pdbPath,originalPdb);
                            File.SetLastWriteTimeUtc(pdbPath,originalTimestamp);
                        }
                        var ready=await SymbolState("loaded");
                        var rebound=ready["breakpoints"]!.AsArray().Single(item=>(string?)item!["breakpointId"]==(string?)pending["breakpointId"]);
                        ObservationSuite.Require((string?)rebound!["state"] is "verified" or "moved","PDB readiness automatically rebinds the pending breakpoint.");
                        Task<string> request=http.GetStringAsync(url,token);
                        try
                        {
                            JsonNode state;
                            do
                            {
                                state=await Call("status");
                                if((string?)state["target"]?["sessionState"]!="stopped")
                                {
                                    if(request.IsCompleted) { await request; throw new InvalidOperationException("Recovered HTTP request missed its breakpoint."); }
                                    await Task.Delay(20,token);
                                }
                            }
                            while((string?)state["target"]?["sessionState"]!="stopped");
                            ObservationSuite.Require((string?)state["stop"]?["reason"]=="breakpoint","Recovered PDB binds a real HTTP breakpoint.");
                            await Call("detach");
                            ObservationSuite.Require((int)JsonNode.Parse(await request)!["result"]! == 85,"Recovered request resumes on detach.");
                        }
                        finally { await connection.DisposeAsync(); try { await request; } catch when(token.IsCancellationRequested) { } }
                    }
                }
                finally
                {
                    if(File.Exists(pdbPath)) RestoreAcl();
                    File.WriteAllBytes(pdbPath,originalPdb);
                    RestoreAcl();
                    File.SetLastWriteTimeUtc(pdbPath,originalTimestamp);
                }
                cases.Add(new JsonObject { ["architecture"]=(string)resource["architecture"]!,["pool"]=pool,["pid"]=pid,["workerCandidates"]=new JsonArray(candidates.Select(value=>(JsonNode?)JsonValue.Create(value)).ToArray()),["shadowPath"]=loadedPath,["business"]=business,["dynamicPage"]=dynamicPage,["symbolTransitions"]=transitions });
            }
            evidence["passed"]=true;
        }
        catch(Exception error) { evidence["error"]=error.ToString(); throw; }
        finally { evidence["atUtc"]=DateTimeOffset.UtcNow.ToString("o");File.WriteAllText(Path.Combine(root,$"artifacts/stage3-4-validation/iis-{configuration}.json"),evidence.ToJsonString()); }
    }
}
