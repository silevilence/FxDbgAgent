using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Host.Architecture;
using Microsoft.Win32.SafeHandles;
using Xunit;

// Supplementary audit experiment against the frozen published Host assembly.
// The only changed ACL belongs to the child started here; no debugger attaches.
public sealed class OwnedProcessAclAudit
{
    [Fact]
    public void Explicit_query_denial_is_actionable_and_restoring_acl_restores_detection()
    {
        string root=Environment.GetEnvironmentVariable("FXDBG_COVERAGE_ROOT")!;
        var start=new ProcessStartInfo(Path.Combine(root,"tests/Debuggees/Fx40.Console.x64/bin/Debug/net40/Fx40.Console.x64.exe")) { UseShellExecute=false,CreateNoWindow=true };
        start.ArgumentList.Add("--wait-milliseconds");start.ArgumentList.Add("30000");
        using var child=Process.Start(start)!;
        IntPtr handle=child.Handle;
        DateTime started=child.StartTime.ToUniversalTime();
        byte[]? original=null;
        bool passed=false;
        try
        {
            Check(OpenProcessToken(GetCurrentProcess(),0xE,out var token));
            using(token)
            {
                Check(CreateRestrictedToken(token,1,0,IntPtr.Zero,0,IntPtr.Zero,0,IntPtr.Zero,out var restricted));
                using(restricted)
                {
                    var detector=new ProcessArchitectureDetector();
                    Assert.Equal(TargetArchitecture.X64,WindowsIdentity.RunImpersonated(restricted,()=>detector.Detect(child.Id)));
                    original=ReadAcl(handle);
                    var descriptor=new RawSecurityDescriptor(original,0);
                    descriptor.DiscretionaryAcl!.InsertAce(0,new CommonAce(AceFlags.None,AceQualifier.AccessDenied,0x1400,
                        new SecurityIdentifier(WellKnownSidType.WorldSid,null),false,null));
                    var denied=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(denied,0);
                    Check(SetKernelObjectSecurity(handle,4,denied));
                    var error=Assert.Throws<FxDbgException>(()=>WindowsIdentity.RunImpersonated(restricted,()=>detector.Detect(child.Id)));
                    Assert.Equal(FxDbgErrorCode.AccessDenied,error.Code);
                    Assert.Contains("Host architecture inspection",error.Message);
                    Assert.False(child.HasExited);
                    Check(SetKernelObjectSecurity(handle,4,original));
                    Assert.Equal(TargetArchitecture.X64,WindowsIdentity.RunImpersonated(restricted,()=>detector.Detect(child.Id)));
                    Assert.Equal(new RawSecurityDescriptor(original,0).GetSddlForm(AccessControlSections.Access),
                        new RawSecurityDescriptor(ReadAcl(handle),0).GetSddlForm(AccessControlSections.Access));
                    passed=true;
                }
            }
        }
        finally
        {
            try { if(original is not null) Check(SetKernelObjectSecurity(handle,4,original)); }
            finally
            {
                if(!child.HasExited) child.Kill();
                Assert.True(child.WaitForExit(5000));
                File.WriteAllText(Path.Combine(root,"artifacts/stage3-4-validation/acl-audit-result.json"),
                    JsonSerializer.Serialize(new {passed,processId=child.Id,startedAtUtc=started,exited=child.HasExited,atUtc=DateTimeOffset.UtcNow}));
            }
        }
    }
    private static byte[] ReadAcl(IntPtr handle)
    {
        GetKernelObjectSecurity(handle,4,null,0,out uint length);
        var result=new byte[length];Check(GetKernelObjectSecurity(handle,4,result,length,out _));return result;
    }
    private static void Check(bool value) { if(!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token,uint flags,uint disabledCount,IntPtr disabled,uint deletedCount,IntPtr deleted,uint restrictedCount,IntPtr restricted,out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetKernelObjectSecurity(IntPtr handle,uint information,byte[]? descriptor,uint length,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetKernelObjectSecurity(IntPtr handle,uint information,byte[] descriptor);
}
