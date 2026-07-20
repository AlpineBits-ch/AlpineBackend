using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.CreateTable(
                name: "players",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    xp = table.Column<long>(type: "bigint", nullable: false),
                    steam_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_players", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "storages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    max_slot_count = table.Column<int>(type: "integer", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storages", x => x.id);
                    table.ForeignKey(
                        name: "fk_storages_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "storage_slots",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    storage_id = table.Column<string>(type: "text", nullable: false),
                    species = table.Column<string>(type: "text", nullable: false),
                    health_data_hunger = table.Column<long>(type: "bigint", nullable: false),
                    health_data_health = table.Column<long>(type: "bigint", nullable: false),
                    health_data_thirst = table.Column<long>(type: "bigint", nullable: false),
                    health_data_stamina = table.Column<long>(type: "bigint", nullable: false),
                    mutations_slots = table.Column<Dictionary<string, string>>(type: "hstore", nullable: true),
                    mutations_elder_stacks = table.Column<int>(type: "integer", nullable: false),
                    mutations_unlocks = table.Column<List<string>>(type: "text[]", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_slots", x => x.id);
                    table.ForeignKey(
                        name: "fk_storage_slots_storages_storage_id",
                        column: x => x.storage_id,
                        principalTable: "storages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_storage_slots_storage_id",
                table: "storage_slots",
                column: "storage_id");

            migrationBuilder.CreateIndex(
                name: "ix_storages_player_id",
                table: "storages",
                column: "player_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_slots");

            migrationBuilder.DropTable(
                name: "storages");

            migrationBuilder.DropTable(
                name: "players");
        }
    }
}
