// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Webhooks;

/// <summary>Declares native GitHub webhook diagnostic and retry headers.</summary>
internal sealed class GitHubWebhookIngressPolicy : WebhookIngressPolicyBase
{
    public override ScmProvider Provider => ScmProvider.GitHub;
    protected override string? EventHeaderName => "X-GitHub-Event";
    protected override string? DeliveryHeaderName => "X-GitHub-Delivery";
}
