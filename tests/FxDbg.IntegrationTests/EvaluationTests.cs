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
            session.Step(stop.ThreadId,StepKind.Over); session.WaitForStop(TimeSpan.FromSeconds(10));
            ExpectError(FxDbgErrorCode.FrameNotFound,()=>session.Evaluate(frame,"number",1000));
            session.Continue(); session.WaitForStop(TimeSpan.FromSeconds(10));
            Require(process.WaitForExit(5000) && process.ExitCode==0,"Target state oracle must confirm every state invariant after evaluation.");
            Console.WriteLine("PASS: evaluation "+configuration+" "+IntPtr.Size*8+" bit, "+expressions.Length+" exact results, errors, cancellation/timeout, object paging, no Continue and target state oracle.");
        }
        finally { if(!process.HasExited){process.Kill();process.WaitForExit(5000);} }
    }
}
