using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AccountCode_TransactionId_Debit_Credit",
                table: "LedgerEntries",
                columns: new[] { "AccountCode", "TransactionId", "Debit", "Credit" });

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_AccountCode",
                table: "LedgerEntries");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItem_BatchId_SaleId_Quantity",
                table: "SaleItem",
                columns: new[] { "BatchId", "SaleId", "Quantity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SaleItem_BatchId_SaleId_Quantity",
                table: "SaleItem");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AccountCode",
                table: "LedgerEntries",
                column: "AccountCode");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_AccountCode_TransactionId_Debit_Credit",
                table: "LedgerEntries");
        }
    }
}
