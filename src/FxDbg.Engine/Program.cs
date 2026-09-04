using System;
using System.Globalization;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Interop;

namespace FxDbg.Engine;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            EngineOptions options = EngineOptions.Parse(args);
            EngineTargetValidator.RequireCurrentArchitecture(options.Architecture);
            using FrameworkDebugSession session = options.Mode == EngineMode.Launch
                ? Launch(options)
                : Attach(options);
            DebugTargetInfo target = session.Target;
            Console.WriteLine(
                "{\"ok\":true,\"sessionId\":\"" + Escape(options.SessionId.ToString()) +
                "\",\"processId\":" + target.ProcessId.ToString(CultureInfo.InvariantCulture) +
                ",\"architecture\":\"" + Format(target.Architecture) +
                "\",\"runtimeVersion\":\"" + Escape(target.RuntimeVersion) +
                "\",\"sessionState\":\"" + target.SessionState.ToString().ToLowerInvariant() + "\"}");
            Console.Out.Flush();
            if (options.HoldSession)
            {
                PumpUntilInputCloses(session);
            }
            else
            {
                DateTime initializationDeadline = DateTime.UtcNow.AddMilliseconds(250);
                session.PumpCallbacksUntil(() => DateTime.UtcNow >= initializationDeadline);
            }

            return 0;
        }
        catch (Exception exception)
        {
            FxDbgException? failure = exception as FxDbgException;
            FxDbgErrorCode code = failure?.Code ?? FxDbgErrorCode.InternalError;
            Console.Error.WriteLine(
                "{\"ok\":false,\"code\":\"" + FxDbgErrorCodeWireName.Format(code) +
                "\",\"message\":\"" + Escape(exception.Message) + "\"}");
            return ExitCode(code);
        }
    }

    private static FrameworkDebugSession Launch(EngineOptions options)
    {
        LaunchRequest request = options.ToLaunchRequest();
        using EngineLaunchPreparation prepared = EngineLaunchPreparation.Create(request);
        var bootstrap = new FrameworkDebuggerBootstrap();
        return bootstrap.Launch(
            prepared.CommandLine,
            prepared.WorkingDirectory,
            prepared.Environment,
            request.Architecture,
            request.StopAtEntry,
            request.Timeout);
    }

    private static FrameworkDebugSession Attach(EngineOptions options)
    {
        int processId = options.ProcessId!.Value;
        EngineTargetValidator.ValidateAttachRuntime(processId, options.Architecture);
        var bootstrap = new FrameworkDebuggerBootstrap();
        return bootstrap.Attach(processId, options.Architecture, options.Timeout);
    }

    private static void PumpUntilInputCloses(FrameworkDebugSession session)
    {
        using var inputClosed = new ManualResetEvent(false);
        var inputMonitor = new Thread(() =>
        {
            try
            {
                while (Console.In.ReadLine() is not null)
                {
                }
            }
            finally
            {
                inputClosed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "FxDbg.Engine.InputMonitor"
        };
        inputMonitor.Start();
        session.PumpCallbacksUntil(() => inputClosed.WaitOne(0));
    }

    private static int ExitCode(FxDbgErrorCode code) => code switch
    {
        FxDbgErrorCode.InvalidRequest or FxDbgErrorCode.InvalidExecutable or FxDbgErrorCode.TargetNotFound => 2,
        FxDbgErrorCode.ArchitectureMismatch or FxDbgErrorCode.UnsupportedArchitecture => 10,
        FxDbgErrorCode.NotManagedProcess => 11,
        FxDbgErrorCode.CoreClrNotSupported => 12,
        FxDbgErrorCode.UnsupportedClrVersion => 13,
        FxDbgErrorCode.AccessDenied => 14,
        FxDbgErrorCode.OperationTimedOut => 15,
        _ => 20
    };

    private static string Format(TargetArchitecture value) => value.ToString().ToLowerInvariant();

    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");
}
