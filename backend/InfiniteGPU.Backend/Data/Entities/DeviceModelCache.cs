using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InfiniteGPU.Backend.Data.Entities;

/// <summary>
/// Tracks which ONNX models are cached on a specific provider device.
/// Used for priority dispatch of inference tasks to devices that already have the model cached.
/// </summary>
public sealed class DeviceModelCache
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid DeviceId { get; set; }

    [Required]
    [MaxLength(2048)]
    public string ModelUrl { get; set; } = string.Empty;

    public DateTime CachedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastAccessedAtUtc { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(DeviceId))]
    public Device Device { get; set; } = null!;
}
