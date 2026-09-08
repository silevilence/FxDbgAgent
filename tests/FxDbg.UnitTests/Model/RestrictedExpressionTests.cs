using System;
using System.Collections.Generic;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class RestrictedExpressionTests
{
    [Fact]
    public void Math_exact_small_integer_overloads_preserve_type_and_overflow()
    {
        foreach (object number in new object[] { (sbyte)7, (byte)7, (short)7, (ushort)7 })
        {
            foreach (string operation in new[] { "Math.Min(value,value)", "Math.Max(value,value)" })
            {
                var result = Evaluate(operation, (_,_)=>new ExpressionValue(number));
                Assert.Equal(number.GetType(),result.Scalar!.GetType()); Assert.Equal(number,result.Scalar);
            }
        }
        foreach (object number in new object[] { (sbyte)-7, (short)-7 })
        {
            var result = Evaluate("Math.Abs(value)",(_,_)=>new ExpressionValue(number));
            Assert.Equal(number.GetType(),result.Scalar!.GetType()); Assert.Equal("7",result.Variable.Format(10));
        }
        foreach (object minimum in new object[] { sbyte.MinValue, short.MinValue })
            Assert.Equal(FxDbgErrorCode.ExpressionArithmeticError,Assert.Throws<FxDbgException>(
                ()=>Evaluate("Math.Abs(value)",(_,_)=>new ExpressionValue(minimum))).Code);
        foreach (object number in new object[] { (byte)7, (ushort)7, (char)7 })
            Assert.IsType<int>(Evaluate("Math.Abs(value)",(_,_)=>new ExpressionValue(number)).Scalar);
        Assert.IsType<int>(Evaluate("Math.Min(a,b)",(name,_)=>new ExpressionValue(name=="a"?(object)(sbyte)1:(short)2)).Scalar);
        Assert.IsType<int>(Evaluate("Math.Max(value,value)",(_,_)=>new ExpressionValue('a')).Scalar);
    }

    private static ExpressionValue Evaluate(string source, Func<string, EvaluationBudget, ExpressionValue>? resolve = null, EvaluationBudget? budget = null)
    {
        using EvaluationBudget? owned = budget is null ? new EvaluationBudget(1000) : null;
        budget ??= owned!;
        return RestrictedExpression.Parse(source, budget).Evaluate(resolve ?? ((_, _) => throw new InvalidOperationException("Unexpected target read")), budget);
    }
    [Theory]
    [InlineData("1 + 2 * 3", "7", "int")]
    [InlineData("(1 + 2) * 3", "9", "int")]
    [InlineData("-7 / 2", "-3", "int")]
    [InlineData("-7 % 2", "-1", "int")]
    [InlineData("+1", "1", "int")]
    [InlineData("-1u", "-1", "long")]
    [InlineData("1u + 2u", "3", "uint")]
    [InlineData("1u + 2", "3", "long")]
    [InlineData("1L + 2", "3", "long")]
    [InlineData("1UL + 2u", "3", "ulong")]
    [InlineData("1.5f * 2", "3", "float")]
    [InlineData("2.5d / 2", "1.25", "double")]
    [InlineData("1e2 - 1", "99", "double")]
    [InlineData("true && !false", "true", "bool")]
    [InlineData("false || 2 > 1 && 3 <= 3", "true", "bool")]
    [InlineData("1 < 2 == 3 >= 3", "true", "bool")]
    [InlineData("1 != 2", "true", "bool")]
    [InlineData("true == false", "false", "bool")]
    [InlineData("null == null", "true", "bool")]
    [InlineData("null != \"x\"", "true", "bool")]
    [InlineData("\"a\" + \"b\"", "ab", "string")]
    [InlineData("\"a\" == \"A\"", "false", "bool")]
    [InlineData("\"abc\"[1]", "b", "char")]
    [InlineData("\"abc\".Length", "3", "int")]
    [InlineData("'a' + 1", "98", "int")]
    [InlineData("Math.Abs(-5)", "5", "int")]
    [InlineData("Math.Abs(-1.5f)", "1.5", "float")]
    [InlineData("Math.Abs(-2.5)", "2.5", "double")]
    [InlineData("Math.Min(2,1)", "1", "int")]
    [InlineData("Math.Max(2L,1)", "2", "long")]
    [InlineData("Math.Min(2f,1f)", "1", "float")]
    [InlineData("Math.Max(2d,1f)", "2", "double")]
    [InlineData("String.IsNullOrEmpty(null)", "true", "bool")]
    [InlineData("String.IsNullOrEmpty(\"\")", "true", "bool")]
    [InlineData("String.Equals(\"a\",\"A\")", "false", "bool")]
    [InlineData("String.Concat(null,\"abc\")", "abc", "string")]
    [InlineData("9007199254740993L != 9007199254740992L", "true", "bool")]
    [InlineData("18446744073709551615UL > 18446744073709551614UL", "true", "bool")]
    [InlineData("0.0 / 0.0 == 0.0 / 0.0", "false", "bool")]
    public void ProducesExactPrimitiveResults(string source, string expected, string type)
    {
        var result = Evaluate(source).Variable;
        Assert.Equal(expected, result.Format(256)); Assert.Equal(type, result.TypeName);
    }
    [Theory]
    [InlineData("2147483647 + 1", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("9223372036854775807L + 1L", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("0u - 1u", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("18446744073709551615UL * 2UL", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("1 / 0", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("Math.Abs(-2147483647 - 1)", FxDbgErrorCode.ExpressionArithmeticError)]
    [InlineData("1UL + 1", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("-1UL", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("true + 1", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("null + 1", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("String.Concat(1,2)", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("Math.Abs(1u)", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("Math.Abs()", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("Math.Abs(1,2,3)", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("\"abc\"[-1]", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("\"abc\"[3]", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("\"abc\"[1,2]", FxDbgErrorCode.ExpressionIndexOutOfRange)]
    [InlineData("\"abc\"[1.5]", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("1.x", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("1[0]", FxDbgErrorCode.ExpressionTypeError)]
    [InlineData("String.Format(\"x\")", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("false && UserMethod()", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("new X()", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("while(true)", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("a = 1", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("a++", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("a += 1", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("a ?? b", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("a?.b", FxDbgErrorCode.ExpressionForbidden)]
    [InlineData("", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("1 +", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("(1", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("1 2", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("'ab'", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("\"abc", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("\"\\q\"", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("1e", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("1e999", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("1ff", FxDbgErrorCode.ExpressionSyntaxError)]
    [InlineData("1;2", FxDbgErrorCode.ExpressionSyntaxError)]
    public void RejectsWithSafeErrors(string source, FxDbgErrorCode code)
    {
        var error = Assert.Throws<FxDbgException>(() => Evaluate(source)); Assert.Equal(code,error.Code);
        Assert.DoesNotContain("UserMethod", error.Message);
    }
    [Fact]
    public void ShortCircuitDoesNotReadTargetAndParsingNeverReadsIt()
    {
        Assert.False(Evaluate("false && missing").Boolean()); Assert.True(Evaluate("true || missing").Boolean());
        var budget = new EvaluationBudget(1000); _ = RestrictedExpression.Parse("missing.Field[0]", budget);
    }
    [Fact]
    public void BoundsInputDepthExecutionStringsAndReads()
    {
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => Evaluate(new string('1',4097))).Code);
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => Evaluate(new string('(',33)+"1"+new string(')',33))).Code);
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => Evaluate(string.Join("+",new string('1',40).ToCharArray()))).Code);
        var budget = new EvaluationBudget(1000); budget.String(32768); budget.String(32768);
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => budget.String(1)).Code);
        Assert.Throws<FxDbgException>(() => new EvaluationBudget().String(32769));
        budget = new EvaluationBudget(1000); for (int i=0;i<1024;i++) budget.Read(); Assert.Throws<FxDbgException>(() => budget.Read());
        budget = new EvaluationBudget(1000); for (int i=0;i<10000;i++) budget.Step(); Assert.Throws<FxDbgException>(() => budget.Step());
    }
    [Fact]
    public void CancellationAndSlowReadHaveDifferentErrors()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(FxDbgErrorCode.OperationCancelled, Assert.Throws<FxDbgException>(() => Evaluate("1", budget:new EvaluationBudget(1000,cancellation.Token))).Code);
        Assert.Equal(FxDbgErrorCode.OperationTimedOut, Assert.Throws<FxDbgException>(() => Evaluate("slow", (_,budget) => { Thread.Sleep(20); budget.Check(); return new ExpressionValue((object)1); },new EvaluationBudget(10))).Code);
        Assert.Equal("2",Evaluate("1+1").Variable.Format(256));
    }
    [Fact]
    public void DeadlineCancelsNativeReadTokenAndHonorsShorterOuterLimit()
    {
        using var budget = new EvaluationBudget(1000, maximumMilliseconds: 10);
        Assert.True(budget.Token.WaitHandle.WaitOne(2000), "Native read token must observe the shorter deadline.");
        Assert.Equal(FxDbgErrorCode.OperationTimedOut, Assert.Throws<FxDbgException>(budget.Check).Code);
        using var slow = new EvaluationBudget(1000, maximumMilliseconds: 10);
        Assert.Equal(FxDbgErrorCode.OperationTimedOut, Assert.Throws<FxDbgException>(() => Evaluate("slow", (_, limits) =>
        {
            Assert.True(limits.Token.WaitHandle.WaitOne(2000)); limits.Token.ThrowIfCancellationRequested();
            return new ExpressionValue((object)1);
        }, slow)).Code);
        Assert.Throws<FxDbgException>(() => new EvaluationBudget(1001, maximumMilliseconds: 1));
        Assert.Throws<FxDbgException>(() => new EvaluationBudget(1000, maximumMilliseconds: 0));
    }
    [Fact]
    public void UsesRuntimeTypesAndSafeFormatting()
    {
        var values = new Dictionary<string,object> { ["small"]=(byte)2, ["shortValue"]=(short)3, ["single"]=1.5f, ["text"]="abcdef" };
        ExpressionValue Resolve(string name, EvaluationBudget _) => new(values[name]);
        Assert.Equal("5",Evaluate("small + shortValue",Resolve).Variable.Format(20));
        Assert.Equal("4.5",Evaluate("single * shortValue",Resolve).Variable.Format(20));
        Assert.Equal("ab…",Evaluate("text",Resolve).Variable.Format(3));
        Assert.Equal("\n\t\r\0\\\"'",Evaluate("\"\\n\\t\\r\\0\\\\\\\"'\"").Scalar);
        Assert.Throws<FxDbgException>(() => new ExpressionValue((object)new object()));
        Assert.Throws<FxDbgException>(() => new EvaluationBudget(0)); Assert.Throws<FxDbgException>(() => new EvaluationBudget(1001));
    }
}
