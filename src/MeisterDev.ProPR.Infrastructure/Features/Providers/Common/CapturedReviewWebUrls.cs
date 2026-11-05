// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Validates saved browser coordinates and encodes native route components.</summary>
internal static class CapturedReviewWebUrls
{
    /// <summary>
    ///     Builds the Azure DevOps pull request page address. Azure DevOps resolves a project identifier in the
    ///     path but opens no pull request for a repository identifier, so the address uses the repository name and
    ///     is null when no name was captured.
    /// </summary>
    internal static string? AzureDevOps(CapturedReviewUrlCoordinates coordinates)
    {
        if (!TryReadBase(coordinates.SourcePath, out var source) || !SafeComponent(coordinates.ProjectId)
                                                                 || coordinates.RepositoryName is not { } repositoryName
                                                                 || !SafeComponent(repositoryName) || coordinates.PullRequestId <= 0)
        {
            return null;
        }

        return
            $"{source}/{Uri.EscapeDataString(coordinates.ProjectId)}/_git/{Uri.EscapeDataString(repositoryName.Trim())}/pullrequest/{coordinates.PullRequestId.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static string? Repository(CapturedReviewUrlCoordinates coordinates, string reviewRoute)
    {
        if (!TryReadBase(coordinates.HostBaseUrl ?? coordinates.SourcePath, out var source)
            || coordinates.PullRequestId <= 0 || string.IsNullOrWhiteSpace(coordinates.RepositoryProjectPath))
        {
            return null;
        }

        var parts = coordinates.RepositoryProjectPath.Split('/');
        if (parts.Length < 2 || parts.Any(part => !SafeComponent(part)))
        {
            return null;
        }

        return
            $"{source}/{string.Join('/', parts.Select(Uri.EscapeDataString))}/{reviewRoute}/{coordinates.PullRequestId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool SafeComponent(string part) => !string.IsNullOrWhiteSpace(part) && part is not "." and not ".."
                                                                                       && !part.Any(char.IsControl) && !part.Contains('\\') &&
                                                                                       !part.Contains('/');

    private static bool TryReadBase(string? value, out string source)
    {
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || Uri.UnescapeDataString(value).Any(char.IsControl) || Uri.UnescapeDataString(value).Contains('\\')
            || Uri.UnescapeDataString(value).Split('/').Any(part => part is "." or ".."))
        {
            return false;
        }

        source = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }
}
