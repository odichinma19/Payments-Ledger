using Microsoft.AspNetCore.Mvc;
using PaymentsLedger.Api.Contracts;
using PaymentsLedger.Api.Services;

namespace PaymentsLedger.Api.Controllers;

[ApiController]
[Route("transfers")]
public sealed class TransfersController(LedgerService ledger) : LedgerControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        TransferRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) =>
        Respond(await ledger.TransferAsync(request.FromAccountId, request.ToAccountId, request.AmountMinor, idempotencyKey, ct));
}
