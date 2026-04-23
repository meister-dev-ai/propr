// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for the latest verification snapshot of an AI connection profile.
/// </summary>
public sealed class AiVerificationSnapshotRecord
{
    public Guid ConnectionProfileId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? FailureCategory { get; set; }

    public string? Summary { get; set; }

    public string? ActionHint { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public string[] Warnings { get; set; } = [];

    public Dictionary<string, string>? DriverMetadata { get; set; }

    public AiConnectionProfileRecord? ConnectionProfile { get; set; }
}
