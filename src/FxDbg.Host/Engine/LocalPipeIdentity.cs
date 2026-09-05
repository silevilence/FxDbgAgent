using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using FxDbg.Core.Errors;
using Microsoft.Win32.SafeHandles;

namespace FxDbg.Host.Engine;

internal static class LocalPipeIdentity
{
    private const int ErrorPipeLocal = 229;
    internal static void RequireEngine(NamedPipeServerStream pipe, int processId)
    {
        var computer = new StringBuilder(256);
        bool hasId = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId);
        bool hasComputer = GetNamedPipeClientComputerName(pipe.SafePipeHandle, computer, (uint)computer.Capacity);
        int computerError = Marshal.GetLastWin32Error();
        bool isLocal = !hasComputer && computerError == ErrorPipeLocal || hasComputer &&
            string.Equals(computer.ToString().TrimStart('\\'), Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (!hasId || clientId != (uint)processId || !isLocal)
            throw new FxDbgException(FxDbgErrorCode.AccessDenied, "Pipe client is not the local Engine process started by this Host.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder computerName, uint length);
}
