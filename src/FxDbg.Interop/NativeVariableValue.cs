using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Variables;

namespace FxDbg.Interop;

internal sealed partial class NativeVariableValue : IVariableValue
{
    private readonly CorDebugValue? value;
    private readonly CorDebugILFrame frame;
    private readonly string typeName;
    private List<FieldSlot>? fields;

    private NativeVariableValue(CorDebugValue original, CorDebugILFrame frame)
    {
        this.frame = frame;
        value = MetadataNames.Dereference(original);
        if (value?.Raw is ICorDebugBoxValue box) value = new CorDebugBoxValue(box).Object;
        // Null references have no heap object from which Desktop CLR can discover ExactType.
        typeName = value is null ? "object" : TypeNameOf(value.ExactType);
    }

    internal static IVariableValue Capture(Func<CorDebugValue> read, CorDebugILFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return new NativeVariableValue(read(), frame); }
        catch (DebugException exception) { return new FaultValue(exception); }
    }

    public VariableStatus Status => value is null ? VariableStatus.Null : VariableStatus.Available;
    public string TypeName => typeName;
    public string? Diagnostic => null;
    private bool HasFields => value is not null && value.Raw is ICorDebugObjectValue && value.Raw is not ICorDebugStringValue;
    public string? ReferenceIdentity => Read(() => value is not null &&
        (value.Raw is ICorDebugArrayValue || HasFields)
        ? typeName + "@" + value.Address.Value.ToString("x", CultureInfo.InvariantCulture) : null);
    public int GetMemberCount(CancellationToken cancellationToken = default) => Read(() => value?.Raw is ICorDebugArrayValue array ? new CorDebugArrayValue(array).Count
        : HasFields ? GetFields(cancellationToken).Count : 0);

    public string Format(int maxStringLength) => Read(() =>
    {
        if (value is null) return "null";
        if (value.Raw is ICorDebugStringValue text)
        {
            var str = new CorDebugStringValue(text);
            bool truncated = str.Length > maxStringLength;
            int length = Math.Min(str.Length, truncated ? maxStringLength - 1 : maxStringLength);
            string result = MetadataNames.ReadString(text, length);
            return result + (truncated ? "…" : "");
        }
        if (value.Raw is ICorDebugArrayValue array) return typeName + " (" + new CorDebugArrayValue(array).Count + " elements)";
        if (value.Raw is ICorDebugGenericValue generic && IsPrimitive(value.Type))
        {
            int size = value.Size;
            if (size < 1 || size > 16) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Unexpected primitive storage size.");
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                new CorDebugGenericValue(generic).GetValue(buffer);
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return FormatPrimitive(value.Type, bytes);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return "{" + typeName + "}";
    });

    public IReadOnlyList<VariableMember> GetMembers(int start, int count, CancellationToken cancellationToken = default) => Read<IReadOnlyList<VariableMember>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = new List<VariableMember>();
        if (value?.Raw is ICorDebugArrayValue array)
        {
            var items = new CorDebugArrayValue(array);
            int end = (int)Math.Min(items.Count, (long)start + count);
            for (int index = start; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int position = index;
                members.Add(new VariableMember("[" + index + "]", VariableKind.ArrayElement, Capture(() => items.GetElementAtPosition(position), frame, cancellationToken)));
            }
        }
        else if (HasFields && value?.Raw is ICorDebugObjectValue raw)
        {
            var instance = new CorDebugObjectValue(raw);
            foreach (FieldSlot field in GetFields(cancellationToken).Skip(start).Take(count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                members.Add(new VariableMember(field.Name, field.IsStatic ? VariableKind.StaticField : VariableKind.InstanceField,
                    Capture(() => field.IsStatic ? field.Type.GetStaticFieldValue(field.Token, frame.Raw)
                        : instance.GetFieldValue(field.Type.Class.Raw, field.Token), frame, cancellationToken)));
            }
        }
        return members;
    });

    private List<FieldSlot> GetFields(CancellationToken cancellationToken)
    {
        if (fields is not null) return fields;
        var result = new List<FieldSlot>();
        CorDebugType? type = value!.ExactType;
        int hierarchyDepth = 0;
        while (type is not null && type.Type != CorElementType.Object)
        {
            if (++hierarchyDepth > 128) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Type hierarchy exceeds 128 levels.");
            MetaDataImport metadata = type.Class.Module.GetMetaDataInterface().MetaDataImport;
            AddFields(type, metadata, result, cancellationToken.ThrowIfCancellationRequested);
            type = type.Base;
        }
        return fields = result;
    }

    private static void AddFields(CorDebugType type, MetaDataImport metadata, List<FieldSlot> result, Action beforeRead)
    {
        var tokens = new mdFieldDef[64];
        IntPtr enumeration = IntPtr.Zero;
        try
        {
            while (true)
            {
                beforeRead();
                int count = MetadataNames.EnumFields(metadata, ref enumeration, type.Class.Token, tokens);
                if (count == 0) break;
                if (result.Count + count > 10000) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Type field count exceeds 10000.");
                for (int index = 0; index < count; index++)
                {
                    beforeRead();
                    GetFieldPropsResult field = metadata.GetFieldProps(tokens[index]);
                    result.Add(new FieldSlot(field.szField, tokens[index], type, (field.pdwAttr & CorFieldAttr.fdStatic) != 0));
                }
            }
        }
        finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
    }

    private static T Read<T>(Func<T> action)
    {
        try { return action(); }
        catch (DebugException exception)
        {
            throw new FxDbgException(exception.HResult == HRESULT.CORDBG_E_IL_VAR_NOT_AVAILABLE
                ? FxDbgErrorCode.ValueOptimizedAway : FxDbgErrorCode.ValueUnavailable,
                "Value could not be read (" + exception.HResult + ").", exception);
        }
    }

    internal static string TypeNameOf(CorDebugType type, int depth = 0)
    {
        if (depth > 32) return "<type depth limit>";
        if (type.Type == CorElementType.Class || type.Type == CorElementType.ValueType)
        {
            string name = MetadataNames.Type(type.Class.Module.GetMetaDataInterface().MetaDataImport, type.Class.Token);
            CorDebugType[] parameters = type.TypeParameters;
            return parameters.Length == 0 ? name : name + "<" + string.Join(", ", parameters.Select(parameter => TypeNameOf(parameter, depth + 1))) + ">";
        }
        if (type.Type == CorElementType.SZArray || type.Type == CorElementType.Array)
            return TypeNameOf(type.FirstTypeParameter, depth + 1) + "[" + new string(',', Math.Max(0, type.Rank - 1)) + "]";
        return type.Type switch
        {
            CorElementType.Boolean => "bool", CorElementType.Char => "char",
            CorElementType.I1 => "sbyte", CorElementType.U1 => "byte",
            CorElementType.I2 => "short", CorElementType.U2 => "ushort",
            CorElementType.I4 => "int", CorElementType.U4 => "uint",
            CorElementType.I8 => "long", CorElementType.U8 => "ulong",
            CorElementType.R4 => "float", CorElementType.R8 => "double",
            CorElementType.I => "System.IntPtr", CorElementType.U => "System.UIntPtr",
            CorElementType.String => "string", CorElementType.Object => "object",
            _ => type.Type.ToString()
        };
    }

    private static bool IsPrimitive(CorElementType type) => type >= CorElementType.Boolean && type <= CorElementType.R8
        || type == CorElementType.I || type == CorElementType.U;

    private static string FormatPrimitive(CorElementType type, byte[] data) => type switch
    {
        CorElementType.Boolean => data[0] == 0 ? "false" : "true",
        CorElementType.Char => ((char)BitConverter.ToUInt16(data, 0)).ToString(),
        CorElementType.I1 => unchecked((sbyte)data[0]).ToString(CultureInfo.InvariantCulture),
        CorElementType.U1 => data[0].ToString(CultureInfo.InvariantCulture),
        CorElementType.I2 => BitConverter.ToInt16(data, 0).ToString(CultureInfo.InvariantCulture),
        CorElementType.U2 => BitConverter.ToUInt16(data, 0).ToString(CultureInfo.InvariantCulture),
        CorElementType.I4 => BitConverter.ToInt32(data, 0).ToString(CultureInfo.InvariantCulture),
        CorElementType.U4 => BitConverter.ToUInt32(data, 0).ToString(CultureInfo.InvariantCulture),
        CorElementType.I8 => BitConverter.ToInt64(data, 0).ToString(CultureInfo.InvariantCulture),
        CorElementType.U8 => BitConverter.ToUInt64(data, 0).ToString(CultureInfo.InvariantCulture),
        CorElementType.R4 => BitConverter.ToSingle(data, 0).ToString("R", CultureInfo.InvariantCulture),
        CorElementType.R8 => BitConverter.ToDouble(data, 0).ToString("R", CultureInfo.InvariantCulture),
        CorElementType.I => data.Length == 4 ? BitConverter.ToInt32(data, 0).ToString(CultureInfo.InvariantCulture) : BitConverter.ToInt64(data, 0).ToString(CultureInfo.InvariantCulture),
        _ => data.Length == 4 ? BitConverter.ToUInt32(data, 0).ToString(CultureInfo.InvariantCulture) : BitConverter.ToUInt64(data, 0).ToString(CultureInfo.InvariantCulture)
    };

    internal sealed class FieldSlot
    {
        internal FieldSlot(string name, mdFieldDef token, CorDebugType type, bool isStatic) { Name = name; Token = token; Type = type; IsStatic = isStatic; }
        internal string Name { get; }
        internal mdFieldDef Token { get; }
        internal CorDebugType Type { get; }
        internal bool IsStatic { get; }
    }

    private sealed class FaultValue : IVariableValue
    {
        private readonly DebugException error;
        internal FaultValue(DebugException error) => this.error = error;
        public VariableStatus Status => error.HResult == HRESULT.CORDBG_E_IL_VAR_NOT_AVAILABLE ? VariableStatus.OptimizedAway : VariableStatus.Unavailable;
        public string TypeName => "unknown";
        public string? Diagnostic => "Value could not be read (" + error.HResult + ").";
        public string? ReferenceIdentity => null;
        public int GetMemberCount(CancellationToken cancellationToken = default) => 0;
        public string Format(int maxStringLength) => "";
        public IReadOnlyList<VariableMember> GetMembers(int start, int count, CancellationToken cancellationToken = default) => Array.Empty<VariableMember>();
    }
}
