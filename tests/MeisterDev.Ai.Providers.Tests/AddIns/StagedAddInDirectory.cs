// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>
///     A temporary add-in directory, populated from the add-ins this test project builds beside itself.
/// </summary>
/// <remarks>
///     The add-ins are copied from their own build output rather than from this project's, because their build
///     output is the shape a deployment holds: the add-in's assembly and its own dependencies, and none of the
///     assemblies the host and the add-in share. Loading out of this project's output would exercise a layout no
///     deployment has.
/// </remarks>
internal sealed class StagedAddInDirectory : IDisposable
{
    /// <summary>The add-in written against the contract, which the host accepts.</summary>
    public const string Example = "MeisterDev.Ai.Providers.ExampleAddIn";

    /// <summary>The add-in declaring a contract version the host does not carry.</summary>
    public const string Stale = "MeisterDev.Ai.Providers.StaleAddIn";

    /// <summary>The add-in that loads without error and then fails a driver check.</summary>
    public const string NonConforming = "MeisterDev.Ai.Providers.NonConformingAddIn";

    /// <summary>The add-in whose assembly exposes two provider families, which one add-in may not.</summary>
    public const string TwoFamilies = "MeisterDev.Ai.Providers.TwoFamilyAddIn";

    private StagedAddInDirectory(string path)
    {
        this.Path = path;
    }

    /// <summary>The directory itself, which exists from construction.</summary>
    public string Path { get; }

    /// <summary>Creates an empty add-in directory that deletes itself when the test is done with it.</summary>
    public static StagedAddInDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "propr-add-in-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);

        return new StagedAddInDirectory(path);
    }

    /// <summary>A path inside this directory that has nothing at it.</summary>
    public static string Missing()
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "propr-add-in-tests",
            Guid.NewGuid().ToString("n"));
    }

    /// <summary>Copies one built add-in into this directory as a family folder.</summary>
    /// <param name="addIn">Which built add-in to install.</param>
    /// <param name="folderName">
    ///     The folder to install it as, defaulting to the add-in's own name. Installing one add-in twice under
    ///     two names produces two files declaring one identity, which a duplicate is.
    /// </param>
    /// <returns>The full path of the installed assembly.</returns>
    public string Install(string addIn, string? folderName = null)
    {
        var name = folderName ?? addIn;
        var folder = Directory.CreateDirectory(System.IO.Path.Combine(this.Path, name));

        foreach (var file in Directory.EnumerateFiles(BuildOutputOf(addIn)))
        {
            var fileName = System.IO.Path.GetFileName(file);

            // Only the add-in's own files take the folder's name, so installing one add-in twice under two names
            // produces two assemblies a loader tells apart. A dependency keeps the name it was built under,
            // which is the name the load context resolves it by, and slicing its name to the add-in's length
            // would rename it to something nothing asks for, or run off the front of a shorter one.
            var destination = fileName.StartsWith(addIn, StringComparison.Ordinal)
                ? name + fileName[addIn.Length..]
                : fileName;

            File.Copy(file, System.IO.Path.Combine(folder.FullName, destination), overwrite: true);
        }

        // The non-conforming fixture writes its load marker here. The host loads an add-in from the bytes it
        // hashed, and an assembly loaded from bytes reports no location, so the fixture is told which folder it
        // was staged into instead of asking itself.
        if (addIn == NonConforming)
        {
            Environment.SetEnvironmentVariable("PROPR_ADDIN_LOAD_MARKER_DIRECTORY", folder.FullName);
        }

        return System.IO.Path.Combine(folder.FullName, name + ".dll");
    }

    /// <summary>Puts an arbitrary file into an add-in's folder.</summary>
    /// <param name="folderName">The family folder to put it in, created if it is not there.</param>
    /// <param name="fileName">The name to give it.</param>
    /// <param name="content">What to write, or null to copy the assembly that <paramref name="fileName" /> names.</param>
    /// <returns>The full path of the written file.</returns>
    public string PutFile(string folderName, string fileName, byte[]? content = null)
    {
        var folder = Directory.CreateDirectory(System.IO.Path.Combine(this.Path, folderName));
        var path = System.IO.Path.Combine(folder.FullName, fileName);

        if (content is null)
        {
            File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, fileName), path, overwrite: true);
        }
        else
        {
            File.WriteAllBytes(path, content);
        }

        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch (IOException)
        {
            // A loaded assembly can keep its file open on some platforms, and a directory left behind in the
            // temporary path is not worth failing a test over.
        }
    }

    /// <summary>
    ///     An add-in's own build output, which is the shape a deployment holds: the add-in's assembly and its own
    ///     dependencies, and none of the assemblies the host and the add-in share.
    /// </summary>
    /// <param name="addIn">Which built add-in to locate.</param>
    public static string BuildOutputOf(string addIn)
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "add-in-builds", addIn);
    }
}
