// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.ProRV.Abstractions;
using MeisterDev.ProPR.ProRV.Core;
using MeisterDev.ProPR.ProRV.Knowledge;
using MeisterDev.ProPR.ProRV.Prompting;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.ProRV.DependencyInjection;

/// <summary>
///     Dependency-injection registration helpers for the ProRV library.
/// </summary>
public static class ProRVServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the bounded ProRV services and embedded knowledge assets.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProRV(this IServiceCollection services)
    {
        services.AddSingleton<IProRVKnowledgeCatalog, EmbeddedProRVKnowledgeCatalog>();
        services.AddSingleton<ProRVPromptFactory>();
        services.AddSingleton<IProRVPrefilter>(provider =>
            new ProRVPrefilter(
                provider.GetRequiredService<IProRVKnowledgeCatalog>(),
                provider.GetRequiredService<ProRVPromptFactory>()));
        return services;
    }
}
