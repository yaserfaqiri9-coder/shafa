using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DailyCashWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE Invoices DROP CHECK CK_Invoice_NotOverpaid, ADD CONSTRAINT CK_Invoice_NotOverpaid CHECK (`Refunded` <= `Paid` AND `Paid` - `Refunded` + `Discount` <= `Total`)");
            migrationBuilder.AddColumn<long>(
                name: "ReplacementPaymentId",
                table: "Payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Payments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CloseNote",
                table: "CashSessions",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CashMovements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    CashSessionId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashMovements_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_CashSessionId",
                table: "CashMovements",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_CreatedAt",
                table: "CashMovements",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_Kind_SourceType_SourceId",
                table: "CashMovements",
                columns: new[] { "Kind", "SourceType", "SourceId" },
                unique: true);

            // Explicit financial reconciliation, append-only. Never rewrite original receipts, journals, closed drawers or Sync state.
            migrationBuilder.Sql("INSERT IGNORE INTO LedgerAccounts (Code, Name) VALUES ('1010', 'بانک و حواله')");
            migrationBuilder.Sql("""
                INSERT INTO LedgerTransactions (Kind,SourceType,SourceId,Description,CreatedByUserId,CreatedAt)
                SELECT 'LegacyRefundReceivable','Invoice',Id,'Daily cash upgrade: reopen refunded receivable',IssuedByUserId,UTC_TIMESTAMP(6)
                FROM Invoices WHERE Status <> 'Void' AND Refunded > 0;
                """);
            migrationBuilder.Sql("""
                INSERT INTO LedgerEntries (TransactionId,AccountCode,Debit,Credit)
                SELECT t.Id,'1100',i.Refunded,0 FROM LedgerTransactions t JOIN Invoices i ON i.Id=t.SourceId WHERE t.Kind='LegacyRefundReceivable'
                UNION ALL
                SELECT t.Id,'4950',0,i.Refunded FROM LedgerTransactions t JOIN Invoices i ON i.Id=t.SourceId WHERE t.Kind='LegacyRefundReceivable';
                """);
            migrationBuilder.Sql("""
                UPDATE Invoices SET Status=CASE WHEN Total-Discount-Paid+Refunded<=0 THEN 'Paid' WHEN Paid-Refunded<=0 THEN 'Issued' ELSE 'PartiallyPaid' END,
                    Version=Version+1 WHERE Status <> 'Void' AND Refunded > 0;
                INSERT INTO LedgerTransactions (Kind,SourceType,SourceId,Description,CreatedByUserId,CreatedAt)
                SELECT 'LegacyNoncash','Payment',Id,'Daily cash upgrade: separate non-cash receipts',ReceivedByUserId,UTC_TIMESTAMP(6) FROM Payments WHERE Method='Other';
                INSERT INTO LedgerEntries (TransactionId,AccountCode,Debit,Credit)
                SELECT t.Id,'1010',p.Amount,0 FROM LedgerTransactions t JOIN Payments p ON p.Id=t.SourceId WHERE t.Kind='LegacyNoncash'
                UNION ALL
                SELECT t.Id,'1000',0,p.Amount FROM LedgerTransactions t JOIN Payments p ON p.Id=t.SourceId WHERE t.Kind='LegacyNoncash';
                INSERT INTO LedgerTransactions (Kind,SourceType,SourceId,Description,CreatedByUserId,CreatedAt)
                SELECT 'LegacyNoncashRefund','Refund',r.Id,'Daily cash upgrade: separate non-cash refunds',r.ApprovedByUserId,UTC_TIMESTAMP(6)
                FROM Refunds r JOIN Payments p ON p.Id=r.PaymentId WHERE p.Method='Other';
                INSERT INTO LedgerEntries (TransactionId,AccountCode,Debit,Credit)
                SELECT t.Id,'1000',r.Amount,0 FROM LedgerTransactions t JOIN Refunds r ON r.Id=t.SourceId WHERE t.Kind='LegacyNoncashRefund'
                UNION ALL
                SELECT t.Id,'1010',0,r.Amount FROM LedgerTransactions t JOIN Refunds r ON r.Id=t.SourceId WHERE t.Kind='LegacyNoncashRefund';
                """);
            // Old cash-out records have no proven drawer. Preserve their known time/source and label them unallocated.
            migrationBuilder.Sql("""
                INSERT INTO CashMovements (CashSessionId,Kind,SourceType,SourceId,Amount,CreatedByUserId,CreatedAt)
                SELECT NULL,t.Kind,t.SourceType,t.SourceId,SUM(e.Debit-e.Credit),t.CreatedByUserId,t.CreatedAt
                FROM LedgerTransactions t JOIN LedgerEntries e ON e.TransactionId=t.Id
                WHERE e.AccountCode='1000' AND t.Kind IN ('Refund','Expense','ExpenseReversal')
                    AND (t.Kind<>'Refund' OR EXISTS(SELECT 1 FROM Refunds r JOIN Payments p ON p.Id=r.PaymentId WHERE r.Id=t.SourceId AND p.Method='Cash'))
                GROUP BY t.Id,t.Kind,t.SourceType,t.SourceId,t.CreatedByUserId,t.CreatedAt;
                """);
            migrationBuilder.Sql("""
                INSERT INTO SyncChanges (TableName,KeyJson,CreatedAt)
                SELECT 'LedgerTransactions',JSON_ARRAY(Id),UTC_TIMESTAMP(6) FROM LedgerTransactions WHERE Kind IN ('LegacyRefundReceivable','LegacyNoncash','LegacyNoncashRefund');
                INSERT INTO SyncChanges (TableName,KeyJson,CreatedAt)
                SELECT 'LedgerEntries',JSON_ARRAY(e.Id),UTC_TIMESTAMP(6) FROM LedgerEntries e JOIN LedgerTransactions t ON e.TransactionId=t.Id WHERE t.Kind IN ('LegacyRefundReceivable','LegacyNoncash','LegacyNoncashRefund');
                INSERT INTO SyncChanges (TableName,KeyJson,CreatedAt)
                SELECT 'Invoices',JSON_ARRAY(Id),UTC_TIMESTAMP(6) FROM Invoices WHERE Status<>'Void' AND Refunded>0;
                INSERT INTO SyncChanges (TableName,KeyJson,CreatedAt)
                SELECT 'CashMovements',JSON_ARRAY(Id),UTC_TIMESTAMP(6) FROM CashMovements;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Financial history cannot be downgraded. Restore the verified pre-upgrade backup.");
    }
}
