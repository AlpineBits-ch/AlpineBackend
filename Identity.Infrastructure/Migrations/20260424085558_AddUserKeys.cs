using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "master_key_hash",
                table: "AspNetUsers",
                newName: "encrypted_master_key");

            migrationBuilder.CreateTable(
                name: "user_keys",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_keys_application_user_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_keys_user_id",
                table: "user_keys",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_keys");

            migrationBuilder.RenameColumn(
                name: "encrypted_master_key",
                table: "AspNetUsers",
                newName: "master_key_hash");
        }
    }
}
