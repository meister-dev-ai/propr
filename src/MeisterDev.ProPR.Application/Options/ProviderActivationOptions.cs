// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>The provider families that an installation enables until an administrator changes their activation.</summary>
public sealed class ProviderActivationOptions
{
    /// <summary>The installation setting that lists the provider families, separated by commas.</summary>
    public const string DefaultEnabledKey = "MEISTER_DEFAULT_ENABLED_PROVIDERS";

    /// <summary>The enabled provider families, or null for the built-in default of Azure DevOps and GitLab.</summary>
    public IReadOnlySet<ScmProvider>? DefaultEnabled { get; set; }
}
