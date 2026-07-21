using System.Globalization;
using HyperVaultManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HyperVaultManager.Data;

public class ManagerDbContext : DbContext
{
    public ManagerDbContext(DbContextOptions<ManagerDbContext> options) : base(options) { }

    public DbSet<HyperVHost> Hosts => Set<HyperVHost>();
    public DbSet<VirtualMachine> VirtualMachines => Set<VirtualMachine>();
    public DbSet<StorageTarget> Storages => Set<StorageTarget>();
    public DbSet<BackupJob> Jobs => Set<BackupJob>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<VerificationRun> VerificationRuns => Set<VerificationRun>();
    public DbSet<RestoreRun> RestoreRuns => Set<RestoreRun>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<VmTag> VmTags => Set<VmTag>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<HyperVHost>().HasIndex(x => x.Name).IsUnique();
        b.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<HyperVHost>().Property(x => x.ApiToken).IsRequired();

        // Tags: reusable catalog + NxN with VirtualMachine.
        b.Entity<Tag>().HasIndex(x => x.Key).IsUnique();
        b.Entity<Tag>().Property(x => x.Key).IsRequired();
        b.Entity<Tag>().Property(x => x.Label).IsRequired();
        b.Entity<VmTag>().HasKey(x => new { x.VmId, x.TagId });
        b.Entity<VmTag>().HasOne(x => x.Vm).WithMany(v => v.VmTags)
            .HasForeignKey(x => x.VmId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<VmTag>().HasOne(x => x.Tag).WithMany(t => t.VmTags)
            .HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<VirtualMachine>().HasIndex(x => new { x.HostId, x.ExternalId }).IsUnique();
        b.Entity<VirtualMachine>().HasOne(x => x.Host)
            .WithMany(h => h.VirtualMachines).HasForeignKey(x => x.HostId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<StorageTarget>().Property(x => x.Type).IsRequired().HasDefaultValue(StorageTypes.LocalPath);

        b.Entity<BackupJob>().HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupJob>().HasOne(x => x.Vm).WithMany().HasForeignKey(x => x.VmId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupJob>().HasOne(x => x.Storage).WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<BackupRun>().HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BackupRun>().HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupRun>().HasOne(x => x.Vm).WithMany().HasForeignKey(x => x.VmId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupRun>().HasOne(x => x.Storage).WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<VerificationRun>().HasOne(x => x.Host).WithMany()
            .HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VerificationRun>().HasOne(x => x.BackupRun).WithMany(r => r.Verifications)
            .HasForeignKey(x => x.BackupRunId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<RestoreRun>().HasOne(x => x.BackupRun).WithMany()
            .HasForeignKey(x => x.BackupRunId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<RestoreRun>().HasOne(x => x.TargetHost).WithMany()
            .HasForeignKey(x => x.TargetHostId).OnDelete(DeleteBehavior.Restrict);

        // SQLite cannot ORDER BY / compare DateTimeOffset expressions directly.
        // Store every DateTimeOffset property as an ISO-8601 round-trip string,
        // which sorts lexicographically the same as chronologically.
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            foreach (var prop in entityType.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                {
                    prop.SetValueConverter(new ValueConverter<DateTimeOffset, string>(
                        v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        v => string.IsNullOrWhiteSpace(v)
                            ? DateTimeOffset.MinValue
                            : DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }
            }
        }
    }
}
