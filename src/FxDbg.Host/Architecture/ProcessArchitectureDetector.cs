using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Host.Architecture;

public sealed class ProcessArchitectureDetector
{
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const int ErrorCallNotImplemented = 120;

    public TargetArchitecture Detect(int processId)
    {
        if (processId <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive process ID is required.");
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            try
            {
                if (TryDetectWithIsWow64Process2(process.Handle, out TargetArchitecture architecture))
                {
                    return architecture;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Windows versions before Windows 10 use the fallback below.
            }

            if (!IsWow64Process(process.Handle, out bool isWow64))
            {
                throw CreateWin32Failure("IsWow64Process", Marshal.GetLastWin32Error());
            }

            return Environment.Is64BitOperatingSystem && !isWow64
                ? TargetArchitecture.X64
                : TargetArchitecture.X86;
        }
        catch (ArgumentException exception)
        {
            throw new FxDbgException(FxDbgErrorCode.TargetNotFound, $"Target process {processId} was not found.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new FxDbgException(FxDbgErrorCode.TargetNotFound, $"Target process {processId} has exited.", exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            throw new FxDbgException(FxDbgErrorCode.AccessDenied, $"Access to target process {processId} was denied.", exception);
        }
    }

    private static bool TryDetectWithIsWow64Process2(IntPtr processHandle, out TargetArchitecture architecture)
    {
        if (!IsWow64Process2(processHandle, out ushort processMachine, out ushort nativeMachine))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorCallNotImplemented)
            {
                architecture = default;
                return false;
            }

            throw CreateWin32Failure("IsWow64Process2", error);
        }

        ushort effectiveMachine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
        architecture = effectiveMachine switch
        {
            ImageFileMachineI386 => TargetArchitecture.X86,
            ImageFileMachineAmd64 => TargetArchitecture.X64,
            _ => throw new FxDbgException(
                FxDbgErrorCode.UnsupportedArchitecture,
                $"Target process machine 0x{effectiveMachine:X4} is not supported.")
        };
        return true;
    }

    private static Win32Exception CreateWin32Failure(string operation, int error)
    {
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr processHandle,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr processHandle, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}
