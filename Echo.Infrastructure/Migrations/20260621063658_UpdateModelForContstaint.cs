using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModelForContstaint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_single_row_enforcer",
                table: "echo_configurations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_single_row_enforcer",
                table: "echo_configurations",
                sql: "enforced_singleton = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_single_row_enforcer",
                table: "echo_configurations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_single_row_enforcer",
                table: "echo_configurations",
                sql: "[enforced_singleton] = 1");
        }
    }
}
