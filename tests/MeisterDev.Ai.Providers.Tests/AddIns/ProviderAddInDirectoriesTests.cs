// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>
///     Where the two add-in directories are, which is the one place the configured value is interpreted.
/// </summary>
/// <remarks>
///     A relative path that resolved against the process's working directory would name a different directory
///     depending on how the host was started, so a family an operator installed would be found on one machine
///     and not on another with the same configuration.
/// </remarks>
public sealed class ProviderAddInDirectoriesTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "propr-content-root");

    [Fact]
    public void ARelativeDirectoryResolvesAgainstTheContentRoot()
    {
        var directories = ProviderAddInDirectories.Resolve("add-ins", ContentRoot);

        Assert.Equal(Path.Combine(ContentRoot, "add-ins"), directories.External);
    }

    [Fact]
    public void AnAbsoluteDirectoryIsTakenAsGiven()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "add-ins");

        var directories = ProviderAddInDirectories.Resolve(absolute, ContentRoot);

        Assert.Equal(absolute, directories.External);
    }

    [Fact]
    public void NoConfiguredDirectoryFallsBackToTheDefaultBesideTheContentRoot()
    {
        var directories = ProviderAddInDirectories.Resolve(null, ContentRoot);

        Assert.Equal(
            Path.Combine(ContentRoot, ProviderAddInDirectories.DefaultExternalDirectory),
            directories.External);
    }

    // A key set to nothing is the same statement as a key that is not set, and answering it differently would
    // make an empty value in a compose file name the process's working directory.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfiguredDirectoryIsTheSameAsNone(string configured)
    {
        var directories = ProviderAddInDirectories.Resolve(configured, ContentRoot);

        Assert.Equal(
            Path.Combine(ContentRoot, ProviderAddInDirectories.DefaultExternalDirectory),
            directories.External);
    }

    // The built-in directory is fixed by whatever built the deployment, so an operator cannot move it and a
    // family shipped inside the image is replaced by a product release rather than by a mounted volume.
    [Fact]
    public void TheBuiltInDirectoryIsBesideTheApplicationAndIsNotTheConfiguredOne()
    {
        var directories = ProviderAddInDirectories.Resolve(
            Path.Combine(Path.GetTempPath(), "elsewhere"),
            ContentRoot);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ProviderAddInDirectories.BuiltInDirectoryName)),
            directories.BuiltIn);
        Assert.NotEqual(directories.BuiltIn, directories.External);
    }

    [Fact]
    public void BothDirectoriesAreAbsoluteWhateverWasConfigured()
    {
        var directories = ProviderAddInDirectories.Resolve("./nested/../add-ins", ContentRoot);

        Assert.True(Path.IsPathFullyQualified(directories.BuiltIn));
        Assert.True(Path.IsPathFullyQualified(directories.External));
        Assert.Equal(Path.Combine(ContentRoot, "add-ins"), directories.External);
    }
}
