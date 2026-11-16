using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisBank.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Triggers that keep the ledger immutable and balanced, plus the
    /// well-known system rows. The SQL lives in <see cref="LedgerIntegritySql"/>.
    /// </summary>
    public partial class LedgerIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LedgerIntegritySql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LedgerIntegritySql.Down);
        }
    }
}
