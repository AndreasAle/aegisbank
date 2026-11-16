using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisBank.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    full_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                    table.CheckConstraint("ck_customers_status", "status IN ('Active','Suspended')");
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    @class = table.Column<string>(name: "class", type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    available_balance_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_balance_nonnegative", "available_balance_minor_units >= 0");
                    table.CheckConstraint("ck_accounts_class", "class IN ('Asset','Liability')");
                    table.CheckConstraint("ck_accounts_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_accounts_customer_is_liability", "customer_id IS NULL OR class = 'Liability'");
                    table.CheckConstraint("ck_accounts_status", "status IN ('Active','Frozen','Closed')");
                    table.CheckConstraint("ck_accounts_version_nonnegative", "version >= 0");
                    table.ForeignKey(
                        name: "FK_accounts_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('System','Customer','Auditor','Admin')");
                    table.ForeignKey(
                        name: "FK_users_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    response_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                    table.CheckConstraint("ck_idempotency_completed_has_response", "status <> 'Completed' OR (response_code IS NOT NULL AND response_body IS NOT NULL)");
                    table.CheckConstraint("ck_idempotency_status", "status IN ('InProgress','Completed')");
                    table.ForeignKey(
                        name: "FK_idempotency_records_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reversal_of_transfer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfers", x => x.id);
                    table.CheckConstraint("ck_transfers_amount_positive", "amount_minor_units > 0");
                    table.CheckConstraint("ck_transfers_distinct_accounts", "source_account_id <> destination_account_id");
                    table.CheckConstraint("ck_transfers_kind", "kind IN ('Internal','OpeningDeposit','Reversal')");
                    table.CheckConstraint("ck_transfers_lifecycle", "(status = 'Completed' AND completed_at IS NOT NULL AND failure_code IS NULL) OR (status = 'Rejected' AND completed_at IS NULL AND failure_code IS NOT NULL)");
                    table.CheckConstraint("ck_transfers_reversal_link", "(kind = 'Reversal') = (reversal_of_transfer_id IS NOT NULL)");
                    table.CheckConstraint("ck_transfers_status", "status IN ('Completed','Rejected')");
                    table.CheckConstraint("ck_transfers_time_order", "completed_at IS NULL OR completed_at >= created_at");
                    table.ForeignKey(
                        name: "FK_transfers_accounts_destination_account_id",
                        column: x => x.destination_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_transfers_accounts_source_account_id",
                        column: x => x.source_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_transfers_transfers_reversal_of_transfer_id",
                        column: x => x.reversal_of_transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_transfers_users_initiated_by_user_id",
                        column: x => x.initiated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    posting_txid = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "txid_current()"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_journal_entries_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    debit_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    credit_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_lines", x => x.id);
                    table.CheckConstraint("ck_ledger_lines_nonnegative", "debit_minor_units >= 0 AND credit_minor_units >= 0");
                    table.CheckConstraint("ck_ledger_lines_one_sided", "(debit_minor_units > 0 AND credit_minor_units = 0) OR (debit_minor_units = 0 AND credit_minor_units > 0)");
                    table.ForeignKey(
                        name: "FK_ledger_lines_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ledger_lines_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_account_number",
                table: "accounts",
                column: "account_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_accounts_customer_id",
                table: "accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_status",
                table: "accounts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_action_created_at",
                table: "audit_logs",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_actor_id",
                table: "audit_logs",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_created_at",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_entity_type_entity_id",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_customer_number",
                table: "customers",
                column: "customer_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_actor_id_idempotency_key",
                table: "idempotency_records",
                columns: new[] { "actor_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_expires_at",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_created_at",
                table: "journal_entries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_reference_number",
                table: "journal_entries",
                column: "reference_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_transfer_id",
                table: "journal_entries",
                column: "transfer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_lines_account_id_created_at",
                table: "ledger_lines",
                columns: new[] { "account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_lines_journal_entry_id_line_number",
                table: "ledger_lines",
                columns: new[] { "journal_entry_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_family_id",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_created_at",
                table: "transfers",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_destination_account_id_created_at",
                table: "transfers",
                columns: new[] { "destination_account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_initiated_by_user_id_idempotency_key",
                table: "transfers",
                columns: new[] { "initiated_by_user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfers_reference_number",
                table: "transfers",
                column: "reference_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfers_source_account_id_created_at",
                table: "transfers",
                columns: new[] { "source_account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_status_created_at",
                table: "transfers",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_transfers_single_completed_reversal",
                table: "transfers",
                column: "reversal_of_transfer_id",
                unique: true,
                filter: "reversal_of_transfer_id IS NOT NULL AND status = 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_users_customer_id",
                table: "users",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "ledger_lines");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
