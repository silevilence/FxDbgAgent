using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Platform;

// Compiled into Host and each Engine so neither process depends on the other's runtime or COM layer.
internal static class WindowsProcessArchitecture
{
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const int ErrorCallNotImplemented = 120;

    internal static TargetArchitecture Detect(IntPtr processHandle)
    {
        try
        {
            if (IsWow64Process2(processHandle, out ushort processMachine, out ushort nativeMachine))
            {
                ushort machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
                return machine switch
                {
                    ImageFileMachineI386 => TargetArchitecture.X86,
                    ImageFileMachineAmd64 => TargetArchitecture.X64,
                    _ => throw new FxDbgException(FxDbgErrorCode.UnsupportedArchitecture, $"Target process machine 0x{machine:X4} is not supported.")
                };
            }
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorCallNotImplemented) throw Failure("IsWow64Process2", error);
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows versions use the fallback below.
        }
        if (!IsWow64Process(processHandle, out bool isWow64)) throw Failure("IsWow64Process", Marshal.GetLastWin32Error());
        return Environment.Is64BitOperatingSystem && !isWow64 ? TargetArchitecture.X64 : TargetArchitecture.X86;
    }

    private static Win32Exception Failure(string operation, int error) => new(error, $"{operation} failed with Win32 error {error}.");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr processHandle, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}
