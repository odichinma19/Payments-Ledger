using Microsoft.EntityFrameworkCore;
using PaymentsLedger.Api.Data;
using PaymentsLedger.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Ledger")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LedgerService>();
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

// Convenient for local runs and docker compose. In a real deployment run migrations as a separate step.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

app.Run();

// Makes the entry point visible to WebApplicationFactory in the test project.
public partial class Program { }
