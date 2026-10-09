// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

/// <summary>Registers pure native policy implementations without live provider adapters.</summary>
public static class ScmLocalPolicyServiceCollectionExtensions
{
    public static IServiceCollection AddScmProviderLocalPolicies(this IServiceCollection services)
    {
        services.AddScmProviderLocalDeclarations();
        services.TryAddScoped<IScmProviderRegistry, ScmProviderRegistry>();
        return services;
    }

    /// <summary>Registers credential-free declarations without changing runtime registry composition.</summary>
    public static IServiceCollection AddScmProviderLocalDeclarations(this IServiceCollection services)
    {
        services
            .TryAddSingleton<IScmProviderCompatibilityCodec,
                MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility.ScmProviderCompatibilityCodec>();
        services.AddAzureDevOpsLocalPolicies();
        services.AddGitHubLocalPolicies();
        services.AddGitLabLocalPolicies();
        services.AddForgejoLocalPolicies();
        return services;
    }
}
