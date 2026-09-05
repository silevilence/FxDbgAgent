using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FxDbg.Cli;

internal static class BackgroundHost
{
    internal static Process Start(string sessionId, string engineDirectory)
    {
        string executable = Environment.ProcessPath!;
        var arguments = new List<string> { executable };
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "fxdbg", StringComparison.OrdinalIgnoreCase))
            arguments.Add(typeof(CliCommand).Assembly.Location);
        arguments.AddRange(new[] { "session-host", sessionId, Path.GetFullPath(engineDirectory) });
        var commandLine = new StringBuilder(string.Join(" ", arguments.Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // A persistent Host must not keep a shell's captured stdout/stderr pipes alive.
        // DETACHED_PROCESS and inheritHandles=false also avoid a visible console window.
        if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00000008,
            IntPtr.Zero, Environment.CurrentDirectory, ref startup, out ProcessInformation info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the background Host.");
        try
        {
            Process process = Process.GetProcessById(info.ProcessId);
            _ = process.Handle;
            return process;
        }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal short ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string executable, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
