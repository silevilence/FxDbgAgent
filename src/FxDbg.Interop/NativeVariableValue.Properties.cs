using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;

namespace FxDbg.Interop;

internal sealed partial class NativeVariableValue
{
    internal sealed class PropertyReadContext
    {
        internal readonly Dictionary<ICorDebugType, MemberCatalog> Types = new();
    }
    internal sealed class MemberCatalog
    {
        internal readonly List<FieldSlot> Fields = new();
        internal readonly List<PropertySlot> Properties = new();
        internal readonly List<CorDebugType> Hierarchy = new();
    }
    internal sealed class PropertySlot
    {
        internal PropertySlot(string name, CorDebugType type, mdMethodDef getter, int depth)
        { Name = name; Type = type; Getter = getter; Depth = depth; }
        internal readonly string Name;
        internal readonly CorDebugType Type;
        internal readonly mdMethodDef Getter;
        internal readonly int Depth;
        internal PropertyProof? Proof;
    }
    internal sealed class PropertyProof
    {
        internal FieldSlot? Field;
        internal ExpressionValue? Constant;
        internal string? Rejection;
        internal byte[]? ReturnSignature;
    }

    private MemberCatalog GetExpressionMembers(EvaluationBudget budget)
    {
        propertyContext ??= new PropertyReadContext();
        budget.MetadataProbe();
        CorDebugType exact = value!.ExactType;
        if (propertyContext.Types.TryGetValue(exact.Raw, out MemberCatalog? cached)) return cached;
        var catalog = new MemberCatalog();
        int depth = 0;
        for (CorDebugType? type = exact; type is not null && type.Type != CorElementType.Object; type = type.Base)
        {
            budget.MetadataProbe();
            if (++depth > 128) throw EvaluationBudget.Limit();
            catalog.Hierarchy.Add(type);
            MetaDataImport metadata = type.Class.Module.GetMetaDataInterface().MetaDataImport;
            AddFields(type, metadata, catalog.Fields, budget.MetadataProbe);
            var propertyTokens = new mdProperty[32]; IntPtr enumeration = IntPtr.Zero;
            try
            {
                while (true)
                {
                    budget.MetadataProbe();
                    HRESULT status = metadata.TryEnumProperties(ref enumeration, type.Class.Token, propertyTokens, out int count);
                    if (status != HRESULT.S_FALSE) ClrDebug.Extensions.ThrowOnFailed(status);
                    if (count == 0) break;
                    for (int index = 0; index < count; index++)
                    {
                        budget.MetadataProbe();
                        GetPropertyPropsResult property = metadata.GetPropertyProps(propertyTokens[index]);
                        catalog.Properties.Add(new PropertySlot(property.szProperty, type, property.pmdGetter, depth));
                    }
                }
            }
            finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
        }
        // Do not retain a partly-built catalogue after cancellation or budget exhaustion.
        budget.Check(); propertyContext.Types.Add(exact.Raw, catalog); return catalog;
    }

    private ExpressionValue ReadProvedProperty(string name, MemberCatalog catalog, EvaluationBudget budget)
    {
        PropertySlot[] matches = catalog.Properties.Where(property => property.Name == name).ToArray();
        if (matches.Length == 0) throw MemberUnavailable("Member was not found.", name, catalog, budget);
        PropertySlot property = matches[0];
        if (matches.Count(candidate => candidate.Depth == property.Depth) != 1) throw MemberUnavailable("Property metadata is ambiguous.", name, catalog, budget);
        PropertyProof proof = property.Proof ?? ProveProperty(property, catalog, budget);
        budget.Check(); property.Proof = proof;
        if (proof.Rejection is not null) throw MemberUnavailable(proof.Rejection, name, catalog, budget);
        if (proof.Constant is not null) return proof.Constant;
        FieldSlot field = proof.Field!;
        var instance = new CorDebugObjectValue((ICorDebugObjectValue)value!.Raw);
        ExpressionValue captured = Expression(() => field.IsStatic ? field.Type.GetStaticFieldValue(field.Token, frame.Raw)
            : instance.GetFieldValue(field.Type.Class.Raw, field.Token), frame, budget, propertyContext);
        byte[] signature = proof.ReturnSignature!;
        CorElementType returnType = (CorElementType)signature[2];
        if (returnType < CorElementType.Boolean || returnType > CorElementType.R8) return captured;
        // ldfld puts narrow integers on the I4 stack; ret applies the declared type.
        object? scalar = captured.Scalar;
        if (scalar is bool or char or sbyte or byte or short or ushort) scalar = Convert.ToInt32(scalar);
        else if (scalar is uint unsigned) scalar = unchecked((int)unsigned);
        else if (scalar is ulong large) scalar = unchecked((long)large);
        return captured.Object is null && ConstantResult(scalar, signature) is { } converted ? converted
            : throw MemberUnavailable("Field value cannot be represented by the getter return type.", name, catalog, budget);
    }

    private static PropertyProof RejectProperty(string reason) => new() { Rejection = reason };
    private static FxDbgException MemberUnavailable(string reason, string name, MemberCatalog catalog, EvaluationBudget budget) => new(FxDbgErrorCode.ExpressionNameNotFound,
        "Getter cannot be proved read-only and will not be called. " + reason +
        MemberSuggestions.Suffix(name, catalog.Fields.Select(field => field.Name).Concat(catalog.Properties.Select(property => property.Name)), budget));

    private static PropertyProof ProveProperty(PropertySlot property, MemberCatalog catalog, EvaluationBudget budget)
    {
        try
        {
            if (property.Getter.Value == 0 || property.Name.IndexOf('.') >= 0) return RejectProperty("Getter is absent or is an explicit interface implementation.");
            CorDebugModule module = property.Type.Class.Module;
            MetaDataImport metadata = module.GetMetaDataInterface().MetaDataImport;
            budget.MetadataProbe();
            MetaDataImport_GetMethodPropsResult method = metadata.GetMethodProps(property.Getter);
            if (method.szMethod.IndexOf('.') >= 0 || (method.pdwAttr & CorMethodAttr.mdAbstract) != 0 ||
                (method.pdwImplFlags & (CorMethodImpl.miCodeTypeMask | CorMethodImpl.miUnmanaged | CorMethodImpl.miInternalCall)) != 0)
                return RejectProperty("Getter is abstract, native or otherwise unsupported.");
            byte[] signature = ReadSignature(method.ppvSigBlob, method.pcbSigBlob);
            bool isStatic = (method.pdwAttr & CorMethodAttr.mdStatic) != 0;
            if (signature.Length < 3 || signature[0] != (isStatic ? 0 : 0x20) || signature[1] != 0)
                return RejectProperty("Only zero-parameter non-generic getters are supported.");
            if ((method.pdwAttr & CorMethodAttr.mdVirtual) != 0 && !HasUnambiguousDispatch(property, method.szMethod, catalog, budget))
                return RejectProperty("Virtual getter dispatch cannot be proved from the property metadata.");
            budget.MetadataProbe();
            CorDebugCode code = module.GetFunctionFromToken(property.Getter).ILCode;
            if (code is null || code.Size < 2 || code.Size > TrivialGetterProof.MaximumIlBytes)
                return RejectProperty($"Getter has no IL or exceeds the {TrivialGetterProof.MaximumIlBytes}-byte whitelist.");
            budget.MetadataProbe();
            if (!HasNoExceptionSections(module, method.pulCodeRVA, code, budget))
                return RejectProperty("Method header is unavailable, inconsistent or contains extra sections.");
            int size = code.Size; budget.InspectIl(size);
            byte[] il = code.GetCode(0, size, size);
            if (il.Length != size) return RejectProperty("IL read was incomplete.");
            TrivialGetterProof? load = TrivialGetterProof.Decode(il, isStatic);
            if (load is null) return RejectProperty("IL contains instructions outside the exact field/constant whitelist.");
            if (load.Kind == GetterLoadKind.Constant)
            {
                ExpressionValue? constant = ConstantResult(load.Constant, signature);
                return constant is null ? RejectProperty("Constant return type is unsupported.") : new PropertyProof { Constant = constant };
            }
            FieldSlot? field = ResolveGetterField(load.FieldToken, property.Type, catalog, budget);
            if (field is null || field.IsStatic != (load.Kind == GetterLoadKind.StaticField))
                return RejectProperty("Field token cannot be matched to one receiver field slot.");
            return new PropertyProof { Field = field, ReturnSignature = signature };
        }
        catch (DebugException) { return RejectProperty("Runtime metadata or IL is unavailable."); }
        catch (COMException) { return RejectProperty("Runtime metadata or IL is unavailable."); }
    }

    private static bool HasUnambiguousDispatch(PropertySlot property, string getterName, MemberCatalog catalog, EvaluationBudget budget)
    {
        for (int depth = 0; depth < property.Depth; depth++)
        {
            CorDebugType type = catalog.Hierarchy[depth];
            MetaDataImport metadata = type.Class.Module.GetMetaDataInterface().MetaDataImport;
            IntPtr enumeration = IntPtr.Zero;
            var bodies = new mdToken[8]; var declarations = new mdToken[8];
            try
            {
                while (true)
                {
                    budget.MetadataProbe();
                    HRESULT status = metadata.TryEnumMethodImpls(ref enumeration, type.Class.Token, bodies, declarations, out int count);
                    if (status != HRESULT.S_FALSE) ClrDebug.Extensions.ThrowOnFailed(status);
                    if (count == 0) break;
                    // A MethodImpl may replace this getter without a Property row. Check
                    // its declaration; unrelated interface implementations do not replace it.
                    for (int index = 0; index < count; index++)
                    {
                        budget.MetadataProbe();
                        mdToken declaration = declarations[index];
                        string name = declaration.Type == CorTokenType.mdtMethodDef
                            ? metadata.GetMethodProps(new mdMethodDef(declaration.Value)).szMethod
                            : declaration.Type == CorTokenType.mdtMemberRef
                                ? metadata.GetMemberRefProps(new mdMemberRef(declaration.Value)).szMember : getterName;
                        if (name == getterName) return false;
                    }
                }
            }
            finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
            if (depth == property.Depth - 1) continue;
            enumeration = IntPtr.Zero;
            try
            {
                budget.MetadataProbe();
                // Implicit overrides also need not carry a Property row. Even an overload
                // with this accessor name is conservatively rejected rather than dispatched.
                HRESULT status = metadata.TryEnumMethodsWithName(ref enumeration, type.Class.Token, getterName, new mdMethodDef[1], out int count);
                if (status != HRESULT.S_FALSE) ClrDebug.Extensions.ThrowOnFailed(status);
                if (count != 0) return false;
            }
            finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
        }
        return true;
    }

    private static byte[] ReadSignature(IntPtr address, int length)
    {
        if (address == IntPtr.Zero || length < 1 || length > 128) return Array.Empty<byte>();
        var bytes = new byte[length]; Marshal.Copy(address, bytes, 0, length); return bytes;
    }
    private static bool HasNoExceptionSections(CorDebugModule module, int rva, CorDebugCode code, EvaluationBudget budget)
    {
        // ECMA-335 II.25.4: tiny headers cannot carry EH; fat headers advertise all
        // additional sections in MoreSects. Read the loaded method, never an on-disk twin.
        if (rva <= 0 || module.IsDynamic || module.IsInMemory) return false;
        ulong headerAddress = checked(module.BaseAddress.Value + (uint)rva);
        byte[] first = ReadTargetBytes(module.Process, headerAddress, 1, budget);
        if ((first[0] & 3) == 2)
            return TrivialGetterProof.HeaderSizeWithoutSections(first, code.Size) == 1 && checked(headerAddress + 1) == code.Address.Value;
        if ((first[0] & 3) != 3) return false;
        byte[] header = ReadTargetBytes(module.Process, headerAddress, 12, budget);
        return TrivialGetterProof.HeaderSizeWithoutSections(header, code.Size) == 12 && checked(headerAddress + 12) == code.Address.Value;
    }
    private static byte[] ReadTargetBytes(CorDebugProcess process, ulong address, int length, EvaluationBudget budget)
    {
        budget.Read();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (process.ReadMemory(new CORDB_ADDRESS(address), length, buffer) != length)
                throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Method header read was incomplete.");
            var bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, length); budget.Check(); return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static ExpressionValue? ConstantResult(object? value, byte[] signature)
    {
        CorElementType type = (CorElementType)signature[2];
        if (value is null) return type is CorElementType.String or CorElementType.Object or CorElementType.Class or CorElementType.SZArray or CorElementType.Array ||
            type == CorElementType.GenericInst && signature.Length > 3 && signature[3] == (byte)CorElementType.Class ? new ExpressionValue((object?)null) : null;
        if (signature.Length != 3) return null;
        if (type == CorElementType.Boolean && value is int flag) return new ExpressionValue((object)(flag != 0));
        if (value is int integer)
        {
            object? constant = type switch
            {
                CorElementType.I1 => (object)unchecked((sbyte)integer), CorElementType.U1 => unchecked((byte)integer),
                CorElementType.I2 => unchecked((short)integer), CorElementType.U2 => unchecked((ushort)integer),
                CorElementType.Char => unchecked((char)integer), CorElementType.I4 => integer,
                CorElementType.U4 => unchecked((uint)integer), _ => null
            };
            return constant is null ? null : new ExpressionValue(constant);
        }
        if (value is long large && type is CorElementType.I8 or CorElementType.U8)
            return new ExpressionValue(type == CorElementType.I8 ? (object)large : unchecked((ulong)large));
        if (value is float && type == CorElementType.R4 || value is double && type == CorElementType.R8) return new ExpressionValue(value);
        if (value is float single && type == CorElementType.R8) return new ExpressionValue((object)(double)single);
        if (value is double real && type == CorElementType.R4) return new ExpressionValue((object)(float)real);
        return null;
    }

    private static FieldSlot? ResolveGetterField(int token, CorDebugType declaringType, MemberCatalog catalog, EvaluationBudget budget)
    {
        CorDebugModule module = declaringType.Class.Module;
        MetaDataImport metadata = module.GetMetaDataInterface().MetaDataImport;
        if ((uint)token >> 24 == 0x04)
        {
            FieldSlot[] exact = catalog.Fields.Where(field => field.Token.Value == (uint)token && Equals(field.Type.Class.Module.Raw, module.Raw)).ToArray();
            return exact.Length == 1 ? exact[0] : null;
        }
        budget.MetadataProbe();
        GetMemberRefPropsResult member = metadata.GetMemberRefProps(new mdMemberRef(token));
        byte[] referenceSignature = ReadSignature(member.ppvSigBlob, member.pbSig);
        var matches = new List<FieldSlot>();
        foreach (FieldSlot field in catalog.Fields)
        {
            budget.Step();
            if (field.Name != member.szMember || !MatchesOwner(metadata, member.ptk, field.Type, declaringType, budget)) continue;
            budget.MetadataProbe();
            MetaDataImport fieldMetadata = field.Type.Class.Module.GetMetaDataInterface().MetaDataImport;
            GetFieldPropsResult fieldInfo = fieldMetadata.GetFieldProps(field.Token);
            byte[] fieldSignature = ReadSignature(fieldInfo.ppvSigBlob, fieldInfo.pcbSigBlob);
            // Same-scope blobs have the same token vocabulary. Across scopes accept only a
            // primitive signature; never equate unrelated TypeRef tokens by their numeric value.
            bool comparable = Equals(metadata.Raw, fieldMetadata.Raw) || referenceSignature.Length == 2 && referenceSignature[0] == 6 && referenceSignature[1] <= (byte)CorElementType.String;
            if (comparable && referenceSignature.Length > 0 && referenceSignature.SequenceEqual(fieldSignature)) matches.Add(field);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool MatchesOwner(MetaDataImport metadata, mdToken token, CorDebugType actual, CorDebugType declaringType, EvaluationBudget budget)
    {
        if (token.Type == CorTokenType.mdtTypeSpec)
        {
            budget.MetadataProbe();
            GetTypeSpecFromTokenResult spec = metadata.GetTypeSpecFromToken(new mdTypeSpec(token.Value));
            byte[] bytes = ReadSignature(spec.ppvSig, spec.pcbSig); int position = 0;
            return MatchesType(bytes, ref position, metadata, actual, declaringType, budget, 0) && position == bytes.Length;
        }
        return MatchesTypeToken(metadata, token, actual, budget);
    }
    private static bool MatchesTypeToken(MetaDataImport metadata, mdToken token, CorDebugType actual, EvaluationBudget budget)
    {
        budget.MetadataProbe();
        if (actual.Type is not (CorElementType.Class or CorElementType.ValueType)) return false;
        MetaDataImport actualMetadata = actual.Class.Module.GetMetaDataInterface().MetaDataImport;
        if (token.Type == CorTokenType.mdtTypeDef) return token.Value == actual.Class.Token.Value && Equals(metadata.Raw, actualMetadata.Raw);
        // ClrDebug 0.4.2 marks ResolveTypeRef as unavailable in the native vtable.
        // A name-only guess could select a same-named type from another assembly.
        return false;
    }
    private static bool MatchesType(byte[] bytes, ref int position, MetaDataImport metadata, CorDebugType actual,
        CorDebugType declaringType, EvaluationBudget budget, int depth)
    {
        budget.Step();
        if (depth >= 16 || position >= bytes.Length) return false;
        CorElementType kind = (CorElementType)bytes[position++];
        if (kind == CorElementType.Var)
        {
            int index = ReadCompressed(bytes, ref position);
            CorDebugType[] parameters = declaringType.TypeParameters;
            return index >= 0 && index < parameters.Length && Equals(parameters[index].Raw, actual.Raw);
        }
        if (kind == CorElementType.GenericInst)
        {
            if (position >= bytes.Length || bytes[position++] != (byte)actual.Type) return false;
            if (!MatchesTypeToken(metadata, ReadTypeToken(bytes, ref position), actual, budget)) return false;
            int count = ReadCompressed(bytes, ref position); CorDebugType[] parameters = actual.TypeParameters;
            if (count < 0 || count != parameters.Length) return false;
            foreach (CorDebugType parameter in parameters)
                if (!MatchesType(bytes, ref position, metadata, parameter, declaringType, budget, depth + 1)) return false;
            return true;
        }
        if (kind != actual.Type) return false;
        if (kind is CorElementType.Class or CorElementType.ValueType) return MatchesTypeToken(metadata, ReadTypeToken(bytes, ref position), actual, budget);
        if (kind == CorElementType.SZArray) return MatchesType(bytes, ref position, metadata, actual.FirstTypeParameter, declaringType, budget, depth + 1);
        return kind >= CorElementType.Boolean && kind <= CorElementType.String || kind == CorElementType.Object;
    }
    private static mdToken ReadTypeToken(byte[] bytes, ref int position)
    {
        int encoded = ReadCompressed(bytes, ref position);
        if (encoded < 0 || (encoded & 3) == 3) return default;
        uint tag = (encoded & 3) switch { 0 => 0x02000000u, 1 => 0x01000000u, _ => 0x1b000000u };
        return new mdToken(tag | (uint)(encoded >> 2));
    }
    private static int ReadCompressed(byte[] bytes, ref int position)
    {
        if (position >= bytes.Length) return -1;
        int value = bytes[position++]; if ((value & 0x80) == 0) return value;
        if ((value & 0xc0) == 0x80 && position < bytes.Length) return ((value & 0x3f) << 8) | bytes[position++];
        if ((value & 0xe0) != 0xc0 || bytes.Length - position < 3) return -1;
        return ((value & 0x1f) << 24) | (bytes[position++] << 16) | (bytes[position++] << 8) | bytes[position++];
    }
}
