namespace FxDbg.Core.Errors;

public enum FxDbgErrorCode
{
    InvalidRequest,
    InvalidSessionState,
    SessionNotFound,
    TargetNotFound,
    ArchitectureMismatch,
    UnsupportedArchitecture,
    InvalidExecutable,
    NotManagedProcess,
    CoreClrNotSupported,
    UnsupportedClrVersion,
    AlreadyDebugged,
    BreakpointNotFound,
    ThreadNotFound,
    FrameNotFound,
    SymbolsMissing,
    SymbolsMismatch,
    SymbolsReadFailed,
    ValueUnavailable,
    ValueOptimizedAway,
    OperationTimedOut,
    OperationCancelled,
    TransportDisconnected,
    EngineExited,
    AccessDenied,
    InternalError
}
