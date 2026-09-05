using System;
using System.Collections.Generic;
using System.Linq;
using ClrDebug;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    private bool stopOnFirstChance;

    public void ConfigureExceptionStops(bool firstChance)
    {
        RequireActive();
        stopOnFirstChance = firstChance;
    }

    private bool HandleExceptionCallback(CallbackEnvelope envelope)
    {
        if (envelope.EventArgs is not Exception2CorDebugManagedCallbackEventArgs exception) return false;
        bool unhandled = exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED;
        if (!unhandled && !(stopOnFirstChance && exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE)) return false;
        pendingEntryController = envelope.Controller;
        StopAt(exception.Thread, StopReason.Exception, unhandled);
        return true;
    }

    private ExceptionInfo CaptureException(CorDebugThread thread, bool unhandled)
    {
        IReadOnlyList<StackFrameInfo> stack = CaptureStack(thread, 0, 32);
        string typeName = "<exception type unavailable>";
        string? message = null;
        string? diagnostic = null;
        try
        {
            CorDebugValue? value = MetadataNames.Dereference(thread.CurrentException);
            if (value?.Raw is ICorDebugObjectValue raw)
            {
                typeName = NativeVariableValue.TypeNameOf(value.ExactType);
                message = ReadExceptionMessage(new CorDebugObjectValue(raw));
            }
            else diagnostic = "The current exception object is unavailable.";
        }
        catch (DebugException error) { diagnostic = "Exception metadata or message is unavailable (" + error.HResult + ")."; }
        return new ExceptionInfo(typeName, message, thread.Id, stack.FirstOrDefault()?.SourceLocation, stack, unhandled, diagnostic);
    }

    private static string? ReadExceptionMessage(CorDebugObjectValue exception)
    {
        CorDebugType? type = exception.ExactType;
        for (int depth = 0; type is not null && depth < 128; depth++, type = type.Base)
        {
            if (type.Type == CorElementType.Object) break;
            MetaDataImport metadata = type.Class.Module.GetMetaDataInterface().MetaDataImport;
            if (MetadataNames.Type(metadata, type.Class.Token) != "System.Exception" ||
                !string.Equals(System.IO.Path.GetFileName(type.Class.Module.Name), "mscorlib.dll", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = new mdFieldDef[32];
            IntPtr enumeration = IntPtr.Zero;
            try
            {
                int count;
                int total = 0;
                while ((count = MetadataNames.EnumFields(metadata, ref enumeration, type.Class.Token, fields)) > 0)
                {
                    if ((total += count) > 1024) return null;
                    for (int index = 0; index < count; index++)
                    {
                        if (metadata.GetFieldProps(fields[index]).szField != "_message") continue;
                        CorDebugValue? field = MetadataNames.Dereference(exception.GetFieldValue(type.Class.Raw, fields[index]));
                        if (field?.Raw is not ICorDebugStringValue text) return null;
                        int length = new CorDebugStringValue(text).Length;
                        return MetadataNames.ReadString(text, Math.Min(length, 4096)) + (length > 4096 ? "…" : "");
                    }
                }
            }
            finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
        }
        return null;
    }
}
