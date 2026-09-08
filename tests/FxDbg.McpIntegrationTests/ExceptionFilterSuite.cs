using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class ExceptionFilterSuite
{
    internal static async Task Run(string bundle,string root,string configuration)
    {
        const string prefix="FxDbg.Debuggees.Filtering.";
        var evidence=new JsonArray();
        foreach(string architecture in new[]{"x86","x64"})
        {
            string name=architecture=="x86"?"Fx40.ModuleLifecycle.x86":"Fx40.ModuleLifecycle";
            string exe=Path.Combine(root,$"tests/Debuggees/{name}/bin/{configuration}/net40/{name}.exe");
            string directory=Path.Combine(root,"artifacts/stage4-validation","filters-mcp-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await using var connection=await McpTestConnection.Create(bundle,timeout.Token);
            var created=await ObservationSuite.Call(connection.Client,"launch",new(){["exe"]=exe,["args"]=new[]{"--exception-filters",directory},["stopAtEntry"]=true},timeout.Token);
            string session=(string)created["sessionId"]!;
            using var process=Process.GetProcessById((int)created["result"]!["processId"]!); _=process.Handle;
            async Task<JsonNode> Call(string method,Dictionary<string,object?>? args=null,string? error=null)
            {
                args??=new(); args["sessionId"]=session;
                return (await ObservationSuite.Call(connection.Client,method,args,timeout.Token,error))[error is null?"result":"error"]!;
            }
            async Task Configure(string kind,string type) => await Call("configure_exceptions",new(){["firstChance"]=true,["rules"]=new[]{new{kind,typeName=type}}});
            void Check(JsonNode stop,string type,string message,bool unhandled=false)
            {
                ObservationSuite.Require((string?)stop["reason"]=="exception"&&(string?)stop["exception"]!["typeName"]==type &&
                    (string?)stop["exception"]!["message"]==message&&(bool?)stop["exception"]!["isUnhandled"]==unhandled,"Correct filtered stop: "+stop);
                ObservationSuite.Require(stop["exception"]!["throwLocation"] is JsonObject && stop["exception"]!["stack"]!.AsArray().Count>0,"Exception source and stack remain present.");
                evidence.Add(new JsonObject{["architecture"]=architecture,["configuration"]=configuration,["stop"]=stop.DeepClone()});
            }
            async Task Next(string type,string message,bool unhandled=false)
                => Check((await Call("continue",new(){["timeoutMs"]=30000}))["stop"]!,type,message,unhandled);
            async Task Gate(string gate)
            { while(!File.Exists(Path.Combine(directory,gate+"-ready"))) await Task.Delay(10,timeout.Token); }
            async Task WaitOperation(string id,string type,string message)
            {
                while(true)
                {
                    var state=await Call("status",new(){["operationId"]=id});
                    if((string?)state["operation"]!["state"]!="running")
                    {
                        ObservationSuite.Require((string?)state["operation"]!["state"]=="completed","Run completes at an actual matching stop.");
                        Check(state["operation"]!["stop"]!,type,message); return;
                    }
                    await Task.Delay(10,timeout.Token);
                }
            }
            try
            {
                await Configure("exact",prefix+"DerivedFailure"); await Next(prefix+"DerivedFailure","exact");
                await Configure("namespace",prefix+"Group"); await Next(prefix+"Group.Failure","namespace");
                await Configure("derived",prefix+"BaseFailure"); await Next(prefix+"BaseFailure","base"); await Next(prefix+"DerivedFailure","derived");
                string operation=(string)(await Call("continue",new(){["waitForStop"]=false,["timeoutMs"]=30000}))["operationId"]!;
                await Gate("running"); await Configure("exact",prefix+"Group.Failure");
                await Call("configure_exceptions",new(){["firstChance"]=true,["rules"]=new[]{new{kind="invalid",typeName="X"}}},"invalid_request");
                await Call("configure_exceptions",new(){["firstChance"]=true,["rules"]=new[]{new{kind="exact",typeName="X*"}}},"invalid_request");
                var before=await Call("status",new(){["operationId"]=operation});
                ObservationSuite.Require((string?)before["operation"]!["state"]=="running"&&before["stop"] is null,"Dynamic configuration does not complete or stop an operation.");
                var clock=Stopwatch.StartNew(); File.WriteAllText(Path.Combine(directory,"running-go"),"go");
                await WaitOperation(operation,prefix+"Group.Failure","dynamic"); clock.Stop();
                ObservationSuite.Require(clock.Elapsed<TimeSpan.FromSeconds(20),"500 ignored exceptions remain bounded: "+clock.ElapsedMilliseconds+" ms.");
                var cleared=await Call("configure_exceptions",new(){["firstChance"]=true,["rules"]=Array.Empty<object>()});
                ObservationSuite.Require((bool?)cleared["firstChance"]==false,"Explicit empty disables first-chance.");
                operation=(string)(await Call("continue",new(){["waitForStop"]=false,["timeoutMs"]=30000}))["operationId"]!;
                await Gate("cleared");
                var running=await Call("status",new(){["operationId"]=operation});
                ObservationSuite.Require((string?)running["operation"]!["state"]=="running"&&running["stop"] is null,"Ignored exception does not complete waitForStop.");
                var legacy=await Call("configure_exceptions",new(){["firstChance"]=true});
                ObservationSuite.Require(legacy["rules"] is null,"Omitted rules preserve all-first-chance.");
                File.WriteAllText(Path.Combine(directory,"cleared-go"),"go"); await WaitOperation(operation,"System.InvalidOperationException","legacy");
                await Call("configure_exceptions",new(){["firstChance"]=false}); await Next(prefix+"BaseFailure","unhandled",true);
                ObservationSuite.Require(File.ReadAllText(Path.Combine(directory,"oracle"))=="ok","Zero target formatting side effects.");
                await Call("terminate"); await process.WaitForExitAsync(timeout.Token);
                Console.WriteLine($"PASS: MCP exception filters {architecture} {configuration}, rule relations, running update, clear/default/legacy, operation continuity, 500 ignored throws {clock.ElapsedMilliseconds} ms and zero formatting.");
            }
            finally { if(!process.HasExited){process.Kill();await process.WaitForExitAsync();} }
        }
        await File.WriteAllTextAsync(Path.Combine(root,$"artifacts/stage4-validation/exception-filters-mcp-{configuration}.json"),evidence.ToJsonString());
    }
}
