// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;

/// <summary>What the administration UI shows about anonymous usage statistics.</summary>
/// <param name="Edition">The edition this installation reports.</param>
/// <param name="Enabled">Whether a snapshot would currently be sent.</param>
/// <param name="CommunityOptIn">The stored toggle. It applies when no license is installed.</param>
/// <param name="ManagedByLicense">Whether the control is locked because a commercial license is installed.</param>
/// <param name="ConsentGateSatisfied">Whether an administrator has been shown what is sent.</param>
/// <param name="NoticeRequired">Whether the consent notice still has to be shown.</param>
/// <param name="LastAttemptAt">When a send was last attempted.</param>
/// <param name="LastAttemptSucceeded">Whether that attempt reached the receiver.</param>
/// <param name="LastAttemptDetail">A short description of the last outcome.</param>
/// <param name="LastSuccessAt">When a snapshot last reached the receiver.</param>
/// <param name="PingEndpoint">The address a snapshot is posted to.</param>
/// <param name="PayloadDocumentationUrl">Where the payload fields are documented.</param>
/// <param name="PrivacyContact">The contact address for privacy questions about the payload.</param>
/// <param name="Update">Version and advisory information from the most recent successful ping.</param>
public sealed record UsageStatisticsSettingsDto(
    UsageStatisticsEdition Edition,
    bool Enabled,
    bool CommunityOptIn,
    bool ManagedByLicense,
    bool ConsentGateSatisfied,
    bool NoticeRequired,
    DateTimeOffset? LastAttemptAt,
    bool? LastAttemptSucceeded,
    string? LastAttemptDetail,
    DateTimeOffset? LastSuccessAt,
    string PingEndpoint,
    string PayloadDocumentationUrl,
    string PrivacyContact,
    UsageStatisticsUpdateStatusDto Update);
