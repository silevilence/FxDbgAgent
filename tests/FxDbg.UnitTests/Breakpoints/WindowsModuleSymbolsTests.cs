using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using FxDbg.Core.Model;
using FxDbg.Symbols.Windows;
using Xunit;

namespace FxDbg.UnitTests.Breakpoints;

public sealed class WindowsModuleSymbolsTests
{
    [Fact]
    public void Denied_pdb_is_read_failed_and_recovers_without_a_timestamp_change()
    {
        string root=FindRoot();
        string configuration=new DirectoryInfo(System.AppContext.BaseDirectory).Parent!.Name;
        string assembly=Path.Combine(root,$"tests/Debuggees/Fx40.Environment.Web/bin/{configuration}/net40/Fx40.Environment.Web.dll");
        string directory=Path.Combine(root,"artifacts/stage3-4-validation","pdb-acl-"+System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pdb=new FileInfo(Path.Combine(directory,"fixture.pdb"));
        File.Copy(Path.ChangeExtension(assembly,".pdb"),pdb.FullName);
        string original=pdb.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var denied=pdb.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.Read,AccessControlType.Deny));
        var timestamp=File.GetLastWriteTimeUtc(pdb.FullName);
        try
        {
            try
            {
                pdb.SetAccessControl(denied);
                using(var symbols=WindowsModuleSymbols.Open(assembly,pdb.FullName)) Assert.Equal(SymbolStatus.ReadFailed,symbols.Status);
            }
            finally
            {
                var restore=new FileSecurity();
                restore.SetSecurityDescriptorSddlForm(original,AccessControlSections.Access);
                pdb.SetAccessControl(restore);
            }
            Assert.Equal(timestamp,File.GetLastWriteTimeUtc(pdb.FullName));
            using var recovered=WindowsModuleSymbols.Open(assembly,pdb.FullName);
            Assert.True(recovered.Status==SymbolStatus.Loaded,recovered.Diagnostic);
        }
        finally { pdb.Delete(); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData("Fx40.Environment.Service.x86", "exe", "Environment.Shared/EnvironmentService.cs", "ENV_SERVICE_CALL")]
    [InlineData("Fx40.Environment.Service.x64", "exe", "Environment.Shared/EnvironmentService.cs", "ENV_SERVICE_CALL")]
    [InlineData("Fx40.Environment.Web", "dll", "Fx40.Environment.Web/Health.cs", "ENV_WEB_CALL")]
    [InlineData("Fx40.Environment.Late", "dll", "Fx40.Environment.Late/LateWork.cs", "ENV_LATE_BREAKPOINT")]
    public void Service_and_iis_fixtures_have_matching_windows_symbols(string project, string extension, string sourcePath, string marker)
    {
        string root = FindRoot();
        string configuration = new DirectoryInfo(System.AppContext.BaseDirectory).Parent!.Name;
        string source = Path.Combine(root, "tests/Debuggees", sourcePath);
        string assembly = Path.Combine(root, "tests/Debuggees", project, "bin", configuration, "net40", project + "." + extension);
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains(marker)).index + 1;
        using var symbols = WindowsModuleSymbols.Open(assembly);
        Assert.Equal(SymbolStatus.Loaded, symbols.Status);
        var result = symbols.Resolve(new SourceLocation(source, line));
        Assert.True(result.DocumentFound);
        Assert.NotEmpty(result.Locations);
        Assert.All(result.Locations, location => Assert.Equal(line, location.Source.Line));
        string differentProject = project == "Fx40.Environment.Web" ? "Fx40.Environment.Late" : "Fx40.Environment.Web";
        string wrongPdb = Path.Combine(root, "tests/Debuggees", differentProject, "bin", configuration, "net40", differentProject + ".pdb");
        using var mismatch = WindowsModuleSymbols.Open(assembly, wrongPdb);
        Assert.Equal(SymbolStatus.Mismatch, mismatch.Status);
    }

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
