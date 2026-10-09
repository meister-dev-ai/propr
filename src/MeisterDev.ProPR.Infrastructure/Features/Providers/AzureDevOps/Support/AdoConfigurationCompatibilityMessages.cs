// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>Stable validation messages used by the existing guided compatibility HTTP endpoints.</summary>
public static class AdoConfigurationCompatibilityMessages
{
    public const string CanonicalOrganizationScope = "The Azure DevOps organization scope must be a canonical URL.";
    public const string OrganizationScopeUnavailable = "The Azure DevOps organization is not configured for this connection.";
    public const string AmbiguousOrganizations = "Several Azure DevOps organizations match this scope.";

    public const string ScopeSelectionRequired =
        "Azure DevOps requires OrganizationScopeId or ProviderScopePath. Other providers require ProviderScopePath.";

    public static string UnscopedConfigurationPathRequired(string configurationKind) =>
        $"ProviderScopePath is required for non-Azure DevOps {configurationKind} configurations.";
}
