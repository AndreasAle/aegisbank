CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE audit_logs (
        id uuid NOT NULL,
        actor_id uuid,
        action character varying(64) NOT NULL,
        entity_type character varying(64) NOT NULL,
        entity_id character varying(64),
        metadata jsonb NOT NULL,
        correlation_id character varying(64),
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE customers (
        id uuid NOT NULL,
        customer_number character varying(20) NOT NULL,
        full_name character varying(120) NOT NULL,
        email character varying(254) NOT NULL,
        status character varying(16) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_customers" PRIMARY KEY (id),
        CONSTRAINT ck_customers_status CHECK (status IN ('Active','Suspended'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE accounts (
        id uuid NOT NULL,
        account_number character varying(20) NOT NULL,
        name character varying(120) NOT NULL,
        customer_id uuid,
        class character varying(16) NOT NULL,
        currency character(3) NOT NULL,
        available_balance_minor_units bigint NOT NULL,
        status character varying(16) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT "PK_accounts" PRIMARY KEY (id),
        CONSTRAINT ck_accounts_balance_nonnegative CHECK (available_balance_minor_units >= 0),
        CONSTRAINT ck_accounts_class CHECK (class IN ('Asset','Liability')),
        CONSTRAINT ck_accounts_currency CHECK (currency ~ '^[A-Z]{3}$'),
        CONSTRAINT ck_accounts_customer_is_liability CHECK (customer_id IS NULL OR class = 'Liability'),
        CONSTRAINT ck_accounts_status CHECK (status IN ('Active','Frozen','Closed')),
        CONSTRAINT ck_accounts_version_nonnegative CHECK (version >= 0),
        CONSTRAINT "FK_accounts_customers_customer_id" FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE users (
        id uuid NOT NULL,
        username character varying(64) NOT NULL,
        email character varying(254) NOT NULL,
        password_hash character varying(512) NOT NULL,
        role character varying(16) NOT NULL,
        customer_id uuid,
        is_active boolean NOT NULL,
        failed_login_count integer NOT NULL,
        lockout_ends_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_users" PRIMARY KEY (id),
        CONSTRAINT ck_users_role CHECK (role IN ('System','Customer','Auditor','Admin')),
        CONSTRAINT "FK_users_customers_customer_id" FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE idempotency_records (
        id uuid NOT NULL,
        idempotency_key character varying(128) NOT NULL,
        actor_id uuid NOT NULL,
        request_fingerprint character varying(64) NOT NULL,
        status character varying(16) NOT NULL,
        response_code integer,
        response_body text,
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_idempotency_records" PRIMARY KEY (id),
        CONSTRAINT ck_idempotency_completed_has_response CHECK (status <> 'Completed' OR (response_code IS NOT NULL AND response_body IS NOT NULL)),
        CONSTRAINT ck_idempotency_status CHECK (status IN ('InProgress','Completed')),
        CONSTRAINT "FK_idempotency_records_users_actor_id" FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE refresh_tokens (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash character varying(64) NOT NULL,
        family_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        revoked_at timestamp with time zone,
        replaced_by_token_id uuid,
        CONSTRAINT "PK_refresh_tokens" PRIMARY KEY (id),
        CONSTRAINT "FK_refresh_tokens_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE transfers (
        id uuid NOT NULL,
        reference_number character varying(32) NOT NULL,
        kind character varying(16) NOT NULL,
        source_account_id uuid NOT NULL,
        destination_account_id uuid NOT NULL,
        amount_minor_units bigint NOT NULL,
        currency character(3) NOT NULL,
        status character varying(16) NOT NULL,
        failure_code character varying(64),
        initiated_by_user_id uuid NOT NULL,
        idempotency_key character varying(128) NOT NULL,
        request_fingerprint character varying(64) NOT NULL,
        reversal_of_transfer_id uuid,
        created_at timestamp with time zone NOT NULL,
        completed_at timestamp with time zone,
        CONSTRAINT "PK_transfers" PRIMARY KEY (id),
        CONSTRAINT ck_transfers_amount_positive CHECK (amount_minor_units > 0),
        CONSTRAINT ck_transfers_distinct_accounts CHECK (source_account_id <> destination_account_id),
        CONSTRAINT ck_transfers_kind CHECK (kind IN ('Internal','OpeningDeposit','Reversal')),
        CONSTRAINT ck_transfers_lifecycle CHECK ((status = 'Completed' AND completed_at IS NOT NULL AND failure_code IS NULL) OR (status = 'Rejected' AND completed_at IS NULL AND failure_code IS NOT NULL)),
        CONSTRAINT ck_transfers_reversal_link CHECK ((kind = 'Reversal') = (reversal_of_transfer_id IS NOT NULL)),
        CONSTRAINT ck_transfers_status CHECK (status IN ('Completed','Rejected')),
        CONSTRAINT ck_transfers_time_order CHECK (completed_at IS NULL OR completed_at >= created_at),
        CONSTRAINT "FK_transfers_accounts_destination_account_id" FOREIGN KEY (destination_account_id) REFERENCES accounts (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_transfers_accounts_source_account_id" FOREIGN KEY (source_account_id) REFERENCES accounts (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_transfers_transfers_reversal_of_transfer_id" FOREIGN KEY (reversal_of_transfer_id) REFERENCES transfers (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_transfers_users_initiated_by_user_id" FOREIGN KEY (initiated_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE journal_entries (
        id uuid NOT NULL,
        transfer_id uuid NOT NULL,
        reference_number character varying(32) NOT NULL,
        currency character(3) NOT NULL,
        description character varying(200) NOT NULL,
        posting_txid bigint NOT NULL DEFAULT (txid_current()),
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_journal_entries" PRIMARY KEY (id),
        CONSTRAINT "FK_journal_entries_transfers_transfer_id" FOREIGN KEY (transfer_id) REFERENCES transfers (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE TABLE ledger_lines (
        id uuid NOT NULL,
        journal_entry_id uuid NOT NULL,
        account_id uuid NOT NULL,
        line_number integer NOT NULL,
        debit_minor_units bigint NOT NULL,
        credit_minor_units bigint NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ledger_lines" PRIMARY KEY (id),
        CONSTRAINT ck_ledger_lines_nonnegative CHECK (debit_minor_units >= 0 AND credit_minor_units >= 0),
        CONSTRAINT ck_ledger_lines_one_sided CHECK ((debit_minor_units > 0 AND credit_minor_units = 0) OR (debit_minor_units = 0 AND credit_minor_units > 0)),
        CONSTRAINT "FK_ledger_lines_accounts_account_id" FOREIGN KEY (account_id) REFERENCES accounts (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_ledger_lines_journal_entries_journal_entry_id" FOREIGN KEY (journal_entry_id) REFERENCES journal_entries (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_accounts_account_number" ON accounts (account_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_accounts_customer_id" ON accounts (customer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_accounts_status" ON accounts (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_audit_logs_action_created_at" ON audit_logs (action, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_audit_logs_actor_id" ON audit_logs (actor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_audit_logs_created_at" ON audit_logs (created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_audit_logs_entity_type_entity_id" ON audit_logs (entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_customers_customer_number" ON customers (customer_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_idempotency_records_actor_id_idempotency_key" ON idempotency_records (actor_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_idempotency_records_expires_at" ON idempotency_records (expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_journal_entries_created_at" ON journal_entries (created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_journal_entries_reference_number" ON journal_entries (reference_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_journal_entries_transfer_id" ON journal_entries (transfer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_ledger_lines_account_id_created_at" ON ledger_lines (account_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_ledger_lines_journal_entry_id_line_number" ON ledger_lines (journal_entry_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_refresh_tokens_family_id" ON refresh_tokens (family_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_refresh_tokens_token_hash" ON refresh_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_refresh_tokens_user_id" ON refresh_tokens (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_transfers_created_at" ON transfers (created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_transfers_destination_account_id_created_at" ON transfers (destination_account_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_transfers_initiated_by_user_id_idempotency_key" ON transfers (initiated_by_user_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_transfers_reference_number" ON transfers (reference_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_transfers_source_account_id_created_at" ON transfers (source_account_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_transfers_status_created_at" ON transfers (status, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX ux_transfers_single_completed_reversal ON transfers (reversal_of_transfer_id) WHERE reversal_of_transfer_id IS NOT NULL AND status = 'Completed';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE INDEX "IX_users_customer_id" ON users (customer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_users_email" ON users (email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_users_username" ON users (username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116042958_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261116042958_InitialCreate', '10.0.15');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116043005_LedgerIntegrity') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116043005_LedgerIntegrity') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261116043005_LedgerIntegrity', '10.0.15');
    END IF;
END $EF$;
COMMIT;

