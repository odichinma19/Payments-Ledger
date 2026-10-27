using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MsSql;

namespace PaymentsLedger.Tests;

public sealed class LedgerApiFactory : WebApplicationFactory<Program> { }

/// <summary>
/// One real SQL Server for the whole test run (UPDLOCK and the unique index need the real engine,
/// not the in-memory provider). Set LEDGER_TEST_CONNECTION to use your own server, e.g. LocalDB:
///   Server=(localdb)\MSSQLLocalDB;Database=PaymentsLedger_Tests;Trusted_Connection=True;TrustServerCertificate=True
/// Otherwise a SQL Server container is started through Docker.
/// </summary>
public sealed class LedgerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public LedgerApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEDGER_TEST_CONNECTION");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();
            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }

        // Read by Program.cs through the normal configuration pipeline. The API migrates the database on startup.
        Environment.SetEnvironmentVariable("ConnectionStrings__Ledger", connectionString);
        Factory = new LedgerApiFactory();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerCollection : ICollectionFixture<LedgerFixture>
{
    public const string Name = "ledger";
}
