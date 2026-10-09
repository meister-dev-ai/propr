// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Webhooks;

/// <summary>Declares native AzureDevOps webhook diagnostic and retry headers.</summary>
internal sealed class AdoWebhookIngressPolicy : WebhookIngressPolicyBase
{
    public override ScmProvider Provider => ScmProvider.AzureDevOps;
    // Service-hook notification identifiers remain in the body; retry keys are absent here.
}
