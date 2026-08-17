using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Carries an installation's mention-answering entitlement onto the capability that now gates it.
    /// </summary>
    /// <remarks>
    ///     Mention scanning used to be gated on the crawl-configuration capability, from when it read crawl
    ///     configurations to decide what to scan. An installation that had explicitly turned that capability
    ///     off got no answers, and would start getting them the moment the gate moved, because the new
    ///     capability has no override of its own and falls back to its default. Copying the override across
    ///     keeps every installation on the entitlement it already had. An installation that never set one is
    ///     left alone: its default was to answer, and the new capability's default is the same.
    /// </remarks>
    public partial class CarryMentionAnsweringEntitlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO premium_capability_overrides
                    (capability_key, override_state, updated_at, updated_by_user_id)
                SELECT 'mention-answering', override_state, updated_at, updated_by_user_id
                FROM premium_capability_overrides
                WHERE capability_key = 'crawl-configs'
                ON CONFLICT (capability_key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM premium_capability_overrides
                WHERE capability_key = 'mention-answering';
                """);
        }
    }
}
