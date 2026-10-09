// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using System.Reflection;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>Provides native local policies to fixtures with substituted remote provider adapters.</summary>
public static class ScmConnectionConfigurationPolicies
{
    private static readonly IReadOnlyDictionary<ScmProvider, IScmConnectionConfigurationPolicy> Policies =
        typeof(MeisterProPRDbContext).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IScmConnectionConfigurationPolicy).IsAssignableFrom(type) &&
                           type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               .Any(constructor => constructor.GetParameters().Length == 0))
            .Select(type => (IScmConnectionConfigurationPolicy)Activator.CreateInstance(type, true)!)
            .ToDictionary(policy => policy.Provider);

    public static IScmConnectionConfigurationPolicy Get(ScmProvider provider) => Policies[provider];
}
