using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FxDbg.Core.Errors;
using Microsoft.Win32.SafeHandles;

namespace FxDbg.Platform;

// Linked into Host and each Engine: no COM dependency, no elevation or policy changes.
internal static class WindowsDebugPrivilege
{
    private static readonly object Gate = new();

    internal static T Execute<T>(Func<T> operation) => Execute(operation, NativeAdjustment.Acquire);

    internal static void InitializeProcessDiagnostics() => Execute(() =>
    {
        // ProcessManager's first use enables SeDebugPrivilege in both .NET Framework
        // and modern .NET. Force that initialization inside a restoring scope before
        // any child is created or concurrent session cleanup can touch Process.
        using (var current = System.Diagnostics.Process.GetCurrentProcess()) return current.HasExited;
    });

    internal static T Execute<T>(Func<T> operation, Func<IDisposable> acquire)
    {
        lock (Gate)
        {
            IDisposable adjustment = acquire();
            T result;
            try { result = operation(); }
            catch { adjustment.Dispose(); throw; }
            try { adjustment.Dispose(); }
            catch
            {
                // A successful attach must not escape unowned if restoring the token fails.
                if (result is IDisposable resource) resource.Dispose();
                throw;
            }
            return result;
        }
    }

    // Process creation in Host shares the gate, so a concurrent launch cannot inherit
    // another attach's temporary process-token adjustment. These callbacks must be synchronous.
    internal static T WithoutAdjustment<T>(Func<T> operation) { lock (Gate) return operation(); }

    internal static bool WasAssigned(bool success, int error)
    {
        if (success && error == 0) return true;
        if (success && error == 1300) return false; // ERROR_NOT_ALL_ASSIGNED is not success.
        throw Failure("adjust SeDebugPrivilege", error);
    }

    private static FxDbgException Failure(string operation, int error) => new(FxDbgErrorCode.AccessDenied,
        "Cannot " + operation + " on the current debugger token (Win32 " + error +
        "). Start the Host with an account/token allowed to debug the target and attach again.", new Win32Exception(error));

    private sealed class NativeAdjustment : IDisposable
    {
        private readonly TokenHandle token;
        private TokenPrivileges previous;
        private bool disposed;
        private NativeAdjustment(TokenHandle token) { this.token = token; }

        internal static IDisposable Acquire()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out TokenHandle token))
                throw Failure("open the debugger process token", Marshal.GetLastWin32Error());
            var adjustment = new NativeAdjustment(token);
            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out Luid luid))
                    throw Failure("look up SeDebugPrivilege", Marshal.GetLastWin32Error());
                var desired = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                bool success = AdjustTokenPrivileges(token, false, ref desired, Marshal.SizeOf(typeof(TokenPrivileges)),
                    out adjustment.previous, out _);
                int error = Marshal.GetLastWin32Error();
                bool assigned = WasAssigned(success, error);
                if (!assigned) adjustment.previous.Count = 0;
                // A missing privilege does not forbid ordinary same-user debugging.
                return adjustment;
            }
            catch { token.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (previous.Count != 0)
                {
                    bool success = AdjustTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
                    int error = Marshal.GetLastWin32Error();
                    if (!success || error != 0) throw Failure("restore SeDebugPrivilege", error);
                }
            }
            finally { token.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { internal uint Count; internal Luid Luid; internal uint Attributes; }
    private sealed class TokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TokenHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out TokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(TokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, int length, out TokenPrivileges previous, out int returnedLength);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(TokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnedLength);
}
