# Ledger design

## Perspective and conventions

The ledger is kept from the **bank's** point of view.

| Account class | Example | Normal side | Debit | Credit |
|---|---|---|---|---|
| Asset | Vault cash (system account `1000000001`) | Debit | increases | decreases |
| Liability | Customer deposit accounts | Credit | decreases | increases |

A customer's balance is money the bank owes the customer, so it is a
liability. This is why moving money *out* of a customer account is a **debit**
to that account, which may look inverted compared with a personal bank
statement.

`accounts.available_balance_minor_units` is always expressed on the account's
normal side and is never negative:

- asset: balance = Σ debits − Σ credits
- liability: balance = Σ credits − Σ debits

There is no equity account in the simulation, so the accounting equation
reduces to **total assets = total liabilities**.

## Postings

Every movement is one journal entry with exactly two lines.

```text
Opening deposit of Rp 100.000,00 to 2900000011
    Dr  1000000001  Vault cash            100.000,00
        Cr  2900000011  Tabungan Harian                 100.000,00

Internal transfer of Rp 80.000,00 from 2900000011 to 2900000021
    Dr  2900000011  Tabungan Harian        80.000,00
        Cr  2900000021  Tabungan Harian                  80.000,00

Reversal of that transfer
    Dr  2900000021  Tabungan Harian        80.000,00
        Cr  2900000011  Tabungan Harian                  80.000,00
```

On the `transfers` row, `source_account_id` is always the debited account and
`destination_account_id` the credited one.

The C++ engine applies the same rule generically: a posting on an account's
normal side adds to its balance; a posting on the other side subtracts and
must not take the balance below zero. That one rule covers transfers, opening
deposits and reversals.

## Where money comes from

Funds enter customer accounts only through an **opening deposit**, a balanced
entry against the Vault cash account. Accounts are always created with a zero
balance. Nothing in the application sets a balance directly, and the demo
seed uses the same posting path as live requests. The Vault cash account and
the internal `system` user are created by a migration.

An internal transfer debits one liability and credits another by the same
amount, so total customer funds do not change.

## Money representation

Amounts are 64-bit integers in minor units. IDR is modelled with two minor
digits as ISO 4217 defines it, so Rp 100.000,00 is `10000000`. No
floating-point value is used anywhere in a calculation; the dashboard converts
to `decimal` only for display. The engine uses checked arithmetic and reports
`BALANCE_OVERFLOW` instead of wrapping.

Only IDR is enabled. Currency is a column on accounts, transfers and journal
entries, and the engine compares all three, so adding a currency means adding
it to `Currencies` and to the check constraint.

## Authoritative data and projections

The ledger lines are the source of truth. `accounts.available_balance_minor_units`
is a projection kept in step inside the same transaction that inserts the
lines. It exists so that the balance check under the row lock is O(1).

## Enforcement in the database

Defined in `Persistence/LedgerIntegritySql.cs` (applied by the
`LedgerIntegrity` migration) and in the EF model.

| Rule | Mechanism |
|---|---|
| A line is a debit or a credit, never both, never zero or negative | `ck_ledger_lines_one_sided`, `ck_ledger_lines_nonnegative` |
| Balances are never negative | `ck_accounts_balance_nonnegative` |
| Transfer amount is positive; source ≠ destination | `ck_transfers_amount_positive`, `ck_transfers_distinct_accounts` |
| Transfer lifecycle: Completed ⇔ has completion time and no failure code; Rejected ⇔ has failure code and no completion time | `ck_transfers_lifecycle` |
| Unique account numbers, transfer references, journal references | unique indexes |
| One transfer per (user, idempotency key) | unique index on `transfers`, unique index on `idempotency_records` |
| One journal per transfer | unique index on `journal_entries.transfer_id` |
| At most one completed reversal per transfer | partial unique index `ux_transfers_single_completed_reversal` |
| Posted rows cannot be updated, deleted or truncated (`ledger_lines`, `journal_entries`, `transfers`, `audit_logs`) | `BEFORE UPDATE OR DELETE` / `BEFORE TRUNCATE` triggers raising an error |
| Lines can only be added by the transaction that created the journal, and only for accounts in the journal's currency | `BEFORE INSERT` trigger comparing `journal_entries.posting_txid` with `txid_current()` |
| Every journal has ≥ 2 lines and Σ debit = Σ credit ≠ 0 | deferred constraint trigger on `journal_entries`, evaluated at commit |
| A completed transfer has exactly one journal whose debit total equals its amount; a rejected transfer has none | deferred constraint trigger on `transfers`, evaluated at commit |

### Journal balancing under concurrent writes

The balance check must hold for *committed* data regardless of what other
sessions do. Three rules together give that:

1. `posting_txid` is stamped by a trigger with `txid_current()` when the
   journal row is inserted. The application cannot choose it.
2. A ledger line can only be inserted while `txid_current()` still equals the
   journal's `posting_txid`. So all lines of a journal are written by one
   transaction, and no other session can add a line later, not even a
   balanced pair.
3. The balance check is a `DEFERRABLE INITIALLY DEFERRED` constraint trigger,
   so it runs at commit, after all lines of that transaction exist. The
   transaction sees its own uncommitted rows, and by rule 2 nobody else can
   contribute rows, so what it checks is exactly what becomes visible.

After commit the rows are immutable, so a journal that was balanced when it
was committed stays balanced. `txid_current()` returns the top-level
transaction id and is not affected by savepoints; the coordinator also
disables EF Core's automatic savepoints.

## Corrections

Posted entries are never edited. A mistake is corrected with a **reversal**:
a new transfer of kind `Reversal` that references the original
(`reversal_of_transfer_id`) and posts the opposite entry. Both remain in the
ledger. A reversal is subject to the same rules as any posting, so it is
rejected with `INSUFFICIENT_FUNDS` if the recipient has already spent the
money. A reversal cannot itself be reversed.

## Reconciliation

`LedgerVerifier` (`GET /api/ledger/verify`, and the Ledger explorer page) runs
these checks in one REPEATABLE READ, read-only transaction, so that all of
them see the same snapshot even while transfers are committing:

| Check | Meaning |
|---|---|
| `journals_balanced` | every journal has ≥ 2 lines and equal totals |
| `projections_reconcile` | every stored balance equals the balance derived from the account's ledger lines |
| `no_negative_balances` | no account is below zero |
| `completed_transfers_posted` | every completed transfer has one journal debiting its source and crediting its destination by its amount |
| `rejected_transfers_not_posted` | no rejected transfer has a journal |
| `ledger_currency_consistent` | lines only touch accounts in the journal's currency |
| `accounting_equation` | total assets = total liabilities, per currency |
| `idempotency_exactly_once` | every transfer is backed by one completed idempotency record with the same fingerprint |
| `no_orphan_idempotency_claims` | no claim without a transfer |
| `single_reversal` | no transfer has more than one completed reversal |
| `ledger_totals_equal` | Σ all debits = Σ all credits |

The endpoint returns 200 with the report when everything passes and 409 with
the same report otherwise. The report lists up to five sample violations per
check.

Constraints cannot prevent a privileged session from changing a balance
projection directly (the projection is an ordinary column). Reconciliation is
what detects that; `DatabaseIntegrityTests.ReconciliationDetectsABalanceChangedBehindTheLedgersBack`
demonstrates it.
