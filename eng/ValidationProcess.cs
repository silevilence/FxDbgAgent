using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace FxDbg.Validation;

/// <summary>Bounds one validation subprocess and remembers only its observed descendants for failure cleanup.</summary>
public static class ValidationProcess
{
    public static int Run(string executable, string[] arguments, string directory, string log, int seconds)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        using var process = Process.Start(start);
        File.WriteAllText(log, "Supervisor started PID " + process.Id + "\n");
        var owned = new Dictionary<int, Process> { [process.Id] = process };
        var identities = new Dictionary<int, string> { [process.Id] = "root name=" + Path.GetFileName(executable) + " started=" + process.StartTime.ToUniversalTime().ToString("o") };
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timer = Stopwatch.StartNew();
        bool timedOut = false;
        int exit = -1;
        var forced = new List<string>();
        try
        {
            while (!process.HasExited)
            {
                Discover(owned, identities);
                if (timer.Elapsed.TotalSeconds >= seconds) { timedOut = true; break; }
                Thread.Sleep(100);
            }
            if (!timedOut) exit = process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) process.Kill(); // Never KillTree; only the process started above.
            // Give debugger Hosts/Engines their normal EOF/parent-exit detach window first.
            var cleanup = Stopwatch.StartNew();
            while (owned.Values.Any(x => !x.HasExited) && cleanup.Elapsed.TotalSeconds < 15)
            {
                Discover(owned, identities);
                Thread.Sleep(100);
            }
            foreach (var child in owned.Values.Where(x => !x.HasExited))
            {
                forced.Add(child.Id + ":" + child.ProcessName);
                child.Kill(); // Owned test fixture cleanup, never accepted as safe-detach evidence.
            }
            foreach (var child in owned.Values) child.WaitForExit(5000);
            bool drained = Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5));
            File.WriteAllText(log, (drained ? stdout.Result + stderr.Result : "Output pipes did not close.\n") +
                "\nSupervisor: exit=" + exit + ", timedOut=" + timedOut + ", forcedOwnedCleanup=" + string.Join(",", forced) + "\n");
            File.WriteAllLines(log + ".processes", owned.Values.Select(x => x.Id + " " + identities[x.Id] + " exited=" + x.HasExited));
            foreach (var child in owned.Values.Where(x => !ReferenceEquals(x, process))) child.Dispose();
            if (!drained) throw new TimeoutException("Validation output pipes did not close: " + log);
        }
        if (timedOut) throw new TimeoutException("Validation subprocess exceeded " + seconds + " seconds: " + log);
        if (forced.Count != 0)
            throw new InvalidOperationException("Validation left owned processes requiring forced cleanup: " + log);
        return exit;
    }

    private static void Discover(Dictionary<int, Process> owned, Dictionary<int, string> identities)
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException("Cannot enumerate validation-owned descendants.");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        var entries = new List<ProcessEntry>();
        if (Process32First(snapshot, ref entry)) do { entries.Add(entry); } while (Process32Next(snapshot, ref entry));
        bool added;
        do
        {
            added = false;
            foreach (var item in entries)
            {
                int id = (int)item.ProcessId;
                if (owned.ContainsKey(id) || !owned.TryGetValue((int)item.ParentProcessId, out var parent)) continue;
                Process child = null;
                try
                {
                    child = Process.GetProcessById(id);
                    _ = child.Handle; // Hold process identity; recycled PID cannot change this cleanup target.
                    if (child.StartTime < parent.StartTime || (parent.HasExited && child.StartTime > parent.ExitTime)) { child.Dispose(); continue; }
                    owned.Add(id, child);
                    identities[id] = "parent=" + item.ParentProcessId + " name=" + item.ExeFile + " started=" + child.StartTime.ToUniversalTime().ToString("o");
                    added = true;
                }
                catch (ArgumentException) { child?.Dispose(); }
                catch (InvalidOperationException) { child?.Dispose(); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 5)
                {
                    child?.Dispose();
                    // A process may already be exiting under a debugger. Do not expand ownership
                    // to an identity we could not verify. The known parent still owns normal cleanup.
                }
            }
        } while (added);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal UIntPtr DefaultHeapId;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
