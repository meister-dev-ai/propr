// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

internal static class ProviderBoundaryReflectionFixtures
{
    internal sealed class IndirectEnvelope
    {
        public NestedEnvelope? Value { get; init; }
    }

    internal sealed class NestedEnvelope
    {
        public IIdentityResolver? Identity { get; init; }
    }

    internal sealed class ConstrainedEnvelope<T> where T : IIdentityResolver
    {
        public T? Value { get; init; }
    }

    internal sealed class ConstrainedOperation
    {
        public void Inspect<T>() where T : IIdentityResolver
        {
        }
    }

    internal class BaseEnvelope
    {
        public IIdentityResolver? Identity { get; init; }
    }

    internal sealed class DerivedEnvelope : BaseEnvelope
    {
    }

    internal interface IEnvelope
    {
        IIdentityResolver Identity { get; }
    }

    internal interface IForwardedEnvelope : IEnvelope
    {
    }

    internal sealed class CyclicEnvelope
    {
        public CyclicEnvelope? Next { get; init; }
        public Dictionary<string, List<CyclicEnvelope>> Children { get; init; } = [];
        public ScmProvider Provider { get; init; }
    }
}
