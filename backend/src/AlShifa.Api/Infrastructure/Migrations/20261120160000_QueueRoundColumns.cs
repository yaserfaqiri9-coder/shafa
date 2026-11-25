using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261120160000_QueueRoundColumns")]
public sealed class QueueRoundColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Visits_VisitDate_QueueShift_DepartmentId_QueueNumber",
            table: "Visits");
        migrationBuilder.DropIndex(
            name: "IX_VisitTokens_TokenDate_QueueShift_QueueNumber",
            table: "VisitTokens");

        migrationBuilder.AddColumn<int>(
            name: "QueueRound",
            table: "Visits",
            type: "int",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddColumn<int>(
            name: "QueueRound",
            table: "VisitTokens",
            type: "int",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddColumn<int>(
            name: "QueueRound",
            table: "PatientCalls",
            type: "int",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.CreateIndex(
            name: "IX_Visits_QueueDay_Shift_Round_Department_Number",
            table: "Visits",
            columns: new[] { "VisitDate", "QueueShift", "QueueRound", "DepartmentId", "QueueNumber" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_VisitTokens_TokenDate_QueueShift_QueueRound_QueueNumber",
            table: "VisitTokens",
            columns: new[] { "TokenDate", "QueueShift", "QueueRound", "QueueNumber" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Queue rounds may contain distinct values; rollback requires restoring a verified backup.");
}
