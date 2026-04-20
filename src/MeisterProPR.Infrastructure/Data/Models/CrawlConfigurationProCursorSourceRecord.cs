// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for explicit ProCursor source associations on a crawl configuration.</summary>
public sealed class CrawlConfigurationProCursorSourceRecord
{
    public Guid CrawlConfigurationId { get; set; }
    public Guid ProCursorSourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public CrawlConfigurationRecord? CrawlConfiguration { get; set; }
    public ProCursorKnowledgeSource? ProCursorSource { get; set; }
}
