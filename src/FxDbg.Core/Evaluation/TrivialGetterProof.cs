using System;

namespace FxDbg.Core.Evaluation;

public enum GetterLoadKind { InstanceField, StaticField, Constant }

/// <summary>Exact IL recognition only. Metadata, exception clauses and field identity must be proved by Interop.</summary>
public sealed class TrivialGetterProof
{
    private TrivialGetterProof(GetterLoadKind kind, int token = 0, object? constant = null)
    { Kind = kind; FieldToken = token; Constant = constant; }
    public GetterLoadKind Kind { get; }
    public int FieldToken { get; }
    public object? Constant { get; }

    /// <summary>ECMA-335 II.25.4: no unknown flags, extra sections or inconsistent code size.</summary>
    public static int HeaderSizeWithoutSections(byte[] header, int codeSize)
    {
        if (header is null || header.Length == 0 || codeSize < 2 || codeSize > 16) return 0;
        if ((header[0] & 3) == 2) return header[0] >> 2 == codeSize ? 1 : 0;
        if (header.Length != 12 || (header[0] & 3) != 3) return 0;
        int flags = BitConverter.ToUInt16(header, 0);
        return flags >> 12 == 3 && (flags & 0x0fec) == 0 && BitConverter.ToUInt16(header, 2) > 0 && BitConverter.ToInt32(header, 4) == codeSize ? 12 : 0;
    }

    public static TrivialGetterProof? Decode(byte[] il, bool staticGetter)
    {
        if (il is null || il.Length < 2 || il.Length > 16 || il[il.Length - 1] != 0x2a) return null;
        if (!staticGetter && il.Length == 7 && il[0] == 0x02 && il[1] == 0x7b)
            return Field(GetterLoadKind.InstanceField, BitConverter.ToInt32(il, 2));
        if (il.Length == 6 && il[0] == 0x7e) return Field(GetterLoadKind.StaticField, BitConverter.ToInt32(il, 1));
        object? constant;
        if (il.Length == 2 && il[0] == 0x14) constant = null;
        else if (il.Length == 2 && il[0] >= 0x15 && il[0] <= 0x1e) constant = (int)il[0] - 0x16;
        else if (il.Length == 3 && il[0] == 0x1f) constant = (int)unchecked((sbyte)il[1]);
        else if (il.Length == 6 && il[0] == 0x20) constant = BitConverter.ToInt32(il, 1);
        else if (il.Length == 10 && il[0] == 0x21) constant = BitConverter.ToInt64(il, 1);
        else if (il.Length == 6 && il[0] == 0x22) constant = BitConverter.ToSingle(il, 1);
        else if (il.Length == 10 && il[0] == 0x23) constant = BitConverter.ToDouble(il, 1);
        else return null;
        return new TrivialGetterProof(GetterLoadKind.Constant, constant: constant);
    }
    private static TrivialGetterProof? Field(GetterLoadKind kind, int token) =>
        (token & 0x00ffffff) != 0 && ((uint)token >> 24 is 0x04 or 0x0a) ? new TrivialGetterProof(kind, token) : null;
}
