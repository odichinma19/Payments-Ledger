using Microsoft.AspNetCore.Mvc;
using PaymentsLedger.Api.Contracts;
using PaymentsLedger.Api.Services;

namespace PaymentsLedger.Api.Controllers;

[ApiController]
public abstract class LedgerControllerBase : ControllerBase
{
    /// <summary>201 for a new transaction, 200 plus Idempotent-Replayed: true for a retry of an earlier one.</summary>
    protected IActionResult Respond(OperationResult result)
    {
        var body = result.Transaction.ToResponse();
        if (!result.Replayed)
            return StatusCode(StatusCodes.Status201Created, body);

        Response.Headers["Idempotent-Replayed"] = "true";
        return Ok(body);
    }
}
