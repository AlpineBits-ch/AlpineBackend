using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendlyIdToPlayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "player_friendly_id_seq",
                startValue: 100000L);

            migrationBuilder.AddColumn<int>(
                name: "friendly_id_seq",
                table: "players",
                type: "integer",
                nullable: false,
                defaultValueSql: "nextval('player_friendly_id_seq')");

            migrationBuilder.AddColumn<string>(
                name: "in_game_name",
                table: "players",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "friendly_id_seq",
                table: "players");

            migrationBuilder.DropColumn(
                name: "in_game_name",
                table: "players");

            migrationBuilder.DropSequence(
                name: "player_friendly_id_seq");
        }
    }
}
