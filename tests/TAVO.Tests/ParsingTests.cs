namespace TAVO.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("5.3", new[] { 5, 3, 0, 0 })]
    [InlineData("UE_5.8", new[] { 5, 8, 0, 0 })]
    [InlineData("Kanzi 4_2_0_1146_alpha3", new[] { 4, 2, 0, 1146 })]
    [InlineData("4.2.0.1146 alpha3", new[] { 4, 2, 0, 1146 })]
    public void ParsesVersions(string text, int[] expected) =>
        Assert.Equal(expected, AppIdentity.ParseVersion(text));

    [Theory]
    [InlineData("++UE5+Release")]
    [InlineData("")]
    [InlineData(null)]
    public void NoVersionInText(string? text) =>
        Assert.Null(AppIdentity.ParseVersion(text));

    [Theory]
    [InlineData("4.2.0.1146 alpha3", "alpha3")]
    [InlineData("Kanzi 4_2_0_1146_alpha3", "alpha3")]
    [InlineData("2.0-beta.2", "beta2")]
    [InlineData("1.4 RC1", "rc1")]
    [InlineData("Preview", "preview")]
    public void ParsesPrereleaseTags(string text, string expected) =>
        Assert.Equal(expected, AppIdentity.ParseTag(text));

    [Theory]
    [InlineData("Source")]          // "rc" inside a word
    [InlineData("Program Files")]
    [InlineData("Alphabet 2")]      // "alpha" followed by letters
    public void IgnoresTagLettersInsideWords(string text) =>
        Assert.Null(AppIdentity.ParseTag(text));

    [Theory]
    [InlineData("blender-launcher", "blender")]
    [InlineData("App_Launcher", "App")]
    [InlineData("launcher", "launcher")]
    [InlineData("UnrealEditor", "UnrealEditor")]
    public void NormalizesLauncherNames(string name, string expected) =>
        Assert.Equal(expected, AppIdentity.NormalizeExeName(name));
}
