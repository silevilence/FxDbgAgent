using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using ClrDebug;

namespace FxDbg.Breakpoint.Probe
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
                Console.WriteLine(Run(options).ToJson());
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "{\"code\":\"breakpoint_probe_failed\",\"error\":\"" +
                    EscapeJson(exception.GetType().FullName) +
                    "\",\"message\":\"" + EscapeJson(exception.Message) + "\"}");
                return 1;
            }
        }

        private static ProbeResult Run(ProbeOptions options)
        {
            using (var callbackQueue = new BlockingCollection<CallbackEnvelope>())
            {
                var callback = new CorDebugManagedCallback();
                callback.OnAnyEvent += delegate(object sender, CorDebugManagedCallbackEventArgs eventArgs)
                {
                    var loadModule = eventArgs as LoadModuleCorDebugManagedCallbackEventArgs;
                    var breakpoint = eventArgs as BreakpointCorDebugManagedCallbackEventArgs;
                    callbackQueue.Add(new CallbackEnvelope(
                        eventArgs.Kind,
                        eventArgs.Controller,
                        loadModule == null ? null : loadModule.Module,
                        breakpoint == null ? null : breakpoint.Thread,
                        Thread.CurrentThread.ManagedThreadId));
                };

                var metaHost = new CLRMetaHost();
                CLRRuntimeInfo runtimeInfo = metaHost.GetRuntime(FrameworkRuntimeVersion);
                CorDebug corDebug = runtimeInfo.GetInterface().CorDebug;
                CorDebugProcess process = null;
                int? launchedProcessId = null;
                try
                {
                    corDebug.Initialize();
                    corDebug.SetManagedHandler(callback);
                    process = corDebug.CreateProcess(
                        QuoteCommandLine(options.TargetPath) + " --breakpoint-probe");
                    int processId = process.Id;
                    launchedProcessId = processId;

                    var bindingStates = new List<string> { "pending" };
                    var frames = new List<RawStackFrame>();
                    string bindingError = null;
                    bool hit = false;
                    bool exited = false;
                    int callbackThreadId = 0;
                    int commandThreadId = Thread.CurrentThread.ManagedThreadId;
                    int callbackCount = 0;
                    int continueCount = 0;
                    DateTime deadline = DateTime.UtcNow.Add(options.Timeout);
                    CorDebugFunctionBreakpoint activeBreakpoint = null;

                    while (!exited)
                    {
                        int remainingMilliseconds = Math.Max(
                            1,
                            (int)Math.Min(int.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds));
                        CallbackEnvelope callbackEvent;
                        if (!callbackQueue.TryTake(out callbackEvent, remainingMilliseconds))
                        {
                            throw new TimeoutException(
                                "Breakpoint probe did not complete within " +
                                options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) +
                                " seconds.");
                        }

                        callbackCount++;
                        bool continueRequired = callbackEvent.Kind != CorDebugManagedCallbackKind.ExitProcess;
                        try
                        {
                            callbackThreadId = callbackEvent.CallbackThreadId;
                            if (callbackEvent.Kind == CorDebugManagedCallbackKind.LoadModule &&
                                callbackEvent.Module != null &&
                                string.Equals(
                                    Path.GetFileName(callbackEvent.Module.Name),
                                    options.ModuleName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    CorDebugFunction function = callbackEvent.Module.GetFunctionFromToken(
                                        new mdMethodDef(options.MethodToken));
                                    activeBreakpoint = function.ILCode.CreateBreakpoint(options.IlOffset);
                                    activeBreakpoint.Activate(true);
                                    bindingStates.Add("verified");
                                }
                                catch (Exception exception)
                                {
                                    bindingStates.Add("unresolved");
                                    bindingError = exception.GetType().FullName + ": " + exception.Message;
                                }
                            }

                            if (callbackEvent.Kind == CorDebugManagedCallbackKind.Breakpoint)
                            {
                                if (callbackEvent.Thread == null)
                                {
                                    throw new InvalidOperationException("Breakpoint callback did not include a thread.");
                                }

                                frames.AddRange(CaptureManagedFrames(callbackEvent.Thread));
                                hit = true;
                            }

                            if (callbackEvent.Kind == CorDebugManagedCallbackKind.ExitProcess)
                            {
                                exited = true;
                                process = null;
                            }
                        }
                        finally
                        {
                            if (continueRequired)
                            {
                                callbackEvent.Controller.Continue(false);
                                continueCount++;
                            }
                        }
                    }

                    GC.KeepAlive(activeBreakpoint);
                    if (continueCount != callbackCount - 1)
                    {
                        throw new InvalidOperationException(
                            "Every callback except ExitProcess must have exactly one Continue.");
                    }

                    string finalBindingState = bindingStates[bindingStates.Count - 1];
                    if (finalBindingState == "verified" && !hit)
                    {
                        throw new InvalidOperationException("The verified breakpoint was not hit.");
                    }

                    var result = new ProbeResult(
                        finalBindingState,
                        bindingStates.ToArray(),
                        bindingError,
                        hit,
                        processId,
                        runtimeInfo.VersionString,
                        callbackThreadId,
                        commandThreadId,
                        callbackCount,
                        continueCount,
                        frames.ToArray());
                    return result;
                }
                finally
                {
                    try
                    {
                        if (process != null)
                        {
                            try
                            {
                                process.Terminate(1);
                            }
                            finally
                            {
                                WaitForProcessExit(launchedProcessId.Value, TimeSpan.FromSeconds(5));
                            }
                        }
                    }
                    finally
                    {
                        corDebug.Terminate();
                    }
                }
            }
        }

        private static void WaitForProcessExit(int processId, TimeSpan timeout)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        throw new TimeoutException(
                            "Launched debuggee did not terminate within " +
                            timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) +
                            " seconds.");
                    }
                }
            }
            catch (ArgumentException)
            {
                // The process exited before Process.GetProcessById observed it.
            }
        }

        private static IEnumerable<RawStackFrame> CaptureManagedFrames(CorDebugThread thread)
        {
            var frames = new List<RawStackFrame>();
            foreach (CorDebugChain chain in thread.Chains)
            {
                if (!chain.IsManaged)
                {
                    continue;
                }

                foreach (CorDebugFrame frame in chain.Frames)
                {
                    CorDebugILFrame ilFrame = frame as CorDebugILFrame;
                    if (ilFrame == null)
                    {
                        ICorDebugILFrame rawIlFrame = frame.Raw as ICorDebugILFrame;
                        if (rawIlFrame == null)
                        {
                            continue;
                        }

                        ilFrame = new CorDebugILFrame(rawIlFrame);
                    }

                    CorDebugFunction function = frame.Function;
                    GetIPResult instructionPointer = ilFrame.IP;
                    frames.Add(new RawStackFrame(
                        function.Module.Name,
                        unchecked((int)function.Token.Value),
                        instructionPointer.pnOffset,
                        instructionPointer.pMappingResult.ToString()));
                }
            }

            return frames;
        }

        private static string QuoteCommandLine(string value)
        {
            return '"' + value.Replace("\"", "\\\"") + '"';
        }

        private static string EscapeJson(string value)
        {
            return value == null
                ? string.Empty
                : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private sealed class CallbackEnvelope
        {
            internal CallbackEnvelope(
                CorDebugManagedCallbackKind kind,
                CorDebugController controller,
                CorDebugModule module,
                CorDebugThread thread,
                int callbackThreadId)
            {
                Kind = kind;
                Controller = controller;
                Module = module;
                Thread = thread;
                CallbackThreadId = callbackThreadId;
            }

            internal CorDebugManagedCallbackKind Kind { get; private set; }
            internal CorDebugController Controller { get; private set; }
            internal CorDebugModule Module { get; private set; }
            internal CorDebugThread Thread { get; private set; }
            internal int CallbackThreadId { get; private set; }
        }

        private sealed class RawStackFrame
        {
            internal RawStackFrame(string module, int methodToken, int ilOffset, string mapping)
            {
                Module = module;
                MethodToken = methodToken;
                IlOffset = ilOffset;
                Mapping = mapping;
            }

            internal string Module { get; private set; }
            internal int MethodToken { get; private set; }
            internal int IlOffset { get; private set; }
            internal string Mapping { get; private set; }

            internal string ToJson()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"module\":\"{0}\",\"methodToken\":\"0x{1:X8}\",\"ilOffset\":{2},\"mapping\":\"{3}\"}}",
                    EscapeJson(Module),
                    MethodToken,
                    IlOffset,
                    EscapeJson(Mapping));
            }
        }

        private sealed class ProbeResult
        {
            internal ProbeResult(
                string bindingState,
                string[] bindingStates,
                string bindingError,
                bool hit,
                int processId,
                string runtimeVersion,
                int callbackThreadId,
                int commandThreadId,
                int callbackCount,
                int continueCount,
                RawStackFrame[] frames)
            {
                BindingState = bindingState;
                BindingStates = bindingStates;
                BindingError = bindingError;
                Hit = hit;
                ProcessId = processId;
                RuntimeVersion = runtimeVersion;
                CallbackThreadId = callbackThreadId;
                CommandThreadId = commandThreadId;
                CallbackCount = callbackCount;
                ContinueCount = continueCount;
                Frames = frames;
            }

            internal string BindingState { get; private set; }
            internal string[] BindingStates { get; private set; }
            internal string BindingError { get; private set; }
            internal bool Hit { get; private set; }
            internal int ProcessId { get; private set; }
            internal string RuntimeVersion { get; private set; }
            internal int CallbackThreadId { get; private set; }
            internal int CommandThreadId { get; private set; }
            internal int CallbackCount { get; private set; }
            internal int ContinueCount { get; private set; }
            internal RawStackFrame[] Frames { get; private set; }

            internal string ToJson()
            {
                var states = new List<string>();
                foreach (string state in BindingStates)
                {
                    states.Add("\"" + EscapeJson(state) + "\"");
                }

                var frames = new List<string>();
                foreach (RawStackFrame frame in Frames)
                {
                    frames.Add(frame.ToJson());
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"bindingState\":\"{0}\",\"bindingStates\":[{1}],\"bindingError\":{2},\"hit\":{3},\"processId\":{4},\"runtimeVersion\":\"{5}\",\"debuggerArchitecture\":\"{6}\",\"callbackThreadId\":{7},\"commandThreadId\":{8},\"callbackCount\":{9},\"continueCount\":{10},\"frames\":[{11}]}}",
                    EscapeJson(BindingState),
                    string.Join(",", states.ToArray()),
                    BindingError == null ? "null" : "\"" + EscapeJson(BindingError) + "\"",
                    Hit ? "true" : "false",
                    ProcessId,
                    EscapeJson(RuntimeVersion),
                    IntPtr.Size == 4 ? "x86" : "x64",
                    CallbackThreadId,
                    CommandThreadId,
                    CallbackCount,
                    ContinueCount,
                    string.Join(",", frames.ToArray()));
            }
        }

        private sealed class ProbeOptions
        {
            private ProbeOptions(
                string targetPath,
                string moduleName,
                int methodToken,
                int ilOffset,
                TimeSpan timeout)
            {
                TargetPath = targetPath;
                ModuleName = moduleName;
                MethodToken = methodToken;
                IlOffset = ilOffset;
                Timeout = timeout;
            }

            internal string TargetPath { get; private set; }
            internal string ModuleName { get; private set; }
            internal int MethodToken { get; private set; }
            internal int IlOffset { get; private set; }
            internal TimeSpan Timeout { get; private set; }

            internal static ProbeOptions Parse(string[] args)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int index = 0; index < args.Length; index += 2)
                {
                    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Options must use --name value pairs.");
                    }

                    values.Add(args[index], args[index + 1]);
                }

                string target = Required(values, "--target");
                if (!File.Exists(target))
                {
                    throw new FileNotFoundException("Target executable does not exist.", target);
                }

                string timeoutValue;
                int timeoutSeconds = values.TryGetValue("--timeout-seconds", out timeoutValue)
                    ? int.Parse(timeoutValue, CultureInfo.InvariantCulture)
                    : 20;
                return new ProbeOptions(
                    Path.GetFullPath(target),
                    Required(values, "--module"),
                    ParseInteger(Required(values, "--method-token")),
                    ParseInteger(Required(values, "--il-offset")),
                    TimeSpan.FromSeconds(timeoutSeconds));
            }

            private static int ParseInteger(string value)
            {
                return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(value, CultureInfo.InvariantCulture);
            }

            private static string Required(IDictionary<string, string> values, string name)
            {
                string value;
                if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Missing required option " + name + ".");
                }

                return value;
            }
        }
    }
}
