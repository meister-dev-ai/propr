// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>Why one send cycle did or did not reach the network.</summary>
public enum UsageStatisticsSendDecision
{
    /// <summary>The community toggle is off, so the installation does not send.</summary>
    Disabled = 0,

    /// <summary>No administrator has been shown what is sent yet.</summary>
    AwaitingConsent = 1,

    /// <summary>A snapshot was sent within the minimum interval.</summary>
    NotDue = 2,

    /// <summary>A snapshot was built and handed to the transport.</summary>
    Sent = 3,
}
