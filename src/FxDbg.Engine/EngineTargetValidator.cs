using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using FxDbg.Platform;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Engine;

internal static class EngineTargetValidator
{
    internal static void RequireCurrentArchitecture(TargetArchitecture requested)
    {
        TargetArchitecture current = IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64;
        if (requested == TargetArchitecture.Auto)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Engine requests require a resolved x86 or x64 architecture.");
        }

        if (requested != current)
        {
            throw new FxDbgException(
                FxDbgErrorCode.ArchitectureMismatch,
                $"Request architecture {TargetArchitectureWireName.Format(requested)} does not match Engine architecture {TargetArchitectureWireName.Format(current)}.");
        }
    }

    internal static void ValidateAttachRuntime(int processId, TargetArchitecture engineArchitecture)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            TargetArchitecture targetArchitecture = WindowsProcessArchitecture.Detect(process.Handle);
            if (targetArchitecture != engineArchitecture)
            {
                throw new FxDbgException(
                    FxDbgErrorCode.ArchitectureMismatch,
                    $"Engine architecture {TargetArchitectureWireName.Format(engineArchitecture)} does not match target architecture {TargetArchitectureWireName.Format(targetArchitecture)}.");
            }

            bool framework4 = false;
            bool framework2 = false;
            bool coreClr = false;
            foreach (ProcessModule module in process.Modules)
            {
                framework4 |= string.Equals(module.ModuleName, "clr.dll", StringComparison.OrdinalIgnoreCase);
                framework2 |= string.Equals(module.ModuleName, "mscorwks.dll", StringComparison.OrdinalIgnoreCase);
                coreClr |= string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase);
            }

            if (coreClr)
            {
                throw new FxDbgException(FxDbgErrorCode.CoreClrNotSupported, "Target uses CoreCLR; only Framework CLR v4 is supported.");
            }

            if (framework2)
            {
                throw new FxDbgException(FxDbgErrorCode.UnsupportedClrVersion, "Target uses CLR v2.0.50727; only CLR v4.0.30319 is supported.");
            }

            if (!framework4)
            {
                ValidateImageWillLoadFramework4(process.MainModule!.FileName);
            }
        }
        catch (FxDbgException)
        {
            throw;
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

    private static void ValidateImageWillLoadFramework4(string path)
    {
        try
        {
            string runtime = Assembly.ReflectionOnlyLoadFrom(path).ImageRuntimeVersion;
            if (!runtime.StartsWith("v4.0", StringComparison.OrdinalIgnoreCase))
            {
                throw new FxDbgException(
                    FxDbgErrorCode.UnsupportedClrVersion,
                    $"Target image requests {runtime}; only CLR v4.0.30319 is supported.");
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new FxDbgException(FxDbgErrorCode.NotManagedProcess, "Target is native and has no loaded Desktop CLR.", exception);
        }
    }

}
