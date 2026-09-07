using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

// Test-only token inspection/restriction. Never part of the debugger or an elevation helper.
internal static class RestrictedTokenProcess
{
    internal static uint? DebugAttributes(int pid)
    {
        using var process = Process.GetProcessById(pid);
        Check(OpenProcessToken(process.Handle, 8, out var token));
        try
        {
            Check(LookupPrivilegeValue(null, "SeDebugPrivilege", out var debug));
            GetTokenInformation(token, 3, IntPtr.Zero, 0, out int length);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Check(GetTokenInformation(token, 3, buffer, length, out _));
                int count = Marshal.ReadInt32(buffer);
                for (int index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<LuidAttributes>(buffer + 4 + index * 12);
                    if (entry.Luid.Low == debug.Low && entry.Luid.High == debug.High) return entry.Attributes;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    internal static void SetDebugEnabled(bool enabled)
    {
        Check(OpenProcessToken(GetCurrentProcess(), 0x28, out var token));
        try
        {
            Check(LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid));
            var state = new Privileges { Count=1, Entry=new LuidAttributes { Luid=luid, Attributes=enabled ? 2u : 0u } };
            Check(AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero));
            int error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
        }
        finally { CloseHandle(token); }
    }

    internal static async Task Run(string[] arguments, string directory, CancellationToken token)
    {
        Check(OpenProcessToken(GetCurrentProcess(), 0xF01FF, out var current));
        IntPtr restricted = IntPtr.Zero;
        IntPtr childOutput = IntPtr.Zero;
        IntPtr desktop = IntPtr.Zero;
        IntPtr descriptor = IntPtr.Zero;
        using var log = File.Create(Path.Combine(directory, "artifacts/stage3-4-validation/restricted-startup.log"));
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        byte[] bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        IntPtr adminSid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, adminSid, bytes.Length);
            var disabled = new SidAttributes { Sid=adminSid };
            Check(CreateRestrictedToken(current, 1, 1, ref disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted));
            string desktopName = "FxDbgPermissionTest" + Guid.NewGuid().ToString("N");
            Check(ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;" + WindowsIdentity.GetCurrent().User!.Value + ")", 1, out descriptor, out _));
            // The elevated token's default DACL may grant only Administrators/System.
            // Once Administrators is deny-only, the child needs access to its own
            // newly created objects. This changes only the disposable restricted token.
            Check(GetSecurityDescriptorDacl(descriptor, out _, out IntPtr defaultDacl, out _));
            Check(SetTokenInformation(restricted, 6, ref defaultDacl, IntPtr.Size));
            var security = new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(), Descriptor=descriptor };
            desktop = CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, ref security);
            Check(desktop != IntPtr.Zero);
            Check(DuplicateHandle(GetCurrentProcess(), log.SafeFileHandle.DangerousGetHandle(), GetCurrentProcess(), out childOutput, 0, true, 2));
            var startup = new StartupInfo { Size=Marshal.SizeOf<StartupInfo>(), Desktop="winsta0\\" + desktopName, Flags=0x101, Show=0, Output=childOutput, Error=childOutput };
            string executable = Environment.ProcessPath!;
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
            Check(CreateProcessAsUser(restricted, executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x10,
                IntPtr.Zero, directory, ref startup, out var created));
            CloseHandle(created.Thread);
            try
            {
                using var process = Process.GetProcessById(created.ProcessId);
                _ = process.Handle;
                try { await process.WaitForExitAsync(token); }
                finally
                {
                    if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("Restricted token fixture failed: " + process.ExitCode);
            }
            finally { CloseHandle(created.Process); }
        }
        finally { Marshal.FreeHGlobal(adminSid); if (desktop != IntPtr.Zero) CloseDesktop(desktop); if (descriptor != IntPtr.Zero) LocalFree(descriptor); if (childOutput != IntPtr.Zero) CloseHandle(childOutput); if (restricted != IntPtr.Zero) CloseHandle(restricted); CloseHandle(current); }
    }

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.EndsWith('\\')) throw new ArgumentException("Test argument cannot contain a quote or trailing slash.");
        return '"' + value + '"';
    }
    private static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] private struct LuidAttributes { internal Luid Luid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Privileges { internal uint Count; internal LuidAttributes Entry; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAttributes { internal IntPtr Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal int X,Y,XSize,YSize,XChars,YChars,Fill,Flags;
        internal short Show,ReservedBytes; internal IntPtr ReservedPointer,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process,Thread; internal int ProcessId,ThreadId; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value,uint version,out IntPtr descriptor,out uint size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,ref SecurityAttributes security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr buffer,int length,out int returned);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,out bool present,out IntPtr dacl,out bool defaulted);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool SetTokenInformation(IntPtr token,int kind,ref IntPtr value,int length);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool AdjustTokenPrivileges(IntPtr token,bool all,ref Privileges state,int length,IntPtr previous,IntPtr returned);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool CreateRestrictedToken(IntPtr token,uint flags,uint disabledCount,
        ref SidAttributes disabled,uint deletedCount,IntPtr deleted,uint restrictedCount,IntPtr restricted,out IntPtr result);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcessAsUser(IntPtr token,string executable,
        StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,
        ref StartupInfo startup,out ProcessInformation process);
}
