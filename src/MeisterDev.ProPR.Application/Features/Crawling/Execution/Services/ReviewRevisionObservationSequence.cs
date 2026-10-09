// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Projects captured revision evidence onto the persisted positive observation-sequence alias.</summary>
public static class ReviewRevisionObservationSequence
{
    public static int? FromRevision(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return null;
        }

        if (int.TryParse(
                revision.ProviderRevisionId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var sequence) && sequence > 0)
        {
            return sequence;
        }

        var key = revision.ProviderRevisionId
                  ?? revision.PatchIdentity
                  ?? $"{revision.BaseSha}::{revision.HeadSha}::{revision.StartSha}";
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
        var value = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return value == 0 ? 1 : value;
    }
}
