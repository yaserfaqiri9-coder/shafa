using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BidirectionalCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncCommands",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Schema = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Method = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    Query = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    Body = table.Column<string>(type: "longtext", nullable: false),
                    BaseSeq = table.Column<long>(type: "bigint", nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    ResponseBody = table.Column<string>(type: "longtext", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCommands", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SyncCommands_StatusCode_CreatedAt",
                table: "SyncCommands",
                columns: new[] { "StatusCode", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncCommands_UserId_CreatedAt",
                table: "SyncCommands",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncCommands");
        }
    }
}
