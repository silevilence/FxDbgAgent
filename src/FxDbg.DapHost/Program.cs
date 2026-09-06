using FxDbg.DapHost;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;
using FxDbg.Host.Sessions;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

try
{
    string directory = Path.Combine(AppContext.BaseDirectory, "engines");
    if (args.Length == 2 && args[0] == "--engine-dir") directory = Path.GetFullPath(args[1]);
    else if (args.Length != 0) throw new ArgumentException("Usage: fxdbg-dap [--engine-dir DIRECTORY]");
    string manifest = Path.Combine(directory, "engine-manifest.json");
    string[] dependencies = File.Exists(manifest) ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [] : [];
    if (dependencies.Length == 0 || dependencies.Any(file => string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || !File.Exists(Path.Combine(directory, file))))
        throw new ArgumentException("Incomplete Engine bundle. Run eng/publish-dap.ps1.");
    using var engine = new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
        new EngineProcessPaths(Path.Combine(directory, "FxDbg.Engine.x86.exe"), Path.Combine(directory, "FxDbg.Engine.x64.exe")));
    await using var sessions = new DebugSessionService(engine);
    // Console's output stream suppresses ERROR_BROKEN_PIPE; FileStream reports it so a
    // disconnected reader cannot leave an attached target waiting for a client command.
    using var output = new FileStream(new SafeFileHandle(StandardOutput.GetStdHandle(-11), ownsHandle: false), FileAccess.Write);
    await new DapServer(sessions, Console.OpenStandardInput(), output).RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is ArgumentException ? error.Message : "DAP transport or session failed; check the bundle and client connection.");
    return 1;
}

internal static class StandardOutput
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int handle);
}
