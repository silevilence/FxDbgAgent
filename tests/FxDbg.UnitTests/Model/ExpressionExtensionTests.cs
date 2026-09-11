using System;
using System.Collections.Generic;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using FxDbg.Core.Model;
using FxDbg.Core.Variables;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class ExpressionExtensionTests
{
    private static ExpressionValue Eval(string text, Func<string, EvaluationBudget, ExpressionValue>? resolve = null, EvaluationBudget? budget = null)
    {
        using EvaluationBudget? owned = budget is null ? new EvaluationBudget(1000) : null;
        budget ??= owned!;
        return RestrictedExpression.Parse(text, budget).Evaluate(resolve ?? ((_, _) => throw new InvalidOperationException("Unexpected read")), budget);
    }

    [Theory]
    [InlineData("true ? 3 : missing", "3", "int")]
    [InlineData("false ? missing : false ? 1 : 2", "2", "int")]
    [InlineData("true ? false ? 1 : 2 : 3", "2", "int")]
    [InlineData("false || true ? 4 : 5", "4", "int")]
    [InlineData("1 | 2 ^ 3 & 4", "3", "int")]
    [InlineData("1 << 2 + 1", "8", "int")]
    [InlineData("8 >> 1 + 1", "2", "int")]
    [InlineData("1 << -1", "-2147483648", "int")]
    [InlineData("1L << 64", "1", "long")]
    [InlineData("-4 >> 1", "-2", "int")]
    [InlineData("0x80000000 >> 1", "1073741824", "uint")]
    [InlineData("~0UL", "18446744073709551615", "ulong")]
    [InlineData("~0u", "4294967295", "uint")]
    [InlineData("~(byte)0", "-1", "int")]
    [InlineData("~0L", "-1", "long")]
    [InlineData("0xFFFFFFFF", "4294967295", "uint")]
    [InlineData("0XffffffffffffffffL", "18446744073709551615", "ulong")]
    [InlineData("0x10u | 1u", "17", "uint")]
    [InlineData("0x10UL ^ 1u", "17", "ulong")]
    [InlineData("(int)0xFFFFFFFF", "-1", "int")]
    [InlineData("(byte)257", "1", "byte")]
    [InlineData("(sbyte)255", "-1", "sbyte")]
    [InlineData("(short)65535", "-1", "short")]
    [InlineData("(ushort)-1", "65535", "ushort")]
    [InlineData("(char)65", "A", "char")]
    [InlineData("(uint)-1", "4294967295", "uint")]
    [InlineData("(long)0xffffffffffffffff", "-1", "long")]
    [InlineData("(ulong)-1", "18446744073709551615", "ulong")]
    [InlineData("(int)-1.9", "-1", "int")]
    [InlineData("(float)3", "3", "float")]
    [InlineData("(double)'a'", "97", "double")]
    [InlineData("true & false | true ^ false", "true", "bool")]
    [InlineData("String.Substring(\"abcdef\",2,3)", "cde", "string")]
    [InlineData("String.Substring(\"abcdef\",6)", "", "string")]
    [InlineData("String.IndexOf(\"AbAb\",\"b\",2)", "3", "int")]
    [InlineData("String.IndexOf(\"AbAb\",\"a\")", "-1", "int")]
    [InlineData("String.Contains(\"abc\",\"b\")", "true", "bool")]
    [InlineData("String.StartsWith(\"abc\",\"A\")", "false", "bool")]
    [InlineData("String.EndsWith(\"abc\",\"bc\")", "true", "bool")]
    [InlineData("String.Trim(\" a \")", "a", "string")]
    [InlineData("String.Trim(\"\\t \")", "", "string")]
    [InlineData("String.Trim(\"\")", "", "string")]
    [InlineData("String.Trim(\"abc\")", "abc", "string")]
    [InlineData("String.CompareOrdinal(null,null)", "0", "int")]
    [InlineData("String.IsNullOrWhiteSpace(\" \\t\")", "true", "bool")]
    [InlineData("Math.Round(2.5)", "2", "double")]
    [InlineData("Math.Round(1.125,2)", "1.12", "double")]
    [InlineData("Math.Floor(-1.2)", "-2", "double")]
    [InlineData("Math.Ceiling(-1.2)", "-1", "double")]
    [InlineData("Math.Truncate(-1.2)", "-1", "double")]
    [InlineData("Math.Sqrt(9)", "3", "double")]
    [InlineData("Math.Sqrt(-1)", "NaN", "double")]
    [InlineData("Math.Sign(-1.2f)", "-1", "int")]
    [InlineData("Math.Sign(0)", "0", "int")]
    [InlineData("Math.Clamp(15,0,10)", "10", "int")]
    [InlineData("Math.Clamp(1.0,0.0/0.0,2.0)", "1", "double")]
    [InlineData("Math.Clamp(1.0,0.0,0.0/0.0)", "1", "double")]
    [InlineData("Math.Sign(1u)", "1", "int")]
    [InlineData("Math.Sign(1UL)", "1", "int")]
    [InlineData("Math.Clamp((byte)3,(byte)4,(byte)5)", "4", "byte")]
    public void Results_match_documented_types(string text, string expected, string type)
    {
        ExpressionValue value = Eval(text);
        Assert.Equal(expected, value.Variable.Format(256)); Assert.Equal(type, value.Variable.TypeName);
    }

    [Theory]
    [InlineData("1 ? 2 : 3", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("true ? 1", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("true ? 1 : target()", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("1 & 1.5", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("1 << 1u", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("~1f", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("(int)true", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("(int)null", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("0x", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("0x10000000000000000", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("0x1uu", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("x |= 2", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("x <<= 2", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("String.Substring(\"a\",2)", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("String.Substring(\"a\",0,-1)", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("String.IndexOf(\"a\",\"a\",-1)", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("String.Contains(null,\"a\")", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("Math.Round(1.2,16)", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("Math.Clamp(0,2,1)", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("Math.Sign(0.0 / 0.0)", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("Math.Sqrt()", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("Math.Round(1,2,3)", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("Array.IndexOf(1,1)", FxDbgErrorCode.ExpressionTypeError)]
    public void Rejections_keep_the_error_contract(string text, FxDbgErrorCode expected) =>
        Assert.Equal(expected, Assert.Throws<FxDbgException>(() => Eval(text)).Code);

    [Fact]
    public void Bitwise_boolean_operators_evaluate_both_operands()
    {
        int reads = 0;
        Assert.False(Eval("false & value", (_, _) => { reads++; return new ExpressionValue((object)true); }).Boolean());
        Assert.Equal(1, reads);
        Assert.True(Eval("true | value", (_, _) => { reads++; return new ExpressionValue((object)false); }).Boolean());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Array_search_is_bounded_and_never_calls_target_equality()
    {
        var array = new CapturedArray(new object?[] { 1, "two", null, double.NaN }, -2);
        ExpressionValue Resolve(string _, EvaluationBudget __) => new(array);
        Assert.Equal(-1, Eval("Array.IndexOf(a,\"two\")", Resolve).Scalar);
        Assert.Equal(0, Eval("Array.IndexOf(a,null)", Resolve).Scalar);
        Assert.Equal(1, Eval("Array.IndexOf(a,0.0/0.0)", Resolve).Scalar);
        Assert.Equal(-3, Eval("Array.IndexOf(a,1L)", Resolve).Scalar);
        array = new CapturedArray(new object?[1100], 0);
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => Eval("Array.IndexOf(a,1)", Resolve)).Code);
        using var budget = new EvaluationBudget(1000); budget.String(65536 / 2); budget.String(65536 / 2);
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => Eval("String.Trim(s)", (_, _) => new ExpressionValue((object)" a "), budget)).Code);
    }

    private sealed class CapturedArray : IExpressionArray
    {
        private readonly object?[] items; private readonly int lower;
        internal CapturedArray(object?[] items, int lower) { this.items = items; this.lower = lower; }
        public IVariableValue Variable => new ExpressionValue((object?)null).Variable;
        public ExpressionValue Field(string name, EvaluationBudget budget) => throw EvaluationBudget.TypeError();
        public ExpressionValue Index(int[] indices, EvaluationBudget budget) { budget.Read(); return new ExpressionValue(items[indices[0] - lower]); }
        public int Length(int dimension, EvaluationBudget budget) { budget.Read(); return items.Length; }
        public int SearchLowerBound(EvaluationBudget budget) { budget.Read(); return lower; }
    }
}
