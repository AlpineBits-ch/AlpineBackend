using System;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
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
                .Annotation("Npgsql:Enum:role_type", "everyone,none");

            migrationBuilder.CreateTable(
                name: "guilds",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<string>(type: "text", nullable: false),
                    encryption_state = table.Column<EncryptionState>(type: "encryption_state", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guilds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_categories_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_invites",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<InviteType>(type: "invite_type", nullable: false),
                    state = table.Column<InviteState>(type: "invite_state", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_invites", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_invites_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<RoleType>(type: "role_type", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_roles_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channels",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<ChannelType>(type: "channel_type", nullable: false),
                    category_id = table.Column<string>(type: "text", nullable: true),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    is_age_restricted = table.Column<bool>(type: "boolean", nullable: false),
                    is_private = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    encryption_state = table.Column<EncryptionState>(type: "encryption_state", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_channels_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_channels_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_members",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<MemberType>(type: "member_type", nullable: false),
                    invite_id = table.Column<string>(type: "text", nullable: true),
                    allow_permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    deny_permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_members_guild_invites_invite_id",
                        column: x => x.invite_id,
                        principalTable: "guild_invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_guild_members_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "public_keys",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    guild_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_public_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_public_keys_channels_guild_id",
                        column: x => x.guild_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_public_keys_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channel_permissions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    member_id = table.Column<string>(type: "text", nullable: true),
                    category_id = table.Column<string>(type: "text", nullable: true),
                    allow_permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    deny_permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_permissions_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_channel_permissions_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_channel_permissions_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_channel_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_members",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: false),
                    member_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_members_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_members_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_guild_id",
                table: "categories",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_category_id",
                table: "channel_permissions",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_channel_id",
                table: "channel_permissions",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_member_id",
                table: "channel_permissions",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_role_id",
                table: "channel_permissions",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_channels_category_id",
                table: "channels",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_channels_guild_id",
                table: "channels",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_invites_guild_id",
                table: "guild_invites",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_members_guild_id",
                table: "guild_members",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_members_invite_id",
                table: "guild_members",
                column: "invite_id");

            migrationBuilder.CreateIndex(
                name: "ix_public_keys_guild_id",
                table: "public_keys",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_members_member_id",
                table: "role_members",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_members_role_id",
                table: "role_members",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_guild_id",
                table: "roles",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_permissions");

            migrationBuilder.DropTable(
                name: "public_keys");

            migrationBuilder.DropTable(
                name: "role_members");

            migrationBuilder.DropTable(
                name: "channels");

            migrationBuilder.DropTable(
                name: "guild_members");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "guild_invites");

            migrationBuilder.DropTable(
                name: "guilds");
        }
    }
}
