using PaymentsLedger.Api.Domain;

namespace PaymentsLedger.Api.Contracts;

// All money amounts are integers in minor units (1 PLN = 100).

public sealed record CreateAccountRequest(string Owner);

public sealed record DepositRequest(long AmountMinor);

public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, long AmountMinor);

public sealed record AccountResponse(Guid Id, string Owner, long BalanceMinor, DateTimeOffset CreatedAt);

public sealed record EntryResponse(
    long Id,
    Guid TransactionId,
    Guid AccountId,
    long AmountMinor,
    long? BalanceAfterMinor,
    DateTimeOffset CreatedAt);

public sealed record TransactionResponse(
    Guid Id,
    string Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<EntryResponse> Entries);

public static class Mappings
{
    public static AccountResponse ToResponse(this Account a) =>
        new(a.Id, a.Owner, a.Balance, a.CreatedAt);

    public static EntryResponse ToResponse(this LedgerEntry e) =>
        new(e.Id, e.TransactionId, e.AccountId, e.Amount, e.BalanceAfter, e.CreatedAt);

    public static TransactionResponse ToResponse(this LedgerTransaction t) =>
        new(t.Id, t.Type.ToString(), t.CreatedAt, t.Entries.OrderBy(e => e.Id).Select(e => e.ToResponse()).ToList());
}
