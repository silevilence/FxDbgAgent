using System;

namespace FxDbg.Core.Model;

public sealed class StackFrameInfo
{
    public StackFrameInfo(
        FrameId frameId,
        int threadId,
        string methodName,
        string moduleName,
        string assemblyName,
        uint ilOffset,
        SourceLocation? sourceLocation,
        string? appDomain)
    {
        FrameId = frameId ?? throw new ArgumentNullException(nameof(frameId));
        ThreadId = threadId;
        MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
        ModuleName = moduleName ?? throw new ArgumentNullException(nameof(moduleName));
        AssemblyName = assemblyName ?? throw new ArgumentNullException(nameof(assemblyName));
        IlOffset = ilOffset;
        SourceLocation = sourceLocation;
        AppDomain = appDomain;
    }

    public FrameId FrameId { get; }

    public int ThreadId { get; }

    public string MethodName { get; }

    public string ModuleName { get; }

    public string AssemblyName { get; }

    public uint IlOffset { get; }

    public SourceLocation? SourceLocation { get; }

    public string? AppDomain { get; }
}
