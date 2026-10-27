using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentsLedger.Api.Data;
using PaymentsLedger.Api.Domain;

namespace PaymentsLedger.Api.Services;

public sealed record OperationResult(LedgerTransaction Transaction, bool Replayed);

public sealed class LedgerService(LedgerDbContext db, TimeProvider clock)
{
    private const long MaxAmountMinor = 1_000_000_000_000;
    private const int MaxKeyLength = 255;

    public async Task<Account> CreateAccountAsync(string? owner, CancellationToken ct)
    {
        owner = owner?.Trim();
        if (string.IsNullOrEmpty(owner) || owner.Length > 200)
            throw new LedgerException(StatusCodes.Status400BadRequest, "Owner is required (max 200 characters).");

        var account = new Account { Owner = owner, CreatedAt = clock.GetUtcNow() };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> GetAccountAsync(Guid id, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && !a.IsSystem, ct)
        ?? throw NotFound(id);

    public async Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid accountId, int take, long? beforeId, CancellationToken ct)
    {
        await GetAccountAsync(accountId, ct); // 404 if missing
        take = Math.Clamp(take, 1, 200);

        return await db.Entries.AsNoTracking()
            .Where(e => e.AccountId == accountId && (beforeId == null || e.Id < beforeId))
            .OrderByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<OperationResult> DepositAsync(Guid accountId, long amount, string? key, CancellationToken ct)
    {
        ValidateAmount(amount);
        ValidateKey(key);
        var hash = Hash(string.Create(CultureInfo.InvariantCulture, $"deposit|{accountId}|{amount}"));

        return RunIdempotentAsync(key, hash, () => DepositCoreAsync(accountId, amount, key, hash, ct), ct);
    }

    public Task<OperationResult> TransferAsync(Guid fromId, Guid toId, long amount, string? key, CancellationToken ct)
    {
        ValidateAmount(amount);
        ValidateKey(key);
        if (fromId == toId)
            throw new LedgerException(StatusCodes.Status400BadRequest, "Cannot transfer to the same account.");
        var hash = Hash(string.Create(CultureInfo.InvariantCulture, $"transfer|{fromId}|{toId}|{amount}"));

        return RunIdempotentAsync(key, hash, () => TransferCoreAsync(fromId, toId, amount, key, hash, ct), ct);
    }

    private async Task<OperationResult> DepositCoreAsync(Guid accountId, long amount, string? key, string hash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var account = await LockAccountAsync(accountId, ct);

        // Re-check under the lock: a concurrent retry with the same key may have just committed.
        if (key is not null && await FindReplayAsync(key, hash, ct) is { } replay)
            return replay;

        var now = clock.GetUtcNow();
        account.Balance += amount;

        var txn = new LedgerTransaction
        {
            Type = TransactionType.Deposit,
            CreatedAt = now,
            IdempotencyKey = key,
            RequestHash = hash,
            Entries =
            {
                new LedgerEntry { AccountId = SystemAccount.Id, Amount = -amount, CreatedAt = now },
                new LedgerEntry { AccountId = account.Id, Amount = amount, BalanceAfter = account.Balance, CreatedAt = now }
            }
        };

        db.Transactions.Add(txn);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new OperationResult(txn, Replayed: false);
    }

    private async Task<OperationResult> TransferCoreAsync(Guid fromId, Guid toId, long amount, string? key, string hash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock both rows in a fixed order (ascending id). Two opposing transfers (A->B and B->A)
        // then queue up on the same first row instead of deadlocking.
        var accounts = new Dictionary<Guid, Account>();
        foreach (var id in new[] { fromId, toId }.Order())
            accounts[id] = await LockAccountAsync(id, ct);

        if (key is not null && await FindReplayAsync(key, hash, ct) is { } replay)
            return replay;

        var from = accounts[fromId];
        var to = accounts[toId];

        if (from.Balance < amount)
            throw new LedgerException(StatusCodes.Status422UnprocessableEntity, "Insufficient funds.");

        var now = clock.GetUtcNow();
        from.Balance -= amount;
        to.Balance += amount;

        var txn = new LedgerTransaction
        {
            Type = TransactionType.Transfer,
            CreatedAt = now,
            IdempotencyKey = key,
            RequestHash = hash,
            Entries =
            {
                new LedgerEntry { AccountId = from.Id, Amount = -amount, BalanceAfter = from.Balance, CreatedAt = now },
                new LedgerEntry { AccountId = to.Id, Amount = amount, BalanceAfter = to.Balance, CreatedAt = now }
            }
        };

        db.Transactions.Add(txn);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new OperationResult(txn, Replayed: false);
    }

    /// <summary>
    /// Idempotency flow: (1) fast path for a key that already committed, (2) re-check under the account
    /// locks inside the core method, (3) the unique index as the final backstop for a true race.
    /// </summary>
    private async Task<OperationResult> RunIdempotentAsync(string? key, string hash, Func<Task<OperationResult>> run, CancellationToken ct)
    {
        if (key is null)
            return await run();

        if (await FindReplayAsync(key, hash, ct) is { } early)
            return early;

        try
        {
            return await run();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var replay = await FindReplayAsync(key, hash, ct);
            if (replay is null) throw;
            return replay;
        }
    }

    private async Task<OperationResult?> FindReplayAsync(string key, string hash, CancellationToken ct)
    {
        var existing = await db.Transactions.AsNoTracking()
            .Include(t => t.Entries)
            .SingleOrDefaultAsync(t => t.IdempotencyKey == key, ct);

        if (existing is null)
            return null;

        if (existing.RequestHash != hash)
            throw new LedgerException(StatusCodes.Status409Conflict, "Idempotency-Key was already used with a different request.");

        return new OperationResult(existing, Replayed: true);
    }

    /// <summary>Takes an update lock on the account row (held until the surrounding transaction ends).</summary>
    private async Task<Account> LockAccountAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Accounts
            .FromSqlInterpolated($"SELECT * FROM Accounts WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .ToListAsync(ct);

        var account = rows.SingleOrDefault() ?? throw NotFound(id);
        if (account.IsSystem)
            throw new LedgerException(StatusCodes.Status422UnprocessableEntity, "The system account cannot be used directly.");
        return account;
    }

    private static void ValidateAmount(long amount)
    {
        if (amount <= 0 || amount > MaxAmountMinor)
            throw new LedgerException(StatusCodes.Status400BadRequest, $"AmountMinor must be between 1 and {MaxAmountMinor}.");
    }

    private static void ValidateKey(string? key)
    {
        if (key is not null && (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength))
            throw new LedgerException(StatusCodes.Status400BadRequest, $"Idempotency-Key must be 1-{MaxKeyLength} characters.");
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static LedgerException NotFound(Guid id) =>
        new(StatusCodes.Status404NotFound, $"Account {id} not found.");
}
