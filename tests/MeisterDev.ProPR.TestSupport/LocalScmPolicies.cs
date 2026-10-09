// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>Resolves production native policies without any credential or live adapter registration.</summary>
public static class LocalScmPolicies
{
    public static IScmProviderRegistry Registry { get; } = ScmLocalPolicyFactory.CreateRegistry();

    public static IEnumerable<IScmConnectionConfigurationPolicy> ConfigurationPolicies =>
        Enum.GetValues<ScmProvider>().Select(Registry.GetConnectionConfigurationPolicy);

    public static IEnumerable<IScmIdentityPolicy> IdentityPolicies =>
        Enum.GetValues<ScmProvider>().Select(Registry.GetIdentityPolicy);

    public static IScmProviderRegistry CreateRuntimeSubstitute()
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.CompatibilityCodec.Returns(Registry.CompatibilityCodec);
        registry.GetWebhookIngressPolicy(Arg.Any<ScmProvider>()).Returns(call => Registry.GetWebhookIngressPolicy(call.Arg<ScmProvider>()));
        registry.GetConnectionConfigurationPolicy(Arg.Any<ScmProvider>()).Returns(call => Registry.GetConnectionConfigurationPolicy(call.Arg<ScmProvider>()));
        registry.GetIdentityPolicy(Arg.Any<ScmProvider>()).Returns(call => Registry.GetIdentityPolicy(call.Arg<ScmProvider>()));
        registry.GetSourceIdentityPolicy(Arg.Any<ScmProvider>()).Returns(call => Registry.GetSourceIdentityPolicy(call.Arg<ScmProvider>()));
        registry.GetCodeReviewPreparationPolicy(Arg.Any<ScmProvider>()).Returns(call => Registry.GetCodeReviewPreparationPolicy(call.Arg<ScmProvider>()));
        return registry;
    }
}
