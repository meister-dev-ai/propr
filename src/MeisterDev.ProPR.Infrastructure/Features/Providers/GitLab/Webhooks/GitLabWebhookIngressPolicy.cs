// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Webhooks;

/// <summary>Declares native GitLab webhook diagnostic and retry headers.</summary>
internal sealed class GitLabWebhookIngressPolicy : WebhookIngressPolicyBase
{
    public override ScmProvider Provider => ScmProvider.GitLab;
    protected override string? EventHeaderName => "X-Gitlab-Event";
    protected override string? DeliveryHeaderName => "X-Gitlab-Event-UUID";
}
