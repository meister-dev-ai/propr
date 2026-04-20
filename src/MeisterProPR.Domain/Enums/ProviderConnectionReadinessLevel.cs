// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Represents the current readiness level of a provider connection or provider family.</summary>
public enum ProviderConnectionReadinessLevel
{
    /// <summary>The readiness level is unknown.</summary>
    Unknown = 0,
    /// <summary>The provider connection is configured.</summary>
    Configured = 1,
    /// <summary>The provider connection is degraded.</summary>
    Degraded = 2,
    /// <summary>The provider connection is ready for onboarding.</summary>
    OnboardingReady = 3,
    /// <summary>The workflow is complete.</summary>
    WorkflowComplete = 4,
}
