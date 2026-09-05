using System.IO;
using System.Linq;
using FxDbg.Core.Model;
using FxDbg.Symbols.Windows;
using Xunit;

namespace FxDbg.UnitTests.Breakpoints;

public sealed class WindowsModuleSymbolsTests
{
    [Fact]
    public void Real_windows_pdb_maps_an_executable_source_line_and_reports_missing_pdb()
    {
        string root = FindRoot();
        string source = Path.Combine(root, "tests", "Debuggees", "Fx40.Console.x86", "Program.cs");
        string configuration = new DirectoryInfo(System.AppContext.BaseDirectory).Parent!.Name;
        string assembly = Path.Combine(root, "tests", "Debuggees", "Fx40.Console.x86", "bin", configuration, "net40", "Fx40.Console.x86.exe");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index))
            .First(item => item.text.Contains("breakpointValueSink = 1;")).index + 1;
        using var symbols = WindowsModuleSymbols.Open(assembly);
        Assert.Equal(SymbolStatus.Loaded, symbols.Status);
        var result = symbols.Resolve(new SourceLocation(source, line));
        Assert.True(result.DocumentFound);
        Assert.NotEmpty(result.Locations);
        Assert.All(result.Locations, location => Assert.Equal(line, location.Source.Line));
        using var missing = WindowsModuleSymbols.Open(assembly, assembly + ".missing.pdb");
        Assert.Equal(SymbolStatus.Missing, missing.Status);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FxDbg.sln"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
