// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>Persisted installation-wide activation state for one SCM provider family.</summary>
public sealed class ProviderActivationRecord
{
    public ScmProvider Provider { get; set; }

    public bool IsEnabled { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
