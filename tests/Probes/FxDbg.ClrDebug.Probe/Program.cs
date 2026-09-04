using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClrDebug;

namespace FxDbg.ClrDebug.Probe
{
    internal static class Program
    {
        private const string FrameworkRuntimeVersion = "v4.0.30319";
        private const ushort ImageFileMachineUnknown = 0;
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineAmd64 = 0x8664;

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
                ProbeFailureException failure = exception as ProbeFailureException;
                Console.Error.WriteLine(
                    "{\"code\":\"" + EscapeJson(failure == null ? "debug_api_error" : failure.Code) +
                    "\",\"error\":\"" + EscapeJson(exception.GetType().FullName) +
                    "\",\"message\":\"" + EscapeJson(exception.Message) + "\"}");
                return 1;
            }
        }

        private static ProbeResult Run(ProbeOptions options)
        {
            using (var callbackQueue = new BlockingCollection<CallbackEnvelope>())
            using (var callbackDurations = new BlockingCollection<long>())
            using (ProbeLogger logger = ProbeLogger.Create(options))
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
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        callbackQueue.Add(new CallbackEnvelope(
                            eventArgs.Kind,
                            eventArgs.Controller,
                            Thread.CurrentThread.ManagedThreadId));
                    }
                    finally
                    {
                        callbackDurations.Add(Stopwatch.GetTimestamp() - started);
                    }
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
                        ValidateAttachTarget(options.ProcessId.Value);
                        requestedProcess = corDebug.DebugActiveProcess(options.ProcessId.Value, false);
                    }

                    DateTime deadline = DateTime.UtcNow.Add(options.Timeout);
                    bool createProcessSeen = false;
                    bool exitProcessSeen = false;
                    int continueCount = 0;
                    int callbackCount = 0;
                    int commandThreadId = Thread.CurrentThread.ManagedThreadId;
                    var callbackKinds = new List<string>();
                    var callbackThreadIds = new HashSet<int>();
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

                        callbackCount++;
                        callbackKinds.Add(callbackEvent.Kind.ToString());
                        callbackThreadIds.Add(callbackEvent.CallbackThreadId);
                        logger.Write(
                            callbackEvent.Kind,
                            requestedProcess.Id,
                            callbackEvent.CallbackThreadId,
                            commandThreadId);

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
                            continueCount++;
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

                    SpinWait.SpinUntil(
                        delegate { return callbackDurations.Count >= callbackCount; },
                        TimeSpan.FromSeconds(1));
                    if (callbackDurations.Count != callbackCount)
                    {
                        throw new InvalidOperationException(
                            "Callback duration metrics did not match the callback event count.");
                    }
                    long maximumCallbackTicks = 0;
                    foreach (long duration in callbackDurations)
                    {
                        maximumCallbackTicks = Math.Max(maximumCallbackTicks, duration);
                    }

                    CorDebugProcess process = callbackProcess;
                    var result = new ProbeResult(
                        options.Mode.ToString().ToLowerInvariant(),
                        "CreateProcess",
                        process.Id,
                        runtimeInfo.VersionString,
                        IntPtr.Size == 4 ? "x86" : "x64",
                        callbackThreadId,
                        callbackKinds.ToArray(),
                        new List<int>(callbackThreadIds).ToArray(),
                        callbackCount,
                        continueCount,
                        commandThreadId,
                        maximumCallbackTicks * 1000.0 / Stopwatch.Frequency,
                        logger.SessionId);

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

        private static void ValidateAttachTarget(int processId)
        {
            using (Process process = Process.GetProcessById(processId))
            {
                string debuggerArchitecture = IntPtr.Size == 4 ? "x86" : "x64";
                string targetArchitecture = GetProcessArchitecture(process);
                if (!string.Equals(debuggerArchitecture, targetArchitecture, StringComparison.Ordinal))
                {
                    throw new ProbeFailureException(
                        "architecture_mismatch",
                        "Debugger architecture " + debuggerArchitecture +
                        " does not match target architecture " + targetArchitecture + ".");
                }

                bool hasFramework4 = false;
                bool hasFramework2 = false;
                bool hasCoreClr = false;
                foreach (ProcessModule module in process.Modules)
                {
                    string moduleName = module.ModuleName.ToLowerInvariant();
                    hasFramework4 = hasFramework4 || moduleName == "clr.dll";
                    hasFramework2 = hasFramework2 || moduleName == "mscorwks.dll";
                    hasCoreClr = hasCoreClr || moduleName == "coreclr.dll";
                }

                if (hasCoreClr)
                {
                    throw new ProbeFailureException(
                        "coreclr_not_supported",
                        "Target uses CoreCLR; this probe supports .NET Framework CLR v4 only.");
                }

                if (hasFramework2)
                {
                    throw new ProbeFailureException(
                        "unsupported_clr_version",
                        "Target uses CLR v2.0.50727; only CLR v4.0.30319 is supported.");
                }

                if (!hasFramework4)
                {
                    throw new ProbeFailureException(
                        "not_managed_process",
                        "Target has no loaded .NET Framework CLR module.");
                }
            }
        }

        private static string GetProcessArchitecture(Process process)
        {
            ushort processMachine;
            ushort nativeMachine;
            if (!IsWow64Process2(process.Handle, out processMachine, out nativeMachine))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "IsWow64Process2 failed.");
            }

            ushort effectiveMachine = processMachine == ImageFileMachineUnknown
                ? nativeMachine
                : processMachine;
            if (effectiveMachine == ImageFileMachineI386)
            {
                return "x86";
            }

            if (effectiveMachine == ImageFileMachineAmd64)
            {
                return "x64";
            }

            throw new ProbeFailureException(
                "unsupported_architecture",
                "Target machine type 0x" + effectiveMachine.ToString("X4", CultureInfo.InvariantCulture) +
                " is not supported.");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(
            IntPtr processHandle,
            out ushort processMachine,
            out ushort nativeMachine);

        private enum ProbeMode
        {
            Launch,
            Attach
        }

        private enum ProbeLogLevel
        {
            Off,
            Info,
            Trace
        }

        private sealed class CallbackEnvelope
        {
            internal CallbackEnvelope(
                CorDebugManagedCallbackKind kind,
                CorDebugController controller,
                int callbackThreadId)
            {
                Kind = kind;
                Controller = controller;
                CallbackThreadId = callbackThreadId;
            }

            internal CorDebugManagedCallbackKind Kind { get; private set; }

            internal CorDebugController Controller { get; private set; }

            internal int CallbackThreadId { get; private set; }
        }

        private sealed class ProbeFailureException : Exception
        {
            internal ProbeFailureException(string code, string message)
                : base(message)
            {
                Code = code;
            }

            internal string Code { get; private set; }
        }

        private sealed class ProbeLogger : IDisposable
        {
            private readonly ProbeLogLevel logLevel;
            private readonly StreamWriter writer;

            private ProbeLogger(ProbeLogLevel logLevel, StreamWriter writer, string sessionId)
            {
                this.logLevel = logLevel;
                this.writer = writer;
                SessionId = sessionId;
            }

            internal string SessionId { get; private set; }

            internal static ProbeLogger Create(ProbeOptions options)
            {
                string sessionId = Guid.NewGuid().ToString("D");
                if (options.LogLevel == ProbeLogLevel.Off)
                {
                    return new ProbeLogger(options.LogLevel, null, sessionId);
                }

                string fullPath = Path.GetFullPath(options.LogFilePath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var writer = new StreamWriter(fullPath, false, new UTF8Encoding(false));
                writer.AutoFlush = true;
                return new ProbeLogger(options.LogLevel, writer, sessionId);
            }

            internal void Write(
                CorDebugManagedCallbackKind callbackKind,
                int processId,
                int callbackThreadId,
                int commandThreadId)
            {
                if (logLevel == ProbeLogLevel.Off ||
                    (logLevel == ProbeLogLevel.Info && !IsCoreCallback(callbackKind)))
                {
                    return;
                }

                writer.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"timestamp\":\"{0}\",\"level\":\"{1}\",\"sessionId\":\"{2}\",\"processId\":{3},\"callback\":\"{4}\",\"callbackThreadId\":{5},\"commandThreadId\":{6}}}",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    logLevel.ToString().ToLowerInvariant(),
                    EscapeJson(SessionId),
                    processId,
                    callbackKind,
                    callbackThreadId,
                    commandThreadId));
            }

            public void Dispose()
            {
                if (writer != null)
                {
                    writer.Dispose();
                }
            }

            private static bool IsCoreCallback(CorDebugManagedCallbackKind callbackKind)
            {
                return callbackKind == CorDebugManagedCallbackKind.CreateProcess ||
                    callbackKind == CorDebugManagedCallbackKind.CreateAppDomain ||
                    callbackKind == CorDebugManagedCallbackKind.LoadAssembly ||
                    callbackKind == CorDebugManagedCallbackKind.LoadModule ||
                    callbackKind == CorDebugManagedCallbackKind.CreateThread;
            }
        }

        private sealed class ProbeResult
        {
            internal ProbeResult(
                string mode,
                string callback,
                int processId,
                string runtimeVersion,
                string debuggerArchitecture,
                int callbackThreadId,
                string[] callbackKinds,
                int[] callbackThreadIds,
                int callbackCount,
                int continueCount,
                int commandThreadId,
                double maxCallbackDurationMilliseconds,
                string sessionId)
            {
                Mode = mode;
                Callback = callback;
                ProcessId = processId;
                RuntimeVersion = runtimeVersion;
                DebuggerArchitecture = debuggerArchitecture;
                CallbackThreadId = callbackThreadId;
                CallbackKinds = callbackKinds;
                CallbackThreadIds = callbackThreadIds;
                CallbackCount = callbackCount;
                ContinueCount = continueCount;
                CommandThreadId = commandThreadId;
                MaxCallbackDurationMilliseconds = maxCallbackDurationMilliseconds;
                SessionId = sessionId;
            }

            internal string Mode { get; private set; }

            internal string Callback { get; private set; }

            internal int ProcessId { get; private set; }

            internal string RuntimeVersion { get; private set; }

            internal string DebuggerArchitecture { get; private set; }

            internal int CallbackThreadId { get; private set; }

            internal string[] CallbackKinds { get; private set; }

            internal int[] CallbackThreadIds { get; private set; }

            internal int CallbackCount { get; private set; }

            internal int ContinueCount { get; private set; }

            internal int CommandThreadId { get; private set; }

            internal double MaxCallbackDurationMilliseconds { get; private set; }

            internal string SessionId { get; private set; }

            internal string ToJson()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"mode\":\"{0}\",\"callback\":\"{1}\",\"processId\":{2},\"runtimeVersion\":\"{3}\",\"debuggerArchitecture\":\"{4}\",\"callbackThreadId\":{5},\"callbackKinds\":{6},\"callbackThreadIds\":{7},\"callbackCount\":{8},\"continueCount\":{9},\"commandThreadId\":{10},\"maxCallbackDurationMilliseconds\":{11},\"sessionId\":\"{12}\",\"managedException\":null}}",
                    EscapeJson(Mode),
                    EscapeJson(Callback),
                    ProcessId,
                    EscapeJson(RuntimeVersion),
                    EscapeJson(DebuggerArchitecture),
                    CallbackThreadId,
                    SerializeStringArray(CallbackKinds),
                    SerializeIntArray(CallbackThreadIds),
                    CallbackCount,
                    ContinueCount,
                    CommandThreadId,
                    MaxCallbackDurationMilliseconds.ToString("0.######", CultureInfo.InvariantCulture),
                    EscapeJson(SessionId));
            }

            private static string SerializeStringArray(IEnumerable<string> values)
            {
                var escaped = new List<string>();
                foreach (string value in values)
                {
                    escaped.Add("\"" + EscapeJson(value) + "\"");
                }

                return "[" + string.Join(",", escaped.ToArray()) + "]";
            }

            private static string SerializeIntArray(IEnumerable<int> values)
            {
                var serialized = new List<string>();
                foreach (int value in values)
                {
                    serialized.Add(value.ToString(CultureInfo.InvariantCulture));
                }

                return "[" + string.Join(",", serialized.ToArray()) + "]";
            }
        }

        private sealed class ProbeOptions
        {
            private ProbeOptions(
                ProbeMode mode,
                string targetPath,
                int? processId,
                TimeSpan timeout,
                ProbeLogLevel logLevel,
                string logFilePath)
            {
                Mode = mode;
                TargetPath = targetPath;
                ProcessId = processId;
                Timeout = timeout;
                LogLevel = logLevel;
                LogFilePath = logFilePath;
            }

            internal ProbeMode Mode { get; private set; }

            internal string TargetPath { get; private set; }

            internal int? ProcessId { get; private set; }

            internal TimeSpan Timeout { get; private set; }

            internal ProbeLogLevel LogLevel { get; private set; }

            internal string LogFilePath { get; private set; }

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
                ProbeLogLevel logLevel = ParseLogLevel(values);
                string logFilePath;
                values.TryGetValue("--log-file", out logFilePath);
                if (logLevel != ProbeLogLevel.Off && string.IsNullOrWhiteSpace(logFilePath))
                {
                    throw new ArgumentException("--log-file is required when callback logging is enabled.");
                }

                if (mode == ProbeMode.Launch)
                {
                    string targetPath;
                    if (!values.TryGetValue("--target", out targetPath) || !File.Exists(targetPath))
                    {
                        throw new ArgumentException("Launch requires --target pointing to an existing executable.");
                    }

                    return new ProbeOptions(
                        mode,
                        Path.GetFullPath(targetPath),
                        null,
                        TimeSpan.FromSeconds(timeoutSeconds),
                        logLevel,
                        logFilePath);
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
                    TimeSpan.FromSeconds(timeoutSeconds),
                    logLevel,
                    logFilePath);
            }

            private static ProbeLogLevel ParseLogLevel(IDictionary<string, string> values)
            {
                string value;
                if (!values.TryGetValue("--log-level", out value) ||
                    string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
                {
                    return ProbeLogLevel.Off;
                }

                if (string.Equals(value, "info", StringComparison.OrdinalIgnoreCase))
                {
                    return ProbeLogLevel.Info;
                }

                if (string.Equals(value, "trace", StringComparison.OrdinalIgnoreCase))
                {
                    return ProbeLogLevel.Trace;
                }

                throw new ArgumentException("Unsupported --log-level: " + value);
            }
        }
    }
}
