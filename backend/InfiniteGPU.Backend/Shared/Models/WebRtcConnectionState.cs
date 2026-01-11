using System.ComponentModel;

namespace InfiniteGPU.Backend.Shared.Models;

/// <summary>
/// State of a WebRTC connection between two partitions in a distributed subtask.
/// </summary>
public enum WebRtcConnectionState
{
    /// <summary>
    /// Connection not yet initiated.
    /// </summary>
    [Description("None")]
    None = 0,

    /// <summary>
    /// WebRTC offer has been sent, awaiting answer.
    /// </summary>
    [Description("Offer Sent")]
    OfferSent = 1,

    /// <summary>
    /// WebRTC offer received, generating answer.
    /// </summary>
    [Description("Offer Received")]
    OfferReceived = 2,

    /// <summary>
    /// WebRTC answer sent, awaiting ICE completion.
    /// </summary>
    [Description("Answer Sent")]
    AnswerSent = 3,

    /// <summary>
    /// ICE negotiation in progress.
    /// </summary>
    [Description("ICE Negotiating")]
    IceNegotiating = 4,

    /// <summary>
    /// Connection fully established and ready for data transfer.
    /// </summary>
    [Description("Connected")]
    Connected = 5,

    /// <summary>
    /// Connection temporarily interrupted, attempting to reconnect.
    /// </summary>
    [Description("Reconnecting")]
    Reconnecting = 6,

    /// <summary>
    /// Connection closed normally.
    /// </summary>
    [Description("Closed")]
    Closed = 7,

    /// <summary>
    /// Connection failed due to error.
    /// </summary>
    [Description("Failed")]
    Failed = 8
}
