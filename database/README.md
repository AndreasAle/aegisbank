# Database

PostgreSQL 17. The schema is owned by EF Core migrations in
`src/AegisBank.Infrastructure/Persistence/Migrations`:

| Migration | Contents |
|---|---|
| `InitialCreate` | Tables, foreign keys, unique indexes, check constraints |
| `LedgerIntegrity` | Immutability triggers, journal-balance constraint triggers, the `system` user and the Vault cash account (SQL in `Persistence/LedgerIntegritySql.cs`) |

`schema.sql` in this folder is the idempotent script generated from those
migrations. It is a reference copy for reading and for DBAs who prefer to
apply SQL by hand; the migrations are authoritative.

## Applying migrations

Any one of:

```bash
# 1. Automatically at API start-up (what docker compose does)
Database__MigrateOnStartup=true

# 2. One-shot command using the API binary
dotnet AegisBank.Api.dll migrate

# 3. EF Core tools
cd src/AegisBank.Infrastructure
AEGIS_DESIGN_DB="Host=localhost;Database=aegisbank;Username=aegis;Password=..." dotnet ef database update

# 4. Plain SQL
psql "$DATABASE_URL" -f database/schema.sql
```

## Regenerating `schema.sql`

```bash
cd src/AegisBank.Infrastructure
dotnet ef migrations script --idempotent -o ../../database/schema.sql
```

## Demo data

```bash
# Requires Seed__AdminPassword, Seed__AuditorPassword, Seed__CustomerPassword.
# Refused when ASPNETCORE_ENVIRONMENT=Production.
dotnet AegisBank.Api.dll seed
```

The seed creates six fictional customers with nine accounts, funds them
through opening-deposit journal entries, and submits ten demo transfers (two
of which the engine rejects for insufficient funds). It is idempotent.

## Tables

| Table | Purpose |
|---|---|
| `users` | Login identities with role and optional customer link |
| `refresh_tokens` | Hashed refresh tokens, grouped in rotation families |
| `customers` | Fictional customers |
| `accounts` | Customer deposit accounts (liabilities) and system accounts (assets), with the balance projection |
| `transfers` | Every attempted movement: completed or rejected |
| `journal_entries` | One per completed transfer |
| `ledger_lines` | Debit and credit lines; the authoritative record |
| `idempotency_records` | Claimed keys with the stored response |
| `audit_logs` | Append-only security and business events |

The integrity rules are explained in [docs/ledger-design.md](../docs/ledger-design.md).
