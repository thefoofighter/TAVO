namespace TAVO.Tests;

public class PinListTests
{
    static readonly string TaskBar = PinScanner.TaskBarDir;
    static readonly string Implicit = PinScanner.ImplicitDir;

    // Strings as TaskbandStrings() extracts them from the registry: lower case, sometimes with a stray prefix byte.
    static readonly List<string> Referenced = new()
    {
        "4kanzi studio 4.2.0.1146 alpha3.lnk",
        "blender.lnk",
        "implicitappshortcuts",
        "199449c2f27401d7",
        "blender 5.0.lnk",
        @"user\appdata\roaming\microsoft\internet explorer\quick launch\user pinned\taskbar\blender.lnk",
    };

    [Fact]
    public void TaskBarShortcutListedByName() =>
        Assert.True(PinScanner.IsReferenced(Path.Combine(TaskBar, "Blender.lnk"), Referenced));

    [Fact]
    public void TaskBarShortcutWithPrefixByte() =>
        Assert.True(PinScanner.IsReferenced(Path.Combine(TaskBar, "Kanzi Studio 4.2.0.1146 alpha3.lnk"), Referenced));

    [Fact]
    public void TaskBarShortcutNotListed() =>
        Assert.False(PinScanner.IsReferenced(Path.Combine(TaskBar, "Old App.lnk"), Referenced));

    [Fact]
    public void ImplicitShortcutListedByFolderAndName() =>
        Assert.True(PinScanner.IsReferenced(Path.Combine(Implicit, "199449c2f27401d7", "Blender 5.0.lnk"), Referenced));

    [Fact]
    public void ImplicitShortcutInOtherFolderIsNotPinned() =>
        Assert.False(PinScanner.IsReferenced(Path.Combine(Implicit, "aaaaaaaaaaaaaaaa", "Blender 5.0.lnk"), Referenced));

    [Fact]
    public void ImplicitShortcutListedByFullPathOnly()
    {
        var resolveOnly = new List<string> { @"...\implicitappshortcuts\0123456789abcdef\tool.lnk" };
        Assert.True(PinScanner.IsReferenced(Path.Combine(Implicit, "0123456789abcdef", "Tool.lnk"), resolveOnly));
    }
}
