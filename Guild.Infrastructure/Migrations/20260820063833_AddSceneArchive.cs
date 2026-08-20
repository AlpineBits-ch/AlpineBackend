using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_scene_states_guild_id",
                table: "scene_states");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "concluded_at",
                table: "scene_states",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "folder_id",
                table: "scene_states",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "scene_folders",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    parent_folder_id = table.Column<string>(type: "text", nullable: true),
                    icon = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scene_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_scene_folders_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scene_folders_scene_folders_parent_folder_id",
                        column: x => x.parent_folder_id,
                        principalTable: "scene_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "scene_tags",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    emoji_id = table.Column<string>(type: "text", nullable: true),
                    emoji_name = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    moderated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scene_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_scene_tags_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scene_tag_assignments",
                columns: table => new
                {
                    scene_channel_id = table.Column<string>(type: "text", nullable: false),
                    tag_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scene_tag_assignments", x => new { x.scene_channel_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_scene_tag_assignments_channels_scene_channel_id",
                        column: x => x.scene_channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scene_tag_assignments_scene_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "scene_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scene_states_folder_id",
                table: "scene_states",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_scene_states_guild_id_folder_id",
                table: "scene_states",
                columns: new[] { "guild_id", "folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scene_folders_guild_id_position",
                table: "scene_folders",
                columns: new[] { "guild_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_scene_folders_parent_folder_id",
                table: "scene_folders",
                column: "parent_folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_scene_tag_assignments_tag_id",
                table: "scene_tag_assignments",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_scene_tags_guild_id_name",
                table: "scene_tags",
                columns: new[] { "guild_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scene_tags_guild_id_position",
                table: "scene_tags",
                columns: new[] { "guild_id", "position" });

            migrationBuilder.AddForeignKey(
                name: "fk_scene_states_scene_folders_folder_id",
                table: "scene_states",
                column: "folder_id",
                principalTable: "scene_folders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_scene_states_scene_folders_folder_id",
                table: "scene_states");

            migrationBuilder.DropTable(
                name: "scene_folders");

            migrationBuilder.DropTable(
                name: "scene_tag_assignments");

            migrationBuilder.DropTable(
                name: "scene_tags");

            migrationBuilder.DropIndex(
                name: "ix_scene_states_folder_id",
                table: "scene_states");

            migrationBuilder.DropIndex(
                name: "ix_scene_states_guild_id_folder_id",
                table: "scene_states");

            migrationBuilder.DropColumn(
                name: "concluded_at",
                table: "scene_states");

            migrationBuilder.DropColumn(
                name: "folder_id",
                table: "scene_states");

            migrationBuilder.CreateIndex(
                name: "ix_scene_states_guild_id",
                table: "scene_states",
                column: "guild_id");
        }
    }
}
