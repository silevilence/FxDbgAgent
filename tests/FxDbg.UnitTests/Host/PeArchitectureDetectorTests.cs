using System;
using System.IO;
using System.Diagnostics;
using FxDbg.Core.Requests;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class PeArchitectureDetectorTests
{
    [Fact]
    public void Detect_honors_machine_type_and_both_32_bit_corflags()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = FindRepositoryRoot();
        var detector = new PeArchitectureDetector();

        Assert.Equal(
            TargetArchitecture.X86,
            detector.Detect(Path.Combine(root, "tests", "Debuggees", "Fx40.Console.x86", "bin", configuration, "net40", "Fx40.Console.x86.exe")));
        Assert.Equal(
            TargetArchitecture.X64,
            detector.Detect(Path.Combine(root, "tests", "Debuggees", "Fx40.Console.x64", "bin", configuration, "net40", "Fx40.Console.x64.exe")));
        Assert.Equal(
            TargetArchitecture.X86,
            detector.Detect(Path.Combine(root, "tests", "Debuggees", "Fx45.Console.AnyCpuPreferred", "bin", configuration, "net45", "Fx45.Console.AnyCpuPreferred.exe")));
    }

    [Fact]
    public void Process_detector_reports_the_current_host_architecture()
    {
        var detector = new ProcessArchitectureDetector();

        TargetArchitecture architecture = detector.Detect(Process.GetCurrentProcess().Id);

        Assert.Equal(Environment.Is64BitProcess ? TargetArchitecture.X64 : TargetArchitecture.X86, architecture);
    }

    [Fact]
    public void Router_rejects_an_explicit_architecture_that_differs_from_the_PE()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Debuggees",
            "Fx40.Console.x86",
            "bin",
            configuration,
            "net40",
            "Fx40.Console.x86.exe");
        var request = new LaunchRequest(
            SessionId.New(),
            path,
            null,
            null,
            null,
            TargetArchitecture.X64,
            false,
            TimeSpan.FromSeconds(10));
        var router = new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector());

        FxDbgException error = Assert.Throws<FxDbgException>(() => router.Resolve(request));

        Assert.Equal(FxDbgErrorCode.ArchitectureMismatch, error.Code);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FxDbg.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate FxDbg.sln.");
    }
}
