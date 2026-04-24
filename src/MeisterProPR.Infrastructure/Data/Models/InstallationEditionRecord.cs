// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>Persisted singleton row representing the active installation edition.</summary>
public sealed class InstallationEditionRecord
{
    public int Id { get; set; }

    public InstallationEdition Edition { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public Guid? ActivatedByUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
