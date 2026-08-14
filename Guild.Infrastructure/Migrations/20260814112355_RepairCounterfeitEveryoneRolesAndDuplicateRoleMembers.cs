using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>
    /// Makes the existing rows satisfy the two uniqueness rules the model is about to declare: one
    /// @everyone role per guild, and one <c>role_members</c> row per (role, member).
    /// </summary>
    public partial class RepairCounterfeitEveryoneRolesAndDuplicateRoleMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RoleUniquenessRepair.DemoteCounterfeitEveryoneRolesSql);
            migrationBuilder.Sql(RoleUniquenessRepair.DeduplicateRoleMembersSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
