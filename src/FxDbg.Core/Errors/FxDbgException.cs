using System;

namespace FxDbg.Core.Errors;

public sealed class FxDbgException : Exception
{
    public FxDbgException(FxDbgErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public FxDbgException(FxDbgErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public FxDbgErrorCode Code { get; }
}
