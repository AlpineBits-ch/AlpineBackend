using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildEmoji : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,role_created,role_deleted,role_positions_changed,role_updated")
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_permission_changed,channel_updated,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,role_created,role_deleted,role_positions_changed,role_updated")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");

            migrationBuilder.CreateTable(
                name: "guild_emojis",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    animated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_emojis", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_emojis_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_emojis_guild_id_name",
                table: "guild_emojis",
                columns: new[] { "guild_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_emojis");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_permission_changed,channel_updated,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,role_created,role_deleted,role_positions_changed,role_updated")
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,role_created,role_deleted,role_positions_changed,role_updated")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");
        }
    }
}
