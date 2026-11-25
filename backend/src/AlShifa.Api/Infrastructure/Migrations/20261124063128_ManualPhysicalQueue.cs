using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ManualPhysicalQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QueueAttendances",
                columns: table => new
                {
                    Day = table.Column<DateTime>(type: "date", nullable: false),
                    Desk = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    EntryKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Present = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueAttendances", x => new { x.Day, x.Desk, x.EntryKey });
                })
                .Annotation("MySQL:Charset", "utf8mb4");



            migrationBuilder.CreateTable(
                name: "QueueCards",
                columns: table => new
                {
                    Day = table.Column<DateTime>(type: "date", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueCards", x => new { x.Day, x.Number });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "QueueDeskLanes",
                columns: table => new
                {
                    Day = table.Column<DateTime>(type: "date", nullable: false),
                    Desk = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    LaneId = table.Column<long>(type: "bigint", nullable: false),
                    EntryKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueDeskLanes", x => new { x.Day, x.Desk, x.LaneId });
                })
                .Annotation("MySQL:Charset", "utf8mb4");
            // Historical shifts/rounds may share a number. Reserve the pair once
            // without changing any visit, token, payment, or previous call.
            migrationBuilder.Sql("""
                INSERT INTO QueueCards (Day, Number)
                SELECT VisitDate, QueueNumber FROM Visits WHERE QueueNumber IS NOT NULL
                UNION
                SELECT TokenDate, QueueNumber FROM VisitTokens WHERE QueueNumber IS NOT NULL;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueueAttendances");

            migrationBuilder.DropTable(
                name: "QueueCards");

            migrationBuilder.DropTable(
                name: "QueueDeskLanes");
        }
    }
}
