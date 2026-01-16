using InfiniteGPU.Contracts.Models;
using TypedSignalR.Client;

namespace InfiniteGPU.Contracts;

[Receiver]
public interface ITaskHubClient
{
    Task OnSubtaskAccepted(SubtaskDto subtask);
    Task OnProgressUpdate(SubtaskProgressUpdateDto payload);
    Task OnExecutionRequested(ExecutionRequestedDto payload);
    Task OnExecutionAcknowledged(ExecutionAcknowledgedDto payload);
    Task OnComplete(SubtaskCompletionDto payload);
    Task OnFailure(SubtaskFailureDto payload);
    Task OnAvailableSubtasksChanged(AvailableSubtasksChangedDto payload);
    Task TaskUpdated(TaskDto task);
    Task TaskCompleted(TaskDto task);
    Task TaskFailed(TaskDto task);
}
