using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Replaces the list of finding types that a client does not post with a destination per finding type. Every
    ///     type a client cleared gets the destination reviewOnly, so its findings stay off the pull request as before.
    ///     Every client gets the default destination summary for requests for missing tests and documentation, except a
    ///     client that cleared the type documentation-tests: the key with the qualifier takes precedence over the key of
    ///     the type, and the default would list findings that client keeps off the pull request. The downgrade writes
    ///     the types whose key alone has the destination reviewOnly back into the list.
    /// </summary>
    public partial class ReplaceWithheldFindingTypesWithFindingTypeDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "finding_type_destinations",
                table: "clients",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"documentation-tests:missing\":\"summary\"}'::jsonb");

            // The column default gives every client the default map. A client with cleared types gets one reviewOnly
            // entry per type, and the default entry only when documentation-tests is not among them.
            migrationBuilder.Sql(
                """
                UPDATE clients
                SET finding_type_destinations =
                    COALESCE(
                        (SELECT jsonb_object_agg(lower(btrim(cleared.type)), 'reviewOnly')
                         FROM unnest(string_to_array(withheld_finding_types, ',')) AS cleared(type)
                         WHERE btrim(cleared.type) <> ''),
                        '{}'::jsonb)
                    || CASE
                           WHEN EXISTS (SELECT 1
                                        FROM unnest(string_to_array(withheld_finding_types, ',')) AS cleared(type)
                                        WHERE lower(btrim(cleared.type)) = 'documentation-tests')
                               THEN '{}'::jsonb
                           ELSE '{"documentation-tests:missing":"summary"}'::jsonb
                       END
                WHERE btrim(withheld_finding_types) <> '';
                """);

            migrationBuilder.DropColumn(
                name: "withheld_finding_types",
                table: "clients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "withheld_finding_types",
                table: "clients",
                type: "text",
                nullable: false,
                defaultValueSql: "''");

            migrationBuilder.Sql(
                """
                UPDATE clients
                SET withheld_finding_types = COALESCE(
                    (SELECT string_agg(entry.key, ',' ORDER BY entry.key COLLATE "C")
                     FROM jsonb_each_text(finding_type_destinations) AS entry
                     WHERE entry.value = 'reviewOnly'
                       AND position(':' IN entry.key) = 0),
                    '');
                """);

            migrationBuilder.DropColumn(
                name: "finding_type_destinations",
                table: "clients");
        }
    }
}
