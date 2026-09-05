using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FxDbg.Core.Events;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunModuleInspection(string root, string configuration)
    {
        string directory = Path.Combine(root, "artifacts", "stage1-9", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string library = Path.Combine(root, "tests", "Debuggees", "Fx40.LateModule", "bin", configuration, "net40", "Fx40.LateModule.dll");
        File.Copy(library, Path.Combine(directory, "Fx40.LateModule.dll"));
        string pdb = Path.Combine(directory, "Fx40.LateModule.pdb");
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
        using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" \"" + directory + "\"", Path.GetDirectoryName(target)!,
            IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64, true, TimeSpan.FromSeconds(15));
        using Process process = Process.GetProcessById(session.Target.ProcessId);
        try
        {
            session.Continue();
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-0")));
            session.Pause();
            ModuleInfo Module() => session.GetModules().Single(item => item.Name == "Fx40.LateModule.dll");
            ModuleInfo initial = Module();
            Require(initial.SymbolStatus == SymbolStatus.Missing && initial.AppDomain == "LateModule-0", "Missing PDB and module domain must be explicit.");
            File.Copy(Path.ChangeExtension(target, ".pdb"), pdb);
            session.RefreshSymbols();
            Require(Module().SymbolStatus == SymbolStatus.Mismatch, "A different valid Windows PDB must be mismatch.");
            File.WriteAllText(pdb, "invalid-pdb");
            session.RefreshSymbols();
            Require(Module().SymbolStatus == SymbolStatus.ReadFailed && !string.IsNullOrEmpty(Module().Diagnostic), "Malformed PDB must be read-failed with diagnostic.");
            File.Copy(Path.ChangeExtension(library, ".pdb"), pdb, true);
            session.RefreshSymbols();
            Require(Module().SymbolStatus == SymbolStatus.Loaded && Module().ModuleId == initial.ModuleId, "Restoring matching PDB must retain the module load identity.");
            File.WriteAllText(Path.Combine(directory, "go-0"), "go");
            session.Continue();
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-1")));
            Require(Module().ModuleId != initial.ModuleId && Module().AppDomain == "LateModule-1", "Reload must have a new load-instance identity.");
            Require(session.GetModules().All(item => item.ModuleId != initial.ModuleId), "Unloaded module must leave the active snapshot.");
            Require(session.Events.OfType<ModuleChangedEvent>().Any(item => item.Change == ModuleChangeKind.Unloaded && item.Module.ModuleId == initial.ModuleId), "Unload must emit an identifiable event.");
            File.WriteAllText(Path.Combine(directory, "go-1"), "go");
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-2")));
            File.WriteAllText(Path.Combine(directory, "go-2"), "go");
            PumpUntil(session, () => session.HasExited);
            Require(session.GetModules().Count == 0, "Exit must clear all active module caches.");
            var changes = session.Events.OfType<ModuleChangedEvent>().Where(item => item.Module.ModuleId == initial.ModuleId).ToArray();
            Require(changes.Select(item => item.Module.SymbolStatus).Contains(SymbolStatus.Mismatch) && changes.Select(item => item.Module.SymbolStatus).Contains(SymbolStatus.ReadFailed), "Symbol-state changes must be observable events.");
            Require(session.Events.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, session.Events.Count).Select(value => (long)value)), "Module and session events must share one ordered sequence.");
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
