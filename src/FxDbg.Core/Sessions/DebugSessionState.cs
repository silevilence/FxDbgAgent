namespace FxDbg.Core.Sessions;

public enum DebugSessionState
{
    Created,
    Starting,
    Running,
    Stopped,
    Detaching,
    Terminated,
    Failed
}
