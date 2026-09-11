using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FxDbg.Core.Model;
using FxDbg.Core.Variables;

namespace FxDbg.Core.Evaluation;

/// <summary>Only Interop implements target objects; interpreter values never contain callable target code.</summary>
public interface IExpressionObject
{
    IVariableValue Variable { get; }
    ExpressionValue Field(string name, EvaluationBudget budget);
    ExpressionValue Index(int[] indices, EvaluationBudget budget);
    int Length(int dimension, EvaluationBudget budget);
}

/// <summary>Array search requires a one-dimensional array and its actual lower bound.</summary>
public interface IExpressionArray : IExpressionObject
{
    int SearchLowerBound(EvaluationBudget budget);
}

public sealed class ExpressionValue
{
    public object? Scalar { get; }
    public IExpressionObject? Object { get; }
    public ExpressionValue(IExpressionObject value) { Object = value ?? throw new ArgumentNullException(nameof(value)); }
    public ExpressionValue(object? scalar)
    {
        if (scalar is not null && scalar is not bool && scalar is not char && scalar is not string &&
            scalar is not sbyte && scalar is not byte && scalar is not short && scalar is not ushort &&
            scalar is not int && scalar is not uint && scalar is not long && scalar is not ulong && scalar is not float && scalar is not double)
            throw EvaluationBudget.TypeError();
        Scalar = scalar;
    }
    public bool Boolean() => Object is null && Scalar is bool value ? value : throw EvaluationBudget.TypeError();
    public int Integer()
    {
        object? value = Scalar;
        if (Object is not null || value is not (sbyte or byte or short or ushort or char or int)) throw EvaluationBudget.TypeError();
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
    public IVariableValue Variable => Object?.Variable ?? new ScalarVariable(Scalar);
    private sealed class ScalarVariable : IVariableValue
    {
        private readonly object? scalar;
        internal ScalarVariable(object? scalar) => this.scalar = scalar;
        public VariableStatus Status => scalar is null ? VariableStatus.Null : VariableStatus.Available;
        public string TypeName => scalar switch { null => "object", bool => "bool", char => "char", string => "string", sbyte => "sbyte", byte => "byte", short => "short", ushort => "ushort", int => "int", uint => "uint", long => "long", ulong => "ulong", float => "float", double => "double", _ => throw EvaluationBudget.TypeError() };
        public string? Diagnostic => null;
        public string? ReferenceIdentity => null;
        public int GetMemberCount(CancellationToken cancellationToken = default) => 0;
        public IReadOnlyList<VariableMember> GetMembers(int start, int count, CancellationToken cancellationToken = default) => Array.Empty<VariableMember>();
        public string Format(int maxStringLength)
        {
            if (scalar is string text) return text.Length <= maxStringLength ? text : text.Substring(0, maxStringLength - 1) + "…";
            return scalar switch { null => "null", bool flag => flag ? "true" : "false", float single => single.ToString("R", CultureInfo.InvariantCulture), double number => number.ToString("R", CultureInfo.InvariantCulture), _ => Convert.ToString(scalar, CultureInfo.InvariantCulture)! };
        }
    }
}
