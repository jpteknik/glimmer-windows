// New: the installer's change to the user's PATH (Glimmer.Setup's PathList), which must add or take off exactly one
// folder and leave everything else in the person's PATH as it was.
using Glimmer.Setup;

namespace Glimmer.Setup.Tests;

public class PathListTests
{
    private const string Folder = @"C:\Users\anna\AppData\Local\Programs\Glimmer";

    [Fact]
    public void AddingPutsTheFolderLastAndOnlyOnce()
    {
        Assert.Equal(Folder, PathList.Add(null, Folder));
        Assert.Equal(Folder, PathList.Add("", Folder));
        Assert.Equal(@"%USERPROFILE%\bin;C:\tools;" + Folder, PathList.Add(@"%USERPROFILE%\bin;C:\tools", Folder));
        Assert.Equal(@"C:\tools;" + Folder, PathList.Add(@"C:\tools;", Folder));
        // Already there, in another case or with a trailing backslash: unchanged.
        var there = @"C:\tools;c:\users\ANNA\appdata\local\programs\glimmer\;D:\x";
        Assert.Same(there, PathList.Add(there, Folder));
    }

    [Fact]
    public void RemovingTakesOffEveryCopyAndKeepsTheRestInOrder()
    {
        Assert.Equal(@"%USERPROFILE%\bin;C:\tools", PathList.Remove(@"%USERPROFILE%\bin;" + Folder + @";C:\tools;" + Folder.ToUpperInvariant() + @"\", Folder));
        Assert.Equal("", PathList.Remove(Folder, Folder));
        Assert.Equal(@"C:\a;;C:\b", PathList.Remove(@"C:\a;;C:\b", Folder));   // not there: as it was, empty entry and all
        Assert.Equal("", PathList.Remove(null, Folder));
        // A folder that merely starts the same way stays.
        Assert.Equal(Folder + "2", PathList.Remove(Folder + "2", Folder));
    }

    [Fact]
    public void AddingThenRemovingGivesThePathBack()
    {
        foreach (var path in new[] { @"C:\a;C:\b", "%PATH_EXTRA%", @"C:\a" })
            Assert.Equal(path, PathList.Remove(PathList.Add(path, Folder), Folder));
        Assert.True(PathList.Contains(PathList.Add(@"C:\a", Folder), Folder));
    }
}
