using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedServerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "federated_server_id",
                table: "profiles",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "federated_server_id",
                table: "profiles");
        }
    }
}
