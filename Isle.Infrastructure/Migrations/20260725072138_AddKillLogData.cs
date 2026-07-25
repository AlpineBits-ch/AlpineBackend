using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKillLogData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kill_logs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    killer_id = table.Column<string>(type: "text", nullable: true),
                    victim_id = table.Column<string>(type: "text", nullable: true),
                    victim_weight_kg = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kill_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_kill_logs_players_killer_id",
                        column: x => x.killer_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_kill_logs_players_victim_id",
                        column: x => x.victim_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kill_logs_killer_id",
                table: "kill_logs",
                column: "killer_id");

            migrationBuilder.CreateIndex(
                name: "ix_kill_logs_victim_id",
                table: "kill_logs",
                column: "victim_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kill_logs");
        }
    }
}
