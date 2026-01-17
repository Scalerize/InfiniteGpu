using System.Collections.Generic;
using InfiniteGPU.Contracts.Models;
using TypedSignalR.Client;

namespace InfiniteGPU.Contracts;

[Hub]
public interface ITaskHub
{
    Task JoinAvailableTasks(string userId, string role, HardwareCapabilitiesDto? hardwareCapabilities = null);
    Task JoinTask(Guid taskId);
    Task LeaveTask(Guid taskId);
    Task BroadcastAvailableTasks();
    Task AcceptSubtask(Guid subtaskId);
    Task ReportProgress(Guid subtaskId, int progress);
    Task AcknowledgeExecutionStart(Guid subtaskId);
    Task SubmitResult(SubmitResultDto result);
    Task FailedResult(FailedResultDto result);
    Task ReportCachedModels(IReadOnlyList<string> modelUrls);
}
