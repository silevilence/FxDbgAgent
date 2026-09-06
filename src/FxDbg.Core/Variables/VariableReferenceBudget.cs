namespace FxDbg.Core.Variables;

/// <summary>Shared across all AppDomain views of one stop, owned by the command thread.</summary>
public sealed class VariableReferenceBudget
{
    private int count;
    internal bool TryReserve()
    {
        if (count == 10000) return false;
        count++;
        return true;
    }
}
