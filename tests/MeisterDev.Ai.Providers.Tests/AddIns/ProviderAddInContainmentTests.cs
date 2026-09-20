// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>
///     Whether a path the filesystem handed the loader still leads inside the directory it was given.
/// </summary>
/// <remarks>
///     Reached by reflection because the type is internal to the hosting assembly and its callers are the
///     discovery pass and an add-in's load context, neither of which takes a path from a test.
/// </remarks>
public sealed class ProviderAddInContainmentTests : IDisposable
{
    private static readonly MethodInfo Reason = typeof(MeisterDev.Ai.Providers.AddIns.ProviderAddInCatalog).Assembly
        .GetType("MeisterDev.Ai.Providers.AddIns.ProviderAddInContainment", throwOnError: true)!
        .GetMethod("GetContainmentFailureReason", BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly string _root = Directory.CreateTempSubdirectory("addin-containment").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void AFileInsideTheDirectoryIsPlaced()
    {
        var inside = Path.Combine(this._root, "driver.dll");
        File.WriteAllText(inside, string.Empty);

        Assert.Null(Contain(this._root, inside, isDirectory: false));
    }

    [Fact]
    public void AFileAboveTheDirectoryIsRefused()
    {
        var outside = Path.Combine(Path.GetDirectoryName(this._root)!, "elsewhere.dll");

        Assert.NotNull(Contain(this._root, outside, isDirectory: false));
    }

    // The path is the same file whatever its casing on Windows and macOS, and a different one on Linux, so the
    // answer is the platform's and not the text's.
    [Fact]
    public void TheDirectorysOwnCasingDecidesNothingOnAPlatformThatIgnoresIt()
    {
        var inside = Path.Combine(this._root, "driver.dll");
        File.WriteAllText(inside, string.Empty);

        var shouted = this._root.ToUpperInvariant();
        var placed = Contain(shouted, Path.Combine(shouted, "driver.dll"), isDirectory: false) is null;

        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), placed);
    }

    // ResolveLinkTarget reads one entry, so a link on a directory above the file leaves the rest of the path as
    // the text it was written as, and that text still reads as inside.
    [Fact]
    public void AFileUnderALinkedParentDirectoryIsRefused()
    {
        var target = Directory.CreateTempSubdirectory("addin-elsewhere").FullName;
        try
        {
            File.WriteAllText(Path.Combine(target, "driver.dll"), string.Empty);

            var link = Path.Combine(this._root, "family");
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Creating a symlink needs a privilege Windows does not grant by default. There is nothing to
                // assert about a link the platform refused to make.
                return;
            }

            Assert.NotNull(Contain(this._root, Path.Combine(link, "driver.dll"), isDirectory: false));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private static string? Contain(string directoryPath, string candidatePath, bool isDirectory)
    {
        return (string?)Reason.Invoke(null, [directoryPath, candidatePath, isDirectory]);
    }
}
