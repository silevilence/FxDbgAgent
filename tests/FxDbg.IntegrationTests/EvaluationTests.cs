using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using FxDbg.Core.Execution;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunEvaluation(string root, string configuration)
    {
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root,"tests","Debuggees",name,"bin",configuration,"net40",name+".exe");
        string source = Path.Combine(root,"tests","Debuggees","Fx40.ModuleLifecycle","Program.cs");
        int line = File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(x=>x.text.Contains("// EVALUATION_BREAKPOINT")).index+1;
        using var session = new FrameworkDebuggerBootstrap().Launch("\""+target+"\" --evaluation",Path.GetDirectoryName(target)!,IntPtr.Zero,
            IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64,true,TimeSpan.FromSeconds(15));
        using var process = Process.GetProcessById(session.Target.ProcessId); _ = process.Handle;
        try
        {
            session.SetBreakpoint(new SourceLocation(source,line)); session.Continue(); var stop=session.WaitForStop(TimeSpan.FromSeconds(15));
            FrameId frame=session.GetStack(stop.ThreadId,0,1).Single().FrameId;
            VariableInfo local=session.GetVariables(frame,maxDepth:0).Single(x=>x.Name=="localNumber");
            if(local.Status==VariableStatus.Available) Require(session.Evaluate(frame,"localNumber",1000).DisplayValue==local.DisplayValue,"Local expression agrees with variable model.");
            else ExpectError(local.Status==VariableStatus.OptimizedAway?FxDbgErrorCode.ValueOptimizedAway:FxDbgErrorCode.ValueUnavailable,()=>session.Evaluate(frame,"localNumber",1000));
            var pairing=(ContinueStopCoordinator)typeof(FrameworkDebugSession).GetField("callbackPairing",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
            long before=pairing.ContinueCount, stops=pairing.StopCount;
            string[] expressions={"number + 2", "message[15]", "message.Length", "node.Label", "node.Counter", "node.Self == node", "node.Empty == null",
                "matrix[1,1]", "matrix.Length", "Array.GetLength(matrix,1)", "shifted[-1,7]", "exact != 9007199254740992L", "String.Concat(message, node.Label)", "Math.Max(number, 7)", "userCodeCalls"};
            string[] expected={"44","p","16","node-label","777","true","true","40","4","2","99","true","abcdefghijklmnopnode-label","42","0"};
            for(int i=0;i<expressions.Length;i++)
            {
                VariableInfo value=session.Evaluate(frame,expressions[i],1000);
                Require(value.DisplayValue==expected[i],"Evaluation mismatch: "+expressions[i]+" => "+value.DisplayValue);
            }
            foreach (var pair in new System.Collections.Generic.Dictionary<string,string>
            {
                ["number == 42 ? 0xFF & 3 : 1 / 0"]="3", ["(byte)257"]="1", ["(int)0xffffffff"]="-1",
                ["~number"]="-43", ["number << 2"]="168", ["fallbackNumber"]="17", ["this.number"]="900",
                ["String.Substring(message,2,3)"]="cde", ["String.IndexOf(message,\"p\")"]="15",
                ["String.Contains(message,\"ijk\")"]="true", ["String.StartsWith(message,\"abc\")"]="true",
                ["String.EndsWith(message,\"nop\")"]="true", ["String.Trim(\" a \")"]="a",
                ["String.CompareOrdinal(message,message)"]="0", ["String.IsNullOrWhiteSpace(\" \")"]="true",
                ["Math.Round(2.5)"]="2", ["Math.Floor(-1.2)"]="-2", ["Math.Ceiling(-1.2)"]="-1",
                ["Math.Truncate(-1.2)"]="-1", ["Math.Sqrt(9)"]="3", ["Math.Sign(number)"]="1",
                ["Math.Clamp(number,0,10)"]="10", ["Array.IndexOf(Values,20)"]="1", ["Array.IndexOf(Values,20L)"]="-1",
                ["Array.IndexOf(ShiftedVector,0)"]="-2", ["Array.IndexOf(ShiftedVector,1)"]="-3"
            }) Require(session.Evaluate(frame,pair.Key,1000).DisplayValue==pair.Value,"Extension result: "+pair.Key);
            ExpectError(FxDbgErrorCode.ExpressionTypeError,()=>session.Evaluate(frame,"Array.IndexOf(matrix,40)",1000));
            foreach (var pair in new System.Collections.Generic.Dictionary<string,string>
            {
                ["List.Count"]="3", ["Queue.Count"]="2", ["Stack.Count"]="1", ["Properties.Auto"]="19", ["Properties.Pure"]="23",
                ["Virtual.Value"]="23", ["Properties.Static"]="37", ["Properties.Constant"]="7", ["Properties.Flag"]="true",
                ["Properties.Letter"]="Z", ["Properties.Long"]="1234567890123", ["Properties.Single"]="1.5", ["Properties.Double"]="2.5",
                ["Properties.Null == null"]="true", ["Properties.Shadow"]="43", ["Struct.Pure"]="31", ["Generic.Pure"]="generic"
            })
            {
                Require(session.Evaluate(frame,pair.Key,1000).DisplayValue==pair.Value,"Proved property result: "+pair.Key);
            }
            foreach (string rejected in new[] { "Dictionary.Count", "Properties.Computed", "Properties.SideEffect", "Properties.WithFinally", "Properties.Cold", "ComputedVirtual.Value", "Properties.Item", "Properties.Explicit", "Generic.OtherInstantiation" })
                ExpectError(FxDbgErrorCode.ExpressionNameNotFound,()=>session.Evaluate(frame,rejected,1000));
            foreach (var diagnostic in new[] { ("Properties.Puer", "Pure"), ("Properties.pure", "Pure"), ("node.Labl", "Label"), ("Properties.Computed", "Computed") })
            {
                try { session.Evaluate(frame,diagnostic.Item1,1000); throw new InvalidOperationException("Missing expected diagnostic."); }
                catch (FxDbgException error)
                {
                    Require(error.Code==FxDbgErrorCode.ExpressionNameNotFound && error.Message.Contains("Candidate members: "+diagnostic.Item2),"Metadata suggestion and frozen error code.");
                    Require(error.Message.Contains("Getter cannot be proved read-only and will not be called.") && !error.Message.Contains(diagnostic.Item1) && !error.Message.Contains("node-label") && !error.Message.Contains("FxDbg.Interop"),"Only metadata names and stable reasons may enter diagnostics.");
                }
            }
            string Balanced(int first, int count) => count == 1 ? "Many.P"+first : "("+Balanced(first,count/2)+"+"+Balanced(first+count/2,count-count/2)+")";
            // Warm only the type catalogue (no getter proofs), so this case isolates the IL cap.
            Require(session.Evaluate(frame,"Many.Field",1000).DisplayValue=="1","Metadata-only catalogue warmup.");
            using var ilBudget=new EvaluationBudget(1000);
            ExpectError(FxDbgErrorCode.ExpressionLimitExceeded,()=>session.Evaluate(frame,Balanced(0,40),budget:ilBudget));
            Require((int)typeof(EvaluationBudget).GetField("ilBytes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(ilBudget)! ==252,"Real proof must hit cumulative IL budget before reading the next getter.");
            using var metadataBudget=new EvaluationBudget(1000);
            ExpectError(FxDbgErrorCode.ExpressionLimitExceeded,()=>session.Evaluate(frame,"Huge.Missing",budget:metadataBudget));
            Require((int)typeof(EvaluationBudget).GetField("metadataProbes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(metadataBudget)! ==257,"Real metadata traversal must stop before probe 257 executes.");
            using var repeatedRoots=new EvaluationBudget(1000);
            Require(session.Evaluate(frame,string.Join("+",Enumerable.Repeat("number",16)),budget:repeatedRoots).DisplayValue=="672","Repeated roots retain their values.");
            Require((int)typeof(EvaluationBudget).GetField("metadataProbes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(repeatedRoots)! < 10,"Repeated root names must not repeat metadata scans.");
            using var shortCircuit=new EvaluationBudget(1000);
            Require(session.Evaluate(frame,"false && missing",budget:shortCircuit).DisplayValue=="false","Short-circuit result.");
            Require((int)typeof(EvaluationBudget).GetField("metadataProbes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shortCircuit)! == 0,"Skipped names must not inspect frame metadata.");
            var node=session.Evaluate(frame,"node",1000,maxDepth:0);
            Require(node.ReferenceId is not null && session.GetVariables(frame,node.ReferenceId).Any(x=>x.Name=="Label" && x.DisplayValue=="node-label"),"Evaluation object references reuse variable paging.");
            ExpectError(FxDbgErrorCode.ExpressionForbidden,()=>session.Evaluate(frame,"node.ToString()",1000));
            ExpectError(FxDbgErrorCode.ExpressionNameNotFound,()=>session.Evaluate(frame,"node.Dangerous",1000));
            ExpectError(FxDbgErrorCode.ExpressionForbidden,()=>session.Evaluate(frame,"node.Counter = 1",1000));
            ExpectError(FxDbgErrorCode.ExpressionSyntaxError,()=>session.Evaluate(frame,"1 +",1000));
            ExpectError(FxDbgErrorCode.ExpressionIndexOutOfRange,()=>session.Evaluate(frame,"matrix[2,0]",1000));
            ExpectError(FxDbgErrorCode.ExpressionIndexOutOfRange,()=>session.Evaluate(frame,"Array.GetLength(matrix,2)",1000));
            ExpectError(FxDbgErrorCode.ExpressionLimitExceeded,()=>session.Evaluate(frame,"oversized",1000));
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            ExpectError(FxDbgErrorCode.OperationCancelled,()=>session.Evaluate(frame,"number",1000,cancellationToken:cancelled.Token));
            using var expired=new EvaluationBudget(1); Thread.Sleep(5);
            ExpectError(FxDbgErrorCode.OperationTimedOut,()=>session.Evaluate(frame,"number",budget:expired));
            Require(session.Evaluate(frame,"userCodeCalls",1000).DisplayValue=="0" && session.Evaluate(frame,"node.Counter",1000).DisplayValue=="777","Forbidden calls and all errors preserve target state.");
            Require(session.CurrentStop==stop && pairing.ContinueCount==before && pairing.StopCount==stops,"Evaluation must neither resume nor manufacture stops.");
            object readsBefore = typeof(FrameworkDebugSession).GetField("propertyReads",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
            session.Step(stop.ThreadId,StepKind.Over); session.WaitForStop(TimeSpan.FromSeconds(10));
            Require(!ReferenceEquals(readsBefore,typeof(FrameworkDebugSession).GetField("propertyReads",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)),"Property cache must expire at a new stop.");
            ExpectError(FxDbgErrorCode.FrameNotFound,()=>session.Evaluate(frame,"number",1000));
            int secondLine=File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(x=>x.text.Contains("// PROPERTY_SECOND_STOP")).index+1;
            session.SetBreakpoint(new SourceLocation(source,secondLine));
            session.Continue(); var secondStop=session.WaitForStop(TimeSpan.FromSeconds(10));
            FrameId secondFrame=session.GetStack(secondStop.ThreadId,0,1).Single().FrameId;
            Require(session.Evaluate(secondFrame,"Properties.Pure",1000).DisplayValue=="24","Property cache must never retain field values across stops.");
            session.Continue(); session.WaitForStop(TimeSpan.FromSeconds(10));
            Require(process.WaitForExit(5000) && process.ExitCode==0,"Target state oracle must confirm every state invariant after evaluation.");
            Console.WriteLine("PASS: evaluation "+configuration+" "+IntPtr.Size*8+" bit, "+expressions.Length+" exact results, errors, cancellation/timeout, object paging, no Continue and target state oracle.");
        }
        finally { if(!process.HasExited){process.Kill();process.WaitForExit(5000);} }
    }
}
