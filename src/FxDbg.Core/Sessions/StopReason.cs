namespace FxDbg.Core.Sessions;

public enum StopReason
{
    Breakpoint,
    Step,
    Exception,
    UserPause,
    Entry,
    ProcessExit
}
