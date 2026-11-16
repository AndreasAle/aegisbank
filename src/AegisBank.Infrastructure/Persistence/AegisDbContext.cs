using System.Text;
using AegisBank.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AegisBank.Infrastructure.Persistence;

public sealed class AegisDbContext(DbContextOptions<AegisDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<LedgerLine> LedgerLines => Set<LedgerLine>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("ck_users_role", "role IN ('System','Customer','Auditor','Admin')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("customers", t => t.HasCheckConstraint("ck_customers_status", "status IN ('Active','Suspended')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.CustomerNumber).IsUnique();
        });

        b.Entity<Account>(e =>
        {
            e.ToTable("accounts", t =>
            {
                t.HasCheckConstraint("ck_accounts_balance_nonnegative", "available_balance_minor_units >= 0");
                t.HasCheckConstraint("ck_accounts_version_nonnegative", "version >= 0");
                t.HasCheckConstraint("ck_accounts_status", "status IN ('Active','Frozen','Closed')");
                t.HasCheckConstraint("ck_accounts_class", "class IN ('Asset','Liability')");
                t.HasCheckConstraint("ck_accounts_currency", "currency ~ '^[A-Z]{3}$'");
                // Customer deposit accounts are always liabilities of the bank.
                t.HasCheckConstraint("ck_accounts_customer_is_liability", "customer_id IS NULL OR class = 'Liability'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Class).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Ignore(x => x.NormalSide);
            e.Ignore(x => x.IsSystemAccount);
            e.HasIndex(x => x.AccountNumber).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Customer).WithMany(c => c.Accounts).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Transfer>(e =>
        {
            e.ToTable("transfers", t =>
            {
                t.HasCheckConstraint("ck_transfers_amount_positive", "amount_minor_units > 0");
                t.HasCheckConstraint("ck_transfers_distinct_accounts", "source_account_id <> destination_account_id");
                t.HasCheckConstraint("ck_transfers_status", "status IN ('Completed','Rejected')");
                t.HasCheckConstraint("ck_transfers_kind", "kind IN ('Internal','OpeningDeposit','Reversal')");
                // Lifecycle: a completed transfer has a completion time and no failure;
                // a rejected transfer has a failure code and no completion time.
                t.HasCheckConstraint("ck_transfers_lifecycle",
                    "(status = 'Completed' AND completed_at IS NOT NULL AND failure_code IS NULL) OR " +
                    "(status = 'Rejected' AND completed_at IS NULL AND failure_code IS NOT NULL)");
                t.HasCheckConstraint("ck_transfers_reversal_link", "(kind = 'Reversal') = (reversal_of_transfer_id IS NOT NULL)");
                t.HasCheckConstraint("ck_transfers_time_order", "completed_at IS NULL OR completed_at >= created_at");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.ReferenceNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(x => x.FailureCode).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.ReferenceNumber).IsUnique();
            // One logical transfer per (actor, key). Permanent barrier behind the idempotency table.
            e.HasIndex(x => new { x.InitiatedByUserId, x.IdempotencyKey }).IsUnique();
            // At most one completed reversal per original transfer.
            e.HasIndex(x => x.ReversalOfTransferId).IsUnique()
                .HasFilter("reversal_of_transfer_id IS NOT NULL AND status = 'Completed'")
                .HasDatabaseName("ux_transfers_single_completed_reversal");
            e.HasIndex(x => new { x.SourceAccountId, x.CreatedAt });
            e.HasIndex(x => new { x.DestinationAccountId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasOne(x => x.SourceAccount).WithMany().HasForeignKey(x => x.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DestinationAccount).WithMany().HasForeignKey(x => x.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.InitiatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transfer>().WithMany().HasForeignKey(x => x.ReversalOfTransferId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<JournalEntry>(e =>
        {
            e.ToTable("journal_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.ReferenceNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(x => x.Description).HasMaxLength(200).IsRequired();
            e.Property(x => x.PostingTxId).HasColumnName("posting_txid").HasDefaultValueSql("txid_current()").ValueGeneratedOnAdd();
            e.HasIndex(x => x.TransferId).IsUnique();
            e.HasIndex(x => x.ReferenceNumber).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Transfer).WithOne(t => t.JournalEntry).HasForeignKey<JournalEntry>(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LedgerLine>(e =>
        {
            e.ToTable("ledger_lines", t =>
            {
                t.HasCheckConstraint("ck_ledger_lines_nonnegative", "debit_minor_units >= 0 AND credit_minor_units >= 0");
                // A line is either a debit or a credit, never both and never empty.
                t.HasCheckConstraint("ck_ledger_lines_one_sided",
                    "(debit_minor_units > 0 AND credit_minor_units = 0) OR (debit_minor_units = 0 AND credit_minor_units > 0)");
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.JournalEntryId, x.LineNumber }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.CreatedAt });
            e.HasOne(x => x.JournalEntry).WithMany(j => j.Lines).HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Metadata).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Action, x.CreatedAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.ActorId);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records", t =>
            {
                t.HasCheckConstraint("ck_idempotency_status", "status IN ('InProgress','Completed')");
                t.HasCheckConstraint("ck_idempotency_completed_has_response",
                    "status <> 'Completed' OR (response_code IS NOT NULL AND response_body IS NOT NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ActorId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });

        ApplySnakeCaseColumnNames(b);
    }

    private static void ApplySnakeCaseColumnNames(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                // Respect explicit names (e.g. posting_txid).
                if (property.FindAnnotation("Relational:ColumnName") is null)
                    property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>Used by `dotnet ef` only. Reads the connection string from AEGIS_DESIGN_DB.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AegisDbContext>
{
    public AegisDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("AEGIS_DESIGN_DB")
            ?? "Host=localhost;Database=aegisbank;Username=aegis;Password=design-time-only";
        var options = new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection).Options;
        return new AegisDbContext(options);
    }
}
