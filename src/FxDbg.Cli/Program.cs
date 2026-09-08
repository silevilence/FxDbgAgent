using System.Diagnostics;
using System.IO.Pipes;
using FxDbg.Cli;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using FxDbg.Host.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, input) => { input.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 3 && args[0] == "session-host")
    {
        await PersistentSessionServer.RunAsync(new SessionId(Guid.Parse(args[1])), Paths(args[2]), cancellation.Token);
        return 0;
    }
    if (args.Length == 0 || args[0] is "--help" or "help" or "-h")
    {
        Console.WriteLine("""
        fxdbg launch --exe app.exe [--arg value | --args "..."] [--arch auto|x86|x64] [--stop-at-entry]
        fxdbg attach --pid PID [--arch auto|x86|x64]
        fxdbg break --session ID --file source.cs --line LINE
        fxdbg continue|pause|wait|threads|modules|state|events --session ID
        fxdbg step --session ID --kind into|over|out --thread ID
        fxdbg stack --session ID --thread ID [--start N] [--count N]
        fxdbg variables --session ID --frame FRAME [--reference REF] [--start N] [--count N] [--max-depth N] [--max-string-length N]
        fxdbg evaluate --session ID --frame FRAME --expression EXPR [--evaluation-timeout-ms N] [--max-depth N] [--count N] [--max-string-length N]
        fxdbg breakpoints|detach|terminate|refresh-symbols --session ID
        fxdbg remove-break|enable-break --session ID --breakpoint ID [--enabled true|false]
        fxdbg exceptions --session ID --first-chance true|false
        Common: --timeout-ms N; launch/attach: --engine-dir DIR; launch: --cwd DIR, --env NAME=VALUE.
        Results are JSON. Session, frame and reference IDs must be copied from preceding results.
        Each session keeps one background Host alive until detach or 30 minutes without a CLI connection.
        """);
        return 0;
    }
    CliCommand command = CliCommand.Parse(args);
    Process? child = null;
    bool started = false;
    try
    {
        if (command.StartsSession)
        {
            string directory = command.EngineDirectory ?? FindEngineDirectory();
            EngineProcessPaths paths = Paths(directory);
            _ = paths.For(FxDbg.Core.Requests.TargetArchitecture.X86);
            _ = paths.For(FxDbg.Core.Requests.TargetArchitecture.X64);
            child = BackgroundHost.Start(command.SessionId.ToString(), directory);
        }
        using var pipe = new NamedPipeClientStream(".", PersistentSessionServer.PipeName(command.SessionId), PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(command.StartsSession ? 10000 : 3000, cancellation.Token); }
        catch (TimeoutException error) { throw new FxDbgException(FxDbgErrorCode.SessionNotFound, "Local session Host was not found.", error); }
        using var peer = new RpcPeer(pipe, command.SessionId.ToString());
        command.Parameters["commandTimeoutMs"] = (int)command.Timeout.TotalMilliseconds;
        JToken result = await peer.CallAsync(command.Method, command.Parameters, command.Timeout + TimeSpan.FromSeconds(5), cancellation.Token);
        Console.WriteLine(new JObject { ["ok"] = true, ["sessionId"] = command.SessionId.ToString(), ["result"] = result }.ToString(Formatting.None));
        started = true;
    }
    finally
    {
        if (child is not null)
        {
            if (!started)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await child.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException) { if (!child.HasExited) child.Kill(); }
            }
            child.Dispose();
        }
    }
    return 0;
}
catch (Exception error)
{
    FxDbgErrorCode code = error is FxDbgException known ? known.Code : error is OperationCanceledException ? FxDbgErrorCode.OperationCancelled : FxDbgErrorCode.InvalidRequest;
    Console.Error.WriteLine(new JObject { ["ok"] = false, ["code"] = FxDbgErrorCodeWireName.Format(code), ["message"] = error.Message }.ToString(Formatting.None));
    return 1;
}

static EngineProcessPaths Paths(string directory) => new(Path.Combine(directory, "FxDbg.Engine.x86.exe"), Path.Combine(directory, "FxDbg.Engine.x64.exe"));
static string FindEngineDirectory()
{
    string bundled = Path.Combine(AppContext.BaseDirectory, "engines");
    if (Directory.Exists(bundled)) return bundled;
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    string configuration = directory.Parent!.Name;
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FxDbg.sln"))) directory = directory.Parent;
    if (directory is not null) return Path.Combine(directory.FullName, "src", "FxDbg.Engine", "bin", configuration, "net48");
    throw new FxDbgException(FxDbgErrorCode.TargetNotFound, "Use --engine-dir to locate the x86/x64 Engine executables.");
}
