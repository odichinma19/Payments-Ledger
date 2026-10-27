using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentsLedger.Api.Contracts;
using PaymentsLedger.Api.Data;

namespace PaymentsLedger.Tests;

/// <summary>Thin typed wrapper over HttpClient so the tests read like the API contract.</summary>
public sealed class TestApi(HttpClient http)
{
    public async Task<AccountResponse> NewAccountAsync(long openingBalance = 0)
    {
        var response = await http.PostAsJsonAsync("/accounts", new CreateAccountRequest("test"));
        response.EnsureSuccessStatusCode();
        var account = (await response.Content.ReadFromJsonAsync<AccountResponse>())!;

        if (openingBalance > 0)
            (await DepositAsync(account.Id, openingBalance)).EnsureSuccessStatusCode();

        return account;
    }

    public Task<HttpResponseMessage> DepositAsync(Guid accountId, long amount, string? key = null) =>
        Send($"/accounts/{accountId}/deposits", new DepositRequest(amount), key);

    public Task<HttpResponseMessage> TransferAsync(Guid from, Guid to, long amount, string? key = null) =>
        Send("/transfers", new TransferRequest(from, to, amount), key);

    public async Task<long> BalanceAsync(Guid accountId) =>
        (await http.GetFromJsonAsync<AccountResponse>($"/accounts/{accountId}"))!.BalanceMinor;

    public async Task<List<EntryResponse>> EntriesAsync(Guid accountId, string query = "") =>
        (await http.GetFromJsonAsync<List<EntryResponse>>($"/accounts/{accountId}/entries{query}"))!;

    private Task<HttpResponseMessage> Send(string url, object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return http.SendAsync(request);
    }
}

public static class Invariants
{
    /// <summary>
    /// Ledger invariants straight from the database: every entry in the system sums to zero, every
    /// transaction sums to zero, and each account's cached balance equals the sum of its entries.
    /// </summary>
    public static async Task AssertBalancedAsync(LedgerApiFactory factory, params Guid[] accountIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        Assert.Equal(0L, await db.Entries.SumAsync(e => e.Amount));

        var unbalancedTransactions = await db.Entries
            .GroupBy(e => e.TransactionId)
            .Where(g => g.Sum(e => e.Amount) != 0)
            .CountAsync();
        Assert.Equal(0, unbalancedTransactions);

        foreach (var id in accountIds)
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == id);
            var sum = await db.Entries.Where(e => e.AccountId == id).SumAsync(e => e.Amount);
            Assert.Equal(sum, account.Balance);
        }
    }
}
