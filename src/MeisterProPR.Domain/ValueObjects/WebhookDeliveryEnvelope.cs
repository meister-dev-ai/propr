// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Normalized provider-neutral webhook delivery context.</summary>
public sealed record WebhookDeliveryEnvelope
{
    /// <summary>Initializes a new instance of the <see cref="WebhookDeliveryEnvelope"/> class.</summary>
    /// <param name="host">The provider host reference.</param>
    /// <param name="deliveryId">The delivery identifier.</param>
    /// <param name="deliveryKind">The delivery kind.</param>
    /// <param name="eventName">The event name.</param>
    /// <param name="repository">The repository reference.</param>
    /// <param name="review">The code review reference.</param>
    /// <param name="revision">The review revision.</param>
    /// <param name="sourceBranch">The source branch.</param>
    /// <param name="targetBranch">The target branch.</param>
    /// <param name="actor">The reviewer identity acting on the event.</param>
    public WebhookDeliveryEnvelope(
        ProviderHostRef host,
        string deliveryId,
        string deliveryKind,
        string eventName,
        RepositoryRef? repository,
        CodeReviewRef? review,
        ReviewRevision? revision,
        string? sourceBranch,
        string? targetBranch,
        ReviewerIdentity? actor)
    {
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        this.DeliveryId = deliveryId.Trim();
        this.DeliveryKind = deliveryKind.Trim();
        this.EventName = eventName.Trim();
        this.Repository = repository;
        this.Review = review;
        this.Revision = revision;
        this.SourceBranch = NormalizeOptional(sourceBranch);
        this.TargetBranch = NormalizeOptional(targetBranch);
        this.Actor = actor;
    }

    /// <summary>Gets the provider host reference.</summary>
    public ProviderHostRef Host { get; }

    /// <summary>Gets the delivery identifier.</summary>
    public string DeliveryId { get; }

    /// <summary>Gets the delivery kind.</summary>
    public string DeliveryKind { get; }

    /// <summary>Gets the event name.</summary>
    public string EventName { get; }

    /// <summary>Gets the repository reference.</summary>
    public RepositoryRef? Repository { get; }

    /// <summary>Gets the code review reference.</summary>
    public CodeReviewRef? Review { get; }

    /// <summary>Gets the review revision.</summary>
    public ReviewRevision? Revision { get; }

    /// <summary>Gets the source branch.</summary>
    public string? SourceBranch { get; }

    /// <summary>Gets the target branch.</summary>
    public string? TargetBranch { get; }

    /// <summary>Gets the reviewer identity acting on the event.</summary>
    public ReviewerIdentity? Actor { get; }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
