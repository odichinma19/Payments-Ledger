using System.Net;
using System.Net.Http.Json;
using PaymentsLedger.Api.Contracts;

namespace PaymentsLedger.Tests;

[Collection(LedgerCollection.Name)]
public class LedgerApiTests(LedgerFixture fixture)
{
    private readonly TestApi _api = new(fixture.Factory.CreateClient());

    [Fact]
    public async Task Transfer_moves_money_and_all_entries_sum_to_zero()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 1_000);
        var bob = await _api.NewAccountAsync();

        var response = await _api.TransferAsync(alice.Id, bob.Id, 300);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transaction = (await response.Content.ReadFromJsonAsync<TransactionResponse>())!;
        Assert.Equal(2, transaction.Entries.Count);
        Assert.Equal(0L, transaction.Entries.Sum(e => e.AmountMinor));

        Assert.Equal(700L, await _api.BalanceAsync(alice.Id));
        Assert.Equal(300L, await _api.BalanceAsync(bob.Id));
        await Invariants.AssertBalancedAsync(fixture.Factory, alice.Id, bob.Id);
    }

    [Fact]
    public async Task Overdraft_returns_422_and_changes_nothing()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 100);
        var bob = await _api.NewAccountAsync();

        var response = await _api.TransferAsync(alice.Id, bob.Id, 101);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(100L, await _api.BalanceAsync(alice.Id));
        Assert.Equal(0L, await _api.BalanceAsync(bob.Id));
        Assert.Empty(await _api.EntriesAsync(bob.Id));
        await Invariants.AssertBalancedAsync(fixture.Factory, alice.Id, bob.Id);
    }

    [Fact]
    public async Task Retry_with_same_idempotency_key_runs_the_transfer_once()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 1_000);
        var bob = await _api.NewAccountAsync();
        var key = Guid.NewGuid().ToString();

        var first = await _api.TransferAsync(alice.Id, bob.Id, 300, key);
        var second = await _api.TransferAsync(alice.Id, bob.Id, 300, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        var firstBody = (await first.Content.ReadFromJsonAsync<TransactionResponse>())!;
        var secondBody = (await second.Content.ReadFromJsonAsync<TransactionResponse>())!;
        Assert.Equal(firstBody.Id, secondBody.Id);

        Assert.Equal(700L, await _api.BalanceAsync(alice.Id));
        Assert.Equal(300L, await _api.BalanceAsync(bob.Id));
        Assert.Single(await _api.EntriesAsync(bob.Id));
    }

    [Fact]
    public async Task Same_idempotency_key_with_a_different_request_returns_409()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 1_000);
        var bob = await _api.NewAccountAsync();
        var key = Guid.NewGuid().ToString();

        await _api.TransferAsync(alice.Id, bob.Id, 300, key);
        var conflicting = await _api.TransferAsync(alice.Id, bob.Id, 400, key);

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Equal(300L, await _api.BalanceAsync(bob.Id));
    }

    [Fact]
    public async Task Deposit_is_idempotent_too()
    {
        var alice = await _api.NewAccountAsync();
        var key = Guid.NewGuid().ToString();

        var first = await _api.DepositAsync(alice.Id, 500, key);
        var second = await _api.DepositAsync(alice.Id, 500, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(500L, await _api.BalanceAsync(alice.Id));
        await Invariants.AssertBalancedAsync(fixture.Factory, alice.Id);
    }

    [Fact]
    public async Task Entries_are_listed_newest_first_and_can_be_paged()
    {
        var alice = await _api.NewAccountAsync();
        for (var i = 1; i <= 3; i++)
            (await _api.DepositAsync(alice.Id, i * 100)).EnsureSuccessStatusCode();

        var firstPage = await _api.EntriesAsync(alice.Id, "?take=2");
        Assert.Equal(new[] { 300L, 200L }, firstPage.Select(e => e.AmountMinor));
        Assert.Equal(600L, firstPage[0].BalanceAfterMinor);

        var secondPage = await _api.EntriesAsync(alice.Id, $"?take=2&beforeId={firstPage[^1].Id}");
        Assert.Equal(new[] { 100L }, secondPage.Select(e => e.AmountMinor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_amount_returns_400(long amount)
    {
        var alice = await _api.NewAccountAsync(openingBalance: 100);
        var bob = await _api.NewAccountAsync();

        var response = await _api.TransferAsync(alice.Id, bob.Id, amount);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_to_the_same_account_returns_400()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 100);

        var response = await _api.TransferAsync(alice.Id, alice.Id, 10);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_to_an_unknown_account_returns_404()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 100);

        var response = await _api.TransferAsync(alice.Id, Guid.NewGuid(), 10);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(100L, await _api.BalanceAsync(alice.Id));
    }
}
