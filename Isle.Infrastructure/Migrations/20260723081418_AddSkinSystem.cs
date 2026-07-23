using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSkinSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skins",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    species = table.Column<string>(type: "text", nullable: false),
                    customizer_body_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_body_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_body_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_body_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_markings_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_markings_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_markings_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_markings_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_flank_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_flank_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_flank_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_flank_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_underbelly_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_underbelly_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_underbelly_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_underbelly_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_detail1color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_detail1color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_detail1color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_detail1color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_eyes_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_eyes_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_eyes_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_eyes_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_male_display_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_male_display_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_male_display_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_male_display_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_teeth_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_teeth_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_teeth_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_teeth_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_mouth_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_mouth_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_mouth_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_mouth_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_claws_color_r = table.Column<double>(type: "double precision", nullable: true),
                    customizer_claws_color_g = table.Column<double>(type: "double precision", nullable: true),
                    customizer_claws_color_b = table.Column<double>(type: "double precision", nullable: true),
                    customizer_claws_color_a = table.Column<double>(type: "double precision", nullable: true),
                    customizer_skin_variation = table.Column<int>(type: "integer", nullable: true),
                    customizer_pattern_index = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skins", x => x.id);
                    table.ForeignKey(
                        name: "fk_skins_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_skins_player_id",
                table: "skins",
                column: "player_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skins");
        }
    }
}
