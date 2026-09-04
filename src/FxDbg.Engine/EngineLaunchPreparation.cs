using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Engine;

internal sealed class EngineLaunchPreparation : IDisposable
{
    private EngineLaunchPreparation(string commandLine, string workingDirectory, IntPtr environment)
    {
        CommandLine = commandLine;
        WorkingDirectory = workingDirectory;
        Environment = environment;
    }

    internal string CommandLine { get; }

    internal string WorkingDirectory { get; }

    internal IntPtr Environment { get; }

    internal static EngineLaunchPreparation Create(LaunchRequest request)
    {
        string executable = Path.GetFullPath(request.ExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidExecutable, $"Executable was not found: {executable}");
        }

        string workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.GetDirectoryName(executable)!
            : Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, $"Working directory was not found: {workingDirectory}");
        }

        return new EngineLaunchPreparation(
            WindowsCommandLine.Build(executable, request.Arguments),
            workingDirectory,
            CreateEnvironmentBlock(request.Environment));
    }

    public void Dispose()
    {
        if (Environment != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Environment);
        }
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count == 0)
        {
            return IntPtr.Zero;
        }

        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            values[(string)entry.Key] = (string)entry.Value;
        }

        foreach (KeyValuePair<string, string> pair in overrides)
        {
            values[pair.Key] = pair.Value;
        }

        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in values)
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static class WindowsCommandLine
    {
        internal static string Build(string executablePath, IReadOnlyList<string> arguments)
        {
            var builder = new StringBuilder(Quote(executablePath));
            foreach (string argument in arguments)
            {
                builder.Append(' ').Append(Quote(argument));
            }

            return builder.ToString();
        }

        private static string Quote(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }

            result.Append('\\', backslashes * 2).Append('"');
            return result.ToString();
        }
    }
}
