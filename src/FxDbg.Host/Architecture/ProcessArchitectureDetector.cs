using System;
using System.ComponentModel;
using System.Diagnostics;
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
            using Process process = Process.GetProcessById(processId);
            return WindowsProcessArchitecture.Detect(process.Handle);
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

}
