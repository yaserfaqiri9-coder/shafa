using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261120153000_QueueShiftNumbering")]
public partial class QueueShiftNumbering : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Visits_VisitDate_DepartmentId_QueueNumber", table: "Visits");
        migrationBuilder.DropIndex(name: "IX_VisitTokens_TokenDate_QueueNumber", table: "VisitTokens");

        migrationBuilder.AlterColumn<string>(
            name: "Period",
            table: "NumberSequences",
            type: "varchar(20)",
            maxLength: 20,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "varchar(10)",
            oldMaxLength: 10);

        migrationBuilder.Sql("UPDATE NumberSequences SET Period = CONCAT(Period, '-Morning') WHERE Scope = 'queue' AND CHAR_LENGTH(Period) = 8");

        migrationBuilder.AddColumn<string>(
            name: "QueueShift",
            table: "Visits",
            type: "varchar(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Morning");

        migrationBuilder.AddColumn<string>(
            name: "QueueShift",
            table: "VisitTokens",
            type: "varchar(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Morning");

        migrationBuilder.AddColumn<string>(
            name: "QueueShift",
            table: "PatientCalls",
            type: "varchar(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Morning");

        migrationBuilder.CreateIndex(
            name: "IX_Visits_VisitDate_QueueShift_DepartmentId_QueueNumber",
            table: "Visits",
            columns: new[] { "VisitDate", "QueueShift", "DepartmentId", "QueueNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_VisitTokens_TokenDate_QueueShift_QueueNumber",
            table: "VisitTokens",
            columns: new[] { "TokenDate", "QueueShift", "QueueNumber" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Queue numbering can contain duplicate numbers across shifts; rollback needs an explicit data reconciliation.");
}
