using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FxDbg.Interop;

// Duplicate the CLR-owned handle while attaching. Never reopen a PID during cleanup:
// a service may require SeDebugPrivilege again, and a PID can identify another process later.
internal sealed class TargetProcessLifetime : IDisposable
{
    private readonly SafeWaitHandle handle;
    internal TargetProcessLifetime(IntPtr clrHandle)
    {
        IntPtr current = GetCurrentProcess();
        if (!DuplicateHandle(current, clrHandle, current, out handle, 0x00100000, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal bool HasExited
    {
        get
        {
            uint state = WaitForSingleObject(handle, 0);
            if (state == 0) return true;
            if (state == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeWaitHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
