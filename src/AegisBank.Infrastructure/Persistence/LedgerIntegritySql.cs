namespace AegisBank.Infrastructure.Persistence;

/// <summary>
/// Database-side integrity rules that cannot be expressed as EF Core
/// constraints. Applied by the LedgerIntegrity migration. See
/// docs/ledger-design.md for the reasoning.
/// </summary>
public static class LedgerIntegritySql
{
    public const string Up = """
        -- 1. Posted records are immutable. Corrections are new reversal entries.
        CREATE FUNCTION aegis_forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION '% on "%" is not allowed: posted records are immutable', TG_OP, TG_TABLE_NAME
                USING ERRCODE = 'integrity_constraint_violation';
        END $$;

        CREATE TRIGGER trg_ledger_lines_immutable BEFORE UPDATE OR DELETE ON ledger_lines
            FOR EACH ROW EXECUTE FUNCTION aegis_forbid_mutation();
        CREATE TRIGGER trg_journal_entries_immutable BEFORE UPDATE OR DELETE ON journal_entries
            FOR EACH ROW EXECUTE FUNCTION aegis_forbid_mutation();
        CREATE TRIGGER trg_transfers_immutable BEFORE UPDATE OR DELETE ON transfers
            FOR EACH ROW EXECUTE FUNCTION aegis_forbid_mutation();
        CREATE TRIGGER trg_audit_logs_immutable BEFORE UPDATE OR DELETE ON audit_logs
            FOR EACH ROW EXECUTE FUNCTION aegis_forbid_mutation();

        CREATE TRIGGER trg_ledger_lines_no_truncate BEFORE TRUNCATE ON ledger_lines
            FOR EACH STATEMENT EXECUTE FUNCTION aegis_forbid_mutation();
        CREATE TRIGGER trg_journal_entries_no_truncate BEFORE TRUNCATE ON journal_entries
            FOR EACH STATEMENT EXECUTE FUNCTION aegis_forbid_mutation();
        CREATE TRIGGER trg_transfers_no_truncate BEFORE TRUNCATE ON transfers
            FOR EACH STATEMENT EXECUTE FUNCTION aegis_forbid_mutation();

        -- 2. Every journal entry records the transaction that posted it.
        CREATE FUNCTION aegis_stamp_journal_entry() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            NEW.posting_txid := txid_current();
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_journal_entries_stamp BEFORE INSERT ON journal_entries
            FOR EACH ROW EXECUTE FUNCTION aegis_stamp_journal_entry();

        -- 3. Ledger lines can only be added by the transaction that posted the
        --    journal entry, and only for accounts in the journal's currency.
        --    Together with rule 1 this freezes a journal at commit.
        CREATE FUNCTION aegis_guard_ledger_line() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            journal_txid bigint;
            journal_currency text;
            account_currency text;
        BEGIN
            SELECT posting_txid, currency INTO journal_txid, journal_currency
              FROM journal_entries WHERE id = NEW.journal_entry_id;
            IF journal_txid IS DISTINCT FROM txid_current() THEN
                RAISE EXCEPTION 'journal entry % is already posted; lines cannot be added', NEW.journal_entry_id
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT currency INTO account_currency FROM accounts WHERE id = NEW.account_id;
            IF account_currency IS DISTINCT FROM journal_currency THEN
                RAISE EXCEPTION 'ledger line currency mismatch for journal entry %', NEW.journal_entry_id
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_ledger_lines_guard BEFORE INSERT ON ledger_lines
            FOR EACH ROW EXECUTE FUNCTION aegis_guard_ledger_line();

        -- 4. At commit, every journal entry posted by the transaction must have
        --    at least two lines and equal, non-zero debit and credit totals.
        CREATE FUNCTION aegis_check_journal_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            total_debit numeric;
            total_credit numeric;
            line_count integer;
        BEGIN
            SELECT COALESCE(SUM(debit_minor_units), 0), COALESCE(SUM(credit_minor_units), 0), COUNT(*)
              INTO total_debit, total_credit, line_count
              FROM ledger_lines WHERE journal_entry_id = NEW.id;
            IF line_count < 2 OR total_debit <> total_credit OR total_debit = 0 THEN
                RAISE EXCEPTION 'journal entry % is not balanced (lines %, debit %, credit %)',
                    NEW.reference_number, line_count, total_debit, total_credit
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END $$;

        CREATE CONSTRAINT TRIGGER trg_journal_entries_balanced AFTER INSERT ON journal_entries
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION aegis_check_journal_balanced();

        -- 5. At commit, a completed transfer must own exactly one journal entry
        --    whose debit total equals the transfer amount; a rejected transfer
        --    must own none.
        CREATE FUNCTION aegis_check_transfer_journal() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            journal_debit numeric;
            journal_count integer;
        BEGIN
            SELECT COUNT(DISTINCT je.id), COALESCE(SUM(l.debit_minor_units), 0)
              INTO journal_count, journal_debit
              FROM journal_entries je
              LEFT JOIN ledger_lines l ON l.journal_entry_id = je.id
             WHERE je.transfer_id = NEW.id;
            IF NEW.status = 'Completed' AND (journal_count <> 1 OR journal_debit <> NEW.amount_minor_units) THEN
                RAISE EXCEPTION 'completed transfer % has no matching journal entry', NEW.reference_number
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.status = 'Rejected' AND journal_count <> 0 THEN
                RAISE EXCEPTION 'rejected transfer % must not have a journal entry', NEW.reference_number
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END $$;

        CREATE CONSTRAINT TRIGGER trg_transfers_journal AFTER INSERT ON transfers
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION aegis_check_transfer_journal();

        -- 6. Well-known system rows: the system actor and the vault cash account.
        INSERT INTO users (id, username, email, password_hash, role, customer_id, is_active,
                           failed_login_count, lockout_ends_at, created_at, updated_at)
        VALUES ('00000000-0000-7000-8000-000000000001', 'system', 'system@aegisbank.invalid', '!', 'System',
                NULL, FALSE, 0, NULL, now(), now());

        INSERT INTO accounts (id, account_number, name, customer_id, class, currency,
                              available_balance_minor_units, status, created_at, updated_at, version)
        VALUES ('00000000-0000-7000-8000-0000000000a1', '1000000001', 'Vault cash', NULL, 'Asset', 'IDR',
                0, 'Active', now(), now(), 0);
        """;

    public const string Down = """
        DELETE FROM accounts WHERE id = '00000000-0000-7000-8000-0000000000a1';
        DELETE FROM users WHERE id = '00000000-0000-7000-8000-000000000001';
        DROP TRIGGER IF EXISTS trg_transfers_journal ON transfers;
        DROP TRIGGER IF EXISTS trg_journal_entries_balanced ON journal_entries;
        DROP TRIGGER IF EXISTS trg_ledger_lines_guard ON ledger_lines;
        DROP TRIGGER IF EXISTS trg_journal_entries_stamp ON journal_entries;
        DROP TRIGGER IF EXISTS trg_transfers_no_truncate ON transfers;
        DROP TRIGGER IF EXISTS trg_journal_entries_no_truncate ON journal_entries;
        DROP TRIGGER IF EXISTS trg_ledger_lines_no_truncate ON ledger_lines;
        DROP TRIGGER IF EXISTS trg_audit_logs_immutable ON audit_logs;
        DROP TRIGGER IF EXISTS trg_transfers_immutable ON transfers;
        DROP TRIGGER IF EXISTS trg_journal_entries_immutable ON journal_entries;
        DROP TRIGGER IF EXISTS trg_ledger_lines_immutable ON ledger_lines;
        DROP FUNCTION IF EXISTS aegis_check_transfer_journal();
        DROP FUNCTION IF EXISTS aegis_check_journal_balanced();
        DROP FUNCTION IF EXISTS aegis_guard_ledger_line();
        DROP FUNCTION IF EXISTS aegis_stamp_journal_entry();
        DROP FUNCTION IF EXISTS aegis_forbid_mutation();
        """;
}
