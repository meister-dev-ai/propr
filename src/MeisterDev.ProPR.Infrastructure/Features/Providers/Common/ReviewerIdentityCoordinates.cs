// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Compares the native login and exact external identifier supplied by reviewer adapters.</summary>
internal static class ReviewerIdentityCoordinates
{
    public static bool Matches(ReviewerIdentity? actual, ReviewerIdentity expected) =>
        actual is not null && (string.Equals(actual.Login, expected.Login, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(actual.ExternalUserId, expected.ExternalUserId, StringComparison.Ordinal));
}
