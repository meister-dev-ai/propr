// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;

namespace MeisterDev.Ai.Providers.NonConformingAddIn;

/// <summary>
///     Writes a file the first time any of this assembly's code runs.
/// </summary>
/// <remarks>
///     <para>
///         The host reads an add-in nobody has activated for its metadata and loads none of it, and this is how
///         a test tells the difference. A module initializer runs on the first access to the module, which a
///         load causes and a metadata-only read does not, so the marker's absence is the property rather than a
///         proxy for it.
///     </para>
///     <para>
///         The folder comes from an environment variable the staging helper sets, not from
///         <see cref="System.Reflection.Assembly.Location" />. The host loads an add-in from the bytes it
///         hashed, and an assembly loaded from bytes reports no location, so a fixture reading that one would
///         write nothing and the test would pass for the wrong reason.
///     </para>
/// </remarks>
internal static class LoadMarker
{
    /// <summary>The file written when this assembly runs.</summary>
    internal const string FileName = "this-add-in-ran";

    /// <summary>Names the folder the marker is written into.</summary>
    internal const string FolderVariable = "PROPR_ADDIN_LOAD_MARKER_DIRECTORY";

    [ModuleInitializer]
    internal static void Record()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } folder
                && Directory.Exists(folder))
            {
                File.WriteAllText(Path.Combine(folder, FileName), string.Empty);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A fixture that cannot write its marker leaves the test asserting nothing, which the test reports
            // as a failure of its own. Taking the load down instead would report it as the wrong thing.
        }
    }
}
