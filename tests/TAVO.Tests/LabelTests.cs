namespace TAVO.Tests;

public class LabelTests
{
    static AppIdentity V(string version, string tag = "")
    {
        var parts = version.Split('.').Select(int.Parse).Concat(new[] { 0, 0, 0, 0 }).Take(4).ToArray();
        return new AppIdentity { ExePath = version, ProductKey = "app", ProductName = "App", Version = parts, Tag = tag };
    }

    static string Labels(params AppIdentity[] versions)
    {
        var map = Planner.Labels(versions.ToList());
        return string.Join(" ", versions.Select(v => map[v.VersionKey]));
    }

    [Fact]
    public void MajorMinorWhenThatIsEnough() =>
        Assert.Equal("5.0 5.3", Labels(V("5.0.1"), V("5.3.0")));

    [Fact]
    public void ThirdPartOnlyForVersionsThatShareMajorMinor() =>
        Assert.Equal("5.3.1 5.3.2 5.8", Labels(V("5.3.1"), V("5.3.2"), V("5.8.4")));

    [Fact]
    public void FourthPartWhenThreeStillClash() =>
        Assert.Equal("5.3.1.100 5.3.1.200", Labels(V("5.3.1.100"), V("5.3.1.200")));

    [Fact]
    public void PrereleaseTagWhenNumbersAreEqual() =>
        Assert.Equal("4.2a3 4.2a4 4.1", Labels(V("4.2.0.1146", "alpha3"), V("4.2.0.1146", "alpha4"), V("4.1")));

    [Fact]
    public void ReleaseNextToPrereleaseOfSameNumber() =>
        Assert.Equal("4.2a3 4.2", Labels(V("4.2.0.1146", "alpha3"), V("4.2.0.1146")));

    [Fact]
    public void LabelsAreAlwaysUnique()
    {
        var versions = new[] { V("1.0"), V("1.0.1"), V("1.0.1", "beta1"), V("1.0.1", "rc2"), V("10.0"), V("1.10") };
        var map = Planner.Labels(versions.ToList());
        Assert.Equal(versions.Length, map.Values.Distinct().Count());
    }
}
