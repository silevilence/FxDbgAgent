using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;

namespace FxDbg.Host.Engine;

public sealed class EngineProcessHost : IDisposable
{
    private readonly ArchitectureRouter router;
    private readonly EngineProcessPaths paths;
    private readonly object gate = new();
    private readonly Dictionary<SessionId, EngineConnection> connections = new();
    private bool disposed;

    public EngineProcessHost(ArchitectureRouter router, EngineProcessPaths paths)
    {
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Task<DebugTargetInfo> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ThrowIfDisposed();
        TargetArchitecture architecture = router.Resolve(request);
        var arguments = CommonArguments("launch", request.SessionId, architecture, request.Timeout);
        arguments.Add("--exe");
        arguments.Add(request.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            arguments.Add("--working-directory");
            arguments.Add(request.WorkingDirectory!);
        }

        foreach (KeyValuePair<string, string> pair in request.Environment)
        {
            arguments.Add("--env");
            arguments.Add(pair.Key + "=" + pair.Value);
        }

        foreach (string argument in request.Arguments)
        {
            arguments.Add("--arg");
            arguments.Add(argument);
        }

        if (request.StopAtEntry)
        {
            arguments.Add("--stop-at-entry");
        }

        return RunAsync(paths.For(architecture), arguments, request.SessionId, true, request.Timeout, cancellationToken);
    }

    public Task<DebugTargetInfo> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ThrowIfDisposed();
        TargetArchitecture architecture = router.Resolve(request);
        var arguments = CommonArguments("attach", request.SessionId, architecture, request.Timeout);
        arguments.Add("--pid");
        arguments.Add(request.ProcessId.ToString(CultureInfo.InvariantCulture));
        return RunAsync(paths.For(architecture), arguments, request.SessionId, false, request.Timeout, cancellationToken);
    }

    public void Dispose()
    {
        List<EngineConnection> active;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            active = new List<EngineConnection>(connections.Values);
            connections.Clear();
        }

        foreach (EngineConnection connection in active)
        {
            connection.Dispose();
        }
    }

    private static List<string> CommonArguments(
        string mode,
        SessionId sessionId,
        TargetArchitecture architecture,
        TimeSpan timeout)
    {
        return new List<string>
        {
            mode,
            "--hold-session",
            "--session-id", sessionId.ToString(),
            "--arch", architecture.ToString().ToLowerInvariant(),
            "--timeout-ms", Math.Ceiling(timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task<DebugTargetInfo> RunAsync(
        string enginePath,
        IReadOnlyList<string> arguments,
        SessionId sessionId,
        bool launchedByDebugger,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(enginePath)!
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new FxDbgException(FxDbgErrorCode.EngineExited, $"Engine failed to start: {enginePath}");
        }

        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout + TimeSpan.FromSeconds(2));
        string? output;
        try
        {
            output = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            Terminate(process);
            process.Dispose();
            FxDbgErrorCode code = cancellationToken.IsCancellationRequested
                ? FxDbgErrorCode.OperationCancelled
                : FxDbgErrorCode.OperationTimedOut;
            throw new FxDbgException(code, $"Engine operation did not complete within {timeout.TotalSeconds} seconds.", exception);
        }

        if (output is null)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            int exitCode = process.ExitCode;
            process.Dispose();
            throw ParseFailure(error, exitCode);
        }

        try
        {
            DebugTargetInfo target = ParseSuccess(output, launchedByDebugger);
            var connection = new EngineConnection(process, stderr, process.StandardOutput.ReadToEndAsync());
            lock (gate)
            {
                if (disposed)
                {
                    connection.Dispose();
                    throw new ObjectDisposedException(nameof(EngineProcessHost));
                }

                if (connections.ContainsKey(sessionId))
                {
                    connection.Dispose();
                    throw new FxDbgException(FxDbgErrorCode.InvalidRequest, $"Session {sessionId} already has an Engine process.");
                }

                connections.Add(sessionId, connection);
            }

            return target;
        }
        catch
        {
            Terminate(process);
            process.Dispose();
            throw;
        }
    }

    private static DebugTargetInfo ParseSuccess(string output, bool launchedByDebugger)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            return new DebugTargetInfo(
                root.GetProperty("processId").GetInt32(),
                ParseArchitecture(root.GetProperty("architecture").GetString()),
                root.GetProperty("runtimeVersion").GetString()!,
                launchedByDebugger,
                ParseSessionState(root.GetProperty("sessionState").GetString()));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FxDbgException(FxDbgErrorCode.EngineExited, "Engine returned an invalid success response.", exception);
        }
    }

    private static FxDbgException ParseFailure(string error, int exitCode)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(error);
            string code = document.RootElement.GetProperty("code").GetString()!;
            string message = document.RootElement.GetProperty("message").GetString()!;
            FxDbgErrorCode mapped = FxDbgErrorCodeWireName.TryParse(code, out FxDbgErrorCode parsed)
                ? parsed
                : FxDbgErrorCode.InternalError;
            return new FxDbgException(mapped, message);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new FxDbgException(
                FxDbgErrorCode.EngineExited,
                $"Engine exited with code {exitCode} and an invalid error response: {error}",
                exception);
        }
    }

    private static TargetArchitecture ParseArchitecture(string? value) => value switch
    {
        "x86" => TargetArchitecture.X86,
        "x64" => TargetArchitecture.X64,
        _ => throw new JsonException("Engine returned an unsupported architecture.")
    };

    private static DebugSessionState ParseSessionState(string? value)
    {
        if (Enum.TryParse(value, true, out DebugSessionState state) &&
            state is DebugSessionState.Running or DebugSessionState.Stopped)
        {
            return state;
        }

        throw new JsonException("Engine returned an unsupported session state.");
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        lock (gate)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(EngineProcessHost));
            }
        }
    }

    private sealed class EngineConnection : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;
        private readonly Task<string> remainingStdout;

        internal EngineConnection(Process process, Task<string> stderr, Task<string> remainingStdout)
        {
            this.process = process;
            this.stderr = stderr;
            this.remainingStdout = remainingStdout;
        }

        public void Dispose()
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000))
                {
                    Terminate(process);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                GC.KeepAlive(stderr);
                GC.KeepAlive(remainingStdout);
                process.Dispose();
            }
        }
    }
}
