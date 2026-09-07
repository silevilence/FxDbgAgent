using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;

string root=args[0];
string architecture=args.Length>1?args[1]:"x86";
string evidence=Path.Combine(root,"artifacts/stage3-4-validation/entry-audit");
string engineRoot=Path.Combine(root,"src/FxDbg.Engine/bin/Debug/net48");
for(int cycle=0;cycle<200;cycle++)
{
    using var host=new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(),new ProcessArchitectureDetector()),
        new EngineProcessPaths(Path.Combine(engineRoot,"FxDbg.Engine.x86.exe"),Path.Combine(engineRoot,"FxDbg.Engine.x64.exe")));
    string observation=Path.Combine(evidence,Guid.NewGuid().ToString("N")+".txt");
    var request=new LaunchRequest(SessionId.New(),Path.Combine(root,"tests/Debuggees/Fx40.Console."+architecture+"/bin/Debug/net40/Fx40.Console."+architecture+".exe"),
        new List<string>{"--launch-observation",observation,"entry-audit"},null,null,TargetArchitecture.Auto,true,TimeSpan.FromSeconds(10));
    DebugTargetInfo target=await host.LaunchAsync(request,CancellationToken.None);
    using var engine=Process.GetProcessById(host.GetEngineProcessId(request.SessionId));
    using var process=Process.GetProcessById(target.ProcessId);
    _=engine.Handle;_=process.Handle;
    var watch=Stopwatch.StartNew();
    try
    {
        if(target.SessionState!=DebugSessionState.Stopped || File.Exists(observation)) throw new Exception("Entry was not stopped");
        host.Dispose();
        long disposedMs=watch.ElapsedMilliseconds;
        bool completed=SpinWait.SpinUntil(()=>File.Exists(observation),TimeSpan.FromSeconds(2));
        var record=new {cycle,completed,disposedMs,elapsedMs=watch.ElapsedMilliseconds,targetPid=process.Id,enginePid=engine.Id,
            engineExited=engine.HasExited,engineExitCode=engine.HasExited?(int?)engine.ExitCode:null,targetExited=process.HasExited};
        File.AppendAllText(Path.Combine(evidence,"cycles.jsonl"),JsonSerializer.Serialize(record)+Environment.NewLine);
        if(!completed)
        {
            Console.WriteLine(JsonSerializer.Serialize(record));
            if(!process.HasExited) Console.WriteLine(string.Join(",",process.Threads.Cast<ProcessThread>().Select(t=>t.Id+":"+t.ThreadState+(t.ThreadState==System.Diagnostics.ThreadState.Wait?":"+t.WaitReason:""))));
            bool late=SpinWait.SpinUntil(()=>File.Exists(observation),TimeSpan.FromSeconds(15));
            Console.WriteLine("Late completion="+late+" Engine exited="+engine.HasExited+" Exit="+(engine.HasExited?engine.ExitCode:-999));
            if(!late && !process.HasExited)
            {
                var native=new ProcessStartInfo(@"C:\Program Files (x86)\Windows Kits\10\Debuggers\x86\cdb.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                string symbols=Path.Combine(evidence,"symbols");Directory.CreateDirectory(symbols);
                foreach(string argument in new[]{"-y","srv*"+symbols+"*https://msdl.microsoft.com/download/symbols","-pv","-p",process.Id.ToString(),"-c",".reload /f clr.dll;.reload /f kernelbase.dll;~* kb;q"}) native.ArgumentList.Add(argument);
                using var debugger=Process.Start(native)!;
                var stdout=debugger.StandardOutput.ReadToEndAsync();var stderr=debugger.StandardError.ReadToEndAsync();
                try { await debugger.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                finally { if(!debugger.HasExited) {debugger.Kill();await debugger.WaitForExitAsync();} }
                File.WriteAllText(Path.Combine(evidence,"target-native-stack.log"),(await stdout)+(await stderr));
            }
            return 1;
        }
    }
    finally
    {
        if(!process.WaitForExit(2000)) { process.Kill();process.WaitForExit(5000); }
        if(File.Exists(observation)) File.Delete(observation);
    }
    if(cycle%10==0) Console.WriteLine("Completed cycle "+cycle);
}
return 0;
