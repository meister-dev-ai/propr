// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Preserves stored app fields for unsupported authentication kinds before native validation.</summary>
internal static class HistoricalScmAppMetadataProjection
{
    public static ScmAppPatchMetadata Prepare(ScmAuthenticationKind kind, long? appId, long? installationId, long? savedAppId, long? savedInstallationId) =>
        new(
            kind == ScmAuthenticationKind.AppInstallation ? appId ?? savedAppId : appId,
            kind == ScmAuthenticationKind.AppInstallation ? installationId ?? savedInstallationId : installationId,
            kind == ScmAuthenticationKind.AppInstallation ? appId ?? savedAppId : null,
            kind == ScmAuthenticationKind.AppInstallation ? installationId ?? savedInstallationId : null);
}
