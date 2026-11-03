// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>Durable source ordering, refresh generations and consumer progress without discussion text.</summary>
public sealed class DiscussionSynchronizationState
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ConnectionId { get; set; }
    public ScmProvider Provider { get; set; }
    public string ProviderScopePath { get; set; } = "";
    public string ProviderProjectKey { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public int PullRequestId { get; set; }
    public long RequestedGeneration { get; set; } = 1;
    public long CompletedGeneration { get; set; }
    public long ObservationSequence { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? LastFullReconciledAt { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    public string? LastReviewRevision { get; set; }
    public long CollectionGeneration { get; set; }
    public long RetentionGeneration { get; set; }
    public bool IsClosed { get; set; }
    public bool FullRefreshRequested { get; set; } = true;
    public string PendingThreadIdsJson { get; set; } = "[]";
    public string ThreadProgressJson { get; set; } = "{}";
    public string ReviewerSummariesJson { get; set; } = "[]";

    /// <summary>The attempts that failed since the last completed observation.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>The delay before the next attempt after the first failed one.</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>The longest delay between two attempts while the observation keeps failing.</summary>
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     The delay before the next attempt after this many failed attempts in a row: <see cref="FirstRetryDelay" />
    ///     after the first, doubled after each further one, and at most <see cref="MaximumRetryDelay" />. A discussion
    ///     that cannot be read, such as one whose connection no longer matches the pull request, is then read four times
    ///     an hour instead of every few seconds, while a single failure is retried soon.
    /// </summary>
    /// <param name="consecutiveFailures">The failed attempts in a row, including the one that just failed.</param>
    public static TimeSpan RetryDelay(int consecutiveFailures)
    {
        var doublings = Math.Clamp(consecutiveFailures - 1, 0, 16);
        var delay = FirstRetryDelay * Math.Pow(2, doublings);
        return delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    public Dictionary<string, DiscussionThreadProgress> ReadProgress() =>
        JsonSerializer.Deserialize<Dictionary<string, DiscussionThreadProgress>>(this.ThreadProgressJson) ?? [];

    public static Guid Identity(Guid clientId, Guid connectionId, ScmProvider provider, string scope, string project, string repository, int pullRequestId)
    {
        return new Guid(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        new
                            { clientId, connectionId, provider, scope, project, repository, pullRequestId })))[..16]);
    }
}

/// <summary>Content digests and successful consumer dependency digests for one provider thread.</summary>
public sealed class DiscussionThreadProgress
{
    public string SourceFingerprint { get; set; } = "";
    public Dictionary<string, string> Consumers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Identifies the fenced checkpoint that persisted the current memory acknowledgment.</summary>
    public string? MemoryCheckpoint { get; set; }

    /// <summary>Keeps memory cleanup retryable when a thread is removed before processing succeeds.</summary>
    public bool MemoryPending { get; set; }

    public bool Needs(string consumer, string source, string dependencies) =>
        !this.Consumers.TryGetValue(consumer, out var completed) || completed != Fingerprint(source, dependencies);

    public void Acknowledge(string consumer, string source, string dependencies)
    {
        this.SourceFingerprint = source;
        this.Consumers[consumer] = Fingerprint(source, dependencies);
    }

    public static string Fingerprint(params string[] inputs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inputs))));
}
