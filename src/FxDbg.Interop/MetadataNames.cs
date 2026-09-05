using System;
using ClrDebug;

namespace FxDbg.Interop;

internal static class MetadataNames
{
    internal static int EnumFields(MetaDataImport metadata, ref IntPtr enumeration, mdTypeDef type, mdFieldDef[] fields)
    {
        HRESULT result = metadata.TryEnumFields(ref enumeration, type, fields, out int count);
        if (result != HRESULT.S_FALSE) ClrDebug.Extensions.ThrowOnFailed(result);
        return count;
    }

    internal static int EnumParams(MetaDataImport metadata, ref IntPtr enumeration, mdMethodDef method, mdParamDef[] parameters)
    {
        HRESULT result = metadata.TryEnumParams(ref enumeration, method, parameters, out int count);
        if (result != HRESULT.S_FALSE) ClrDebug.Extensions.ThrowOnFailed(result);
        return count;
    }

    internal static string Method(CorDebugFunction function)
    {
        MetaDataImport metadata = function.Module.GetMetaDataInterface().MetaDataImport;
        MetaDataImport_GetMethodPropsResult method = metadata.GetMethodProps(function.Token);
        return Type(metadata, method.pClass) + "." + method.szMethod;
    }

    internal static string Type(MetaDataImport metadata, mdTypeDef token)
    {
        string name = metadata.GetTypeDefProps(token).szTypeDef;
        if (metadata.TryGetNestedClassProps(token, out mdTypeDef parent) == HRESULT.S_OK)
            return Type(metadata, parent) + "+" + name;
        return name;
    }

    internal static string? ThreadName(CorDebugThread thread)
    {
        try
        {
            CorDebugValue? value = Dereference(thread.Object);
            if (value?.Raw is not ICorDebugObjectValue raw) return null;
            var instance = new CorDebugObjectValue(raw);
            CorDebugClass type = instance.Class;
            MetaDataImport metadata = type.Module.GetMetaDataInterface().MetaDataImport;
            var fields = new mdFieldDef[32];
            IntPtr enumeration = IntPtr.Zero;
            try
            {
                int count;
                int inspected = 0;
                while ((count = MetadataNames.EnumFields(metadata, ref enumeration, type.Token, fields)) > 0)
                {
                    if ((inspected += count) > 1024) return null;
                    for (int index = 0; index < count; index++)
                    {
                        string name = metadata.GetFieldProps(fields[index]).szField;
                        if (name != "m_Name" && name != "_name") continue;
                        CorDebugValue? field = Dereference(instance.GetFieldValue(type.Raw, fields[index]));
                        if (field?.Raw is not ICorDebugStringValue text) return null;
                        var valueString = new CorDebugStringValue(text);
                        return ReadString(text, Math.Min(valueString.Length, 256));
                    }
                }
            }
            finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
        }
        catch (DebugException)
        {
            // Thread.Object can be unavailable for runtime threads; null means no readable name.
        }
        return null;
    }

    internal static CorDebugValue? Dereference(CorDebugValue value)
    {
        if (value.Raw is not ICorDebugReferenceValue raw) return value;
        var reference = new CorDebugReferenceValue(raw);
        return reference.IsNull ? null : reference.Dereference();
    }

    internal static string ReadString(ICorDebugStringValue value, int length)
    {
        if (length == 0) return string.Empty;
        var buffer = new char[length + 1];
        HRESULT result = value.GetString(buffer.Length, out int written, buffer);
        if (unchecked((int)result) < 0) ClrDebug.Extensions.ThrowOnFailed(result);
        // Desktop CLR may report the full required length even when the buffer is truncated.
        return new string(buffer, 0, Math.Min(length, Math.Max(0, written)));
    }
}
