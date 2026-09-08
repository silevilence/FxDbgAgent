using System;
using System.Collections;
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
    private static void RunExceptionFilters(string root, string configuration)
    {
        const string prefix = "FxDbg.Debuggees.Filtering.";
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root,"tests","Debuggees",name,"bin",configuration,"net40",name+".exe");
        string directory = Path.Combine(root,"artifacts","stage4-validation","filters-native-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var session = new FrameworkDebuggerBootstrap().Launch("\""+target+"\" --exception-filters \""+directory+"\"",Path.GetDirectoryName(target)!,IntPtr.Zero,
            IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64,true,TimeSpan.FromSeconds(15));
        using var process = Process.GetProcessById(session.Target.ProcessId); _=process.Handle;
        var pairing=(ContinueStopCoordinator)typeof(FrameworkDebugSession).GetField("callbackPairing",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
        var queue=(ICollection)typeof(FrameworkDebugSession).GetField("callbacks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
        int peak=0;
        StopInfo Next(string type, string message, bool unhandled=false)
        {
            session.Continue(); StopInfo stop=session.WaitForStop(TimeSpan.FromSeconds(30));
            Check(stop,type,message,unhandled); return stop;
        }
        void Check(StopInfo stop,string type,string message,bool unhandled=false)
        {
            Require(stop.Reason==StopReason.Exception && stop.Exception?.TypeName==type && stop.Exception.Message==message && stop.Exception.IsUnhandled==unhandled,"Exact exception filter stop information.");
            Require(stop.Exception!.Stack.Count>0 && stop.Exception.ThrowLocation is not null && stop.Exception.ThreadId==stop.ThreadId,"Exception stack/thread/location preserved.");
            Require(pairing.StopCount==pairing.ContinueCount+1,"One native stop remains paired; hidden exceptions continued exactly once.");
        }
        void Configure(string text) => session.ConfigureExceptionStops(true,ExceptionStopConfiguration.ParseRules(text));
        void WaitGate(string gate)
        {
            var clock=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(directory,gate+"-ready")))
            {
                Require(clock.Elapsed<TimeSpan.FromSeconds(20) && session.State==FxDbg.Core.Sessions.DebugSessionState.Running,"Unmatched exceptions must not stop before gate.");
                peak=Math.Max(peak,queue.Count); session.PumpNextCallback(TimeSpan.FromMilliseconds(5),default);
            }
        }
        try
        {
            Configure("exact:"+prefix+"DerivedFailure"); Next(prefix+"DerivedFailure","exact");
            Configure("namespace:"+prefix+"Group"); Next(prefix+"Group.Failure","namespace");
            Configure("derived:"+prefix+"BaseFailure"); Next(prefix+"BaseFailure","base"); Next(prefix+"DerivedFailure","derived");
            session.Continue(); WaitGate("running"); Configure("exact:"+prefix+"Group.Failure");
            int stops=session.Events.OfType<StoppedEvent>().Count(); var clock=Stopwatch.StartNew();
            File.WriteAllText(Path.Combine(directory,"running-go"),"go");
            while(session.State==FxDbg.Core.Sessions.DebugSessionState.Running)
            {
                Require(clock.Elapsed<TimeSpan.FromSeconds(20),"High-frequency matching stop must remain bounded.");
                peak=Math.Max(peak,queue.Count); session.PumpNextCallback(TimeSpan.FromMilliseconds(5),default);
            }
            Check(session.CurrentStop!,prefix+"Group.Failure","dynamic");
            clock.Stop();
            Require(session.Events.OfType<StoppedEvent>().Count()==stops+1 && peak<=16,"No ignored stop events or callback accumulation.");
            Require(!session.ConfigureExceptionStops(true,Array.Empty<ExceptionTypeRule>()).FirstChance,"Explicit empty clears.");
            session.Continue(); WaitGate("cleared");
            Require(session.ConfigureExceptionStops(true).Rules is null,"Omitted rules retain legacy all-first-chance.");
            File.WriteAllText(Path.Combine(directory,"cleared-go"),"go");
            Check(session.WaitForStop(TimeSpan.FromSeconds(10)),"System.InvalidOperationException","legacy");
            session.ConfigureExceptionStops(false,ExceptionStopConfiguration.ParseRules("derived:"+prefix+"BaseFailure"));
            Next(prefix+"BaseFailure","unhandled",true);
            Require(File.ReadAllText(Path.Combine(directory,"oracle"))=="ok","Exception Message/ToString side effects remain zero.");
            session.Terminate(TimeSpan.FromSeconds(5)); Require(process.WaitForExit(5000),"Owned unhandled target cleanup.");
            Console.WriteLine($"PASS: exception filters {configuration} {IntPtr.Size*8} bit, exact/namespace/derived, dynamic/clear/default/legacy, 500 ignored throws {clock.ElapsedMilliseconds} ms, sampled queue peak {peak}, paired Continue and zero formatting.");
        }
        finally { if(!process.HasExited){process.Kill();process.WaitForExit(5000);} }
    }
}
