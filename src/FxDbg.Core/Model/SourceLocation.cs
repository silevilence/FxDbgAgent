using System;

namespace FxDbg.Core.Model;

public sealed class SourceLocation
{
    public SourceLocation(string filePath, int line, int? column = null, string? originalFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A source file path is required.", nameof(filePath));
        }

        if (line <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        FilePath = filePath;
        Line = line;
        Column = column;
        OriginalFilePath = originalFilePath;
    }

    public string FilePath { get; }

    public int Line { get; }

    public int? Column { get; }
    public string? OriginalFilePath { get; }
}
