using System;
using System.Collections.Generic;
using System.Linq;
using FxDbg.Core.Sessions;

namespace FxDbg.Host.Sessions;

internal static class SessionRetention
{
    internal static SessionId[] Evictions(IEnumerable<(SessionId Id, DateTimeOffset Ended)> terminal, int totalCount, DateTimeOffset now, int extraRecords = 0)
    {
        int overCapacity = Math.Max(0, totalCount + extraRecords - 1024);
        return terminal.OrderBy(x => x.Ended).Where((x, index) => index < overCapacity || now - x.Ended >= TimeSpan.FromMinutes(10)).Select(x => x.Id).ToArray();
    }
}
