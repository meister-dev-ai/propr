// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;

/// <summary>The position of a page in a list whose length is known.</summary>
internal static class PageOffset
{
    /// <summary>
    ///     Returns the number of rows before the page, or null when the page starts behind the last row. The product of
    ///     the page number and the page size is computed in 64 bits, because it exceeds the range of an int for a large
    ///     page number.
    /// </summary>
    /// <param name="page">The page, starting at 1.</param>
    /// <param name="pageSize">The rows of a page, at least 1.</param>
    /// <param name="total">The rows of the whole list.</param>
    public static int? Of(int page, int pageSize, int total)
    {
        var offset = ((long)page - 1) * pageSize;
        return offset < total ? (int)offset : null;
    }
}
