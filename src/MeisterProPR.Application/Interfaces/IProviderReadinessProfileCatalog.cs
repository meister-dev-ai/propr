// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Clients.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Resolves provider-family readiness evidence profiles by host variant.</summary>
public interface IProviderReadinessProfileCatalog
{
    /// <summary>Returns the readiness profile that applies to the given provider family and host.</summary>
    ProviderReadinessProfile GetProfile(ScmProvider providerFamily, string hostBaseUrl);

    /// <summary>Returns the known readiness profiles for the given provider family.</summary>
    IReadOnlyList<ProviderReadinessProfile> GetProfiles(ScmProvider providerFamily);
}
