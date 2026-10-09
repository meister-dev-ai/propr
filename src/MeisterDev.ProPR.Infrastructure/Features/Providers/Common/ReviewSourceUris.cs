// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Validates HTTP source addresses without provider-specific scope rules.</summary>
internal static class ReviewSourceUris
{
    internal static bool TryRead(string value, out Uri uri, bool rejectQuery = true)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https" && string.IsNullOrEmpty(parsed.UserInfo) &&
            (!rejectQuery || string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    internal static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(left.GetLeftPart(UriPartial.Authority), right.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
