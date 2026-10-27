using Microsoft.AspNetCore.Mvc;
using PaymentsLedger.Api.Contracts;
using PaymentsLedger.Api.Services;

namespace PaymentsLedger.Api.Controllers;

[ApiController]
[Route("accounts")]
public sealed class AccountsController(LedgerService ledger) : LedgerControllerBase
{
    [HttpPost]
    public async Task<ActionResult<AccountResponse>> Create(CreateAccountRequest request, CancellationToken ct)
    {
        var account = await ledger.CreateAccountAsync(request.Owner, ct);
        return CreatedAtAction(nameof(Get), new { id = account.Id }, account.ToResponse());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AccountResponse>> Get(Guid id, CancellationToken ct) =>
        (await ledger.GetAccountAsync(id, ct)).ToResponse();

    [HttpPost("{id:guid}/deposits")]
    public async Task<IActionResult> Deposit(
        Guid id,
        DepositRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) =>
        Respond(await ledger.DepositAsync(id, request.AmountMinor, idempotencyKey, ct));

    /// <summary>Newest first. Pass the smallest id you have seen as beforeId to get the next page.</summary>
    [HttpGet("{id:guid}/entries")]
    public async Task<ActionResult<IReadOnlyList<EntryResponse>>> Entries(
        Guid id,
        [FromQuery] int take = 50,
        [FromQuery] long? beforeId = null,
        CancellationToken ct = default)
    {
        var entries = await ledger.GetEntriesAsync(id, take, beforeId, ct);
        return Ok(entries.Select(e => e.ToResponse()).ToList());
    }
}
