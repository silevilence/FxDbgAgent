using System;
using System.Linq;
using System.Runtime.InteropServices;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using FxDbg.Core.Variables;

namespace FxDbg.Interop;

internal sealed partial class NativeVariableValue : IExpressionObject
{
    IVariableValue IExpressionObject.Variable => this;
    internal static ExpressionValue Expression(Func<CorDebugValue> read, CorDebugILFrame frame, EvaluationBudget budget)
    {
        budget.Read();
        IVariableValue captured = Capture(read, frame, budget.Token);
        if (captured is not NativeVariableValue native)
            throw new FxDbgException(captured.Status == Core.Model.VariableStatus.OptimizedAway ? FxDbgErrorCode.ValueOptimizedAway : FxDbgErrorCode.ValueUnavailable, "Expression value is unavailable.");
        ExpressionValue result = native.ToExpression(budget); budget.Check(); return result;
    }
    private ExpressionValue ToExpression(EvaluationBudget budget) => Read(() =>
    {
        if (value is null) return new ExpressionValue((object?)null);
        if (value.Raw is ICorDebugStringValue text)
        {
            int length = new CorDebugStringValue(text).Length; budget.String(length);
            return new ExpressionValue((object)MetadataNames.ReadString(text, length));
        }
        if (value.Raw is ICorDebugGenericValue generic && IsPrimitive(value.Type))
        {
            int size = value.Size;
            if (size < 1 || size > 8) throw EvaluationBudget.TypeError();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                new CorDebugGenericValue(generic).GetValue(buffer);
                var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size);
                object scalar;
                switch (value.Type)
                {
                    case CorElementType.Boolean: scalar = bytes[0] != 0; break;
                    case CorElementType.Char: scalar = (char)BitConverter.ToUInt16(bytes,0); break;
                    case CorElementType.I1: scalar = unchecked((sbyte)bytes[0]); break;
                    case CorElementType.U1: scalar = bytes[0]; break;
                    case CorElementType.I2: scalar = BitConverter.ToInt16(bytes,0); break;
                    case CorElementType.U2: scalar = BitConverter.ToUInt16(bytes,0); break;
                    case CorElementType.I4: scalar = BitConverter.ToInt32(bytes,0); break;
                    case CorElementType.U4: scalar = BitConverter.ToUInt32(bytes,0); break;
                    case CorElementType.I8: scalar = BitConverter.ToInt64(bytes,0); break;
                    case CorElementType.U8: scalar = BitConverter.ToUInt64(bytes,0); break;
                    case CorElementType.R4: scalar = BitConverter.ToSingle(bytes,0); break;
                    case CorElementType.R8: scalar = BitConverter.ToDouble(bytes,0); break;
                    default: throw EvaluationBudget.TypeError();
                }
                return new ExpressionValue(scalar);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new ExpressionValue((IExpressionObject)this);
    });
    ExpressionValue IExpressionObject.Field(string name, EvaluationBudget budget) => Read(() =>
    {
        budget.Check();
        if (!HasFields || value?.Raw is not ICorDebugObjectValue raw) throw EvaluationBudget.TypeError();
        FieldSlot? field = GetFields(budget.Token).FirstOrDefault(candidate => candidate.Name == name);
        if (field is null) throw new FxDbgException(FxDbgErrorCode.ExpressionNameNotFound, "Expression field was not found; getters are never invoked.");
        var instance = new CorDebugObjectValue(raw);
        return Expression(() => field.IsStatic ? field.Type.GetStaticFieldValue(field.Token, frame.Raw) : instance.GetFieldValue(field.Type.Class.Raw, field.Token), frame, budget);
    });
    ExpressionValue IExpressionObject.Index(int[] indices, EvaluationBudget budget) => Read(() =>
    {
        budget.Check();
        if (value?.Raw is not ICorDebugArrayValue raw) throw EvaluationBudget.TypeError();
        var array = new CorDebugArrayValue(raw);
        int rank = array.Rank;
        if (rank < 1 || rank > 32 || indices.Length != rank) throw RestrictedExpression.IndexError();
        int[] dimensions = array.GetDimensions(rank);
        int[] bases = array.HasBaseIndicies() ? array.GetBaseIndicies(rank) : new int[rank];
        for (int dimension = 0; dimension < rank; dimension++)
            if ((long)indices[dimension] - bases[dimension] < 0 || (long)indices[dimension] - bases[dimension] >= dimensions[dimension]) throw RestrictedExpression.IndexError();
        return Expression(() => array.GetElement(rank, indices), frame, budget);
    });
    int IExpressionObject.Length(int dimension, EvaluationBudget budget) => Read(() =>
    {
        budget.Read();
        if (value?.Raw is not ICorDebugArrayValue raw) throw EvaluationBudget.TypeError();
        var array = new CorDebugArrayValue(raw);
        if (dimension == -1) return array.Count;
        int rank = array.Rank;
        if (rank < 1 || rank > 32 || dimension < 0 || dimension >= rank) throw RestrictedExpression.IndexError();
        return array.GetDimensions(rank)[dimension];
    });
}
