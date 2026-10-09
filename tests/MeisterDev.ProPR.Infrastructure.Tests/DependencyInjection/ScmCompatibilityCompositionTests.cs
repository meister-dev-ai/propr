// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.DependencyInjection;

public sealed class ScmCompatibilityCompositionTests
{
    [Fact]
    public void RemoteModuleResolvesSingletonEvidenceCodecWithoutOperationalRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProCursorRemoteMode(new ConfigurationBuilder().Build());
        var registryResolutions = 0;
        services.AddScoped<IScmProviderRegistry>(_ =>
        {
            registryResolutions++;
            throw new InvalidOperationException("Operational registry must not be resolved.");
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var codec = provider.GetRequiredService<IScmProviderCompatibilityCodec>();
        using var scope = provider.CreateScope();

        Assert.Same(codec, scope.ServiceProvider.GetRequiredService<IScmProviderCompatibilityCodec>());
        Assert.Equal(ThreadResolutionIntent.ClaimsFix, codec.DecodeStoredThreadResolution("Fixed"));
        Assert.Equal(ThreadResolutionIntent.Active, codec.DecodeStoredThreadResolution(" Fixed "));
        Assert.Equal(0, registryResolutions);
    }

    [Fact]
    public void CapturedSyntheticContextIsIndependentFromLiveCapabilityRegistration()
    {
        var registry = ScmLocalPolicyFactory.CreateRegistry();
        var review = new CodeReviewRef(
            new RepositoryRef(
                new ProviderHostRef(
                    (ScmProvider)792,
                    "https://synthetic.example"), "repo", "owner", "owner/project"),
            CodeReviewPlatformKind.PullRequest, "external-review", 42);

        var captured = registry.CompatibilityCodec.PrepareSynchronizationJobSource(
            review,
            "https://synthetic.example/saved", "saved-project", 42);

        Assert.Equal(CodeReviewSourceContext.FromReview(review), captured);
        Assert.False(registry.IsRegistered((ScmProvider)792));
        Assert.Equal(
            ScmProvider.AzureDevOps,
            registry.CompatibilityCodec.PrepareSynchronizationJobSource(null, "https://synthetic.example/saved", "saved-project", 42).Provider);
        Assert.False(registry.IsRegistered(ScmProvider.AzureDevOps));
    }
}
