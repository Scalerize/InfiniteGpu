using System.Text.Json;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace InfiniteGPU.Backend.Shared.Services;

/// <summary>
/// Manages subtask partitioning decisions and partition lifecycle.
/// </summary>
public class SubtaskPartitionManager
{
    private readonly AppDbContext _context;
    private readonly ILogger<SubtaskPartitionManager> _logger;

    // Configuration thresholds
    private const long DefaultPartitionThresholdBytes = 4L * 1024 * 1024 * 1024; // 4GB
    private const long MinimumPartitionSizeBytes = 512 * 1024 * 1024; // 512MB minimum per partition
    private const int MaxPartitionsPerSubtask = 8;
    private const long MinimumBandwidthBps = 100_000_000; // 100 Mbps minimum

    public SubtaskPartitionManager(
        AppDbContext context,
        ILogger<SubtaskPartitionManager> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes a subtask to determine if partitioning is needed and generates a partition plan.
    /// </summary>
    public async Task<PartitionPlan?> AnalyzeSubtaskAsync(
        Subtask subtask,
        IEnumerable<DeviceCandidate> availableDevices,
        long modelSizeBytes,
        CancellationToken cancellationToken = default)
    {
        var devices = availableDevices.ToList();

        _logger.LogInformation(
            "Analyzing subtask {SubtaskId} for partitioning. Model size: {ModelSizeMB} MB, Available devices: {DeviceCount}",
            subtask.Id,
            modelSizeBytes / (1024 * 1024),
            devices.Count);

        if (devices.Count == 0)
        {
            _logger.LogWarning("No devices available for partitioning analysis");
            return null;
        }

        // Find the device with the most memory
        var largestDevice = devices.OrderByDescending(d => d.AvailableMemoryBytes).First();

        // Check if model fits in the largest available device
        if (modelSizeBytes <= largestDevice.AvailableMemoryBytes * 0.8) // 80% threshold for safety
        {
            _logger.LogInformation(
                "Model fits in single device (largest has {DeviceMemoryMB} MB). No partitioning needed.",
                largestDevice.AvailableMemoryBytes / (1024 * 1024));

            return new PartitionPlan
            {
                RequiresPartitioning = false,
                TotalModelMemoryBytes = modelSizeBytes,
                PartitionCount = 1,
                Strategy = PartitionStrategy.None,
                Reason = "Model fits in single device memory"
            };
        }

        // Calculate how many partitions are needed
        var totalAvailableMemory = devices.Sum(d => d.AvailableMemoryBytes);
        
        if (totalAvailableMemory < modelSizeBytes)
        {
            _logger.LogWarning(
                "Total available memory ({TotalMemoryMB} MB) is less than model size ({ModelSizeMB} MB)",
                totalAvailableMemory / (1024 * 1024),
                modelSizeBytes / (1024 * 1024));
            return null;
        }

        // Estimate partition count based on available devices and model size
        var partitionCount = Math.Min(
            Math.Max(2, (int)Math.Ceiling((double)modelSizeBytes / DefaultPartitionThresholdBytes)),
            Math.Min(devices.Count, MaxPartitionsPerSubtask));

        // Select parent peer (best bandwidth/latency for downloading full model)
        var parentPeer = SelectParentPeer(devices);

        var plan = new PartitionPlan
        {
            RequiresPartitioning = true,
            TotalModelMemoryBytes = modelSizeBytes,
            PartitionCount = partitionCount,
            Strategy = PartitionStrategy.PipelineParallelism,
            Reason = $"Model ({modelSizeBytes / (1024 * 1024)} MB) exceeds single device capacity ({largestDevice.AvailableMemoryBytes / (1024 * 1024)} MB)",
            MinimumBandwidthBps = MinimumBandwidthBps,
            MaximumLatencyMs = 100,
            ParentPeerDeviceId = parentPeer.DeviceId,
            ParentPeerPartitionIndex = 0, // Parent peer handles first partition
            OnnxFullModelBlobUri = subtask.OnnxModelBlobUri,
            DeviceNetworkInfos = devices.Select((d, idx) => new DeviceCandidateNetworkInfo
            {
                DeviceId = d.DeviceId,
                ConnectionId = d.ConnectionId ?? "",
                PartitionIndex = idx,
                EstimatedBandwidthBps = d.EstimatedBandwidthBps,
                EstimatedLatencyMs = d.EstimatedLatencyMs,
                AvailableMemoryBytes = d.AvailableMemoryBytes
            }).ToList()
        };

        // Create partition definitions with estimated resource requirements
        var memoryPerPartition = modelSizeBytes / partitionCount;
        var computePerPartition = modelSizeBytes / partitionCount; // Simplified - in reality this would come from model analysis

        for (int i = 0; i < partitionCount; i++)
        {
            plan.Partitions.Add(new PartitionDefinition
            {
                PartitionIndex = i,
                EstimatedMemoryBytes = memoryPerPartition,
                EstimatedComputeCost = computePerPartition,
                EstimatedOutputBytes = memoryPerPartition / 10, // Estimated intermediate tensor size
                IsEntryPoint = i == 0,
                IsExitPoint = i == partitionCount - 1,
                MinimumDeviceMemoryBytes = (long)(memoryPerPartition * 1.2) // 20% overhead
            });
        }

        _logger.LogInformation(
            "Generated partition plan for subtask {SubtaskId}: {PartitionCount} partitions using {Strategy}",
            subtask.Id,
            partitionCount,
            plan.Strategy);

        return plan;
    }

    /// <summary>
    /// Creates partition records in the database for a subtask based on the partition plan.
    /// </summary>
    public async Task<IEnumerable<Partition>> CreatePartitionsAsync(
        Subtask subtask,
        PartitionPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!plan.RequiresPartitioning || plan.PartitionCount <= 1)
        {
            _logger.LogInformation("Subtask {SubtaskId} does not require partitioning", subtask.Id);
            return Enumerable.Empty<Partition>();
        }

        _logger.LogInformation(
            "Creating {PartitionCount} partitions for subtask {SubtaskId}",
            plan.PartitionCount,
            subtask.Id);

        var partitions = new List<Partition>();
        Partition? previousPartition = null;
        Partition? parentPartition = null;

        foreach (var definition in plan.Partitions.OrderBy(p => p.PartitionIndex))
        {
            var isParent = definition.PartitionIndex == plan.ParentPeerPartitionIndex;

            var partition = new Partition
            {
                SubtaskId = subtask.Id,
                PartitionIndex = definition.PartitionIndex,
                TotalPartitions = plan.PartitionCount,
                Status = PartitionStatus.Pending,
                EstimatedMemoryBytes = definition.EstimatedMemoryBytes,
                EstimatedComputeCost = definition.EstimatedComputeCost,
                EstimatedTransferBytes = definition.EstimatedOutputBytes,
                InputTensorNamesJson = JsonSerializer.Serialize(definition.InputTensorNames),
                OutputTensorNamesJson = JsonSerializer.Serialize(definition.OutputTensorNames),
                CreatedAtUtc = DateTime.UtcNow,
                // Parent peer gets full model URI, child peers receive subgraph via WebRTC
                IsParentPeer = isParent,
                OnnxFullModelBlobUri = isParent ? plan.OnnxFullModelBlobUri : null,
                SubgraphReceiveStatus = isParent
                    ? SubgraphReceiveStatus.NotApplicable
                    : SubgraphReceiveStatus.WaitingForConnection
            };

            if (isParent)
            {
                parentPartition = partition;
            }

            // Link to previous partition
            if (previousPartition is not null)
            {
                partition.UpstreamPartitionId = previousPartition.Id;
            }

            _context.Partitions.Add(partition);
            partitions.Add(partition);

            // Update previous partition's downstream link
            if (previousPartition is not null)
            {
                previousPartition.DownstreamPartitionId = partition.Id;
            }

            previousPartition = partition;
        }

        // Update subtask with partitioning info
        // Note: PartitionCount is computed from Partitions.Count() automatically
        subtask.RequiresPartitioning = true;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created {Count} partitions for subtask {SubtaskId}",
            partitions.Count,
            subtask.Id);

        return partitions;
    }

    /// <summary>
    /// Assigns partitions to devices based on capabilities and network topology.
    /// </summary>
    public async Task<PartitionAssignmentResult> AssignPartitionsToDevicesAsync(
        IEnumerable<Partition> partitions,
        IEnumerable<DeviceCandidate> availableDevices,
        CancellationToken cancellationToken = default)
    {
        var partitionList = partitions.OrderBy(p => p.PartitionIndex).ToList();
        var deviceList = availableDevices.Where(d => d.IsConnected).ToList();

        _logger.LogInformation(
            "Assigning {PartitionCount} partitions to {DeviceCount} available devices",
            partitionList.Count,
            deviceList.Count);

        if (deviceList.Count < partitionList.Count)
        {
            return new PartitionAssignmentResult
            {
                Success = false,
                FailureReason = $"Not enough connected devices ({deviceList.Count}) for partitions ({partitionList.Count})"
            };
        }

        var assignments = new List<PartitionDeviceAssignment>();
        var usedDevices = new HashSet<Guid>();

        foreach (var partition in partitionList)
        {
            // Find best device for this partition
            var bestDevice = deviceList
                .Where(d => !usedDevices.Contains(d.DeviceId))
                .Where(d => d.AvailableMemoryBytes >= partition.EstimatedMemoryBytes)
                .OrderByDescending(d => CalculateDeviceScore(d, partition))
                .FirstOrDefault();

            if (bestDevice is null)
            {
                return new PartitionAssignmentResult
                {
                    Success = false,
                    FailureReason = $"No suitable device found for partition {partition.PartitionIndex} (requires {partition.EstimatedMemoryBytes / (1024 * 1024)} MB)"
                };
            }

            // Assign partition to device
            partition.DeviceId = bestDevice.DeviceId;
            partition.AssignedProviderId = bestDevice.ProviderId;
            partition.ConnectionId = bestDevice.ConnectionId;
            partition.Status = PartitionStatus.Assigned;
            partition.AssignedAtUtc = DateTime.UtcNow;

            assignments.Add(new PartitionDeviceAssignment
            {
                PartitionId = partition.Id,
                PartitionIndex = partition.PartitionIndex,
                DeviceId = bestDevice.DeviceId,
                ProviderId = bestDevice.ProviderId,
                ConnectionId = bestDevice.ConnectionId ?? ""
            });

            usedDevices.Add(bestDevice.DeviceId);

            _logger.LogInformation(
                "Assigned partition {PartitionIndex} to device {DeviceId} (provider: {ProviderId})",
                partition.PartitionIndex,
                bestDevice.DeviceId,
                bestDevice.ProviderId);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new PartitionAssignmentResult
        {
            Success = true,
            Assignments = assignments
        };
    }

    /// <summary>
    /// Gets all partitions for a subtask.
    /// </summary>
    public async Task<List<Partition>> GetPartitionsForSubtaskAsync(
        Guid subtaskId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId)
            .OrderBy(p => p.PartitionIndex)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Updates partition status and tracks progress.
    /// </summary>
    public async Task<Partition?> UpdatePartitionStatusAsync(
        Guid partitionId,
        PartitionStatus newStatus,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        var partition = await _context.Partitions.FindAsync(new object[] { partitionId }, cancellationToken);
        if (partition is null)
        {
            return null;
        }

        partition.Status = newStatus;

        switch (newStatus)
        {
            case PartitionStatus.Executing:
                partition.StartedAtUtc = DateTime.UtcNow;
                break;
            case PartitionStatus.Completed:
                partition.CompletedAtUtc = DateTime.UtcNow;
                if (partition.StartedAtUtc.HasValue)
                {
                    partition.DurationSeconds = (DateTime.UtcNow - partition.StartedAtUtc.Value).TotalSeconds;
                }
                partition.Progress = 100;
                break;
            case PartitionStatus.Failed:
                partition.FailedAtUtc = DateTime.UtcNow;
                partition.FailureReason = failureReason;
                break;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return partition;
    }

    /// <summary>
    /// Checks if all partitions in a subtask are completed.
    /// </summary>
    public async Task<bool> AreAllPartitionsCompletedAsync(
        Guid subtaskId,
        CancellationToken cancellationToken = default)
    {
        var partitions = await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId)
            .ToListAsync(cancellationToken);

        return partitions.All(p => p.Status == PartitionStatus.Completed);
    }

    /// <summary>
    /// Cancels all incomplete partitions for a subtask.
    /// </summary>
    public async Task CancelPartitionsAsync(
        Guid subtaskId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var partitions = await _context.Partitions
            .Where(p => p.SubtaskId == subtaskId)
            .Where(p => p.Status != PartitionStatus.Completed && p.Status != PartitionStatus.Failed)
            .ToListAsync(cancellationToken);

        foreach (var partition in partitions)
        {
            partition.Status = PartitionStatus.Cancelled;
            partition.FailureReason = reason;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Cancelled {Count} partitions for subtask {SubtaskId}: {Reason}",
            partitions.Count,
            subtaskId,
            reason);
    }

    /// <summary>
    /// Calculates a score for how suitable a device is for a specific partition.
    /// </summary>
    private static double CalculateDeviceScore(DeviceCandidate device, Partition partition)
    {
        // Score based on:
        // 1. Available memory (more is better)
        // 2. Compute capability (more is better)
        // Memory headroom factor (prefer devices with comfortable memory margin)
        var memoryHeadroom = (double)device.AvailableMemoryBytes / partition.EstimatedMemoryBytes;
        var memoryScore = Math.Min(memoryHeadroom, 2.0); // Cap at 2x to not overly favor huge devices

        // Compute score (normalized)
        var computeScore = device.ComputeCapability / 1_000_000_000.0; // Normalize to GFLOPS

        // Combined score
        return memoryScore * 0.6 + computeScore * 0.4;
    }

    /// <summary>
    /// Selects the best device to be the parent peer based on network quality.
    /// Parent peer is responsible for downloading the full model and distributing subgraphs.
    /// </summary>
    private DeviceCandidate SelectParentPeer(List<DeviceCandidate> devices)
    {
        if (devices.Count == 1)
        {
            return devices[0];
        }

        // Calculate parent peer score for each device
        foreach (var device in devices)
        {
            device.ParentPeerScore = CalculateParentPeerScore(device);
        }

        // Select device with highest parent peer score
        var bestDevice = devices.OrderByDescending(d => d.ParentPeerScore).First();

        _logger.LogInformation(
            "Selected device {DeviceId} as parent peer (score: {Score:F2}, bandwidth: {BandwidthMbps} Mbps, latency: {LatencyMs} ms)",
            bestDevice.DeviceId,
            bestDevice.ParentPeerScore,
            bestDevice.EstimatedBandwidthBps / 1_000_000,
            bestDevice.EstimatedLatencyMs);

        return bestDevice;
    }

    /// <summary>
    /// Calculates a score for how suitable a device is to be the parent peer.
    /// Higher score is better. Considers bandwidth, latency, memory, and compute.
    /// </summary>
    private static double CalculateParentPeerScore(DeviceCandidate device)
    {
        // Parent peer needs:
        // 1. High bandwidth to download full model quickly
        // 2. Low latency for WebRTC signaling
        // 3. Sufficient memory to hold full model temporarily during partitioning
        // 4. Good compute for partitioning operation

        // Bandwidth score: normalized to 1Gbps (1_000_000_000 bps)
        var bandwidthScore = Math.Min(device.EstimatedBandwidthBps / 1_000_000_000.0, 1.0);

        // Latency score: inversely proportional, lower is better
        // Assume max acceptable latency is 200ms, perfect is <20ms
        var latencyScore = device.EstimatedLatencyMs > 0
            ? Math.Max(0, 1.0 - (device.EstimatedLatencyMs / 200.0))
            : 0.5; // Default score if no latency info

        // Memory score: normalized to 16GB
        var memoryScore = Math.Min(device.AvailableMemoryBytes / (16L * 1024 * 1024 * 1024), 1.0);

        // Compute score: normalized to 100 TFLOPS
        var computeScore = Math.Min(device.ComputeCapability / 100_000_000_000_000.0, 1.0);

        // Weighted combination:
        // - Bandwidth is most important for downloading full model (40%)
        // - Memory is important for holding model during partitioning (30%)
        // - Latency affects responsiveness (20%)
        // - Compute affects partitioning speed (10%)
        return bandwidthScore * 0.40 +
               memoryScore * 0.30 +
               latencyScore * 0.20 +
               computeScore * 0.10;
    }
}
