// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Webhooks;

/// <summary>Declares native Forgejo webhook diagnostic and retry headers.</summary>
internal sealed class ForgejoWebhookIngressPolicy : WebhookIngressPolicyBase
{
    public override ScmProvider Provider => ScmProvider.Forgejo;
    protected override string? EventHeaderName => "X-Gitea-Event";
    protected override string? DeliveryHeaderName => "X-Gitea-Delivery";
}
