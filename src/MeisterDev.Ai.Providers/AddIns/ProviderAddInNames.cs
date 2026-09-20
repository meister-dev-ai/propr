// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>How a rejection category and a directory are spelled wherever they are reported.</summary>
/// <remarks>
///     Named in one place rather than at each surface, so the log line, the API response and the administration
///     page carry one spelling. An operator who found a category in the log finds the same word on the page.
/// </remarks>
public static class ProviderAddInNames
{
    /// <summary>The reported name of <paramref name="category" />.</summary>
    /// <param name="category">The category to name.</param>
    public static string Format(ProviderAddInRejectionCategory category)
    {
        return category switch
        {
            ProviderAddInRejectionCategory.Failed => "failed",
            ProviderAddInRejectionCategory.Duplicate => "duplicate",
            ProviderAddInRejectionCategory.MisPackaged => "mis-packaged",
            ProviderAddInRejectionCategory.VersionMismatch => "version-mismatch",
            ProviderAddInRejectionCategory.NonConforming => "non-conforming",

            // A member added without a name here still reports something a reader can match to the enum.
            _ => category.ToString(),
        };
    }

    /// <summary>The reported name of <paramref name="origin" />.</summary>
    /// <param name="origin">The directory to name.</param>
    public static string Format(ProviderAddInOrigin origin)
    {
        return origin switch
        {
            ProviderAddInOrigin.BuiltIn => "built-in",
            ProviderAddInOrigin.External => "external",
            _ => origin.ToString(),
        };
    }
}
