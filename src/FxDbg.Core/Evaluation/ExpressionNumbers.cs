using System;
using System.Globalization;

namespace FxDbg.Core.Evaluation;

internal static class ExpressionNumbers
{
    private enum NumberKind { Int, UInt, Long, ULong, Float, Double }
    private static NumberKind Kind(object? value) => value switch
    {
        sbyte or byte or short or ushort or char or int => NumberKind.Int,
        uint => NumberKind.UInt, long => NumberKind.Long, ulong => NumberKind.ULong,
        float => NumberKind.Float, double => NumberKind.Double, _ => throw EvaluationBudget.TypeError()
    };
    private static object Primitive(object value) => value is char c ? (int)c : value;
    private static NumberKind Promote(object left, object right)
    {
        NumberKind a = Kind(left), b = Kind(right);
        if (a == NumberKind.Double || b == NumberKind.Double) return NumberKind.Double;
        if (a == NumberKind.Float || b == NumberKind.Float) return NumberKind.Float;
        if (a == NumberKind.ULong || b == NumberKind.ULong)
        {
            object other = a == NumberKind.ULong ? right : left;
            if (other is sbyte or short or int or long) throw EvaluationBudget.TypeError();
            return NumberKind.ULong;
        }
        if (a == NumberKind.Long || b == NumberKind.Long) return NumberKind.Long;
        if (a == NumberKind.UInt || b == NumberKind.UInt)
            return left is sbyte or short or int || right is sbyte or short or int ? NumberKind.Long : NumberKind.UInt;
        return NumberKind.Int;
    }
    internal static object Unary(string op, object? value)
    {
        NumberKind kind = Kind(value);
        if (op == "~") return kind switch
        {
            NumberKind.Int => (object)~Convert.ToInt32(value, CultureInfo.InvariantCulture),
            NumberKind.UInt => ~(uint)value!, NumberKind.Long => ~(long)value!, NumberKind.ULong => ~(ulong)value!,
            _ => throw EvaluationBudget.TypeError()
        };
        if (op == "+") return kind == NumberKind.Int ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : value!;
        if (kind == NumberKind.ULong) throw EvaluationBudget.TypeError();
        if (kind == NumberKind.Float) return -(float)value!;
        if (kind == NumberKind.Double) return -(double)value!;
        decimal result = -Convert.ToDecimal(Primitive(value!), CultureInfo.InvariantCulture);
        return Box(result, kind == NumberKind.UInt ? NumberKind.Long : kind);
    }
    internal static object Binary(string op, object? left, object? right)
    {
        if (left is null || right is null) throw EvaluationBudget.TypeError();
        if (op is "&" or "|" or "^")
        {
            if (left is bool a && right is bool b) return op switch { "&" => a & b, "|" => a | b, _ => a ^ b };
            NumberKind integral = Promote(left, right);
            if (integral is NumberKind.Float or NumberKind.Double) throw EvaluationBudget.TypeError();
            ulong xBits = Bits(left), yBits = Bits(right);
            ulong bits = op switch { "&" => xBits & yBits, "|" => xBits | yBits, _ => xBits ^ yBits };
            return FromBits(bits, integral);
        }
        if (op is "<<" or ">>")
        {
            int shift = new ExpressionValue(right).Integer();
            return Kind(left) switch
            {
                NumberKind.Int => (object)(op == "<<" ? Convert.ToInt32(left, CultureInfo.InvariantCulture) << shift : Convert.ToInt32(left, CultureInfo.InvariantCulture) >> shift),
                NumberKind.UInt => op == "<<" ? (uint)left << shift : (uint)left >> shift,
                NumberKind.Long => op == "<<" ? (long)left << shift : (long)left >> shift,
                NumberKind.ULong => op == "<<" ? (ulong)left << shift : (ulong)left >> shift,
                _ => throw EvaluationBudget.TypeError()
            };
        }
        NumberKind kind = Promote(left, right);
        if (kind is NumberKind.Double or NumberKind.Float)
        {
            double a = Convert.ToDouble(Primitive(left), CultureInfo.InvariantCulture), b = Convert.ToDouble(Primitive(right), CultureInfo.InvariantCulture);
            if (kind == NumberKind.Float) { a = (float)a; b = (float)b; }
            switch (op)
            {
                case "==": return a == b; case "!=": return a != b; case "<": return a < b; case "<=": return a <= b; case ">": return a > b; case ">=": return a >= b;
            }
            double result = op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b, "min" => Math.Min(a,b), "max" => Math.Max(a,b), _ => throw EvaluationBudget.TypeError() };
            if (kind == NumberKind.Float) return (float)result;
            return result;
        }
        decimal x = Convert.ToDecimal(Primitive(left), CultureInfo.InvariantCulture), y = Convert.ToDecimal(Primitive(right), CultureInfo.InvariantCulture);
        switch (op)
        {
            case "==": return x == y; case "!=": return x != y; case "<": return x < y; case "<=": return x <= y; case ">": return x > y; case ">=": return x >= y;
        }
        decimal z = op switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" => decimal.Truncate(x / y), "%" => x % y, "min" => Math.Min(x,y), "max" => Math.Max(x,y), _ => throw EvaluationBudget.TypeError() };
        return Box(z, kind);
    }
    internal static object Abs(object? value)
    {
        // Intrinsics with an exact overload do not use binary arithmetic promotion.
        if (value is sbyte narrow) return Math.Abs(narrow);
        if (value is short small) return Math.Abs(small);
        NumberKind kind = Kind(value);
        if (kind is NumberKind.UInt or NumberKind.ULong) throw EvaluationBudget.TypeError();
        if (kind == NumberKind.Float) return Math.Abs((float)value!);
        if (kind == NumberKind.Double) return Math.Abs((double)value!);
        return Box(Math.Abs(Convert.ToDecimal(Primitive(value!), CultureInfo.InvariantCulture)), kind);
    }
    internal static object MinMax(bool minimum, object? left, object? right)
    {
        if (left is sbyte a && right is sbyte b) return minimum ? Math.Min(a,b) : Math.Max(a,b);
        if (left is byte c && right is byte d) return minimum ? Math.Min(c,d) : Math.Max(c,d);
        if (left is short e && right is short f) return minimum ? Math.Min(e,f) : Math.Max(e,f);
        if (left is ushort g && right is ushort h) return minimum ? Math.Min(g,h) : Math.Max(g,h);
        return Binary(minimum ? "min" : "max", left, right);
    }
    internal static object Clamp(object? value, object? low, object? high)
    {
        if (value is null || low is null || high is null) throw EvaluationBudget.TypeError();
        // Reuse the existing numeric promotion contract, but compare rather than min/max:
        // NaN bounds must not turn an otherwise finite result into NaN.
        object sample = MinMax(true, MinMax(false, value, low), high);
        string type = new ExpressionValue(sample).Variable.TypeName;
        object x = Cast(type, value), minimum = Cast(type, low), maximum = Cast(type, high);
        if ((bool)Binary(">", minimum, maximum)) throw new ArithmeticException();
        if ((bool)Binary("<", x, minimum)) return minimum;
        if ((bool)Binary(">", x, maximum)) return maximum;
        return x;
    }
    private static object Box(decimal value, NumberKind kind)
    {
        switch (kind)
        {
            case NumberKind.Int: return checked((int)value);
            case NumberKind.UInt: return checked((uint)value);
            case NumberKind.Long: return checked((long)value);
            case NumberKind.ULong: return checked((ulong)value);
            default: throw EvaluationBudget.TypeError();
        }
    }

    internal static bool IsCastType(string type) => type is "sbyte" or "byte" or "short" or "ushort" or "char" or "int" or "uint" or "long" or "ulong" or "float" or "double";
    internal static object Cast(string type, object? value)
    {
        NumberKind kind = Kind(value);
        if (type == "float") return Convert.ToSingle(Primitive(value!), CultureInfo.InvariantCulture);
        if (type == "double") return Convert.ToDouble(Primitive(value!), CultureInfo.InvariantCulture);
        // Integral narrowing keeps the low bits. Floating conversion uses the Engine CLR's
        // unchecked conv.* behavior; out-of-range/NaN results are implementation-specific in C#.
        if (kind is NumberKind.Float or NumberKind.Double)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            unchecked
            {
                return type switch
                {
                    "sbyte" => (object)(sbyte)number, "byte" => (byte)number, "short" => (short)number,
                    "ushort" => (ushort)number, "char" => (char)number, "int" => (int)number,
                    "uint" => (uint)number, "long" => (long)number, "ulong" => (ulong)number,
                    _ => throw EvaluationBudget.TypeError()
                };
            }
        }
        ulong bits = Bits(value!);
        unchecked
        {
            return type switch
            {
                "sbyte" => (object)(sbyte)bits, "byte" => (byte)bits, "short" => (short)bits,
                "ushort" => (ushort)bits, "char" => (char)bits, "int" => (int)bits,
                "uint" => (uint)bits, "long" => (long)bits, "ulong" => bits,
                _ => throw EvaluationBudget.TypeError()
            };
        }
    }
    private static ulong Bits(object value) => value is ulong unsigned ? unsigned : value is uint word ? word : unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
    private static object FromBits(ulong bits, NumberKind kind) => kind switch
    {
        NumberKind.Int => (object)unchecked((int)bits), NumberKind.UInt => unchecked((uint)bits),
        NumberKind.Long => unchecked((long)bits), NumberKind.ULong => bits, _ => throw EvaluationBudget.TypeError()
    };
}
