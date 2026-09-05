using System;
using System.Globalization;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Engine.Scheduling;
using FxDbg.Interop;

namespace FxDbg.Engine;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "serve") return EngineServer.Run(args);
            EngineOptions options = EngineOptions.Parse(args);
            EngineTargetValidator.RequireCurrentArchitecture(options.Architecture);
            FrameworkDebugSession? session = null;
            using var scheduler = new SingleThreadCommandScheduler(
                "FxDbg.Engine.CommandScheduler",
                cancellationToken => session?.PumpNextCallback(TimeSpan.FromMilliseconds(10), cancellationToken));
            try
            {
                session = scheduler.EnqueueAsync(
                        _ => options.Mode == EngineMode.Launch ? Launch(options) : Attach(options),
                        AddShutdownGrace(options.Timeout))
                    .GetAwaiter()
                    .GetResult();
                ContinuePairingSnapshot? verification = null;
                if (options.VerificationCycles > 0)
                {
                    FrameworkDebugSession captured = session;
                    verification = scheduler.EnqueueAsync(
                            cancellationToken => captured.RunPauseContinueCycles(
                                options.VerificationCycles,
                                options.Timeout,
                                cancellationToken),
                            AddShutdownGrace(options.Timeout))
                        .GetAwaiter()
                        .GetResult();
                }

                DebugTargetInfo target = session.Target;
                Console.WriteLine(
                    "{\"ok\":true,\"sessionId\":\"" + Escape(options.SessionId.ToString()) +
                    "\",\"processId\":" + target.ProcessId.ToString(CultureInfo.InvariantCulture) +
                    ",\"architecture\":\"" + Format(target.Architecture) +
                    "\",\"runtimeVersion\":\"" + Escape(target.RuntimeVersion) +
                    "\",\"sessionState\":\"" + target.SessionState.ToString().ToLowerInvariant() + "\"" +
                    FormatVerification(verification) + "}");
                Console.Out.Flush();
                if (options.HoldSession)
                {
                    WaitUntilInputCloses();
                }
                else
                {
                    Thread.Sleep(250);
                }
            }
            finally
            {
                if (session is not null)
                {
                    FrameworkDebugSession captured = session;
                    scheduler.EnqueueAsync(
                            _ =>
                            {
                                captured.Dispose();
                                return true;
                            },
                            TimeSpan.FromSeconds(5))
                        .GetAwaiter()
                        .GetResult();
                    session = null;
                }
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

    internal static FrameworkDebugSession Launch(EngineOptions options)
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
            request.Timeout,
            request.SessionId);
    }

    internal static FrameworkDebugSession Attach(EngineOptions options)
    {
        int processId = options.ProcessId!.Value;
        EngineTargetValidator.ValidateAttachRuntime(processId, options.Architecture);
        var bootstrap = new FrameworkDebuggerBootstrap();
        return bootstrap.Attach(processId, options.Architecture, options.Timeout, options.SessionId);
    }

    private static void WaitUntilInputCloses()
    {
        while (Console.In.ReadLine() is not null)
        {
        }
    }

    private static TimeSpan AddShutdownGrace(TimeSpan timeout)
    {
        double milliseconds = Math.Min(TimeSpan.MaxValue.TotalMilliseconds, timeout.TotalMilliseconds + 1_000);
        return TimeSpan.FromMilliseconds(milliseconds);
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

    private static string FormatVerification(ContinuePairingSnapshot? verification)
    {
        if (verification is null)
        {
            return string.Empty;
        }

        return ",\"verificationStopCount\":" + verification.StopCount.ToString(CultureInfo.InvariantCulture) +
            ",\"verificationContinueCount\":" + verification.ContinueCount.ToString(CultureInfo.InvariantCulture) +
            ",\"verificationOutstandingStopCount\":" + verification.OutstandingStopCount.ToString(CultureInfo.InvariantCulture);
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");
}
