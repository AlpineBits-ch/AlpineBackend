using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the <c>web_push</c> member of <c>push_token_kind</c>, plus the two key columns a
    /// browser subscription needs (<c>p256dh</c>, <c>auth</c>).
    /// </summary>
    public partial class AddWebPushSubscriptions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TYPE push_token_kind ADD VALUE IF NOT EXISTS 'web_push';",
                suppressTransaction: true);

            migrationBuilder.AddColumn<string>(
                name: "auth",
                table: "user_push_tokens",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "p256dh",
                table: "user_push_tokens",
                type: "text",
                nullable: true);
        }

        /// <summary>Drops the two columns, and deletes the rows that needed them first.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM user_push_tokens WHERE kind = 'web_push';");

            migrationBuilder.DropColumn(
                name: "auth",
                table: "user_push_tokens");

            migrationBuilder.DropColumn(
                name: "p256dh",
                table: "user_push_tokens");
        }
    }
}
