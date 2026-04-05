// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProCursorKnowledgeIndexing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "procursor_knowledge_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_kind = table.Column<short>(type: "smallint", nullable: false),
                    organization_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    repository_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    root_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    symbol_mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_knowledge_sources", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_knowledge_sources_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "procursor_tracked_branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    refresh_trigger_mode = table.Column<short>(type: "smallint", nullable: false),
                    mini_index_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_seen_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_indexed_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_tracked_branches", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_tracked_branches_procursor_knowledge_sources_know~",
                        column: x => x.knowledge_source_id,
                        principalTable: "procursor_knowledge_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "procursor_index_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracked_branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    job_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_index_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_index_jobs_procursor_knowledge_sources_knowledge_~",
                        column: x => x.knowledge_source_id,
                        principalTable: "procursor_knowledge_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_procursor_index_jobs_procursor_tracked_branches_tracked_bra~",
                        column: x => x.tracked_branch_id,
                        principalTable: "procursor_tracked_branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "procursor_index_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracked_branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    base_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    supports_symbol_queries = table.Column<bool>(type: "boolean", nullable: false),
                    file_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    chunk_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    symbol_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_index_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_index_snapshots_procursor_index_snapshots_base_sn~",
                        column: x => x.base_snapshot_id,
                        principalTable: "procursor_index_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procursor_index_snapshots_procursor_knowledge_sources_knowl~",
                        column: x => x.knowledge_source_id,
                        principalTable: "procursor_knowledge_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_procursor_index_snapshots_procursor_tracked_branches_tracke~",
                        column: x => x.tracked_branch_id,
                        principalTable: "procursor_tracked_branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "procursor_knowledge_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    chunk_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    chunk_ordinal = table.Column<int>(type: "integer", nullable: false),
                    line_start = table.Column<int>(type: "integer", nullable: true),
                    line_end = table.Column<int>(type: "integer", nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_text = table.Column<string>(type: "text", nullable: false),
                    embedding_vector = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_knowledge_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_knowledge_chunks_procursor_index_snapshots_snapsh~",
                        column: x => x.snapshot_id,
                        principalTable: "procursor_index_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "procursor_symbol_edges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_symbol_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    to_symbol_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    edge_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    line_start = table.Column<int>(type: "integer", nullable: true),
                    line_end = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_symbol_edges", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_symbol_edges_procursor_index_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "procursor_index_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "procursor_symbol_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    display_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    symbol_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    containing_symbol_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    file_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    line_start = table.Column<int>(type: "integer", nullable: false),
                    line_end = table.Column<int>(type: "integer", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_symbol_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_procursor_symbol_records_procursor_index_snapshots_snapshot~",
                        column: x => x.snapshot_id,
                        principalTable: "procursor_index_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_index_jobs_branch_dedup",
                table: "procursor_index_jobs",
                columns: new[] { "tracked_branch_id", "dedup_key" });

            migrationBuilder.CreateIndex(
                name: "IX_procursor_index_jobs_knowledge_source_id",
                table: "procursor_index_jobs",
                column: "knowledge_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_procursor_index_jobs_status_queued_at",
                table: "procursor_index_jobs",
                columns: new[] { "status", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "IX_procursor_index_snapshots_base_snapshot_id",
                table: "procursor_index_snapshots",
                column: "base_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ix_procursor_index_snapshots_source_branch_completed_at",
                table: "procursor_index_snapshots",
                columns: new[] { "knowledge_source_id", "tracked_branch_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_procursor_index_snapshots_tracked_branch_id",
                table: "procursor_index_snapshots",
                column: "tracked_branch_id");

            migrationBuilder.CreateIndex(
                name: "uq_procursor_index_snapshots_source_branch_commit",
                table: "procursor_index_snapshots",
                columns: new[] { "knowledge_source_id", "tracked_branch_id", "commit_sha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_procursor_knowledge_chunks_embedding_hnsw",
                table: "procursor_knowledge_chunks",
                column: "embedding_vector")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "uq_procursor_knowledge_chunks_snapshot_path_ordinal",
                table: "procursor_knowledge_chunks",
                columns: new[] { "snapshot_id", "source_path", "chunk_ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_procursor_knowledge_sources_client_enabled",
                table: "procursor_knowledge_sources",
                columns: new[] { "client_id", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_knowledge_sources_coordinates",
                table: "procursor_knowledge_sources",
                columns: new[] { "client_id", "source_kind", "organization_url", "project_id", "repository_id", "root_path" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_symbol_edges_snapshot_from_symbol",
                table: "procursor_symbol_edges",
                columns: new[] { "snapshot_id", "from_symbol_key" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_symbol_edges_snapshot_relation",
                table: "procursor_symbol_edges",
                columns: new[] { "snapshot_id", "from_symbol_key", "to_symbol_key", "edge_kind", "file_path", "line_start", "line_end" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_symbol_records_snapshot_display_name",
                table: "procursor_symbol_records",
                columns: new[] { "snapshot_id", "display_name" });

            migrationBuilder.CreateIndex(
                name: "uq_procursor_symbol_records_snapshot_symbol_key",
                table: "procursor_symbol_records",
                columns: new[] { "snapshot_id", "symbol_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_procursor_tracked_branches_source_enabled",
                table: "procursor_tracked_branches",
                columns: new[] { "knowledge_source_id", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "uq_procursor_tracked_branches_source_branch",
                table: "procursor_tracked_branches",
                columns: new[] { "knowledge_source_id", "branch_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "procursor_index_jobs");

            migrationBuilder.DropTable(
                name: "procursor_knowledge_chunks");

            migrationBuilder.DropTable(
                name: "procursor_symbol_edges");

            migrationBuilder.DropTable(
                name: "procursor_symbol_records");

            migrationBuilder.DropTable(
                name: "procursor_index_snapshots");

            migrationBuilder.DropTable(
                name: "procursor_tracked_branches");

            migrationBuilder.DropTable(
                name: "procursor_knowledge_sources");
        }
    }
}
