using System;
using System.Diagnostics;
using System.Threading;
using FxDbg.Core.Errors;

namespace FxDbg.Core.Evaluation;

/// <summary>Shared budget for parsing, interpretation and native read boundaries.</summary>
public sealed class EvaluationBudget : IDisposable
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly int milliseconds;
    private readonly CancellationToken externalCancellation;
    private readonly CancellationTokenSource deadline;
    private int steps, reads, characters;
    public CancellationToken Token { get; }
    public EvaluationBudget(int milliseconds = 250, CancellationToken token = default, int? maximumMilliseconds = null)
    {
        if (milliseconds < 1 || milliseconds > 1000) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Evaluation deadline must be 1-1000 ms.");
        if (maximumMilliseconds <= 0) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Outer deadline must be positive.");
        this.milliseconds = Math.Min(milliseconds, maximumMilliseconds ?? milliseconds);
        externalCancellation = token;
        deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = deadline.Token;
        deadline.CancelAfter(this.milliseconds);
    }
    public void Check()
    {
        if (externalCancellation.IsCancellationRequested) throw new FxDbgException(FxDbgErrorCode.OperationCancelled, "Evaluation cancelled.");
        if (clock.ElapsedMilliseconds >= milliseconds || Token.IsCancellationRequested) throw new FxDbgException(FxDbgErrorCode.OperationTimedOut, "Evaluation timed out.");
    }
    public void Dispose() => deadline.Dispose();
    public void Step() { Check(); if (++steps > 10000) throw Limit(); }
    public void Read() { Step(); if (++reads > 1024) throw Limit(); }
    public void String(int length)
    {
        Check();
        if (length < 0 || length > 32768 || length > 65536 - characters) throw Limit();
        characters += length;
    }
    public static FxDbgException Limit() => new(FxDbgErrorCode.ExpressionLimitExceeded, "Expression resource budget exceeded.");
    public static FxDbgException TypeError() => new(FxDbgErrorCode.ExpressionTypeError, "Expression operand types are not supported.");
}
