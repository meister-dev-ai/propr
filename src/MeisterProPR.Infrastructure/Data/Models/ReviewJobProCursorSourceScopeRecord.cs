// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for snapshotted ProCursor source scope on a queued review job.</summary>
public sealed class ReviewJobProCursorSourceScopeRecord
{
    public Guid ReviewJobId { get; set; }
    public Guid ProCursorSourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ReviewJob? ReviewJob { get; set; }
    public ProCursorKnowledgeSource? ProCursorSource { get; set; }
}
