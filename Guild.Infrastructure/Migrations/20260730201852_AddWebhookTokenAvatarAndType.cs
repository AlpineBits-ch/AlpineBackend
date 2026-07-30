using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookTokenAvatarAndType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_url",
                table: "webhook_configs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "token",
                table: "webhook_configs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "webhook_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every pre-existing webhook lands on the column default of "" above, and an empty
            // token is not merely useless - it would be a webhook whose credential check compares
            // "" against "", so any caller who guessed the id could post to the channel.
            migrationBuilder.Sql("""
                UPDATE webhook_configs
                SET token = replace(gen_random_uuid()::text, '-', '') || replace(gen_random_uuid()::text, '-', '')
                WHERE token = '' OR token IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avatar_url",
                table: "webhook_configs");

            migrationBuilder.DropColumn(
                name: "token",
                table: "webhook_configs");

            migrationBuilder.DropColumn(
                name: "type",
                table: "webhook_configs");
        }
    }
}
