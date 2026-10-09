// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>Projects persisted deployment URLs with their existing path and userinfo representation.</summary>
internal static class AdoStoredDeploymentCoordinates
{
    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("HostBaseUrl must be an absolute URL.", nameof(value));
        }

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
