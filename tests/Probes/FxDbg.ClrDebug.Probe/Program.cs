using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using ClrDebug;

namespace FxDbg.ClrDebug.Probe
{
    internal static class Program
    {
        private const string FrameworkRuntimeVersion = "v4.0.30319";

        [MTAThread]
        private static int Main(string[] args)
        {
            try
            {
                ProbeOptions options = ProbeOptions.Parse(args);
                ProbeResult result = Run(options);
                Console.WriteLine(result.ToJson());
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "{\"error\":\"" + EscapeJson(exception.GetType().FullName) +
                    "\",\"message\":\"" + EscapeJson(exception.Message) + "\"}");
                return 1;
            }
        }

        private static ProbeResult Run(ProbeOptions options)
        {
            using (var callbackQueue = new BlockingCollection<CallbackEnvelope>())
            {
                CorDebugProcess callbackProcess = null;
                int callbackThreadId = 0;
                var callback = new CorDebugManagedCallback();
                callback.OnCreateProcess += delegate(object sender, CreateProcessCorDebugManagedCallbackEventArgs eventArgs)
                {
                    callbackProcess = eventArgs.Process;
                    callbackThreadId = Thread.CurrentThread.ManagedThreadId;
                };
                callback.OnAnyEvent += delegate(object sender, CorDebugManagedCallbackEventArgs eventArgs)
                {
                    callbackQueue.Add(new CallbackEnvelope(eventArgs.Kind, eventArgs.Controller));
                };

                var metaHost = new CLRMetaHost();
                CLRRuntimeInfo runtimeInfo = metaHost.GetRuntime(FrameworkRuntimeVersion);
                CorDebug corDebug = runtimeInfo.GetInterface().CorDebug;
                CorDebugProcess requestedProcess = null;
                try
                {
                    corDebug.Initialize();
                    corDebug.SetManagedHandler(callback);
                    if (options.Mode == ProbeMode.Launch)
                    {
                        requestedProcess = corDebug.CreateProcess(
                            QuoteCommandLine(options.TargetPath) + " --probe");
                    }
                    else
                    {
                        requestedProcess = corDebug.DebugActiveProcess(options.ProcessId.Value, false);
                    }

                    DateTime deadline = DateTime.UtcNow.Add(options.Timeout);
                    bool createProcessSeen = false;
                    bool exitProcessSeen = false;
                    while (!exitProcessSeen)
                    {
                        int remainingMilliseconds = Math.Max(
                            1,
                            (int)Math.Min(int.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds));
                        int waitMilliseconds = options.Mode == ProbeMode.Attach && createProcessSeen
                            ? Math.Min(250, remainingMilliseconds)
                            : remainingMilliseconds;

                        CallbackEnvelope callbackEvent;
                        if (!callbackQueue.TryTake(out callbackEvent, waitMilliseconds))
                        {
                            if (options.Mode == ProbeMode.Attach && createProcessSeen)
                            {
                                break;
                            }

                            throw new TimeoutException(string.Format(
                                CultureInfo.InvariantCulture,
                                "No CreateProcess callback was received within {0} seconds.",
                                options.Timeout.TotalSeconds));
                        }

                        if (callbackEvent.Kind == CorDebugManagedCallbackKind.CreateProcess)
                        {
                            createProcessSeen = true;
                        }

                        if (callbackEvent.Kind == CorDebugManagedCallbackKind.ExitProcess)
                        {
                            exitProcessSeen = true;
                        }
                        else
                        {
                            callbackEvent.Controller.Continue(false);
                        }
                    }

                    if (!createProcessSeen || callbackProcess == null)
                    {
                        throw new InvalidOperationException("The callback stream did not contain CreateProcess.");
                    }

                    if (requestedProcess == null || callbackProcess.Id != requestedProcess.Id)
                    {
                        throw new InvalidOperationException(
                            "The CreateProcess callback did not match the requested debug process.");
                    }

                    CorDebugProcess process = callbackProcess;
                    var result = new ProbeResult(
                        options.Mode.ToString().ToLowerInvariant(),
                        "CreateProcess",
                        process.Id,
                        runtimeInfo.VersionString,
                        IntPtr.Size == 4 ? "x86" : "x64",
                        callbackThreadId);

                    if (options.Mode == ProbeMode.Attach)
                    {
                        process.Stop(0);
                        process.Detach();
                    }

                    requestedProcess = null;
                    return result;
                }
                finally
                {
                    try
                    {
                        if (requestedProcess != null)
                        {
                            requestedProcess.Stop(0);
                            requestedProcess.Detach();
                        }
                    }
                    finally
                    {
                        corDebug.Terminate();
                    }
                }
            }
        }

        private static string QuoteCommandLine(string value)
        {
            return '"' + value.Replace("\"", "\\\"") + '"';
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private enum ProbeMode
        {
            Launch,
            Attach
        }

        private sealed class CallbackEnvelope
        {
            internal CallbackEnvelope(CorDebugManagedCallbackKind kind, CorDebugController controller)
            {
                Kind = kind;
                Controller = controller;
            }

            internal CorDebugManagedCallbackKind Kind { get; private set; }

            internal CorDebugController Controller { get; private set; }
        }

        private sealed class ProbeResult
        {
            internal ProbeResult(
                string mode,
                string callback,
                int processId,
                string runtimeVersion,
                string debuggerArchitecture,
                int callbackThreadId)
            {
                Mode = mode;
                Callback = callback;
                ProcessId = processId;
                RuntimeVersion = runtimeVersion;
                DebuggerArchitecture = debuggerArchitecture;
                CallbackThreadId = callbackThreadId;
            }

            internal string Mode { get; private set; }

            internal string Callback { get; private set; }

            internal int ProcessId { get; private set; }

            internal string RuntimeVersion { get; private set; }

            internal string DebuggerArchitecture { get; private set; }

            internal int CallbackThreadId { get; private set; }

            internal string ToJson()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"mode\":\"{0}\",\"callback\":\"{1}\",\"processId\":{2},\"runtimeVersion\":\"{3}\",\"debuggerArchitecture\":\"{4}\",\"callbackThreadId\":{5},\"managedException\":null}}",
                    EscapeJson(Mode),
                    EscapeJson(Callback),
                    ProcessId,
                    EscapeJson(RuntimeVersion),
                    EscapeJson(DebuggerArchitecture),
                    CallbackThreadId);
            }
        }

        private sealed class ProbeOptions
        {
            private ProbeOptions(ProbeMode mode, string targetPath, int? processId, TimeSpan timeout)
            {
                Mode = mode;
                TargetPath = targetPath;
                ProcessId = processId;
                Timeout = timeout;
            }

            internal ProbeMode Mode { get; private set; }

            internal string TargetPath { get; private set; }

            internal int? ProcessId { get; private set; }

            internal TimeSpan Timeout { get; private set; }

            internal static ProbeOptions Parse(string[] args)
            {
                if (args.Length == 0)
                {
                    throw new ArgumentException("Expected mode: launch or attach.");
                }

                ProbeMode mode;
                if (args[0] == "launch")
                {
                    mode = ProbeMode.Launch;
                }
                else if (args[0] == "attach")
                {
                    mode = ProbeMode.Attach;
                }
                else
                {
                    throw new ArgumentException("Unsupported mode: " + args[0]);
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int index = 1; index < args.Length; index += 2)
                {
                    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Invalid option near argument " + index + ": " + args[index]);
                    }

                    values.Add(args[index], args[index + 1]);
                }

                string timeoutValue;
                int timeoutSeconds = values.TryGetValue("--timeout-seconds", out timeoutValue)
                    ? int.Parse(timeoutValue, CultureInfo.InvariantCulture)
                    : 15;

                if (mode == ProbeMode.Launch)
                {
                    string targetPath;
                    if (!values.TryGetValue("--target", out targetPath) || !File.Exists(targetPath))
                    {
                        throw new ArgumentException("Launch requires --target pointing to an existing executable.");
                    }

                    return new ProbeOptions(mode, Path.GetFullPath(targetPath), null, TimeSpan.FromSeconds(timeoutSeconds));
                }

                string processIdValue;
                if (!values.TryGetValue("--pid", out processIdValue))
                {
                    throw new ArgumentException("Attach requires --pid.");
                }

                return new ProbeOptions(
                    mode,
                    null,
                    int.Parse(processIdValue, CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(timeoutSeconds));
            }
        }
    }
}
