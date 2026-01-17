using System.ComponentModel;

namespace InfiniteGPU.Contracts.Models;

public enum TaskType
{
    [Description("Train")]
    Train = 0,

    [Description("Inference")]
    Inference = 1
}
