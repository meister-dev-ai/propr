// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

/// <summary>Provider-local verifier for Azure DevOps webhook authorization headers.</summary>
public interface IAdoWebhookBasicAuthVerifier
{
    bool IsAuthorized(string? authorizationHeader, string protectedSecret);
}
