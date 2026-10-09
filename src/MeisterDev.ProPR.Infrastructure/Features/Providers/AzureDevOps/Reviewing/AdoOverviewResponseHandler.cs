// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Preserves outcomes from final SDK responses for customer overview reads.</summary>
internal sealed class AdoOverviewResponseHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            // The inner SDK handler completes authentication challenges and retries before returning this response.
            var connectionWide = !request.RequestUri!.AbsolutePath.Contains("/_apis/git/repositories/", StringComparison.OrdinalIgnoreCase);
            AdoReadFailures.ThrowIfDeniedOrThrottled(response, connectionWide);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
