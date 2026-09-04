using System;
using System.IO;
using System.Reflection.PortableExecutable;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Host.Architecture;

public sealed class PeArchitectureDetector
{
    public TargetArchitecture Detect(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "An executable path is required.");
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FxDbgException(FxDbgErrorCode.TargetNotFound, $"Target executable was not found: {fullPath}");
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            PEHeaders headers = reader.PEHeaders;
            if (headers.PEHeader is null)
            {
                throw new FxDbgException(FxDbgErrorCode.InvalidExecutable, $"Target is not a valid PE image: {fullPath}");
            }

            if (!reader.HasMetadata || headers.CorHeader is null)
            {
                throw new FxDbgException(FxDbgErrorCode.NotManagedProcess, $"Target is not a managed executable: {fullPath}");
            }

            if (headers.CoffHeader.Machine == Machine.Amd64)
            {
                return TargetArchitecture.X64;
            }

            if (headers.CoffHeader.Machine != Machine.I386)
            {
                throw new FxDbgException(
                    FxDbgErrorCode.UnsupportedArchitecture,
                    $"Target PE machine {headers.CoffHeader.Machine} is not supported.");
            }

            CorFlags flags = headers.CorHeader.Flags;
            if ((flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit)) != 0)
            {
                return TargetArchitecture.X86;
            }

            if ((flags & CorFlags.ILOnly) == 0)
            {
                return TargetArchitecture.X86;
            }

            return Environment.Is64BitOperatingSystem ? TargetArchitecture.X64 : TargetArchitecture.X86;
        }
        catch (FxDbgException)
        {
            throw;
        }
        catch (BadImageFormatException exception)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidExecutable, $"Target is not a valid PE image: {fullPath}", exception);
        }
        catch (IOException exception)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidExecutable, $"Target PE image could not be read: {fullPath}", exception);
        }
    }
}
