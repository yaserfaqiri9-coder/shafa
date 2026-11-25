using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261121120000_UserChat")]
public sealed class UserChat : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ChatMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                SenderUserId = table.Column<long>(type: "bigint", nullable: false),
                RecipientUserId = table.Column<long>(type: "bigint", nullable: true),
                ClientMessageId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Content = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatMessages", x => x.Id);
                table.ForeignKey("FK_ChatMessages_AspNetUsers_RecipientUserId", x => x.RecipientUserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ChatMessages_AspNetUsers_SenderUserId", x => x.SenderUserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_ChatMessages_RecipientUserId_SenderUserId_Id", "ChatMessages",
            new[] { "RecipientUserId", "SenderUserId", "Id" });
        migrationBuilder.CreateIndex("IX_ChatMessages_SenderUserId_RecipientUserId_Id", "ChatMessages",
            new[] { "SenderUserId", "RecipientUserId", "Id" });
        migrationBuilder.CreateIndex("IX_ChatMessages_SenderUserId_ClientMessageId", "ChatMessages",
            new[] { "SenderUserId", "ClientMessageId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ChatMessages");
}
