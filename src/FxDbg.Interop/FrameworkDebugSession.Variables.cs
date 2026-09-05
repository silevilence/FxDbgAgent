using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Variables;
using FxDbg.Symbols.Windows;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    private VariableReader? variableReader;

    public IReadOnlyList<VariableInfo> GetVariables(FrameId frameId, VariableReferenceId? referenceId = null,
        int start = 0, int count = 100, int maxDepth = 1, int maxStringLength = 256, CancellationToken cancellationToken = default)
    {
        RequireStopped();
        VariableReader.Validate(maxDepth, count, maxStringLength);
        if (start < 0) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Variable page start must be nonnegative.");
        if (!framesById.TryGetValue(frameId, out FrameHandle? handle))
            throw new FxDbgException(FxDbgErrorCode.FrameNotFound, "Frame ID is unknown or belongs to an earlier stop.");
        cancellationToken.ThrowIfCancellationRequested();
        variableReader ??= new VariableReader(SessionId + ":" + stopGeneration);
        if (referenceId is not null) return variableReader.Expand(referenceId, start, count, maxDepth, maxStringLength);
        var roots = new List<VariableMember>();
        foreach (RootVariable root in DescribeRoots(handle.Frame).Skip(start).Take(count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            roots.Add(new VariableMember(root.Name, root.Kind, NativeVariableValue.Capture(root.Read, handle.Frame)));
        }
        return variableReader.Read(roots, maxDepth, count, maxStringLength);
    }

    private IEnumerable<RootVariable> DescribeRoots(CorDebugILFrame frame)
    {
        CorDebugFunction function = frame.Function;
        MetaDataImport metadata = function.Module.GetMetaDataInterface().MetaDataImport;
        MetaDataImport_GetMethodPropsResult method = metadata.GetMethodProps(function.Token);
        bool isStatic = (method.pdwAttr & CorMethodAttr.mdStatic) != 0;
        if (!isStatic) yield return new RootVariable("this", VariableKind.Argument, () => frame.GetArgument(0));
        var parameters = new mdParamDef[32];
        IntPtr enumeration = IntPtr.Zero;
        try
        {
            int count;
            int total = 0;
            while ((count = MetadataNames.EnumParams(metadata, ref enumeration, function.Token, parameters)) > 0)
            {
                if ((total += count) > 10000) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Parameter metadata exceeds its budget.");
                for (int index = 0; index < count; index++)
                {
                    GetParamPropsResult parameter = metadata.GetParamProps(parameters[index]);
                    if (parameter.pulSequence == 0) continue;
                    int position = parameter.pulSequence - (isStatic ? 1 : 0);
                    string name = string.IsNullOrEmpty(parameter.szName) ? "arg" + parameter.pulSequence : parameter.szName;
                    yield return new RootVariable(name, VariableKind.Argument, () => frame.GetArgument(position));
                }
            }
        }
        finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
        modules.TryGetValue(function.Module.Raw, out DebugModule? module);
        if (module?.HasSymbols == true)
        {
            foreach (LocalVariableSlot slot in module.GetLocals(unchecked((int)function.Token.Value), frame.IP.pnOffset))
                yield return new RootVariable(slot.Name, VariableKind.Local, () => frame.GetLocalVariable(slot.Index));
        }
        else
        {
            int index = 0;
            foreach (CorDebugValue value in frame.EnumerateLocalVariables())
            {
                if (index == 10000) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Local variable count exceeds its budget.");
                CorDebugValue captured = value;
                yield return new RootVariable("local[" + index++ + "]", VariableKind.Local, () => captured);
            }
        }
        var fields = new mdFieldDef[64];
        enumeration = IntPtr.Zero;
        try
        {
            int count;
            int total = 0;
            while ((count = MetadataNames.EnumFields(metadata, ref enumeration, method.pClass, fields)) > 0)
            {
                if ((total += count) > 10000) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Static field metadata exceeds its budget.");
                for (int index = 0; index < count; index++)
                {
                    mdFieldDef token = fields[index];
                    GetFieldPropsResult field = metadata.GetFieldProps(token);
                    if ((field.pdwAttr & CorFieldAttr.fdStatic) == 0) continue;
                    yield return new RootVariable(field.szField, VariableKind.StaticField,
                        () => function.Class.GetStaticFieldValue(unchecked((int)token.Value), frame.Raw));
                }
            }
        }
        finally { if (enumeration != IntPtr.Zero) metadata.CloseEnum(enumeration); }
    }

    private sealed class RootVariable
    {
        internal RootVariable(string name, VariableKind kind, Func<CorDebugValue> read) { Name = name; Kind = kind; Read = read; }
        internal string Name { get; }
        internal VariableKind Kind { get; }
        internal Func<CorDebugValue> Read { get; }
    }
}
