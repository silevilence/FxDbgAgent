using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FxDbg.Platform;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Host.Architecture;

public sealed class ProcessArchitectureDetector
{
    public TargetArchitecture Detect(int processId)
    {
        if (processId <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive process ID is required.");
        }

        try
        {
            try { return DetectWithQueryHandle(processId); }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
            {
                return WindowsDebugPrivilege.Execute(() => DetectWithQueryHandle(processId));
            }
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
            throw new FxDbgException(FxDbgErrorCode.AccessDenied, $"Host architecture inspection of target {processId} was denied. Start the Host with a token permitted to debug that process and attach again.", exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 87)
        {
            throw new FxDbgException(FxDbgErrorCode.TargetNotFound, $"Target process {processId} was not found or has exited.", exception);
        }
    }

    private static TargetArchitecture DetectWithQueryHandle(int processId)
    {
        using SafeProcessHandle handle = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION, never ALL_ACCESS.
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return WindowsProcessArchitecture.Detect(handle.DangerousGetHandle());
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
}
