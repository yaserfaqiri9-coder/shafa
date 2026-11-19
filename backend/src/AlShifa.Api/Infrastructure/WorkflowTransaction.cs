using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlShifa.Api.Infrastructure;

/// <summary>Ordinary requests own their transaction. A queued command owns an outer transaction;
/// existing workflows then use savepoints so their commit cannot escape the command receipt.</summary>
public sealed class WorkflowTransaction(IDbContextTransaction transaction, string? savepoint) : IAsyncDisposable
{
    private bool finished;

    public static async Task<WorkflowTransaction> BeginAsync(AppDbContext db, CancellationToken ct)
    {
        if (db.CommandExecution && db.Database.CurrentTransaction is { } outer)
        {
            var name = "workflow_" + Guid.NewGuid().ToString("N");
            await outer.CreateSavepointAsync(name, ct);
            return new(outer, name);
        }
        return new(await db.Database.BeginTransactionAsync(ct), null);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (savepoint is null) await transaction.CommitAsync(ct);
        else await transaction.ReleaseSavepointAsync(savepoint, ct);
        finished = true;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (savepoint is null) await transaction.RollbackAsync(ct);
        else await transaction.RollbackToSavepointAsync(savepoint, ct);
        finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (savepoint is null) await transaction.DisposeAsync();
        else if (!finished) await transaction.RollbackToSavepointAsync(savepoint);
    }
}

public static class WorkflowTransactions
{
    public static Task<WorkflowTransaction> BeginWorkflowTransactionAsync(this AppDbContext db, CancellationToken ct = default) =>
        WorkflowTransaction.BeginAsync(db, ct);
}
