using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FxDbg.Debuggees.Filtering
{
    public class BaseFailure : Exception
    {
        public BaseFailure(string message) : base(message) { }
        public override string Message { get { ExceptionFilterScenarios.FormattingCalls++; return "GETTER_EXECUTED"; } }
        public override string ToString() { ExceptionFilterScenarios.FormattingCalls++; return "FORMAT_EXECUTED"; }
    }
    public sealed class DerivedFailure : BaseFailure { public DerivedFailure(string message) : base(message) { } }
    public static class ExceptionFilterScenarios
    {
        public static int FormattingCalls;
        public static void Run(string directory)
        {
            Caught(new InvalidOperationException("ignored-before"));
            Caught(new DerivedFailure("exact"));
            Caught(new BaseFailure("not-namespace"));
            Caught(new Grouping.Failure("prefix-decoy"));
            Caught(new Group.Failure("namespace"));
            Caught(new BaseFailure("base"));
            Caught(new DerivedFailure("derived"));
            Gate(directory, "running");
            for (int index = 0; index < 500; index++) Caught(new InvalidOperationException("noise"));
            Caught(new Group.Failure("dynamic"));
            Caught(new DerivedFailure("cleared"));
            Gate(directory, "cleared");
            Caught(new InvalidOperationException("legacy"));
            Caught(new DerivedFailure("disabled"));
            if (FormattingCalls != 0) throw new InvalidOperationException("Debugger executed target formatting.");
            File.WriteAllText(Path.Combine(directory, "oracle"), "ok");
            throw new BaseFailure("unhandled"); // FILTER_UNHANDLED
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Caught(Exception error)
        {
            try { throw error; } // FILTER_THROW
            catch (Exception) { }
            GC.KeepAlive(error);
        }
        private static void Gate(string directory, string name)
        {
            File.WriteAllText(Path.Combine(directory, name + "-ready"), "ready");
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(Path.Combine(directory, name + "-go")))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Exception filter fixture gate timed out.");
                Thread.Sleep(5);
            }
        }
    }
}
namespace FxDbg.Debuggees.Filtering.Group
{ public sealed class Failure : FxDbg.Debuggees.Filtering.BaseFailure { public Failure(string message) : base(message) { } } }
namespace FxDbg.Debuggees.Filtering.Grouping
{ public sealed class Failure : FxDbg.Debuggees.Filtering.BaseFailure { public Failure(string message) : base(message) { } } }
