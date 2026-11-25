using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OperationsAndStaffPayroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OperationDefinitionId",
                table: "SurgeryCases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmploymentEndDate",
                table: "Staff",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmploymentStartDate",
                table: "Staff",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StaffId",
                table: "Expenses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaffPaymentKind",
                table: "Expenses",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OperationDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    ServiceId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationDefinitions_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "StaffSalaryTerms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    StaffId = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "date", nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffSalaryTerms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffSalaryTerms_Staff_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SurgerySupplyPlans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    SurgeryCaseId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurgerySupplyPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurgerySupplyPlans_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurgerySupplyPlans_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurgerySupplyPlans_SurgeryCases_SurgeryCaseId",
                        column: x => x.SurgeryCaseId,
                        principalTable: "SurgeryCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OperationPackages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    OperationDefinitionId = table.Column<int>(type: "int", nullable: false),
                    PackageId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationPackages_OperationDefinitions_OperationDefinitionId",
                        column: x => x.OperationDefinitionId,
                        principalTable: "OperationDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationPackages_SurgeryPackages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "SurgeryPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_OperationDefinitionId",
                table: "SurgeryCases",
                column: "OperationDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_StaffId",
                table: "Expenses",
                column: "StaffId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationDefinitions_ServiceId",
                table: "OperationDefinitions",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationPackages_OperationDefinitionId_PackageId",
                table: "OperationPackages",
                columns: new[] { "OperationDefinitionId", "PackageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationPackages_PackageId",
                table: "OperationPackages",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffSalaryTerms_StaffId_EffectiveFrom",
                table: "StaffSalaryTerms",
                columns: new[] { "StaffId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurgerySupplyPlans_LocationId",
                table: "SurgerySupplyPlans",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgerySupplyPlans_ProductId",
                table: "SurgerySupplyPlans",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgerySupplyPlans_SurgeryCaseId",
                table: "SurgerySupplyPlans",
                column: "SurgeryCaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_Staff_StaffId",
                table: "Expenses",
                column: "StaffId",
                principalTable: "Staff",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SurgeryCases_OperationDefinitions_OperationDefinitionId",
                table: "SurgeryCases",
                column: "OperationDefinitionId",
                principalTable: "OperationDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Staff_StaffId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_SurgeryCases_OperationDefinitions_OperationDefinitionId",
                table: "SurgeryCases");

            migrationBuilder.DropTable(
                name: "OperationPackages");

            migrationBuilder.DropTable(
                name: "StaffSalaryTerms");

            migrationBuilder.DropTable(
                name: "SurgerySupplyPlans");

            migrationBuilder.DropTable(
                name: "OperationDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_SurgeryCases_OperationDefinitionId",
                table: "SurgeryCases");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_StaffId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "OperationDefinitionId",
                table: "SurgeryCases");

            migrationBuilder.DropColumn(
                name: "EmploymentEndDate",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "EmploymentStartDate",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "StaffId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "StaffPaymentKind",
                table: "Expenses");
        }
    }
}
