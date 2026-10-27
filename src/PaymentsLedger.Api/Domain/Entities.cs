namespace PaymentsLedger.Api.Domain;

public class Account
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Owner { get; set; } = "";

    /// <summary>
    /// Cached balance in minor units (1 PLN = 100). The source of truth is the sum of the
    /// account's ledger entries. Only changed while the account row is locked (UPDLOCK).
    /// Not maintained for the system account (see <see cref="SystemAccount"/>).
    /// </summary>
    public long Balance { get; set; }

    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum TransactionType
{
    Deposit = 1,
    Transfer = 2
}

/// <summary>A balanced group of entries. The entries of one transaction always sum to zero.</summary>
public class LedgerTransaction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public TransactionType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Value of the Idempotency-Key header, if the client sent one. Unique when not null.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>SHA-256 of the request, to detect a key being reused for a different request.</summary>
    public string? RequestHash { get; set; }

    public List<LedgerEntry> Entries { get; set; } = [];
}

/// <summary>One signed line of the ledger: credit is positive, debit is negative. Append-only.</summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public long Amount { get; set; }

    /// <summary>Account balance right after this entry. Null for the system account.</summary>
    public long? BalanceAfter { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The counter-party of every deposit ("money coming from outside"). It can go negative and its
/// balance is not cached or locked, so deposits never contend on one global row.
/// </summary>
public static class SystemAccount
{
    public static readonly Guid Id = new("00000000-0000-0000-0000-000000000001");
}
