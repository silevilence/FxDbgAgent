<%@ Page Language="C#" Debug="true" %>
<% Response.ContentType = "application/json"; Response.Write(FxDbg.Debuggees.EnvironmentSetup.Health.Read()); %>
