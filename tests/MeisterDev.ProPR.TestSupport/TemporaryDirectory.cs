// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     An empty directory of a test's own under the system temporary path, deleted with everything in it when
///     the test disposes it.
/// </summary>
/// <remarks>
///     The directory is created directly under the temporary path with a random suffix. No parent directory is
///     shared between tests, so nothing remains in the temporary path after disposal.
/// </remarks>
public sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path)
    {
        this.Path = path;
    }

    /// <summary>The full path of the directory, which exists from creation until disposal.</summary>
    public string Path { get; }

    /// <summary>Creates an empty directory whose name starts with <paramref name="prefix" />.</summary>
    /// <param name="prefix">The start of the directory name, for example the name of the test class.</param>
    public static TemporaryDirectory Create(string prefix)
    {
        return new TemporaryDirectory(Directory.CreateTempSubdirectory(prefix).FullName);
    }

    /// <summary>
    ///     Deletes the directory and everything in it. A directory that the code under test already deleted is
    ///     not an error.
    /// </summary>
    public void Dispose()
    {
        try
        {
            this.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Git writes its object files read-only, and Windows refuses to delete a read-only file. A process
            // the test started can also still be writing into the directory. The attributes are cleared and the
            // deletion runs a second time.
            try
            {
                foreach (var file in Directory.EnumerateFiles(this.Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                this.Delete();
            }
            catch (Exception retryException) when (retryException is IOException or UnauthorizedAccessException)
            {
                // Dispose does not throw, because an exception thrown here would replace the exception of a
                // failing test, which is the one a reader needs to see.
            }
        }
    }

    private void Delete()
    {
        if (Directory.Exists(this.Path))
        {
            Directory.Delete(this.Path, true);
        }
    }
}
