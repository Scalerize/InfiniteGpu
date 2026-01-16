using System.Text.Json.Serialization;

namespace InfiniteGPU.Contracts.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InferencePayloadType
{
    Json = 0,
    Text = 1,
    Binary = 2
}
