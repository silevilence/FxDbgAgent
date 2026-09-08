using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class ConditionalBreakpointSuite
{
    internal static async Task Run(string bundle,string root,string configuration)
    {
        var cases=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,"tests/Debuggees/conditional-breakpoint-cases.json")))!.AsArray();
        string source=Path.Combine(root,"tests/Debuggees/Fx40.ModuleLifecycle/ConditionalBreakpointScenarios.cs");
        int line=(await File.ReadAllLinesAsync(source)).Select((text,index)=>(text,index)).Single(x=>x.text.Contains("// CONDITIONAL_HIT")).index+1;
        foreach(string architecture in new[]{"x86","x64"})
        foreach(JsonNode test in cases!)
        {
            string name=architecture=="x86"?"Fx40.ModuleLifecycle.x86":"Fx40.ModuleLifecycle";
            string exe=Path.Combine(root,$"tests/Debuggees/{name}/bin/{configuration}/net40/{name}.exe");
            string oracle=Path.Combine(root,"artifacts/stage4-validation","conditional-mcp-"+Guid.NewGuid().ToString("N"));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var connection=await McpTestConnection.Create(bundle,timeout.Token);
            var created=await ObservationSuite.Call(connection.Client,"launch",new(){["exe"]=exe,["args"]=new[]{"--conditional-breakpoints",oracle},["stopAtEntry"]=true},timeout.Token);
            string session=(string)created["sessionId"]!;
            using var process=Process.GetProcessById((int)created["result"]!["processId"]!); _=process.Handle;
            async Task<JsonNode> Call(string method,Dictionary<string,object?>? args=null,string? error=null)
            {
                args??=new();args["sessionId"]=session;
                return (await ObservationSuite.Call(connection.Client,method,args,timeout.Token,error))[error is null?"result":"error"]!;
            }
            async Task<JsonNode> Resume(int? expectedIteration = null)
            {
                var operation=await Call("continue",new(){["waitForStop"]=false,["timeoutMs"]=30000});
                while(true)
                {
                    var status=await Call("status",new(){["operationId"]=(string)operation["operationId"]!});
                    if((string?)status["operation"]!["state"]!="running")
                    {
                        ObservationSuite.Require((string?)status["operation"]!["state"]=="completed","Conditional run completes at actual stop/exit.");
                        return status["operation"]!["stop"]!;
                    }
                    // A real stop snapshot can arrive before its event completes the operation.
                    if (status["stop"] is { } visible)
                        ObservationSuite.Require(expectedIteration.HasValue && (string?)visible["reason"]=="breakpoint" &&
                            (long?)status["breakpoints"]?[0]?["hitCount"]==expectedIteration.Value,
                            "Only the expected actual stop may be visible before operation completion: "+status);
                    await Task.Delay(10,timeout.Token);
                }
            }
            try
            {
                var parameters=new Dictionary<string,object?>{["file"]=source,["line"]=line};
                foreach(string field in new[]{"condition","hitCondition"}) if(test[field] is not null) parameters[field]=(string)test[field]!;
                var point=await Call("set_breakpoint",parameters); string id=(string)point["breakpointId"]!;
                ObservationSuite.Require((long)point["hitCount"]! == 0,"New breakpoint starts at zero.");
                await Call("set_breakpoint",new(){["file"]=source,["line"]=line,["hitCondition"]="=0"},"invalid_request");
                await Call("set_breakpoint",new(){["breakpointId"]=id,["enabled"]=true,["condition"]="false"},"invalid_request");
                foreach(JsonNode expected in test["stops"]!.AsArray()!)
                {
                    int iteration=(int)expected; var stop=await Resume(iteration);
                    ObservationSuite.Require((string?)stop["reason"]=="breakpoint"&&(string?)stop["breakpointId"]==id,"Matching logical breakpoint.");
                    var status=await Call("status"); var current=status["breakpoints"]!.AsArray().Single();
                    ObservationSuite.Require((long)current!["hitCount"]! == iteration,"Counter equals actual iteration.");
                    bool error=(bool?)test["diagnostic"]==true;
                    ObservationSuite.Require((current["conditionDiagnostic"] is not null)==error,"Fail-stop diagnostic is visible.");
                    var frames=await Call("stack",new(){["threadId"]=(int)stop["threadId"]!,["count"]=1});
                    var value=await Call("evaluate",new(){["frameId"]=(string)frames[0]!["frameId"]!,["expression"]="iteration"});
                    ObservationSuite.Require((string?)value["displayValue"]==iteration.ToString(),"Frame matches condition iteration.");
                    if(error) await Call("set_breakpoint",new(){["breakpointId"]=id,["enabled"]=false});
                }
                ObservationSuite.Require((string?)(await Resume())["reason"]=="processExit","False conditions do not complete wait before exit.");
                var final=await Call("status");
                ObservationSuite.Require((long)final["breakpoints"]![0]!["hitCount"]! == ((bool?)test["diagnostic"]==true?1:6),"Final actual hit count.");
                ObservationSuite.Require(await File.ReadAllTextAsync(oracle)=="ok","Target state and forbidden formatters unchanged.");
                Console.WriteLine($"PASS: MCP conditional {architecture} {configuration} {test["name"]}, async operation continuity, counters, diagnostics, state oracle.");
            }
            finally { if(!process.HasExited){process.Kill();await process.WaitForExitAsync();} }
        }
    }
}
