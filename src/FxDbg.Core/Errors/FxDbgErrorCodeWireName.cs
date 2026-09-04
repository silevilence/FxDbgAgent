using System;
using System.Text;

namespace FxDbg.Core.Errors;

public static class FxDbgErrorCodeWireName
{
    public static string Format(FxDbgErrorCode value)
    {
        string name = value.ToString();
        var result = new StringBuilder();
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (index > 0 && char.IsUpper(character))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    public static bool TryParse(string value, out FxDbgErrorCode code)
    {
        foreach (FxDbgErrorCode candidate in Enum.GetValues(typeof(FxDbgErrorCode)))
        {
            if (string.Equals(Format(candidate), value, StringComparison.Ordinal))
            {
                code = candidate;
                return true;
            }
        }

        code = FxDbgErrorCode.InternalError;
        return false;
    }
}
