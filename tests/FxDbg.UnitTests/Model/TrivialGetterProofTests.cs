using System;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class TrivialGetterProofTests
{
    [Fact]
    public void Method_headers_must_prove_no_exception_or_extra_sections()
    {
        Assert.Equal(1, TrivialGetterProof.HeaderSizeWithoutSections(new byte[] { (7 << 2) | 2 }, 7));
        Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(new byte[] { (7 << 2) | 2 }, 6));
        var fat = new byte[] { 0x13, 0x30, 1, 0, 7, 0, 0, 0, 1, 0, 0, 0x11 };
        Assert.Equal(12, TrivialGetterProof.HeaderSizeWithoutSections(fat, 7));
        fat[0] |= 8; Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(fat, 7));
        fat[0] = 0x23; Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(fat, 7));
        fat[0] = 3; fat[1] = 0x40; Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(fat, 7));
        fat[1] = 0x30; fat[2] = 0; Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(fat, 7));
        Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(new byte[] { 3 }, 7));
        Assert.Equal(0, TrivialGetterProof.HeaderSizeWithoutSections(Array.Empty<byte>(), 7));
    }
    [Fact]
    public void Only_exact_load_patterns_are_accepted()
    {
        var field = TrivialGetterProof.Decode(new byte[] { 2, 0x7b, 1, 0, 0, 4, 0x2a }, false);
        Assert.Equal(GetterLoadKind.InstanceField, field!.Kind); Assert.Equal(0x04000001, field.FieldToken);
        Assert.Null(TrivialGetterProof.Decode(new byte[] { 2, 0x7b, 1, 0, 0, 4, 0x2a }, true));
        Assert.Equal(GetterLoadKind.StaticField, TrivialGetterProof.Decode(new byte[] { 0x7e, 1, 0, 0, 10, 0x2a }, true)!.Kind);
        foreach (byte op in new byte[] { 0, 0x28, 0x6f, 0x2b, 0x7d, 0x7f, 0xfe })
            Assert.Null(TrivialGetterProof.Decode(new[] { op, (byte)0x2a }, false));
        Assert.Null(TrivialGetterProof.Decode(new byte[] { 0, 2, 0x7b, 1, 0, 0, 4, 0x2a }, false));
        Assert.Null(TrivialGetterProof.Decode(new byte[] { 2, 0x7b, 1, 0, 0, 6, 0x2a }, false));
        Assert.Null(TrivialGetterProof.Decode(new byte[] { 2, 0x7b, 0, 0, 0, 4, 0x2a }, false));
        Assert.Null(TrivialGetterProof.Decode(Array.Empty<byte>(), false));
        Assert.Null(TrivialGetterProof.Decode(new byte[17], false));
    }
    [Fact]
    public void Constants_preserve_the_IL_stack_type()
    {
        for (byte op = 0x15; op <= 0x1e; op++) Assert.Equal((int)op - 0x16, TrivialGetterProof.Decode(new[] { op, (byte)0x2a }, false)!.Constant);
        Assert.Null(TrivialGetterProof.Decode(new byte[] { 0x14, 0x2a }, false)!.Constant);
        Assert.Equal(-128, TrivialGetterProof.Decode(new byte[] { 0x1f, 128, 0x2a }, false)!.Constant);
        foreach (var pair in new[] { (Op: (byte)0x20, Value: (object)42, Bytes: BitConverter.GetBytes(42)),
            (Op: (byte)0x21, Value: (object)long.MinValue, Bytes: BitConverter.GetBytes(long.MinValue)),
            (Op: (byte)0x22, Value: (object)1.5f, Bytes: BitConverter.GetBytes(1.5f)),
            (Op: (byte)0x23, Value: (object)2.5d, Bytes: BitConverter.GetBytes(2.5d)) })
        {
            var il = new byte[pair.Bytes.Length + 2]; il[0] = pair.Op; pair.Bytes.CopyTo(il, 1); il[il.Length - 1] = 0x2a;
            Assert.Equal(pair.Value, TrivialGetterProof.Decode(il, false)!.Constant);
            il[il.Length - 1] = 0; Assert.Null(TrivialGetterProof.Decode(il, false));
        }
    }
    [Fact]
    public void Metadata_and_IL_have_independent_bounded_budgets()
    {
        using var probes = new EvaluationBudget(1000);
        for (int index = 0; index < 256; index++) probes.MetadataProbe();
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(probes.MetadataProbe).Code);
        using var il = new EvaluationBudget(1000);
        for (int index = 0; index < 16; index++) il.InspectIl(16);
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, Assert.Throws<FxDbgException>(() => il.InspectIl(1)).Code);
        Assert.Throws<FxDbgException>(() => new EvaluationBudget().InspectIl(17));
    }
}
