// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Features.ProCursor.Remote;

/// <summary>
///     Shared constants for the ProCursor internal service boundary authentication.
/// </summary>
public static class ProCursorSharedKeyAuthenticationDefaults
{
    /// <summary>
    ///     Header carrying the internal ProCursor shared key.
    /// </summary>
    public const string HeaderName = "X-ProCursor-Key";

    /// <summary>
    ///     Authentication scheme name for internal ProCursor requests.
    /// </summary>
    public const string Scheme = "ProCursorSharedKey";
}
