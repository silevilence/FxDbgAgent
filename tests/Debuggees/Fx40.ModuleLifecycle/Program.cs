using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;

namespace FxDbg.Debuggees
{
    public sealed class Worker : MarshalByRefObject
    {
        public void Run(string directory, int cycle)
        {
            Assembly library = Assembly.LoadFrom(Path.Combine(directory, "Fx40.LateModule.dll"));
            File.WriteAllText(Path.Combine(directory, "loaded-" + cycle), "ready");
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(directory, "go-" + cycle)))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Test gate did not open.");
                Thread.Sleep(10);
            }
            object result = library.GetType("FxDbg.Debuggees.LateCode").GetMethod("Run").Invoke(null, null);
            if ((int)result != 42) throw new InvalidOperationException("Unexpected target result.");
        }
    }

    public class VirtualProofBase
    {
        public static int Calls;
        public int Field = 13;
        public virtual int Value => Field;
        public static void Record() { Calls++; }
    }
    public sealed class InheritedVirtualModel : VirtualProofBase { }

    internal sealed class Program
    {
        private static int executionSink;
        private static int variableSink;
        private static int userCodeCalls;

        private static void Main(string[] args)
        {
            if (args[0] == "--detach-noise") { Filtering.ExceptionFilterScenarios.RunDetachNoise(args[1]); return; }
            if (args[0] == "--conditional-breakpoints") { ConditionalBreakpointScenarios.Run(args[1]); return; }
            if (args[0] == "--exception-filters") { Filtering.ExceptionFilterScenarios.Run(args[1]); return; }
            if (args[0] == "--evaluation")
            {
                var node = new Node(); node.Self = node;
                var matrix = new int[,] { { 10,20 }, { 30,40 } };
                Array shifted = Array.CreateInstance(typeof(int), new[] {2,3}, new[] {-2,5}); shifted.SetValue(99,-1,7);
                variableSink = Node.Counter;
                new Program().EvaluateTarget(42,"abcdefghijklmnop",node,matrix,shifted,9007199254740993L,new string('s',32769));
                if (userCodeCalls != 0 || VirtualProofBase.Calls != 0 || Node.Counter != 777 || node.Label != "node-label" || node.Self != node ||
                    matrix[1,1] != 40 || (int)shifted.GetValue(-1,7) != 99 || variableSink != 42)
                    throw new InvalidOperationException("Evaluation changed target state.");
                if (args.Length > 1) File.WriteAllText(args[1],"ok");
                return;
            }
            if (args[0] == "--breakpoint-race") { RunBreakpointRace(args[1]); return; }
            if (args[0] == "--exceptions")
            {
                try { ThrowObserved("handled-message"); }
                catch (ObservedException) { }
                ThrowObserved("unhandled-message");
                return;
            }
            if (args[0] == "--variables")
            {
                var node = new Node();
                node.Self = node;
                var values = new int[100000];
                values[0] = 10;
                values[99999] = 909;
                variableSink = Node.Counter;
                ObserveVariables(42, "abcdefghijklmnop", node, values, null);
                if (userCodeCalls != 0) throw new InvalidOperationException("Debugger executed target formatting code.");
                return;
            }
            if (args[0] == "--execution") { RunExecution(args[1]); return; }
            for (int cycle = 0; cycle < 3; cycle++)
            {
                AppDomain domain = AppDomain.CreateDomain("LateModule-" + cycle);
                var worker = (Worker)domain.CreateInstanceAndUnwrap(typeof(Worker).Assembly.FullName, typeof(Worker).FullName);
                worker.Run(args[0], cycle);
                AppDomain.Unload(domain);
                File.WriteAllText(Path.Combine(args[0], "unloaded-" + cycle), "done");
            }
        }

        // This fixture isolates native stop counts and needs a bindable loop sequence point.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void RunBreakpointRace(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "race-ready"), "ready");
            for (int index = 0; index < 32; index++)
            {
                while (!File.Exists(Path.Combine(directory, "go-" + index))) Thread.Sleep(10);
                File.WriteAllText(Path.Combine(directory, "hit-" + index), "hit"); // RACE_BREAKPOINT
            }
            while (!File.Exists(Path.Combine(directory, "race-done"))) Thread.Sleep(10);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunExecution(string directory)
        {
            executionSink = AddOne(7); // STEP_CALL
            executionSink += 10; // STEP_AFTER
            executionSink = AddOne(executionSink); // STEP_OVER
            File.WriteAllText(Path.Combine(directory, "execution-ready"), executionSink.ToString()); // STEP_READY
            while (!File.Exists(Path.Combine(directory, "execution-done"))) Thread.Sleep(10);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AddOne(int value)
        { // STEP_INNER_ENTRY
            int result = value + 1; // STEP_INNER
            return result;
        }

        private sealed class Node
        {
            public static int Counter = 777;
            public Node Self;
            public string Label = "node-label";
            public object Empty = null;
            public int Dangerous { get { userCodeCalls++; throw new InvalidOperationException("Getter executed"); } }
            public override string ToString() { userCodeCalls++; return "FORMATTING_EXECUTED"; }
        }

        private sealed class ObservedException : Exception
        {
            internal ObservedException(string message) : base(message) { }
            public override string Message { get { userCodeCalls++; return "custom-message-must-not-run"; } }
            public override string ToString() { userCodeCalls++; return "custom-format-must-not-run"; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowObserved(string message)
        {
            throw new ObservedException(message); // EXCEPTION_THROW
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ObserveVariables(int number, string message, Node node, int[] values, object nothing)
        {
            int localNumber = number * 2;
            string localText = "local-text";
            int optimized = number * 3;
            variableSink = optimized;
            variableSink = number; // VARIABLE_BREAKPOINT
            GC.KeepAlive(localNumber);
            GC.KeepAlive(localText);
            GC.KeepAlive(number);
            GC.KeepAlive(message);
            GC.KeepAlive(node);
            GC.KeepAlive(values);
            GC.KeepAlive(nothing);
        }

        public int fallbackNumber = 17;
        public int number = 900;
        public int[] Values = new[] {10,20,30};
        public Array ShiftedVector = Array.CreateInstance(typeof(int), new[] {3}, new[] {-2});
        public System.Collections.Generic.List<int> List = new System.Collections.Generic.List<int>(new[] {1,2,3});
        public System.Collections.Generic.Dictionary<int,int> Dictionary = new System.Collections.Generic.Dictionary<int,int> { {1,2} };
        public System.Collections.Generic.Queue<int> Queue = new System.Collections.Generic.Queue<int>(new[] {1,2});
        public System.Collections.Generic.Stack<int> Stack = new System.Collections.Generic.Stack<int>(new[] {1});
        public PropertyModel Properties = new PropertyModel();
        public PropertyBase Virtual = new PropertyModel();
        public VirtualProofBase OddVirtual = MakeHiddenOverride(true);
        public VirtualProofBase ImplicitVirtual = MakeHiddenOverride(false);
        public VirtualProofBase InheritedVirtual = new InheritedVirtualModel();
        public PropertyBase ComputedVirtual = new ComputedPropertyModel();
        public ValueModel Struct = new ValueModel { Field = 31 };
        public GenericModel<string> Generic = new GenericModel<string> { Field = "generic" };
        public ManyProperties Many = new ManyProperties();
        public ManyFields Huge = new ManyFields();
        private static VirtualProofBase MakeHiddenOverride(bool explicitMapping)
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("HiddenGetter"+Guid.NewGuid().ToString("N")), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("hidden");
            var type = module.DefineType("HiddenGetter", TypeAttributes.Public, typeof(VirtualProofBase));
            var method = type.DefineMethod(explicitMapping ? "HiddenImplementation" : "get_Value", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(VirtualProofBase).GetMethod("Record"));
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 99); il.Emit(System.Reflection.Emit.OpCodes.Ret);
            if (explicitMapping) type.DefineMethodOverride(method, typeof(VirtualProofBase).GetProperty("Value").GetGetMethod());
            return (VirtualProofBase)Activator.CreateInstance(type.CreateType());
        }
        private interface IProperty { int Explicit { get; } }
        public class PropertyBase
        {
            protected int baseValue = 11;
            public virtual int Value => baseValue;
            public int Shadow => baseValue;
        }
        public sealed class PropertyModel : PropertyBase, IProperty
        {
            public byte Tiny = 5;
            public int Promoted => Tiny;
            public byte StillTiny => Tiny;
            public uint Unsigned = 4000000000;
            public uint UnsignedProperty => Unsigned;
            public ulong Wide = 18000000000000000000;
            public ulong WideProperty => Wide;
            public float Real = 1.5f;
            public double RealProperty => Real;
            public int Field = 23;
            public static int StaticField = 37;
            public new int Shadow = 43;
            public int Auto { get; private set; }
            public PropertyModel() { Auto = 19; }
            public int Pure => Field;
            public override int Value => Field;
            public static int Static => StaticField;
            public int Constant => 7;
            public bool Flag => true;
            public char Letter => 'Z';
            public long Long => 1234567890123L;
            public float Single => 1.5f;
            public double Double => 2.5;
            public string Null => null;
            public int Computed => Field + 1;
            public int SideEffect { get { userCodeCalls++; return Field; } }
            public int WithFinally { get { try { return Field; } finally { userCodeCalls++; } } }
            public int Cold => ColdState.Value;
            public int this[int index] => Field;
            int IProperty.Explicit => Field;
        }
        public sealed class ComputedPropertyModel : PropertyBase
        {
            public override int Value { get { userCodeCalls++; return baseValue; } }
        }
        public struct ValueModel
        {
            public int Field;
            public int Pure => Field;
        }
        private static class ColdState
        {
            internal static int Value;
            static ColdState() { userCodeCalls++; Value = 97; }
        }
        public sealed class GenericModel<T>
        {
            public T Field;
            public static int StaticField;
            static GenericModel() { if (typeof(T) == typeof(int)) userCodeCalls++; StaticField = 67; }
            public T Pure => Field;
            public int OtherInstantiation => GenericModel<int>.StaticField;
        }
        public sealed class ManyProperties
        {
            public int Field = 1;
            public int P0 => Field;
            public int P1 => Field;
            public int P2 => Field;
            public int P3 => Field;
            public int P4 => Field;
            public int P5 => Field;
            public int P6 => Field;
            public int P7 => Field;
            public int P8 => Field;
            public int P9 => Field;
            public int P10 => Field;
            public int P11 => Field;
            public int P12 => Field;
            public int P13 => Field;
            public int P14 => Field;
            public int P15 => Field;
            public int P16 => Field;
            public int P17 => Field;
            public int P18 => Field;
            public int P19 => Field;
            public int P20 => Field;
            public int P21 => Field;
            public int P22 => Field;
            public int P23 => Field;
            public int P24 => Field;
            public int P25 => Field;
            public int P26 => Field;
            public int P27 => Field;
            public int P28 => Field;
            public int P29 => Field;
            public int P30 => Field;
            public int P31 => Field;
            public int P32 => Field;
            public int P33 => Field;
            public int P34 => Field;
            public int P35 => Field;
            public int P36 => Field;
            public int P37 => Field;
            public int P38 => Field;
            public int P39 => Field;
        }
        public sealed class ManyFields
        {
            public int F0 = 0, F1 = 0, F2 = 0, F3 = 0, F4 = 0, F5 = 0, F6 = 0, F7 = 0, F8 = 0, F9 = 0, F10 = 0, F11 = 0, F12 = 0, F13 = 0, F14 = 0, F15 = 0, F16 = 0, F17 = 0, F18 = 0, F19 = 0, F20 = 0, F21 = 0, F22 = 0, F23 = 0, F24 = 0, F25 = 0, F26 = 0, F27 = 0, F28 = 0, F29 = 0, F30 = 0, F31 = 0, F32 = 0, F33 = 0, F34 = 0, F35 = 0, F36 = 0, F37 = 0, F38 = 0, F39 = 0, F40 = 0, F41 = 0, F42 = 0, F43 = 0, F44 = 0, F45 = 0, F46 = 0, F47 = 0, F48 = 0, F49 = 0, F50 = 0, F51 = 0, F52 = 0, F53 = 0, F54 = 0, F55 = 0, F56 = 0, F57 = 0, F58 = 0, F59 = 0, F60 = 0, F61 = 0, F62 = 0, F63 = 0, F64 = 0, F65 = 0, F66 = 0, F67 = 0, F68 = 0, F69 = 0, F70 = 0, F71 = 0, F72 = 0, F73 = 0, F74 = 0, F75 = 0, F76 = 0, F77 = 0, F78 = 0, F79 = 0, F80 = 0, F81 = 0, F82 = 0, F83 = 0, F84 = 0, F85 = 0, F86 = 0, F87 = 0, F88 = 0, F89 = 0, F90 = 0, F91 = 0, F92 = 0, F93 = 0, F94 = 0, F95 = 0, F96 = 0, F97 = 0, F98 = 0, F99 = 0, F100 = 0, F101 = 0, F102 = 0, F103 = 0, F104 = 0, F105 = 0, F106 = 0, F107 = 0, F108 = 0, F109 = 0, F110 = 0, F111 = 0, F112 = 0, F113 = 0, F114 = 0, F115 = 0, F116 = 0, F117 = 0, F118 = 0, F119 = 0, F120 = 0, F121 = 0, F122 = 0, F123 = 0, F124 = 0, F125 = 0, F126 = 0, F127 = 0, F128 = 0, F129 = 0, F130 = 0, F131 = 0, F132 = 0, F133 = 0, F134 = 0, F135 = 0, F136 = 0, F137 = 0, F138 = 0, F139 = 0, F140 = 0, F141 = 0, F142 = 0, F143 = 0, F144 = 0, F145 = 0, F146 = 0, F147 = 0, F148 = 0, F149 = 0, F150 = 0, F151 = 0, F152 = 0, F153 = 0, F154 = 0, F155 = 0, F156 = 0, F157 = 0, F158 = 0, F159 = 0, F160 = 0, F161 = 0, F162 = 0, F163 = 0, F164 = 0, F165 = 0, F166 = 0, F167 = 0, F168 = 0, F169 = 0, F170 = 0, F171 = 0, F172 = 0, F173 = 0, F174 = 0, F175 = 0, F176 = 0, F177 = 0, F178 = 0, F179 = 0, F180 = 0, F181 = 0, F182 = 0, F183 = 0, F184 = 0, F185 = 0, F186 = 0, F187 = 0, F188 = 0, F189 = 0, F190 = 0, F191 = 0, F192 = 0, F193 = 0, F194 = 0, F195 = 0, F196 = 0, F197 = 0, F198 = 0, F199 = 0, F200 = 0, F201 = 0, F202 = 0, F203 = 0, F204 = 0, F205 = 0, F206 = 0, F207 = 0, F208 = 0, F209 = 0, F210 = 0, F211 = 0, F212 = 0, F213 = 0, F214 = 0, F215 = 0, F216 = 0, F217 = 0, F218 = 0, F219 = 0, F220 = 0, F221 = 0, F222 = 0, F223 = 0, F224 = 0, F225 = 0, F226 = 0, F227 = 0, F228 = 0, F229 = 0, F230 = 0, F231 = 0, F232 = 0, F233 = 0, F234 = 0, F235 = 0, F236 = 0, F237 = 0, F238 = 0, F239 = 0, F240 = 0, F241 = 0, F242 = 0, F243 = 0, F244 = 0, F245 = 0, F246 = 0, F247 = 0, F248 = 0, F249 = 0, F250 = 0, F251 = 0, F252 = 0, F253 = 0, F254 = 0, F255 = 0, F256 = 0, F257 = 0, F258 = 0, F259 = 0;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EvaluateTarget(int number, string message, Node node, int[,] matrix, Array shifted, long exact, string oversized)
        {
            int localNumber = number * 2;
            variableSink = PropertyModel.StaticField;
            variableSink = number; // EVALUATION_BREAKPOINT
            if (fallbackNumber != 17 || this.number != 900 || Values[1] != 20 || (int)ShiftedVector.GetValue(-1) != 0)
                throw new InvalidOperationException("Evaluation receiver state changed.");
            if (List.Count != 3 || Dictionary.Count != 1 || Queue.Count != 2 || Stack.Count != 1 || Properties.Field != 23 || Struct.Field != 31)
                throw new InvalidOperationException("Property receiver state changed.");
            if ((int)shifted.GetValue(-1,7) != 99 || oversized.Length != 32769 || matrix[1,1] != 40 || exact != 9007199254740993L ||
                message != "abcdefghijklmnop" || node.Label != "node-label" || number != 42) throw new InvalidOperationException("Evaluation arguments changed.");
            GC.KeepAlive(number); GC.KeepAlive(message); GC.KeepAlive(node); GC.KeepAlive(matrix);
            GC.KeepAlive(shifted); GC.KeepAlive(exact); GC.KeepAlive(oversized); GC.KeepAlive(localNumber);
            Properties.Field = 24;
            variableSink = number; // PROPERTY_SECOND_STOP
            VerifyPropertyState();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void VerifyPropertyState()
        {
            if (Properties.Field != 24) throw new InvalidOperationException("Property state at second stop changed.");
        }
    }
}
