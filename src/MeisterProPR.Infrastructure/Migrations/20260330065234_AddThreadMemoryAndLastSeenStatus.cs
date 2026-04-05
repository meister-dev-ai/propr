// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadMemoryAndLastSeenStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "last_seen_status",
                table: "review_pr_scan_threads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "thread_memory_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    change_excerpt = table.Column<string>(type: "text", nullable: true),
                    comment_history_digest = table.Column<string>(type: "text", nullable: false),
                    resolution_summary = table.Column<string>(type: "text", nullable: false),
                    embedding_vector = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_memory_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_thread_memory_records_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_embedding_hnsw",
                table: "thread_memory_records",
                column: "embedding_vector")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "uq_thread_memory_records_thread",
                table: "thread_memory_records",
                columns: new[] { "client_id", "repository_id", "thread_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "last_seen_status",
                table: "review_pr_scan_threads");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
