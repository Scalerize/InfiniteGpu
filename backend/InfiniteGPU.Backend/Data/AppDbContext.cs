using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using InfiniteGPU.Backend.Data.Entities;
using TaskEntity = InfiniteGPU.Backend.Data.Entities.Task;

namespace InfiniteGPU.Backend.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<TaskEntity> Tasks { get; set; } = null!;
    public DbSet<Subtask> Subtasks { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<Earning> Earnings { get; set; } = null!;
    public DbSet<Withdrawal> Withdrawals { get; set; } = null!;
    public DbSet<Settlement> Settlements { get; set; } = null!;
    public DbSet<Device> Devices { get; set; } = null!;
    public DbSet<ApiKey> ApiKeys { get; set; } = null!;
    public DbSet<SubtaskTimelineEvent> SubtaskTimelineEvents { get; set; } = null!;
    public DbSet<ProviderModelCache> ProviderModelCaches { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<TaskEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.OnnxModelBlobUri);
            entity.HasIndex(e => e.LastProgressAtUtc);
            entity.HasIndex(e => e.LastHeartbeatAtUtc);
            entity.Property(e => e.CompletionPercent)
                .HasColumnType("decimal(5,2)")
                .HasDefaultValue(0m);
            entity.Property(e => e.RowVersion)
                .IsRowVersion();
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.Subtasks)
                .WithOne(s => s.Task)
                .HasForeignKey(s => s.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Withdrawals)
                .WithOne(w => w.Task)
                .HasForeignKey(w => w.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.OwnsMany(e => e.InferenceBindings, navigationBuilder =>
            {
                navigationBuilder.ToTable("TaskInferenceBindings");
                navigationBuilder.WithOwner().HasForeignKey("TaskId");
                navigationBuilder.Property<Guid>("Id");
                navigationBuilder.HasKey("Id");
                navigationBuilder.Property(b => b.TensorName).HasMaxLength(256);
                navigationBuilder.Property(b => b.Payload).HasColumnType("nvarchar(max)");
                navigationBuilder.Property(b => b.FileUrl).HasMaxLength(2048);
                navigationBuilder.Property(b => b.PayloadType);
            });

            entity.OwnsMany(e => e.OutputBindings, navigationBuilder =>
            {
                navigationBuilder.ToTable("TaskOutputBindings");
                navigationBuilder.WithOwner().HasForeignKey("TaskId");
                navigationBuilder.Property<Guid>("Id");
                navigationBuilder.HasKey("Id");
                navigationBuilder.Property(b => b.TensorName).HasMaxLength(256);
                navigationBuilder.Property(b => b.PayloadType); 
                navigationBuilder.Property(b => b.FileUrl).HasMaxLength(2048);
                navigationBuilder.Property(b => b.FileFormat);
            });
        });

        builder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.Prefix }).IsUnique();
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.Prefix).HasMaxLength(16);
            entity.Property(e => e.Description).HasMaxLength(256);
            entity.Property(e => e.Key).HasMaxLength(128);
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(e => e.LastUsedAtUtc).HasColumnType("datetime2");
            entity.HasOne(e => e.User)
                .WithMany(u => u.ApiKeys)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Device>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceIdentifier).IsUnique();
            entity.HasIndex(e => e.ProviderUserId);
            entity.Property(e => e.DeviceIdentifier)
                .HasMaxLength(128);
            entity.Property(e => e.DisplayName)
                .HasMaxLength(256);
            entity.Property(e => e.LastConnectionId)
                .HasMaxLength(128);
            entity.Property(e => e.CreatedAtUtc)
                .HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(e => e.LastConnectedAtUtc)
                .HasColumnType("datetime2");
            entity.Property(e => e.LastDisconnectedAtUtc)
                .HasColumnType("datetime2");
            entity.Property(e => e.LastSeenAtUtc)
                .HasColumnType("datetime2");
            entity.HasOne(e => e.Provider)
                .WithMany()
                .HasForeignKey(e => e.ProviderUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProviderModelCache>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProviderUserId, e.StoredFileName })
                .IsUnique();
            entity.Property(e => e.StoredFileName)
                .HasMaxLength(256);
            entity.HasOne(e => e.Provider)
                .WithMany()
                .HasForeignKey(e => e.ProviderUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Subtask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId);
            entity.HasIndex(e => e.AssignedProviderId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.RequiresReassignment);
            entity.HasIndex(e => e.NextHeartbeatDueAtUtc);
            entity.HasIndex(e => e.DeviceId);
            entity.Property(e => e.OnnxModelBlobUri)
                .HasMaxLength(2048);
            entity.Property(e => e.FailureReason)
                .HasMaxLength(2048);
            entity.Property(e => e.RowVersion)
                .IsRowVersion();
            entity.HasOne(e => e.Task)
                .WithMany(t => t.Subtasks)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AssignedProvider)
                .WithMany()
                .HasForeignKey(e => e.AssignedProviderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Device)
                .WithMany(d => d.Subtasks)
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId)
                .HasMaxLength(450);
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18,6)");
            entity.Property(e => e.CreatedAtUtc)
                .HasColumnType("datetime2")
                .HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(e => e.UpdatedAtUtc)
                .HasColumnType("datetime2");
            entity.Property(e => e.SettledAtUtc)
                .HasColumnType("datetime2");
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAtUtc);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.StripeId);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SubtaskTimelineEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SubtaskId);
            entity.HasIndex(e => e.EventType);
            entity.Property(e => e.EventType)
                .HasMaxLength(128);
            entity.Property(e => e.Message)
                .HasMaxLength(2048);
            entity.Property(e => e.MetadataJson)
                .HasColumnType("nvarchar(max)");
            entity.Property(e => e.CreatedAtUtc)
                .HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasOne(e => e.Subtask)
                .WithMany(s => s.TimelineEvents)
                .HasForeignKey(e => e.SubtaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Earning>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18,6)");
            entity.Property(e => e.CreatedAtUtc)
                .HasColumnType("datetime2")
                .HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(e => e.UpdatedAtUtc)
                .HasColumnType("datetime2");
            entity.Property(e => e.PaidAtUtc)
                .HasColumnType("datetime2");
            entity.HasIndex(e => e.CreatedAtUtc);
            entity.HasIndex(e => e.ProviderUserId);
            entity.HasIndex(e => e.TaskId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SubtaskId);
            entity.HasOne(e => e.Provider)
                .WithMany()
                .HasForeignKey(e => e.ProviderUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Task)
                .WithMany()
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Subtask)
                .WithMany(s => s.Earnings)
                .HasForeignKey(e => e.SubtaskId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Withdrawal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18,6)");
            entity.Property(e => e.CreatedAtUtc)
                .HasColumnType("datetime2")
                .HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(e => e.UpdatedAtUtc)
                .HasColumnType("datetime2");
            entity.Property(e => e.SettledAtUtc)
                .HasColumnType("datetime2");
            entity.HasIndex(e => e.CreatedAtUtc);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.RequestorUserId);
            entity.HasIndex(e => e.TaskId);
            entity.HasIndex(e => e.SubtaskId);
            entity.HasOne(e => e.Requestor)
                .WithMany()
                .HasForeignKey(e => e.RequestorUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Task)
                .WithMany(t => t.Withdrawals)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.Subtask)
                .WithMany(s => s.Withdrawals)
                .HasForeignKey(e => e.SubtaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.HasIndex(e => e.IsActive);
            entity.Property(e => e.Balance)
                .HasColumnType("decimal(18,6)")
                .HasDefaultValue(0m);
            
            // Configure UserAddress as an owned entity (stored in same table with prefixed columns)
            entity.OwnsOne(e => e.Address, addressBuilder =>
            {
                addressBuilder.Property(a => a.Line1).HasColumnName("Address_Line1");
                addressBuilder.Property(a => a.Line2).HasColumnName("Address_Line2");
                addressBuilder.Property(a => a.City).HasColumnName("Address_City");
                addressBuilder.Property(a => a.State).HasColumnName("Address_State");
                addressBuilder.Property(a => a.PostalCode).HasColumnName("Address_PostalCode");
                addressBuilder.Property(a => a.Country).HasColumnName("Address_Country");
            });
        });

        builder.Entity<Settlement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18,6)");
            entity.Property(e => e.BankAccountDetails)
                .HasMaxLength(1024);
            entity.Property(e => e.StripeTransferId)
                .HasMaxLength(256);
            entity.Property(e => e.FailureReason)
                .HasMaxLength(2048);
            entity.Property(e => e.CreatedAtUtc)
                .HasColumnType("datetime2")
                .HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(e => e.UpdatedAtUtc)
                .HasColumnType("datetime2");
            entity.Property(e => e.CompletedAtUtc)
                .HasColumnType("datetime2");
            entity.HasIndex(e => e.CreatedAtUtc);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
