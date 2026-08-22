using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Discovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialDiscoverySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_topics",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    game_application_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    aliases = table.Column<string[]>(type: "text[]", nullable: false),
                    steam_app_id = table.Column<string>(type: "text", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_topics", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "guild_profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    icon_url = table.Column<string>(type: "text", nullable: true),
                    banner_url = table.Column<string>(type: "text", nullable: true),
                    member_count = table.Column<int>(type: "integer", nullable: false),
                    active_member_count = table.Column<int>(type: "integer", nullable: false),
                    features = table.Column<string>(type: "text", nullable: false),
                    projected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interest_visibilities",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    visible = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interest_visibilities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "listings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    headline = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    pitch = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    join_policy = table.Column<string>(type: "text", nullable: false),
                    links = table.Column<List<string>>(type: "text[]", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    suspended_reason = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_bumped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_listings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    alias_of = table.Column<string>(type: "text", nullable: true),
                    usage_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_interests",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    topic_id = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_interests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "listing_topics",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    listing_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    topic_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_listing_topics", x => x.id);
                    table.ForeignKey(
                        name: "fk_listing_topics_listings_listing_id",
                        column: x => x.listing_id,
                        principalTable: "listings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_topics_game_application_id",
                table: "game_topics",
                column: "game_application_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guild_profiles_guild_id",
                table: "guild_profiles",
                column: "guild_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_interest_visibilities_user_id",
                table: "interest_visibilities",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listing_topics_kind_topic_id",
                table: "listing_topics",
                columns: new[] { "kind", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_listing_topics_listing_id_kind_topic_id",
                table: "listing_topics",
                columns: new[] { "listing_id", "kind", "topic_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listings_guild_id",
                table: "listings",
                column: "guild_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listings_state_last_bumped_at",
                table: "listings",
                columns: new[] { "state", "last_bumped_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tags_slug",
                table: "tags",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_interests_user_id",
                table: "user_interests",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_interests_user_id_kind_topic_id",
                table: "user_interests",
                columns: new[] { "user_id", "kind", "topic_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_topics");

            migrationBuilder.DropTable(
                name: "guild_profiles");

            migrationBuilder.DropTable(
                name: "interest_visibilities");

            migrationBuilder.DropTable(
                name: "listing_topics");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "user_interests");

            migrationBuilder.DropTable(
                name: "listings");
        }
    }
}
