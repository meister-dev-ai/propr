// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeisterProPR.ProCursor.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(ProCursorOperationalDbContext))]
public sealed class ProCursorOperationalDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.7")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<ProCursorIndexJob>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<int>("AttemptCount").HasColumnName("attempt_count").HasDefaultValue(0);
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnName("completed_at");
            b.Property<string>("DedupKey").HasColumnType("character varying(256)").HasMaxLength(256).HasColumnName("dedup_key");
            b.Property<string?>("FailureReason").HasColumnType("text").HasColumnName("failure_reason");
            b.Property<string>("JobKind").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("job_kind");
            b.Property<Guid>("KnowledgeSourceId").HasColumnName("knowledge_source_id");
            b.Property<DateTimeOffset>("QueuedAt").HasColumnName("queued_at");
            b.Property<string?>("RequestedCommitSha").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("requested_commit_sha");
            b.Property<DateTimeOffset?>("StartedAt").HasColumnName("started_at");
            b.Property<short>("Status").HasColumnName("status");
            b.Property<Guid>("TrackedBranchId").HasColumnName("tracked_branch_id");
            b.HasKey("Id");
            b.HasIndex("TrackedBranchId", "DedupKey").HasDatabaseName("ix_procursor_index_jobs_branch_dedup");
            b.HasIndex("Status", "QueuedAt").HasDatabaseName("ix_procursor_index_jobs_status_queued_at");
            b.ToTable("procursor_index_jobs", (string?)null);
        });

        modelBuilder.Entity<ProCursorIndexSnapshot>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<Guid?>("BaseSnapshotId").HasColumnName("base_snapshot_id");
            b.Property<int>("ChunkCount").HasColumnName("chunk_count").HasDefaultValue(0);
            b.Property<string>("CommitSha").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("commit_sha");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnName("completed_at");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnName("created_at");
            b.Property<string?>("FailureReason").HasColumnType("text").HasColumnName("failure_reason");
            b.Property<int>("FileCount").HasColumnName("file_count").HasDefaultValue(0);
            b.Property<Guid>("KnowledgeSourceId").HasColumnName("knowledge_source_id");
            b.Property<string>("SnapshotKind").HasColumnType("character varying(32)").HasMaxLength(32).HasColumnName("snapshot_kind");
            b.Property<string>("Status").HasColumnType("character varying(32)").HasMaxLength(32).HasColumnName("status");
            b.Property<bool>("SupportsSymbolQueries").HasColumnName("supports_symbol_queries");
            b.Property<int>("SymbolCount").HasColumnName("symbol_count").HasDefaultValue(0);
            b.Property<Guid>("TrackedBranchId").HasColumnName("tracked_branch_id");
            b.HasKey("Id");
            b.HasIndex("KnowledgeSourceId", "TrackedBranchId", "CommitSha").IsUnique().HasDatabaseName("uq_procursor_index_snapshots_source_branch_commit");
            b.HasIndex("KnowledgeSourceId", "TrackedBranchId", "CompletedAt").HasDatabaseName("ix_procursor_index_snapshots_source_branch_completed_at");
            b.ToTable("procursor_index_snapshots", (string?)null);
        });

        modelBuilder.Entity<ProCursorKnowledgeChunk>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<int?>("LineEnd").HasColumnName("line_end");
            b.Property<int?>("LineStart").HasColumnName("line_start");
            b.Property<int>("ChunkOrdinal").HasColumnName("chunk_ordinal");
            b.Property<string>("ChunkKind").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("chunk_kind");
            b.Property<string>("ContentHash").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("content_hash");
            b.Property<string>("ContentText").HasColumnType("text").HasColumnName("content_text");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnName("created_at");
            b.Property<float[]>("EmbeddingVector").HasColumnType("vector(1536)").HasColumnName("embedding_vector");
            b.Property<Guid>("SnapshotId").HasColumnName("snapshot_id");
            b.Property<string>("SourcePath").HasColumnType("text").HasColumnName("source_path");
            b.Property<string?>("Title").HasColumnType("character varying(256)").HasMaxLength(256).HasColumnName("title");
            b.HasKey("Id");
            b.HasIndex("EmbeddingVector").HasDatabaseName("ix_procursor_knowledge_chunks_embedding_hnsw");
            b.HasIndex("SnapshotId", "SourcePath", "ChunkOrdinal").IsUnique().HasDatabaseName("uq_procursor_knowledge_chunks_snapshot_path_ordinal");
            b.ToTable("procursor_knowledge_chunks", (string?)null);
        });

        modelBuilder.Entity<ProCursorSymbolEdge>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<string>("EdgeKind").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("edge_kind");
            b.Property<string>("FilePath").HasColumnType("text").HasColumnName("file_path");
            b.Property<string>("FromSymbolKey").HasColumnType("text").HasColumnName("from_symbol_key");
            b.Property<int?>("LineEnd").HasColumnName("line_end");
            b.Property<int?>("LineStart").HasColumnName("line_start");
            b.Property<Guid>("SnapshotId").HasColumnName("snapshot_id");
            b.Property<string>("ToSymbolKey").HasColumnType("text").HasColumnName("to_symbol_key");
            b.HasKey("Id");
            b.HasIndex("SnapshotId", "FromSymbolKey").HasDatabaseName("ix_procursor_symbol_edges_snapshot_from_symbol");
            b.HasIndex("SnapshotId", "FromSymbolKey", "ToSymbolKey", "EdgeKind", "FilePath", "LineStart", "LineEnd").HasDatabaseName("ix_procursor_symbol_edges_snapshot_relation");
            b.ToTable("procursor_symbol_edges", (string?)null);
        });

        modelBuilder.Entity<ProCursorSymbolRecord>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<string?>("ContainingSymbolKey").HasColumnType("text").HasColumnName("containing_symbol_key");
            b.Property<string>("DisplayName").HasColumnType("character varying(512)").HasMaxLength(512).HasColumnName("display_name");
            b.Property<string>("FilePath").HasColumnType("text").HasColumnName("file_path");
            b.Property<string>("Language").HasColumnType("character varying(32)").HasMaxLength(32).HasColumnName("language");
            b.Property<int>("LineEnd").HasColumnName("line_end");
            b.Property<int>("LineStart").HasColumnName("line_start");
            b.Property<string>("SearchText").HasColumnType("text").HasColumnName("search_text");
            b.Property<string>("Signature").HasColumnType("text").HasColumnName("signature");
            b.Property<Guid>("SnapshotId").HasColumnName("snapshot_id");
            b.Property<string>("SymbolKey").HasColumnType("text").HasColumnName("symbol_key");
            b.Property<string>("SymbolKind").HasColumnType("character varying(64)").HasMaxLength(64).HasColumnName("symbol_kind");
            b.HasKey("Id");
            b.HasIndex("SnapshotId", "DisplayName").HasDatabaseName("ix_procursor_symbol_records_snapshot_display_name");
            b.HasIndex("SnapshotId", "SymbolKey").IsUnique().HasDatabaseName("uq_procursor_symbol_records_snapshot_symbol_key");
            b.ToTable("procursor_symbol_records", (string?)null);
        });

        modelBuilder.Entity<ProCursorTokenUsageEvent>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<Guid?>("AiConnectionId").HasColumnName("ai_connection_id");
            b.Property<short>("CallType").HasColumnName("call_type");
            b.Property<long>("CompletionTokens").HasColumnName("completion_tokens");
            b.Property<bool>("CostEstimated").HasColumnName("cost_estimated");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnName("created_at_utc");
            b.Property<string>("DeploymentName").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("deployment_name");
            b.Property<decimal?>("EstimatedCostUsd").HasPrecision(18, 6).HasColumnName("estimated_cost_usd");
            b.Property<Guid?>("IndexJobId").HasColumnName("index_job_id");
            b.Property<Guid?>("KnowledgeChunkId").HasColumnName("knowledge_chunk_id");
            b.Property<string>("ModelName").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("model_name");
            b.Property<DateTimeOffset>("OccurredAtUtc").HasColumnName("occurred_at_utc");
            b.Property<long>("PromptTokens").HasColumnName("prompt_tokens");
            b.Property<Guid>("ProCursorSourceId").HasColumnName("procursor_source_id");
            b.Property<Guid>("ClientId").HasColumnName("client_id");
            b.Property<string>("RequestId").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("request_id");
            b.Property<string?>("ResourceId").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("resource_id");
            b.Property<string?>("SafeMetadataJson").HasColumnType("jsonb").HasColumnName("safe_metadata_json");
            b.Property<string?>("SourcePath").HasColumnType("character varying(500)").HasMaxLength(500).HasColumnName("source_path");
            b.Property<string>("SourceDisplayNameSnapshot").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("source_display_name_snapshot");
            b.Property<string>("TokenizerName").HasColumnType("character varying(50)").HasMaxLength(50).HasColumnName("tokenizer_name");
            b.Property<long>("TotalTokens").HasColumnName("total_tokens");
            b.Property<bool>("TokensEstimated").HasColumnName("tokens_estimated");
            b.HasKey("Id");
            b.HasIndex("ClientId", "ModelName", "OccurredAtUtc").HasDatabaseName("ix_procursor_token_usage_events_client_model_occurred_at");
            b.HasIndex("ClientId", "OccurredAtUtc").HasDatabaseName("ix_procursor_token_usage_events_client_occurred_at");
            b.HasIndex("ClientId", "RequestId").IsUnique().HasDatabaseName("ux_procursor_token_usage_events_client_request");
            b.HasIndex("IndexJobId").HasDatabaseName("ix_procursor_token_usage_events_index_job_id");
            b.HasIndex("ProCursorSourceId", "OccurredAtUtc").HasDatabaseName("ix_procursor_token_usage_events_source_occurred_at");
            b.ToTable("procursor_token_usage_events", (string?)null);
        });

        modelBuilder.Entity<ProCursorTokenUsageRollup>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnName("id");
            b.Property<DateOnly>("BucketStartDate").HasColumnName("bucket_start_date");
            b.Property<Guid>("ClientId").HasColumnName("client_id");
            b.Property<long>("CompletionTokens").HasColumnName("completion_tokens");
            b.Property<long>("EstimatedEventCount").HasColumnName("estimated_event_count");
            b.Property<decimal?>("EstimatedCostUsd").HasPrecision(18, 6).HasColumnName("estimated_cost_usd");
            b.Property<long>("EventCount").HasColumnName("event_count");
            b.Property<short>("Granularity").HasColumnName("granularity");
            b.Property<DateTimeOffset>("LastRecomputedAtUtc").HasColumnName("last_recomputed_at_utc");
            b.Property<string>("ModelName").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("model_name");
            b.Property<long>("PromptTokens").HasColumnName("prompt_tokens");
            b.Property<Guid?>("ProCursorSourceId").HasColumnName("procursor_source_id");
            b.Property<string?>("SourceDisplayNameSnapshot").HasColumnType("character varying(200)").HasMaxLength(200).HasColumnName("source_display_name_snapshot");
            b.Property<long>("TotalTokens").HasColumnName("total_tokens");
            b.HasKey("Id");
            b.HasIndex("ClientId", "Granularity", "BucketStartDate").HasDatabaseName("ix_procursor_token_usage_rollups_client_granularity_bucket");
            b.HasIndex("ClientId", "ModelName", "Granularity", "BucketStartDate").HasDatabaseName("ix_procursor_token_usage_rollups_client_model_granularity_bucket");
            b.HasIndex("ClientId", "ProCursorSourceId", "BucketStartDate", "Granularity", "ModelName").IsUnique().HasDatabaseName("ux_procursor_token_usage_rollups_scope");
            b.HasIndex("ClientId", "ProCursorSourceId", "Granularity", "BucketStartDate").HasDatabaseName("ix_procursor_token_usage_rollups_source_granularity_bucket");
            b.ToTable("procursor_token_usage_rollups", (string?)null);
        });
#pragma warning restore 612, 618
    }
}
