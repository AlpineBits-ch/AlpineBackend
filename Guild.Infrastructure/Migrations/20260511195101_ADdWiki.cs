using System;
using System.Collections.Generic;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ADdWiki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "admin,default,moderator,owner")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "admin,default,moderator,owner")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none");

            migrationBuilder.CreateTable(
                name: "wiki_categories",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wiki_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_wiki_categories_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wiki_pages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    author_id = table.Column<string>(type: "text", nullable: false),
                    last_editor_id = table.Column<string>(type: "text", nullable: true),
                    parent_page_id = table.Column<string>(type: "text", nullable: true),
                    category_id = table.Column<string>(type: "text", nullable: true),
                    visibility = table.Column<WikiVisibility>(type: "wiki_visibility", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wiki_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_wiki_pages_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wikis",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wikis", x => x.id);
                    table.ForeignKey(
                        name: "fk_wikis_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wiki_revisions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    page_id = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    editor_id = table.Column<string>(type: "text", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wiki_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_wiki_revisions_wiki_pages_page_id",
                        column: x => x.page_id,
                        principalTable: "wiki_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wiki_categories_guild_id",
                table: "wiki_categories",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_wiki_pages_guild_id",
                table: "wiki_pages",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_wiki_revisions_page_id",
                table: "wiki_revisions",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "ix_wikis_guild_id",
                table: "wikis",
                column: "guild_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wiki_categories");

            migrationBuilder.DropTable(
                name: "wiki_revisions");

            migrationBuilder.DropTable(
                name: "wikis");

            migrationBuilder.DropTable(
                name: "wiki_pages");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "admin,default,moderator,owner")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "admin,default,moderator,owner")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");
        }
    }
}
