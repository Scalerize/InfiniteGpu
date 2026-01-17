using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InfiniteGPU.Backend.Data.Entities;

public class Device
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string DeviceIdentifier { get; set; } = string.Empty;

    [Required]
    [Column("ProviderUserId")]
    public string ProviderUserId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? DisplayName { get; set; }

    public bool IsConnected { get; set; }

    [MaxLength(128)]
    public string? LastConnectionId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastConnectedAtUtc { get; set; }

    public DateTime? LastDisconnectedAtUtc { get; set; }

    public DateTime? LastSeenAtUtc { get; set; }

    [ForeignKey(nameof(ProviderUserId))]
    public virtual ApplicationUser Provider { get; set; } = null!;

    public virtual ICollection<Subtask> Subtasks { get; set; } = new List<Subtask>();

    public virtual ICollection<DeviceModelCache> CachedModels { get; set; } = new List<DeviceModelCache>();
}