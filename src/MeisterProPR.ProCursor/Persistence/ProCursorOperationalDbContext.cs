// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.ProCursor.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MeisterProPR.ProCursor.Persistence;

/// <summary>
///     ProCursor-owned operational EF Core context used by the extracted host.
/// </summary>
public sealed class ProCursorOperationalDbContext(DbContextOptions<ProCursorOperationalDbContext> options) : DbContext(options)
{
    public DbSet<ProCursorIndexJob> ProCursorIndexJobs => this.Set<ProCursorIndexJob>();

    public DbSet<ProCursorIndexSnapshot> ProCursorIndexSnapshots => this.Set<ProCursorIndexSnapshot>();

    public DbSet<ProCursorKnowledgeChunk> ProCursorKnowledgeChunks => this.Set<ProCursorKnowledgeChunk>();

    public DbSet<ProCursorSymbolRecord> ProCursorSymbolRecords => this.Set<ProCursorSymbolRecord>();

    public DbSet<ProCursorSymbolEdge> ProCursorSymbolEdges => this.Set<ProCursorSymbolEdge>();

    public DbSet<ProCursorTokenUsageEvent> ProCursorTokenUsageEvents => this.Set<ProCursorTokenUsageEvent>();

    public DbSet<ProCursorTokenUsageRollup> ProCursorTokenUsageRollups => this.Set<ProCursorTokenUsageRollup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        new ProCursorIndexJobEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorIndexJob>());
        new ProCursorIndexSnapshotEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorIndexSnapshot>());
        new ProCursorKnowledgeChunkEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorKnowledgeChunk>());
        new ProCursorSymbolRecordEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorSymbolRecord>());
        new ProCursorSymbolEdgeEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorSymbolEdge>());
        new ProCursorTokenUsageEventEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorTokenUsageEvent>());
        new ProCursorTokenUsageRollupEntityTypeConfiguration().Configure(modelBuilder.Entity<ProCursorTokenUsageRollup>());

        if (this.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            modelBuilder.HasPostgresExtension("vector");
            modelBuilder.Entity<ProCursorKnowledgeChunk>()
                .Property(chunk => chunk.EmbeddingVector)
                .HasColumnType("vector(1536)")
                .HasConversion(
                    v => new Vector(v),
                    v => v.ToArray());
        }
    }
}
