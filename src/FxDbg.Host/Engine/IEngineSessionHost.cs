using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using Newtonsoft.Json.Linq;

namespace FxDbg.Host.Engine;

// Internal seam for deterministic ordering tests; the product implementation remains EngineProcessHost.
internal interface IEngineSessionHost : IDisposable
{
    Task<DebugTargetInfo> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken);
    Task<DebugTargetInfo> AttachAsync(AttachRequest request, CancellationToken cancellationToken);
    Task<JToken> InvokeAsync(SessionId sessionId, string method, JObject? arguments = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    IReadOnlyList<JObject> DrainEvents(SessionId sessionId);
    void CloseSession(SessionId sessionId);
}
