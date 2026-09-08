using FxDbg.Core.Model;
using FxDbg.Engine.Protocol;
using Newtonsoft.Json.Linq;

namespace FxDbg.DapHost;

internal sealed partial class DapServer
{
    private async Task<JObject> SetExceptionBreakpoints(JObject args, CancellationToken token)
    {
        if (args["filters"] is not JArray filters) throw Invalid("Exception filters must be an array.");
        if (args["filterOptions"] is not null && args["filterOptions"] is not JArray) throw Invalid("Exception filterOptions must be an array.");
        if (args["exceptionOptions"] is not null && (args["exceptionOptions"] is not JArray exceptionOptions || exceptionOptions.Count != 0))
            throw Invalid("Exception path options are not supported.");
        var options = args["filterOptions"] as JArray ?? new JArray();
        if (filters.Count + options.Count > 64) throw Invalid("At most 64 exception filters are allowed.");
        var rules = new List<ExceptionTypeRule>();
        var breakpoints = new JArray();
        bool all = false;
        foreach (JToken filter in filters)
        {
            if (filter.Type != JTokenType.String || (string?)filter != "firstChance") throw Invalid("Unknown exception filter.");
            all = true; breakpoints.Add(new JObject { ["verified"] = true });
        }
        foreach (JToken option in options)
        {
            if (option is not JObject item || (string?)item["filterId"] != "firstChance") throw Invalid("Unknown exception filter option.");
            if (item["condition"] is not null && item["condition"]!.Type != JTokenType.String) throw Invalid("Exception condition must be a string.");
            string? condition = (string?)item["condition"];
            if (string.IsNullOrWhiteSpace(condition)) all = true;
            else rules.AddRange(ExceptionStopConfiguration.ParseRules(condition));
            breakpoints.Add(new JObject { ["verified"] = true });
        }
        // DAP filters and filterOptions are additive, including unconditional firstChance.
        _ = new ExceptionStopConfiguration(true, rules);
        var configure = new JObject { ["firstChance"] = all || rules.Count != 0 };
        if (!all) configure["rules"] = WireJson.Value(rules);
        await Invoke("configure_exceptions", configure, token);
        return new JObject { ["breakpoints"] = breakpoints };
    }
}
