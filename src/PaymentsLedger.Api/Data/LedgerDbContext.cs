using Microsoft.EntityFrameworkCore;
using PaymentsLedger.Api.Domain;

namespace PaymentsLedger.Api.Data;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerTransaction> Transactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            // Safety net: even if the service had a bug, the database refuses an overdraft.
            e.ToTable("Accounts", t => t.HasCheckConstraint("CK_Accounts_NoOverdraft", "[IsSystem] = 1 OR [Balance] >= 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Owner).HasMaxLength(200).IsRequired();

            e.HasData(new Account
            {
                Id = SystemAccount.Id,
                Owner = "SYSTEM",
                Balance = 0,
                IsSystem = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<LedgerTransaction>(e =>
        {
            e.ToTable("Transactions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(255);
            e.Property(x => x.RequestHash).HasMaxLength(64);

            // A retried request can never create a second transaction, even under a race.
            e.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasFilter("[IdempotencyKey] IS NOT NULL");

            e.HasMany(x => x.Entries)
                .WithOne()
                .HasForeignKey(x => x.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.ToTable("Entries", t => t.HasCheckConstraint("CK_Entries_NonZero", "[Amount] <> 0"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AccountId, x.Id });

            e.HasOne<Account>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
