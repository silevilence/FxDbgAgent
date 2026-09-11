using System;
using System.Collections.Generic;
using System.Linq;

namespace FxDbg.Core.Evaluation;

/// <summary>Bounded diagnostics over metadata names only; never formats an expression or a value.</summary>
public static class MemberSuggestions
{
    public static string Suffix(string requested, IEnumerable<string> metadataNames, EvaluationBudget budget)
    {
        if (requested.Length == 0 || requested.Length > 64) return string.Empty;
        var candidates = new List<(string Name, int Rank, int Distance)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in metadataNames)
        {
            budget.Step();
            if (!SafeName(name, budget) || !seen.Add(name)) continue;
            int rank, distance = 0;
            if (name.StartsWith(requested, StringComparison.Ordinal)) rank = 0;
            else if (name.StartsWith(requested, StringComparison.OrdinalIgnoreCase)) rank = 1;
            else
            {
                rank = 2;
                distance = Distance(requested, name, budget);
                if (distance > 2) continue;
            }
            candidates.Add((name, rank, distance));
        }
        string[] names = candidates.OrderBy(candidate => candidate.Rank).ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal).Take(8).Select(candidate => candidate.Name).ToArray();
        return names.Length == 0 ? string.Empty : " Candidate members: " + string.Join(", ", names) + ".";
    }

    private static bool SafeName(string name, EvaluationBudget budget)
    {
        if (name.Length == 0 || name.Length > 64) return false;
        for (int index = 0; index < name.Length; index++)
        {
            budget.Step();
            char character = name[index];
            if (character != '_' && !char.IsLetter(character) && !(index > 0 && char.IsDigit(character))) return false;
        }
        return true;
    }

    private static int Distance(string left, string right, EvaluationBudget budget)
    {
        if (Math.Abs(left.Length - right.Length) > 2) return 3;
        var previous = new int[right.Length + 1];
        for (int column = 0; column <= right.Length; column++) previous[column] = column;
        for (int row = 1; row <= left.Length; row++)
        {
            var current = Enumerable.Repeat(3, right.Length + 1).ToArray();
            current[0] = row;
            int minimum = 3;
            for (int column = Math.Max(1, row - 2); column <= Math.Min(right.Length, row + 2); column++)
            {
                budget.Step();
                int substitution = char.ToUpperInvariant(left[row - 1]) == char.ToUpperInvariant(right[column - 1]) ? 0 : 1;
                current[column] = Math.Min(previous[column - 1] + substitution, Math.Min(previous[column] + 1, current[column - 1] + 1));
                minimum = Math.Min(minimum, current[column]);
            }
            if (minimum > 2) return 3;
            previous = current;
        }
        return previous[right.Length];
    }
}
