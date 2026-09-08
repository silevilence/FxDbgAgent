using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FxDbg.Core.Events;
using FxDbg.Core.Execution;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunConditionalBreakpoints(string root,string configuration)
    {
        string name=IntPtr.Size==4?"Fx40.ModuleLifecycle.x86":"Fx40.ModuleLifecycle";
        string target=Path.Combine(root,$"tests/Debuggees/{name}/bin/{configuration}/net40/{name}.exe");
        string source=Path.Combine(root,"tests/Debuggees/Fx40.ModuleLifecycle/ConditionalBreakpointScenarios.cs");
        int line=File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(x=>x.text.Contains("// CONDITIONAL_HIT")).index+1;
        var cases=new (string Name,string? Expression,string? Hits,int[] Stops,bool Error)[] {
            ("unconditional",null,null,new[]{1,2,3,4,5,6},false),("exact",null,"=3",new[]{3},false),
            ("threshold",null,">=3",new[]{3,4,5,6},false),("expression","iteration % 2 == 0 && node.Value == 7",null,new[]{2,4,6},false),
            ("combined","iteration % 2 == 0",">=3",new[]{4,6},false),("false","false && missing",null,Array.Empty<int>(),false),
            ("nonboolean","iteration",null,new[]{1},true),("error","1 / 0 == iteration",null,new[]{1},true),("getter","node.Getter > 0",null,new[]{1},true),
            ("small-overflow","Math.Abs(node.Small) < 0",null,new[]{1},true)
        };
        foreach(var test in cases)
        {
            string oracle=Path.Combine(root,"artifacts/stage4-validation","conditional-native-"+Guid.NewGuid().ToString("N"));
            using var session=new FrameworkDebuggerBootstrap().Launch("\""+target+"\" --conditional-breakpoints \""+oracle+"\"",Path.GetDirectoryName(target)!,IntPtr.Zero,
                IntPtr.Size==4?TargetArchitecture.X86:TargetArchitecture.X64,true,TimeSpan.FromSeconds(15));
            using var process=Process.GetProcessById(session.Target.ProcessId); _=process.Handle;
            try
            {
                var point=session.SetBreakpoint(new SourceLocation(source,line),condition:test.Expression,hitCondition:test.Hits);
                var pairing=(ContinueStopCoordinator)typeof(FrameworkDebugSession).GetField("callbackPairing",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
                foreach(int expected in test.Stops)
                {
                    session.Continue(); var stop=session.WaitForStop(TimeSpan.FromSeconds(15));
                    Require(stop.Reason==StopReason.Breakpoint && stop.BreakpointId==point.BreakpointId,"Only expected breakpoint stops.");
                    var current=session.GetBreakpoints().Single(); Require(current.HitCount==expected,"Actual hit count "+test.Name);
                    var frame=session.GetStack(stop.ThreadId,0,1).Single().FrameId;
                    Require(session.Evaluate(frame,"iteration").DisplayValue==expected.ToString(),"Correct conditional iteration.");
                    Require((current.ConditionDiagnostic is not null)==test.Error,"Explicit condition diagnostic policy.");
                    Require(pairing.StopCount==pairing.ContinueCount+1,"Conditional hidden stops preserve Continue pairing.");
                    if(test.Error) session.SetBreakpointEnabled(point.BreakpointId,false);
                }
                session.Continue(); Require(session.WaitForStop(TimeSpan.FromSeconds(15)).Reason==StopReason.ProcessExit,"No hidden condition stop leaks.");
                Require(session.GetBreakpoints().Single().HitCount==(test.Error?1:6),"Only enabled actual hits count, even after unload.");
                Require(session.Events.OfType<StoppedEvent>().Count(e=>e.Stop.Reason==StopReason.Breakpoint)==test.Stops.Length,"No internal stop events.");
                Require(File.ReadAllText(oracle)=="ok","Target state and forbidden formatting counters preserved.");
                Console.WriteLine($"PASS: conditional native {IntPtr.Size*8} {configuration} {test.Name}, exact stops/counters, paired Continue, state oracle.");
            }
            finally { if(!process.HasExited){process.Kill();process.WaitForExit(5000);} }
        }
        RunConditionalReload(root,configuration,target);
    }

    private static void RunConditionalReload(string root,string configuration,string target)
    {
        string directory=Path.Combine(root,"artifacts/stage4-validation","conditional-reload-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string library=Path.Combine(root,$"tests/Debuggees/Fx40.LateModule/bin/{configuration}/net40/Fx40.LateModule.dll");
        File.Copy(library,Path.Combine(directory,"Fx40.LateModule.dll")); File.Copy(Path.ChangeExtension(library,".pdb"),Path.Combine(directory,"Fx40.LateModule.pdb"));
        string source=Path.Combine(root,"tests/Debuggees/Fx40.LateModule/LateCode.cs");
        int line=File.ReadAllLines(source).Select((text,index)=>(text,index)).Single(x=>x.text.Contains("LATE_BREAKPOINT")).index+1;
        using var session=new FrameworkDebuggerBootstrap().Launch("\""+target+"\" \""+directory+"\"",Path.GetDirectoryName(target)!,IntPtr.Zero,
            IntPtr.Size==4?TargetArchitecture.X86:TargetArchitecture.X64,true,TimeSpan.FromSeconds(15));
        using var process=Process.GetProcessById(session.Target.ProcessId); _=process.Handle;
        try
        {
            var point=session.SetBreakpoint(new SourceLocation(source,line),condition:"true",hitCondition:"=2"); session.Continue();
            for(int cycle=0;cycle<3;cycle++)
            {
                PumpUntil(session,()=>File.Exists(Path.Combine(directory,"loaded-"+cycle)));
                Require(session.GetBreakpoints().Single().HitCount==cycle,"Counter survives real module unload/reload.");
                File.WriteAllText(Path.Combine(directory,"go-"+cycle),"go");
                if(cycle==1)
                {
                    Require(session.WaitForStop(TimeSpan.FromSeconds(15)).BreakpointId==point.BreakpointId,"Second load reaches logical threshold.");
                    Require(session.GetBreakpoints().Single().HitCount==2,"Second actual hit preserved.");
                    session.RefreshSymbols(); Require(session.GetBreakpoints().Single().HitCount==2,"Symbol refresh preserves counter."); session.Continue();
                }
            }
            Require(session.WaitForStop(TimeSpan.FromSeconds(15)).Reason==StopReason.ProcessExit,"Third hit does not satisfy =2.");
            Require(session.GetBreakpoints().Single().HitCount==3,"Final logical count across three loads.");
            Console.WriteLine($"PASS: conditional native {IntPtr.Size*8} {configuration}, real 3-domain reload, =2 and symbol refresh preserve counter.");
        }
        finally { if(!process.HasExited){process.Kill();process.WaitForExit(5000);} }
    }
}
