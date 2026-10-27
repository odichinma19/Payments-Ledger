![CI](https://github.com/odichinma19/Payments-Ledger/actions/workflows/ci.yml/badge.svg)

# Payments Ledger

A small payments ledger as an ASP.NET Core Web API. Accounts, deposits and transfers are stored as
double-entry ledger entries, retried requests are safe, and concurrent transfers cannot overdraw an account.

**Stack:** C#, .NET 10 (LTS), ASP.NET Core, Entity Framework Core, SQL Server, xUnit, Testcontainers, Docker Compose, GitHub Actions.

## The problem

Moving money looks like `balance -= x; other += x`, and that breaks in three ways:

1. **Lost or invented money.** Two writes can half-succeed. Here every operation is a set of ledger entries
   that sum to zero, written in one database transaction.
2. **Races.** Two transfers reading the same balance at the same time can both pass the overdraft check.
   Here the account rows are locked (`UPDLOCK`) before the balance is read.
3. **Retries.** A client that times out will send the request again. Here an `Idempotency-Key` makes the retry
   return the original result instead of moving money twice.

## Run it

```bash
docker compose up --build
```

The API is on `http://localhost:8080` (SQL Server on `localhost:1433`). The database is created and migrated on startup.

```bash
# create two accounts
curl -s -X POST localhost:8080/accounts -H 'Content-Type: application/json' -d '{"owner":"Alice"}'
curl -s -X POST localhost:8080/accounts -H 'Content-Type: application/json' -d '{"owner":"Bob"}'

# deposit 100.00 PLN into Alice (amounts are minor units)
curl -s -X POST localhost:8080/accounts/<ALICE_ID>/deposits -H 'Content-Type: application/json' -d '{"amountMinor":10000}'

# transfer 25.00 PLN, safe to repeat
curl -s -X POST localhost:8080/transfers -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-1' \
  -d '{"fromAccountId":"<ALICE_ID>","toAccountId":"<BOB_ID>","amountMinor":2500}'
```

Without Docker: open `PaymentsLedger.slnx` in Visual Studio and run the API. It uses LocalDB by default
(`ConnectionStrings:Ledger` in `appsettings.json`). A ready-made `PaymentsLedger.Api.http` file is next to the project.

## API

| Method | Path | Description |
|---|---|---|
| POST | `/accounts` | Create an account. Body `{ "owner": "..." }`. |
| GET | `/accounts/{id}` | Account with its balance. |
| POST | `/accounts/{id}/deposits` | Deposit. Body `{ "amountMinor": 1000 }`. Optional `Idempotency-Key`. |
| POST | `/transfers` | Transfer. Body `{ "fromAccountId", "toAccountId", "amountMinor" }`. Optional `Idempotency-Key`. |
| GET | `/accounts/{id}/entries?take=50&beforeId=` | Ledger entries, newest first, cursor paging. |
| GET | `/health` | Liveness. |

| Status | When |
|---|---|
| 201 | Transaction created. |
| 200 + `Idempotent-Replayed: true` | Retry of a request that already succeeded. Same body as the first response. |
| 400 | Invalid amount, same account on both sides, bad key. |
| 404 | Unknown account. |
| 409 | `Idempotency-Key` reused with a different request. |
| 422 | Insufficient funds. |

Errors are RFC 7807 problem responses. OpenAPI is served at `/openapi/v1.json` in Development.

## How it works

**Double-entry.** Each operation is a `LedgerTransaction` with signed `LedgerEntry` rows (credit positive, debit negative)
that sum to zero. A deposit debits a built-in `SYSTEM` account and credits the user, so the ledger as a whole always sums to zero.
Entries are append-only. `Account.Balance` is a cached sum, only changed while the row is locked.

**One transaction, fixed lock order.** A transfer opens a database transaction, then locks both account rows with
`SELECT ... WITH (UPDLOCK, ROWLOCK)` in ascending id order. A→B and B→A running at the same time queue on the same
first row instead of deadlocking. The balance is read after the lock, so the overdraft check sees the latest committed value.

**Idempotency in three layers.**
1. Fast path: the key already committed, return the stored transaction.
2. Re-check after taking the locks, which covers a concurrent retry that just committed.
3. A unique index on `IdempotencyKey` is the last backstop. If two requests still collide, the loser catches the
   unique violation and returns the winner's result.

A request hash is stored with the key, so reusing a key for a different request gives 409 instead of a wrong replay.

**Defence in depth.** The database also refuses what the service should never do: a check constraint blocks negative
balances on user accounts and zero-amount entries.

## Tests

```bash
dotnet test
```

The tests run against a real SQL Server started by Testcontainers (Docker required), because `UPDLOCK` and the
unique index cannot be tested on the in-memory provider. To use your own server (e.g. LocalDB), set
`LEDGER_TEST_CONNECTION` to its connection string.

| Test | What it proves |
|---|---|
| Transfer moves money, entries sum to zero | Each transaction, the whole ledger, and every cached balance agree with the entries. |
| Overdraft returns 422 | Nothing changes when a transfer is rejected. |
| Retry with the same key | The transfer runs once and the replay returns the same transaction. |
| Same key, different request | 409. |
| 20 parallel transfers of 100 from a balance of 1000 | Exactly 10 succeed, 10 get 422, the balance ends at exactly 0. |
| 20 parallel opposing transfers | No deadlock, all succeed, balances unchanged. |
| 10 parallel retries with one key | Exactly one 201, nine replays, money moved once. |

CI (`.github/workflows/ci.yml`) builds, runs the tests, and smoke-tests `docker compose up` with a real transfer.

## Project layout

```
src/PaymentsLedger.Api/
  Domain/        Account, LedgerTransaction, LedgerEntry
  Data/          LedgerDbContext, EF Core model, migrations
  Services/      LedgerService (transactions, locking, idempotency), error handling
  Controllers/   Accounts, Transfers
  Contracts/     request/response records
tests/PaymentsLedger.Tests/
```

## Scope and trade-offs

- Single currency (PLN), amounts are `long` minor units. No floating point anywhere.
- No authentication. This is a ledger core, not a product.
- Migrations run on startup for convenience. In production they would be a separate deploy step.
- The `SYSTEM` account balance is not cached or locked, so deposits do not serialize on one row.
- Idempotency keys are kept forever. A real system would expire them.
