using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FxDbg.Core.Model;

public sealed class ExceptionInfo
{
    public ExceptionInfo(
        string typeName,
        string? message,
        int threadId,
        SourceLocation? throwLocation,
        IReadOnlyList<StackFrameInfo> stack,
        bool isUnhandled = true,
        string? diagnostic = null)
    {
        TypeName = string.IsNullOrWhiteSpace(typeName)
            ? throw new ArgumentException("An exception type is required.", nameof(typeName))
            : typeName;
        Message = message;
        ThreadId = threadId;
        ThrowLocation = throwLocation;
        IsUnhandled = isUnhandled;
        Diagnostic = diagnostic;
        Stack = new ReadOnlyCollection<StackFrameInfo>(
            stack is null
                ? throw new ArgumentNullException(nameof(stack))
                : new List<StackFrameInfo>(stack));
    }

    public string TypeName { get; }

    public string? Message { get; }

    public int ThreadId { get; }

    public SourceLocation? ThrowLocation { get; }

    public IReadOnlyList<StackFrameInfo> Stack { get; }
    public bool IsUnhandled { get; }
    public string? Diagnostic { get; }
}
