// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>A server-captured immutable report payload with explicit client membership.</summary>
public sealed class ReviewerPerformanceReport
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CalculationVersion { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public ICollection<ReviewerPerformanceReportClient> Clients { get; set; } = new List<ReviewerPerformanceReportClient>();
}

/// <summary>All client scopes contained in a report; access requires the complete membership.</summary>
public sealed class ReviewerPerformanceReportClient
{
    public Guid ReportId { get; set; }
    public Guid ClientId { get; set; }
}
