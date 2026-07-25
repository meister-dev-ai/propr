// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;

namespace MeisterDev.Ai.Providers.Catalog;

/// <summary>
///     The catalog snapshot shipped inside this assembly, so a fresh installation has a populated catalog
///     without the application making any network request of its own. It is a starting point rather than a
///     living feed: a newer snapshot reaches a running installation by being uploaded, which is what keeps the
///     "no outbound fetch" property intact.
/// </summary>
/// <remarks>
///     The bundled copy is trimmed to the providers this product can reach. An operator who needs a provider
///     outside that set uploads a full snapshot in the same format; the importer is indifferent to which it is
///     given. Attribution for the bundled data is recorded in NOTICE at the repository root.
/// </remarks>
public static class BundledCatalogSnapshot
{
    private const string ResourceName = "MeisterDev.Ai.Providers.Catalog.Snapshot.models.dev.json";

    /// <summary>The snapshot format the bundled copy is written in, matching an importer's source format.</summary>
    public static string SourceFormat => "models.dev";

    /// <summary>Opens the bundled snapshot for reading. The caller owns the returned stream.</summary>
    /// <exception cref="InvalidOperationException">The snapshot is missing from the assembly, which means the build dropped an embedded resource.</exception>
    public static Stream Open()
    {
        return typeof(BundledCatalogSnapshot).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
               ?? throw new InvalidOperationException($"The bundled catalog snapshot '{ResourceName}' is not present in the assembly.");
    }
}
