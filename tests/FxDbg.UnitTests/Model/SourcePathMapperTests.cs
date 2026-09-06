using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class SourcePathMapperTests
{
    [Fact]
    public void Module_scope_and_longest_complete_prefix_apply_in_both_directions()
    {
        var mapper = new SourcePathMapper(new[]
        {
            new SourcePathMapping(@"C:\build", @"D:\global"),
            new SourcePathMapping(@"C:\build\src", @"D:\specific"),
            new SourcePathMapping(@"C:\build", @"D:\module", "app.dll")
        });
        Assert.Equal(@"D:\module\src\a.cs", mapper.ToLocal(new SourceLocation(@"c:/BUILD/src/a.cs", 12), "app.dll").FilePath);
        var local = mapper.ToLocal(new SourceLocation(@"C:\build\src\a.cs", 12, 3), "other.dll");
        Assert.Equal(@"D:\specific\a.cs", local.FilePath);
        Assert.Equal(@"C:\build\src\a.cs", local.OriginalFilePath);
        Assert.Equal(3, local.Column);
        Assert.Equal(@"C:\build\src\a.cs", mapper.ToBuild(local, "other.dll").FilePath);
        Assert.Equal(@"C:\builder\a.cs", mapper.ToLocal(new SourceLocation(@"C:\builder\a.cs", 12), "app.dll").FilePath);
        Assert.Equal(@"C:\build\src\a.cs", mapper.ToBuild(new SourceLocation(@"D:\module\src\a.cs", 12), "app.dll").FilePath);
    }

    [Fact]
    public void Conflicting_forward_and_reverse_rules_are_rejected_before_launch()
    {
        foreach (var second in new[] { new SourcePathMapping(@"C:\build", @"D:\two"), new SourcePathMapping(@"C:\other", @"D:\one") })
            Assert.Equal(FxDbgErrorCode.InvalidRequest, Assert.Throws<FxDbgException>(() => new SourcePathMapper(new[]
            { new SourcePathMapping(@"C:\build", @"D:\one"), second })).Code);
    }

    [Fact]
    public void Unc_paths_spaces_and_drive_roots_are_supported_without_partial_matches()
    {
        var mapper = new SourcePathMapper(new[] { new SourcePathMapping(@"\\server\share\build space", @"D:\local space") });
        Assert.Equal(@"D:\local space\a.cs", mapper.ToLocal(new SourceLocation(@"\\server\share\build space\a.cs", 1), "app.dll").FilePath);
        Assert.Throws<FxDbgException>(() => new SourcePathMapping("relative", @"D:\local"));
        var root = new SourcePathMapper(new[] { new SourcePathMapping(@"C:\", @"D:\") });
        Assert.Equal(@"D:\a.cs", root.ToLocal(new SourceLocation(@"C:\a.cs", 1), "app.dll").FilePath);
    }
}
