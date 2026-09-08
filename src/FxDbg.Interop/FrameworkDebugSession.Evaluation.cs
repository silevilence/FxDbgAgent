using System;
using System.Linq;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using FxDbg.Core.Model;
using FxDbg.Core.Variables;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    public VariableInfo Evaluate(FrameId frameId, string expression, int evaluationTimeoutMs = 250, int maxDepth = 1,
        int count = 100, int maxStringLength = 256, CancellationToken cancellationToken = default, string? appDomainId = null,
        EvaluationBudget? budget = null)
    {
        RequireStopped(); RequireAppDomain(appDomainId);
        VariableReader.Validate(maxDepth, count, maxStringLength);
        using EvaluationBudget? ownedBudget = budget is null ? new EvaluationBudget(evaluationTimeoutMs, cancellationToken) : null;
        budget ??= ownedBudget!;
        try { return EvaluateCore(frameId, expression, maxDepth, count, maxStringLength, appDomainId, budget); }
        catch (OperationCanceledException) { budget.Check(); throw; }
    }

    private VariableInfo EvaluateCore(FrameId frameId, string expression, int maxDepth, int count, int maxStringLength,
        string? appDomainId, EvaluationBudget budget)
    {
        budget.Check();
        if (!framesById.TryGetValue(frameId, out FrameHandle? handle))
            throw new FxDbgException(FxDbgErrorCode.FrameNotFound, "Frame ID is unknown or belongs to an earlier stop.");
        if (appDomainId is not null && handle.AppDomain.AppDomainId != appDomainId)
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Frame does not belong to the selected AppDomain.");
        RestrictedExpression parsed = RestrictedExpression.Parse(expression, budget);
        ExpressionValue value = parsed.Evaluate((name, limits) =>
        {
            foreach (RootVariable root in DescribeRoots(handle.Frame))
            {
                limits.Step();
                if (root.Name == name) return NativeVariableValue.Expression(root.Read, handle.Frame, limits);
            }
            throw new FxDbgException(FxDbgErrorCode.ExpressionNameNotFound, "Expression name is unavailable in this frame.");
        }, budget);
        if (!variableReaders.TryGetValue(handle.AppDomain.AppDomainId, out VariableReader? reader))
        {
            reader = new VariableReader(SessionId + ":" + stopGeneration + ":" + handle.AppDomain.AppDomainId, handle.AppDomain, variableReferenceBudget);
            variableReaders.Add(handle.AppDomain.AppDomainId, reader);
        }
        // A fixed result name prevents source expressions from leaking through serialized values/logs.
        VariableInfo result = reader.Read(new[] { new VariableMember("result", VariableKind.Local, value.Variable) }, maxDepth, count, maxStringLength, budget.Token).Single();
        budget.Check(); return result;
    }
}
