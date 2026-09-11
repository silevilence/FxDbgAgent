using System;
using System.Globalization;

namespace FxDbg.Core.Evaluation;

/// <summary>Closed operations over captured primitives. Never invokes a target method or equality overload.</summary>
internal static class ExpressionIntrinsics
{
    internal static bool Supports(string? name) => name is
        "String.Substring" or "String.IndexOf" or "String.Contains" or "String.StartsWith" or "String.EndsWith" or
        "String.Trim" or "String.CompareOrdinal" or "String.IsNullOrWhiteSpace" or
        "Math.Round" or "Math.Floor" or "Math.Ceiling" or "Math.Truncate" or "Math.Sqrt" or "Math.Sign" or "Math.Clamp" or "Array.IndexOf";

    internal static ExpressionValue Call(string name, ExpressionValue[] args, EvaluationBudget budget)
    {
        budget.Step();
        int minimum = name switch
        {
            "String.Substring" or "String.IndexOf" or "String.Contains" or "String.StartsWith" or "String.EndsWith" or "String.CompareOrdinal" or "Array.IndexOf" => 2,
            "Math.Clamp" => 3, _ => 1
        };
        int maximum = name is "String.Substring" or "String.IndexOf" ? 3 : name == "Math.Round" ? 2 : minimum;
        if (args.Length < minimum || args.Length > maximum) throw EvaluationBudget.TypeError();
        object? result;
        switch (name)
        {
            case "String.IsNullOrWhiteSpace": result = string.IsNullOrWhiteSpace(Text(args[0], true)); break;
            case "String.CompareOrdinal": result = string.CompareOrdinal(Text(args[0], true), Text(args[1], true)); break;
            case "String.Trim":
                string trimmed = Text(args[0])!.Trim(); budget.String(trimmed.Length); result = trimmed; break;
            case "String.Substring":
                string text = Text(args[0])!; int start = args[1].Integer();
                int count = args.Length == 3 ? args[2].Integer() : text.Length - start;
                if (start < 0 || start > text.Length || count < 0 || count > text.Length - start) throw RestrictedExpression.IndexError();
                budget.String(count); result = text.Substring(start, count); break;
            case "String.IndexOf":
                string haystack = Text(args[0])!, needle = Text(args[1])!;
                int offset = args.Length == 3 ? args[2].Integer() : 0;
                if (offset < 0 || offset > haystack.Length) throw RestrictedExpression.IndexError();
                result = haystack.IndexOf(needle, offset, StringComparison.Ordinal); break;
            case "String.Contains": result = Text(args[0])!.IndexOf(Text(args[1])!, StringComparison.Ordinal) >= 0; break;
            case "String.StartsWith": result = Text(args[0])!.StartsWith(Text(args[1])!, StringComparison.Ordinal); break;
            case "String.EndsWith": result = Text(args[0])!.EndsWith(Text(args[1])!, StringComparison.Ordinal); break;
            case "Math.Clamp":
                result = ExpressionNumbers.Clamp(Scalar(args[0]), Scalar(args[1]), Scalar(args[2])); break;
            case "Math.Sign":
                object? number = Scalar(args[0]);
                if (number is float single) result = Math.Sign(single);
                else if (number is double real) result = Math.Sign(real);
                else if (number is sbyte or byte or short or ushort or char or int or uint or long)
                    result = Math.Sign(Convert.ToInt64(number, CultureInfo.InvariantCulture));
                else if (number is ulong unsigned) result = unsigned == 0 ? 0 : 1;
                else throw EvaluationBudget.TypeError();
                break;
            case "Math.Round":
                double rounded = Real(args[0]); int digits = args.Length == 2 ? args[1].Integer() : 0;
                if (digits < 0 || digits > 15) throw RestrictedExpression.IndexError();
                result = Math.Round(rounded, digits, MidpointRounding.ToEven); break;
            case "Math.Floor": result = Math.Floor(Real(args[0])); break;
            case "Math.Ceiling": result = Math.Ceiling(Real(args[0])); break;
            case "Math.Truncate": result = Math.Truncate(Real(args[0])); break;
            case "Math.Sqrt": result = Math.Sqrt(Real(args[0])); break;
            case "Array.IndexOf": return Search(args[0], args[1], budget);
            default: throw EvaluationBudget.TypeError();
        }
        budget.Check(); return new ExpressionValue(result);
    }

    private static object? Scalar(ExpressionValue value) => value.Object is null ? value.Scalar : throw EvaluationBudget.TypeError();
    private static string? Text(ExpressionValue value, bool allowNull = false)
    {
        object? scalar = Scalar(value);
        return scalar is string text ? text : scalar is null && allowNull ? null : throw EvaluationBudget.TypeError();
    }
    private static double Real(ExpressionValue value) => (double)ExpressionNumbers.Cast("double", Scalar(value));
    private static ExpressionValue Search(ExpressionValue value, ExpressionValue sought, EvaluationBudget budget)
    {
        if (value.Object is not IExpressionArray array) throw EvaluationBudget.TypeError();
        int lower = array.SearchLowerBound(budget), length = array.Length(0, budget);
        for (int position = 0; position < length; position++)
        {
            budget.Step();
            int index = checked(lower + position);
            ExpressionValue item = array.Index(new[] { index }, budget);
            bool equal;
            if (item.Object is not null || sought.Object is not null)
                equal = item.Object?.Variable.ReferenceIdentity is { } identity && sought.Object?.Variable.ReferenceIdentity == identity;
            else
                // Only interpreter primitives reach this call. Exact boxed types match Array.IndexOf;
                // primitive Equals also treats NaN as equal, unlike the expression == operator.
                equal = Equals(item.Scalar, sought.Scalar);
            if (equal) return new ExpressionValue((object)index);
        }
        return new ExpressionValue((object)unchecked(lower - 1));
    }
}
