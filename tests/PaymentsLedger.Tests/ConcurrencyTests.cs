using System.Net;

namespace PaymentsLedger.Tests;

[Collection(LedgerCollection.Name)]
public class ConcurrencyTests(LedgerFixture fixture)
{
    private readonly TestApi _api = new(fixture.Factory.CreateClient());

    [Fact]
    public async Task Twenty_parallel_transfers_never_overdraw_the_account()
    {
        // 1000 available, 20 x 100 requested at once: exactly 10 can succeed.
        var alice = await _api.NewAccountAsync(openingBalance: 1_000);
        var bob = await _api.NewAccountAsync();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => _api.TransferAsync(alice.Id, bob.Id, 100)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0L, await _api.BalanceAsync(alice.Id));
        Assert.Equal(1_000L, await _api.BalanceAsync(bob.Id));
        await Invariants.AssertBalancedAsync(fixture.Factory, alice.Id, bob.Id);
    }

    [Fact]
    public async Task Opposing_parallel_transfers_do_not_deadlock()
    {
        // A->B and B->A at the same time. Without a fixed lock order this is the classic deadlock.
        var alice = await _api.NewAccountAsync(openingBalance: 10_000);
        var bob = await _api.NewAccountAsync(openingBalance: 10_000);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => i % 2 == 0
                ? _api.TransferAsync(alice.Id, bob.Id, 10)
                : _api.TransferAsync(bob.Id, alice.Id, 10)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(10_000L, await _api.BalanceAsync(alice.Id));
        Assert.Equal(10_000L, await _api.BalanceAsync(bob.Id));
        await Invariants.AssertBalancedAsync(fixture.Factory, alice.Id, bob.Id);
    }

    [Fact]
    public async Task Parallel_retries_with_the_same_key_run_the_transfer_once()
    {
        var alice = await _api.NewAccountAsync(openingBalance: 1_000);
        var bob = await _api.NewAccountAsync();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => _api.TransferAsync(alice.Id, bob.Id, 100, key)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(900L, await _api.BalanceAsync(alice.Id));
        Assert.Equal(100L, await _api.BalanceAsync(bob.Id));
        await Invariants.AssertBalancedAsync(fixture.Factory, alice.Id, bob.Id);
    }
}
