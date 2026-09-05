using System;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Host.Architecture;

public sealed class ArchitectureRouter
{
    private readonly PeArchitectureDetector peDetector;
    private readonly ProcessArchitectureDetector processDetector;

    public ArchitectureRouter(PeArchitectureDetector peDetector, ProcessArchitectureDetector processDetector)
    {
        this.peDetector = peDetector ?? throw new ArgumentNullException(nameof(peDetector));
        this.processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
    }

    public TargetArchitecture Resolve(LaunchRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MatchRequested(request.Architecture, peDetector.Detect(request.ExecutablePath));
    }

    public TargetArchitecture Resolve(AttachRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return MatchRequested(request.Architecture, processDetector.Detect(request.ProcessId));
    }

    private static TargetArchitecture MatchRequested(
        TargetArchitecture requested,
        TargetArchitecture actual)
    {
        if (requested != TargetArchitecture.Auto && requested != actual)
        {
            throw new FxDbgException(
                FxDbgErrorCode.ArchitectureMismatch,
                $"Requested {TargetArchitectureWireName.Format(requested)} engine does not match " +
                $"the target architecture {TargetArchitectureWireName.Format(actual)}.");
        }

        return actual;
    }
}
