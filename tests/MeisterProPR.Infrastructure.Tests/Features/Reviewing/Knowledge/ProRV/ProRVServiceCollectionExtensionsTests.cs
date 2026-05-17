// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Knowledge.ProRV;

public sealed class ProRVServiceCollectionExtensionsTests
{
    [Fact]
    public void AddProRV_RegistersPublicPrefilterEntryPoint()
    {
        var services = new ServiceCollection();

        services.AddProRV();

        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<IProRVPrefilter>(services));
    }

    private static ServiceLifetime GetLifetime<TService>(IServiceCollection services)
    {
        return services.Single(descriptor => descriptor.ServiceType == typeof(TService)).Lifetime;
    }
}
