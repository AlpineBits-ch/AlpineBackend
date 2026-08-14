using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModulePermissionsAndRoleMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bot_user_id",
                table: "roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hoist",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "icon_url",
                table: "roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "integration_id",
                table: "roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_managed",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "mentionable",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "module_permissions",
                table: "roles",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "unicode_emoji",
                table: "roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "allow_module_permissions",
                table: "guild_members",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "deny_module_permissions",
                table: "guild_members",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "allow_module_permissions",
                table: "channel_permissions",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "deny_module_permissions",
                table: "channel_permissions",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bot_user_id",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "hoist",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "icon_url",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "integration_id",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "is_managed",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "mentionable",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "module_permissions",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "unicode_emoji",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "allow_module_permissions",
                table: "guild_members");

            migrationBuilder.DropColumn(
                name: "deny_module_permissions",
                table: "guild_members");

            migrationBuilder.DropColumn(
                name: "allow_module_permissions",
                table: "channel_permissions");

            migrationBuilder.DropColumn(
                name: "deny_module_permissions",
                table: "channel_permissions");
        }
    }
}
