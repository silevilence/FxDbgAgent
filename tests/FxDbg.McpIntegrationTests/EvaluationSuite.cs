using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class EvaluationSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        var evidence = new JsonArray();
        string source = Path.Combine(root,"tests/Debuggees/Fx40.ModuleLifecycle/Program.cs");
        int line = File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(x=>x.text.Contains("// EVALUATION_BREAKPOINT")).index+1;
        foreach(string architecture in new[]{"x86","x64"})
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string auditDirectory=Path.Combine(root,"artifacts/stage4-validation/diagnostics-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(auditDirectory);
            string auditPath=Path.Combine(auditDirectory,"audit.jsonl");
            await using var connection = await McpTestConnection.Create(bundle,timeout.Token,arguments:["--log-level","debug","--log-file",auditPath,"--value-logs","off"]);
            string name = architecture=="x86" ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
            string exe=Path.Combine(root,$"tests/Debuggees/{name}/bin/{configuration}/net40/{name}.exe");
            var created=await ObservationSuite.Call(connection.Client,"launch",new(){["exe"]=exe,["args"]=new[]{"--evaluation"},["stopAtEntry"]=true},timeout.Token);
            string session=(string)created["sessionId"]!;
            using var process=Process.GetProcessById((int)created["result"]!["processId"]!); _=process.Handle;
            async Task<JsonNode> Call(string method,Dictionary<string,object?>? args=null,string? error=null)
            {
                args ??=new(); args["sessionId"]=session;
                return (await ObservationSuite.Call(connection.Client,method,args,timeout.Token,error))[error is null?"result":"error"]!;
            }
            try
            {
                await Call("set_breakpoint",new(){["file"]=source,["line"]=line}); await Call("continue");
                var before=await Call("status"); int thread=(int)before["stop"]!["threadId"]!;
                string frame=(string)(await Call("stack",new(){["threadId"]=thread}))[0]!["frameId"]!;
                async Task<JsonNode> Eval(string expression,string? error=null) => await Call("evaluate",new(){["frameId"]=frame,["expression"]=expression,["evaluationTimeoutMs"]=1000,["maxDepth"]=0},error);
                foreach(var pair in new Dictionary<string,string>{["number + 2"]="44",["(number & 0x1) == 0 ? Math.Clamp(number,0,10) : 0"]="10",["(byte)257"]="1",["String.Substring(message,2,3)"]="cde",["fallbackNumber"]="17",["Array.IndexOf(Values,20)"]="1",["matrix[1,1]"]="40",["shifted[-1,7]"]="99",["exact == 9007199254740993L"]="true",["node.Self == node"]="true",["String.Equals(node.Label,\"node-label\")"]="true",["List.Count"]="3",["Properties.Pure"]="23",["Virtual.Value"]="23",["Properties.Static"]="37",["Struct.Pure"]="31",["Properties.Flag"]="true",["userCodeCalls"]="0"})
                {
                    var result=await Eval(pair.Key); ObservationSuite.Require((string?)result["displayValue"]==pair.Value,"MCP evaluation result: "+pair.Key);
                    evidence.Add(new JsonObject{["configuration"]=configuration,["architecture"]=architecture,["expression"]=pair.Key,["result"]=result.DeepClone()});
                }
                var node=await Eval("node");
                var children=(await Call("variables",new(){["frameId"]=frame,["referenceId"]=(string)node["referenceId"]!})).AsArray();
                ObservationSuite.Require(children.Any(x=>(string?)x!["name"]=="Label"&&(string?)x["displayValue"]=="node-label"),"MCP evaluation result expands using variables.");
                foreach (string rejected in new[]{"Dictionary.Count","Properties.Computed","Properties.SideEffect","ComputedVirtual.Value","Properties.Item","Properties.Explicit","Properties.Cold","Generic.OtherInstantiation"}) await Eval(rejected,"expression_name_not_found");
                foreach (var diagnostic in new[]{("Properties.Puer","Pure"),("Properties.pure","Pure"),("node.Labl","Label"),("Properties.Computed","Computed")})
                {
                    var error=await Eval(diagnostic.Item1,"expression_name_not_found"); string text=(string)error["message"]!;
                    ObservationSuite.Require(text.Contains("Candidate members: "+diagnostic.Item2) && text.Contains("Getter cannot be proved read-only") && !text.Contains(diagnostic.Item1) && !text.Contains("node-label") && !text.Contains("FxDbg.Interop"),"MCP bounded metadata diagnostics, no expression/value/stack.");
                }
                await Eval("String.Concat(\"SECRET_DIAGNOSTIC_EXPRESSION\", node.Labl)","expression_name_not_found");
                await Eval("node.ToString()","expression_forbidden"); await Eval("node.Dangerous","expression_name_not_found");
                await Eval("1/0","expression_arithmetic_error"); await Eval("1+","expression_syntax_error");
                await Eval("oversized","expression_limit_exceeded"); await Eval("Array.GetLength(matrix,-1)","expression_index_out_of_range");
                await Call("evaluate",new(){["frameId"]=frame,["expression"]="1",["evaluationTimeoutMs"]=1001},"invalid_request");
                await Call("evaluate",new(){["frameId"]=frame,["expression"]=new string('x',4097)},"invalid_request");
                var after=await Call("status");
                ObservationSuite.Require(before["stop"]!.ToJsonString()==after["stop"]!.ToJsonString() && (string?)after["target"]!["sessionState"]=="stopped","No evaluation stop leaks.");
                await Call("step",new(){["threadId"]=thread,["kind"]="over"}); await Eval("number","frame_not_found");
                await Call("continue"); await process.WaitForExitAsync(timeout.Token);
                ObservationSuite.Require(process.ExitCode==0,"MCP target confirms unchanged state and zero side effects.");
                await connection.DisposeAsync();
                string diagnostics=connection.StandardError+File.ReadAllText(auditPath);
                foreach(string forbidden in new[]{"SECRET_DIAGNOSTIC_EXPRESSION","node-label","abcdefghijklmnop","Properties.Puer","node.Labl","FxDbg.Interop"})
                    ObservationSuite.Require(!diagnostics.Contains(forbidden),"Evaluation audit/stderr must not leak expressions, values or internal stacks.");
                Console.WriteLine($"PASS: MCP evaluation {architecture} {configuration}, values, paging, rejection, stale frame, state oracle.");
            }
            finally { if(!process.HasExited){process.Kill();await process.WaitForExitAsync();} }
        }
        string directory=Path.Combine(root,"artifacts/stage4-validation"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,$"evaluation-mcp-{configuration}.json"),evidence.ToJsonString());
    }
}
