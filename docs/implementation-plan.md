# Implementation plan

This file records the decisions taken before and during implementation. It is
kept as a historical note; the other documents in `docs/` describe the system
as built.

## Environment found at start

| Tool | Windows host | WSL (Ubuntu 24.04) |
|------|--------------|--------------------|
| .NET SDK | not installed (runtime 8.0 only) | not installed |
| C++ compiler / CMake | not installed | not installed |
| PostgreSQL | not installed | not installed |
| Docker Engine + Compose | not installed | 28.4.0 / v2.39.2 |
| k6 | not installed | not installed |

Consequence: every build, test and benchmark is executed inside containers
started from WSL. `docker/dev.Dockerfile` defines the toolchain image
(.NET 10 SDK, g++ 13, CMake, Ninja, GoogleTest, `dotnet-ef`).

## Decisions

1. **.NET 10 (LTS)**, EF Core 10, Npgsql provider, PostgreSQL 17.
2. **Two hosts sharing one application core.** `AegisBank.Api` (JWT bearer,
   REST, OpenAPI) and `AegisBank.Dashboard` (Razor Pages, cookie auth,
   antiforgery). Both call the same services; the dashboard does not go
   through HTTP. Running both against one database is also the simplest
   multi-instance test of the locking design.
3. **Bank-perspective chart of accounts.** Customer deposit accounts are
   liabilities (credit-normal). One system account, *Vault cash*, is an asset
   (debit-normal). A transfer debits the source customer account and credits
   the destination. Opening funds debit Vault cash and credit the customer.
4. **Engine boundary.** The C++ engine receives two account snapshots and an
   amount, and returns a decision: result code, resulting balances and two
   ledger instructions. It holds no state and performs no I/O.
5. **Single database transaction per transfer** at READ COMMITTED with
   explicit row locks (`SELECT … FOR UPDATE`) taken in ascending account-id
   order. The idempotency claim is an `INSERT … ON CONFLICT DO NOTHING` inside
   the same transaction, so a claim is never visible without its outcome.
6. **Rejected transfers are recorded** (status `Rejected`, no journal) so
   replays return the same 422 and the dashboard can show failures.
7. **Database as last barrier.** Check constraints, unique indexes, a deferred
   constraint trigger that rejects unbalanced journals at commit, and triggers
   that forbid UPDATE/DELETE on posted ledger rows.
8. **Passwords** use `PasswordHasher<T>` from ASP.NET Core Identity
   (PBKDF2-HMAC-SHA512). Refresh tokens are random, stored hashed, rotated on
   use, and a reused token revokes its whole family.

## Order of work

1. C++ engine, C ABI, GoogleTest suite, native benchmark.
2. Domain + Application contracts.
3. Infrastructure: EF model, migrations, triggers, native wrapper,
   transfer coordinator, verifier, auth, queries, seeding.
4. API host.
5. Dashboard host.
6. Unit, integration and concurrency tests against real PostgreSQL.
7. Dockerfiles, Compose, CI.
8. Benchmarks (native, BenchmarkDotNet, k6).
9. Documentation written from the finished code.
