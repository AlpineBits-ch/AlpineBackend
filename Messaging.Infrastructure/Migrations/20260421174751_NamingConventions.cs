using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NamingConventions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Members_Conversations_ConversationId",
                table: "Members");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Members",
                table: "Members");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Conversations",
                table: "Conversations");

            migrationBuilder.RenameTable(
                name: "Members",
                newName: "members");

            migrationBuilder.RenameTable(
                name: "Conversations",
                newName: "conversations");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "members",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "members",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "members",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "members",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "ConversationId",
                table: "members",
                newName: "conversation_id");

            migrationBuilder.RenameIndex(
                name: "IX_Members_ConversationId",
                table: "members",
                newName: "ix_members_conversation_id");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "conversations",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "conversations",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "conversations",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "conversations",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CorrelationId",
                table: "conversations",
                newName: "correlation_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_members",
                table: "members",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_conversations",
                table: "conversations",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_members_conversations_conversation_id",
                table: "members",
                column: "conversation_id",
                principalTable: "conversations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_members_conversations_conversation_id",
                table: "members");

            migrationBuilder.DropPrimaryKey(
                name: "pk_members",
                table: "members");

            migrationBuilder.DropPrimaryKey(
                name: "pk_conversations",
                table: "conversations");

            migrationBuilder.RenameTable(
                name: "members",
                newName: "Members");

            migrationBuilder.RenameTable(
                name: "conversations",
                newName: "Conversations");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "Members",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "Members",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "Members",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "Members",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "conversation_id",
                table: "Members",
                newName: "ConversationId");

            migrationBuilder.RenameIndex(
                name: "ix_members_conversation_id",
                table: "Members",
                newName: "IX_Members_ConversationId");

            migrationBuilder.RenameColumn(
                name: "name",
                table: "Conversations",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "Conversations",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "Conversations",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "Conversations",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "correlation_id",
                table: "Conversations",
                newName: "CorrelationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Members",
                table: "Members",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Conversations",
                table: "Conversations",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Members_Conversations_ConversationId",
                table: "Members",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
