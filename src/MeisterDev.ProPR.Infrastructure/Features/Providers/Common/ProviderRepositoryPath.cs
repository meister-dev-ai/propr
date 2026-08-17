// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Tells an <c>owner/name</c> pair apart from a provider-native repository identifier.
/// </summary>
/// <remarks>
///     Guided selection stores a repository by its numeric id, which survives a rename, so a caller holding
///     that id has to look the pair up before addressing an API that is written in terms of it. A caller that
///     assembles a pair out of parts it happens to have instead can produce something shaped like a path and
///     addressed at nothing.
/// </remarks>
internal static class ProviderRepositoryPath
{
    /// <summary>Reports whether a value is already an <c>owner/name</c> pair.</summary>
    internal static bool LooksLikeOwnerAndName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains('/', StringComparison.Ordinal)
               && value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
    }
}
