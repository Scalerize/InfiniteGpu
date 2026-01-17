using System.ComponentModel;

namespace InfiniteGPU.Contracts.Models;

public enum SubtaskStatus
{
    [Description("Pending")]
    Pending = 0,

    [Description("Assigned")]
    Assigned = 1,

    [Description("Executing")]
    Executing = 2,

    [Description("Completed")]
    Completed = 3,

    [Description("Failed")]
    Failed = 4
}
