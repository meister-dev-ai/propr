// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Widens every stored thread identifier from a number to the provider's own text form.
    /// </summary>
    /// <remarks>
    ///     Azure DevOps thread ids are already the provider's own identifier, so their digits carry across
    ///     unchanged and every existing key still resolves to the thread it named before. PostgreSQL will not
    ///     cast a bigint to text on its own, so the conversion is spelled out; the primary key and the unique
    ///     indexes over these columns are rebuilt by the type change itself and need no separate rewrite.
    /// </remarks>
    public partial class ProviderNativeThreadIdentity : Migration
    {
        private static readonly (string Table, string Column)[] ThreadIdentityColumns =
        [
            ("review_pr_scan_threads", "thread_id"),
            ("thread_memory_records", "thread_id"),
            ("thread_pass_handled_threads", "thread_id"),
            ("mention_reply_jobs", "thread_id"),
            ("posted_finding_records", "provider_thread_id"),
            ("memory_activity_log", "thread_id"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var (table, column) in ThreadIdentityColumns)
            {
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table}
                         ALTER COLUMN {column} TYPE character varying(256)
                         USING {column}::character varying(256);
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Only a value that was a number to begin with can go back, which on an Azure DevOps installation
            // is all of them. A row carrying another provider's identifier stops the rollback rather than
            // being silently discarded.
            foreach (var (table, column) in ThreadIdentityColumns)
            {
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table}
                         ALTER COLUMN {column} TYPE bigint
                         USING {column}::bigint;
                     """);
            }
        }
    }
}
