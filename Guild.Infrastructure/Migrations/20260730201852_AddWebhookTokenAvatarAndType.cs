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
            // "" against "", so any caller who guessed the id could post to the channel. (The
            // route requires a non-empty {token} segment so it isn't reachable today, but leaving
            // rows one route change away from being open is not acceptable.)
            //
            // Generated in SQL rather than by rewriting rows from C#: two concatenated UUIDv4s is
            // ~244 bits of entropy from gen_random_uuid(), which is core Postgres 13+ and needs no
            // pgcrypto extension - self-hosters get no new install step. The shape differs from
            // WebhookConfig.GenerateToken()'s base64, which is fine: nothing parses a token, it is
            // only ever compared.
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
