// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     The two ways reading a paginated provider collection can end without having read all of it.
/// </summary>
/// <remarks>
///     Both are reported as failures rather than absorbed. A review that reads part of a pull request and says
///     nothing looks exactly like one that read all of it and had less to say, so there is no outcome here that
///     returns a short collection quietly. The message is the whole explanation the operator gets: a failing
///     fetch is recorded verbatim as the job's error, so each one names which of the two happened and what it
///     means for the review.
/// </remarks>
internal static class ProviderPaginationFailure
{
    /// <summary>
    ///     The provider still reports more items after the loop has read as many pages as it will read. Either
    ///     the pull request is beyond what the provider's own API will serve, or it is beyond what this
    ///     integration reads in one review. Neither is recoverable by asking again.
    /// </summary>
    internal static string ExceededLimit(string collectionDescription, int itemsRead)
    {
        return $"{collectionDescription} exceeds the {itemsRead.ToString(CultureInfo.InvariantCulture)} items this " +
               "review can read and more remain, so the pull request cannot be reviewed completely.";
    }

    /// <summary>
    ///     The provider itself reported that what it returned is not the whole collection. GitLab's
    ///     merge-request change listing does this instead of paginating: past the host's diff limits it sets an
    ///     overflow flag and leaves the remainder out of the payload.
    /// </summary>
    internal static string ProviderTruncated(string collectionDescription)
    {
        return $"{collectionDescription} was cut short by the host, which reported that it did not return the " +
               "whole collection, so the pull request cannot be reviewed completely.";
    }

    /// <summary>
    ///     The host answered a request for the next page with a page it had already served, so it is not
    ///     applying the pagination the request asked for. What was read is not a known part of the collection,
    ///     it is an unknown one: continuing would review a set nobody can describe.
    /// </summary>
    internal static string RepeatedPage(string collectionDescription)
    {
        return $"{collectionDescription} returned a page the host had already served, so the host is not " +
               "paginating as requested and the rest of the collection cannot be read.";
    }
}
