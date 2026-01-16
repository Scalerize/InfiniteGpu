using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using InfiniteGPU.Contracts.Models;
using SubtaskStatusEnum = InfiniteGPU.Contracts.Models.SubtaskStatus;

namespace InfiniteGPU.Backend.Data.Entities;

public class Subtask
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid TaskId { get; set; }

    [Column("ProviderUserId")]
    public string? AssignedProviderId { get; set; }

    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Stored as integer representation of <see cref="SubtaskStatus" />.
    /// </summary>
    public SubtaskStatusEnum Status { get; set; } = SubtaskStatusEnum.Pending;

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int Progress { get; set; } = 0;

    [Column(TypeName = "nvarchar(max)")]
    public string Params { get; set; } = string.Empty;

    [Column(TypeName = "nvarchar(max)")]
    public string ExecutionSpecJson { get; set; } = string.Empty;

    /// <summary>
    /// Immutable reference to the ONNX model blob URI snapshot at assignment time.
    /// </summary>
    [MaxLength(2048)]
    public string? OnnxModelBlobUri { get; set; }

    [Column(TypeName = "nvarchar(max)")]
    public string? ExecutionStateJson { get; set; }

    [Column("ResultData", TypeName = "nvarchar(max)")]
    public string? ResultsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? AssignedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public double? DurationSeconds { get; set; }

    [Column(TypeName = "decimal(18,6)")]
    public decimal? CostUsd { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    [NotMapped]
    public DateTime? LastHeartbeatAt
    {
        get => LastHeartbeatAtUtc;
        set => LastHeartbeatAtUtc = value;
    }

    public DateTime? NextHeartbeatDueAtUtc { get; set; }

    public DateTime? LastCommandAtUtc { get; set; }

    [NotMapped]
    public DateTime? LastCommandAt
    {
        get => LastCommandAtUtc;
        set => LastCommandAtUtc = value;
    }

    [MaxLength(2048)]
    public string? FailureReason { get; set; }

    public DateTime? FailedAtUtc { get; set; }

    public bool RequiresReassignment { get; set; }

    public DateTime? ReassignmentRequestedAtUtc { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    [ForeignKey(nameof(TaskId))]
    public virtual Task Task { get; set; } = null!;

    [ForeignKey(nameof(AssignedProviderId))]
    public virtual ApplicationUser? AssignedProvider { get; set; }

    [ForeignKey(nameof(DeviceId))]
    public virtual Device? Device { get; set; }

    public virtual ICollection<SubtaskTimelineEvent> TimelineEvents { get; set; } = new List<SubtaskTimelineEvent>();

    public virtual ICollection<Earning> Earnings { get; set; } = new List<Earning>();

    public virtual ICollection<Withdrawal> Withdrawals { get; set; } = new List<Withdrawal>();
}

