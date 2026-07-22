using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInviteSystemMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friend_requests");

            migrationBuilder.CreateTable(
                name: "player_invites",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    sender_player_id = table.Column<string>(type: "text", nullable: false),
                    receiver_player_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_invites", x => x.id);
                    table.ForeignKey(
                        name: "fk_player_invites_players_receiver_player_id",
                        column: x => x.receiver_player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_invites_players_sender_player_id",
                        column: x => x.sender_player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_player_invites_receiver_player_id_status",
                table: "player_invites",
                columns: new[] { "receiver_player_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_player_invites_sender_player_id",
                table: "player_invites",
                column: "sender_player_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_invites");

            migrationBuilder.CreateTable(
                name: "friend_requests",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    receiver_player_id = table.Column<string>(type: "text", nullable: false),
                    sender_player_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_friend_requests_players_receiver_player_id",
                        column: x => x.receiver_player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_friend_requests_players_sender_player_id",
                        column: x => x.sender_player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_receiver_player_id_status",
                table: "friend_requests",
                columns: new[] { "receiver_player_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_sender_player_id",
                table: "friend_requests",
                column: "sender_player_id");
        }
    }
}
