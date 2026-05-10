// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeisterProPR.ProCursor.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(ProCursorOperationalDbContext))]
[Migration("20260510120000_InitialProCursorOperationalSchema")]
public sealed class InitialProCursorOperationalSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_index_jobs (
                id uuid PRIMARY KEY,
                knowledge_source_id uuid NOT NULL,
                tracked_branch_id uuid NOT NULL,
                requested_commit_sha character varying(64),
                job_kind character varying(64) NOT NULL,
                status smallint NOT NULL,
                dedup_key character varying(256) NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                queued_at timestamp with time zone NOT NULL,
                started_at timestamp with time zone,
                completed_at timestamp with time zone,
                failure_reason text
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_index_snapshots (
                id uuid PRIMARY KEY,
                knowledge_source_id uuid NOT NULL,
                tracked_branch_id uuid NOT NULL,
                commit_sha character varying(64) NOT NULL,
                snapshot_kind character varying(32) NOT NULL,
                base_snapshot_id uuid,
                status character varying(32) NOT NULL,
                supports_symbol_queries boolean NOT NULL,
                file_count integer NOT NULL DEFAULT 0,
                chunk_count integer NOT NULL DEFAULT 0,
                symbol_count integer NOT NULL DEFAULT 0,
                created_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone,
                failure_reason text,
                CONSTRAINT fk_procursor_index_snapshots_base_snapshot
                    FOREIGN KEY (base_snapshot_id)
                    REFERENCES procursor_index_snapshots(id)
                    ON DELETE RESTRICT
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_knowledge_chunks (
                id uuid PRIMARY KEY,
                snapshot_id uuid NOT NULL,
                source_path text NOT NULL,
                chunk_kind character varying(64) NOT NULL,
                title character varying(256),
                chunk_ordinal integer NOT NULL,
                line_start integer,
                line_end integer,
                content_hash character varying(64) NOT NULL,
                content_text text NOT NULL,
                embedding_vector vector(1536) NOT NULL,
                created_at timestamp with time zone NOT NULL,
                CONSTRAINT fk_procursor_knowledge_chunks_snapshot
                    FOREIGN KEY (snapshot_id)
                    REFERENCES procursor_index_snapshots(id)
                    ON DELETE CASCADE
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_symbol_records (
                id uuid PRIMARY KEY,
                snapshot_id uuid NOT NULL,
                language character varying(32) NOT NULL,
                symbol_key text NOT NULL,
                display_name character varying(512) NOT NULL,
                symbol_kind character varying(64) NOT NULL,
                containing_symbol_key text,
                file_path text NOT NULL,
                line_start integer NOT NULL,
                line_end integer NOT NULL,
                signature text NOT NULL,
                search_text text NOT NULL,
                CONSTRAINT fk_procursor_symbol_records_snapshot
                    FOREIGN KEY (snapshot_id)
                    REFERENCES procursor_index_snapshots(id)
                    ON DELETE CASCADE
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_symbol_edges (
                id uuid PRIMARY KEY,
                snapshot_id uuid NOT NULL,
                from_symbol_key text NOT NULL,
                to_symbol_key text NOT NULL,
                edge_kind character varying(64) NOT NULL,
                file_path text NOT NULL,
                line_start integer,
                line_end integer,
                CONSTRAINT fk_procursor_symbol_edges_snapshot
                    FOREIGN KEY (snapshot_id)
                    REFERENCES procursor_index_snapshots(id)
                    ON DELETE CASCADE
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_token_usage_events (
                id uuid PRIMARY KEY,
                client_id uuid NOT NULL,
                procursor_source_id uuid NOT NULL,
                source_display_name_snapshot character varying(200) NOT NULL,
                index_job_id uuid,
                request_id character varying(200) NOT NULL,
                occurred_at_utc timestamp with time zone NOT NULL,
                call_type smallint NOT NULL,
                ai_connection_id uuid,
                deployment_name character varying(200) NOT NULL,
                model_name character varying(200) NOT NULL,
                tokenizer_name character varying(50) NOT NULL,
                prompt_tokens bigint NOT NULL,
                completion_tokens bigint NOT NULL,
                total_tokens bigint NOT NULL,
                tokens_estimated boolean NOT NULL,
                estimated_cost_usd numeric(18,6),
                cost_estimated boolean NOT NULL,
                resource_id character varying(200),
                source_path character varying(500),
                knowledge_chunk_id uuid,
                safe_metadata_json jsonb,
                created_at_utc timestamp with time zone NOT NULL
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS procursor_token_usage_rollups (
                id uuid PRIMARY KEY,
                client_id uuid NOT NULL,
                procursor_source_id uuid,
                source_display_name_snapshot character varying(200),
                bucket_start_date date NOT NULL,
                granularity smallint NOT NULL,
                model_name character varying(200) NOT NULL,
                prompt_tokens bigint NOT NULL,
                completion_tokens bigint NOT NULL,
                total_tokens bigint NOT NULL,
                estimated_cost_usd numeric(18,6),
                event_count bigint NOT NULL,
                estimated_event_count bigint NOT NULL,
                last_recomputed_at_utc timestamp with time zone NOT NULL
            );
            """);

        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_index_jobs_branch_dedup ON procursor_index_jobs (tracked_branch_id, dedup_key);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_index_jobs_status_queued_at ON procursor_index_jobs (status, queued_at);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS uq_procursor_index_snapshots_source_branch_commit ON procursor_index_snapshots (knowledge_source_id, tracked_branch_id, commit_sha);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_index_snapshots_source_branch_completed_at ON procursor_index_snapshots (knowledge_source_id, tracked_branch_id, completed_at);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS uq_procursor_knowledge_chunks_snapshot_path_ordinal ON procursor_knowledge_chunks (snapshot_id, source_path, chunk_ordinal);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_knowledge_chunks_embedding_hnsw ON procursor_knowledge_chunks USING hnsw (embedding_vector vector_cosine_ops);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS uq_procursor_symbol_records_snapshot_symbol_key ON procursor_symbol_records (snapshot_id, symbol_key);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_symbol_records_snapshot_display_name ON procursor_symbol_records (snapshot_id, display_name);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_symbol_edges_snapshot_relation ON procursor_symbol_edges (snapshot_id, from_symbol_key, to_symbol_key, edge_kind, file_path, line_start, line_end);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_symbol_edges_snapshot_from_symbol ON procursor_symbol_edges (snapshot_id, from_symbol_key);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_procursor_token_usage_events_client_request ON procursor_token_usage_events (client_id, request_id);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_events_client_occurred_at ON procursor_token_usage_events (client_id, occurred_at_utc);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_events_source_occurred_at ON procursor_token_usage_events (procursor_source_id, occurred_at_utc);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_events_client_model_occurred_at ON procursor_token_usage_events (client_id, model_name, occurred_at_utc);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_events_index_job_id ON procursor_token_usage_events (index_job_id);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_procursor_token_usage_rollups_scope ON procursor_token_usage_rollups (client_id, procursor_source_id, bucket_start_date, granularity, model_name);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_rollups_client_granularity_bucket ON procursor_token_usage_rollups (client_id, granularity, bucket_start_date);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_rollups_source_granularity_bucket ON procursor_token_usage_rollups (client_id, procursor_source_id, granularity, bucket_start_date);");
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_procursor_token_usage_rollups_client_model_granularity_bucket ON procursor_token_usage_rollups (client_id, model_name, granularity, bucket_start_date);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_token_usage_rollups;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_token_usage_events;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_symbol_edges;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_symbol_records;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_knowledge_chunks;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_index_snapshots;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS procursor_index_jobs;");
    }
}
