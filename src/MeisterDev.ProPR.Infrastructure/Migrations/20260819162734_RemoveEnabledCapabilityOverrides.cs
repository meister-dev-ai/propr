using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Removes the capability overrides that carried the enable state.
    /// </summary>
    /// <remarks>
    ///     An override can only take a capability away: what an installation is entitled to comes from the
    ///     license it has activated. An earlier build let an administrator store an override that switched a
    ///     capability on, and rows written then still carry a state the product no longer defines. Capability
    ///     resolution already ignores that state, so deleting the rows changes nothing an installation can do
    ///     and only removes stored values nothing reads. Re-running the delete removes nothing further.
    /// </remarks>
    public partial class RemoveEnabledCapabilityOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 was the enable state; 0 leaves the capability to the license and 2 disables it.
            migrationBuilder.Sql(
                """
                DELETE FROM premium_capability_overrides
                WHERE override_state = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The deleted rows carried a state no build defines, so there is nothing to restore.
        }
    }
}
