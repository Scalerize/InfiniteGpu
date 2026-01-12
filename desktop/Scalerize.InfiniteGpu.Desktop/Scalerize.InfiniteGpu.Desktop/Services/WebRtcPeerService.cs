using Microsoft.AspNetCore.SignalR.Client;
using InfiniteGPU.Contracts.Hubs;
using InfiniteGPU.Contracts.Hubs.Payloads;
using SIPSorcery.Net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Type aliases to avoid conflicts with local event args
using ContractPartitionAssignment = InfiniteGPU.Contracts.Hubs.Payloads.PartitionAssignment;
using ContractWebRtcPeerInfo = InfiniteGPU.Contracts.Hubs.Payloads.WebRtcPeerInfo;
using ContractWebRtcSignalingMessage = InfiniteGPU.Contracts.Hubs.Payloads.WebRtcSignalingMessage;
using ContractPartitionReadyNotification = InfiniteGPU.Contracts.Hubs.Payloads.PartitionReadyNotification;
using ContractSubtaskPartitionsReadyPayload = InfiniteGPU.Contracts.Hubs.Payloads.SubtaskPartitionsReadyPayload;
using ContractSubgraphDistributionStartPayload = InfiniteGPU.Contracts.Hubs.Payloads.SubgraphDistributionStartPayload;
using ContractSubgraphTransferProgressPayload = InfiniteGPU.Contracts.Hubs.Payloads.SubgraphTransferProgressPayload;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    /// <summary>
    /// Manages WebRTC peer connections for distributed partition execution using SIPSorcery.
    /// Handles signaling through SignalR and data channel communication for tensor/subgraph transfer.
    /// </summary>
    public sealed class WebRtcPeerService : IAsyncDisposable
    {
        private readonly TensorSerializer _tensorSerializer;
        private readonly OnnxSubgraphSerializer _subgraphSerializer;
        private readonly ConcurrentDictionary<Guid, WebRtcPeerConnection> _peerConnections = new();
        private readonly ConcurrentDictionary<Guid, ContractPartitionAssignment> _partitionAssignments = new();
        private readonly ConcurrentDictionary<(Guid SubtaskId, Guid TensorId), TensorReceiveBuffer> _tensorReceiveBuffers = new();
        private readonly ConcurrentDictionary<(Guid SubtaskId, Guid PartitionId), SubgraphReceiveBuffer> _subgraphReceiveBuffers = new();
        
        // ICE server configuration
        private RTCIceServer[]? _iceServers;
        
        private HubConnection? _hubConnection;
        private IDisposable? _offerSubscription;
        private IDisposable? _answerSubscription;
        private IDisposable? _iceCandidateSubscription;
        private IDisposable? _iceRestartSubscription;
        private IDisposable? _partitionAssignedSubscription;
        private IDisposable? _partitionReadySubscription;
        private IDisposable? _partitionsReadySubscription;
        private IDisposable? _subgraphDistributionStartSubscription;
        private IDisposable? _subgraphTransferProgressSubscription;

        /// <summary>Raised when a partition is assigned to this device.</summary>
        public event EventHandler<PartitionAssignedEventArgs>? PartitionAssigned;
        
        /// <summary>Raised when a tensor is fully received from another partition.</summary>
        public event EventHandler<TensorReceivedEventArgs>? TensorReceived;
        
        /// <summary>Raised when all partitions are ready for execution.</summary>
        public event EventHandler<PartitionsReadyEventArgs>? PartitionsReady;
        
        /// <summary>Raised when a single partition is ready with output tensors.</summary>
        public event EventHandler<PartitionReadyEventArgs>? PartitionReady;
        
        /// <summary>Raised when a subgraph is fully received from parent peer.</summary>
        public event EventHandler<SubgraphReceivedEventArgs>? SubgraphReceived;
        
        /// <summary>Raised when a WebRTC connection state changes.</summary>
        public event EventHandler<WebRtcConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public WebRtcPeerService(TensorSerializer tensorSerializer, OnnxSubgraphSerializer subgraphSerializer)
        {
            _tensorSerializer = tensorSerializer ?? throw new ArgumentNullException(nameof(tensorSerializer));
            _subgraphSerializer = subgraphSerializer ?? throw new ArgumentNullException(nameof(subgraphSerializer));
        }

        /// <summary>
        /// Initializes the service with a SignalR hub connection.
        /// </summary>
        public void Initialize(HubConnection hubConnection)
        {
            _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
            RegisterSignalRHandlers();
        }

        /// <summary>
        /// Registers SignalR event handlers for WebRTC signaling and partition coordination.
        /// </summary>
        private void RegisterSignalRHandlers()
        {
            if (_hubConnection is null) return;

            _offerSubscription = _hubConnection.On<ContractWebRtcSignalingMessage>(
                TaskHubEvents.OnWebRtcOffer, HandleWebRtcOfferAsync);

            _answerSubscription = _hubConnection.On<ContractWebRtcSignalingMessage>(
                TaskHubEvents.OnWebRtcAnswer, HandleWebRtcAnswerAsync);

            _iceCandidateSubscription = _hubConnection.On<ContractWebRtcSignalingMessage>(
                TaskHubEvents.OnWebRtcIceCandidate, HandleWebRtcIceCandidateAsync);

            _partitionAssignedSubscription = _hubConnection.On<ContractPartitionAssignment>(
                TaskHubEvents.OnPartitionAssigned, HandlePartitionAssignedAsync);

            _partitionReadySubscription = _hubConnection.On<ContractPartitionReadyNotification>(
                TaskHubEvents.OnPartitionReady, HandlePartitionReadyAsync);

            _partitionsReadySubscription = _hubConnection.On<ContractSubtaskPartitionsReadyPayload>(
                TaskHubEvents.OnSubtaskPartitionsReady, HandlePartitionsReadyAsync);
                
            _subgraphDistributionStartSubscription = _hubConnection.On<ContractSubgraphDistributionStartPayload>(
                TaskHubEvents.OnSubgraphDistributionStart, HandleSubgraphDistributionStartAsync);
                
            _subgraphTransferProgressSubscription = _hubConnection.On<ContractSubgraphTransferProgressPayload>(
                TaskHubEvents.OnSubgraphTransferProgress, HandleSubgraphTransferProgressAsync);
        }

        /// <summary>
        /// Handles incoming WebRTC offer and creates an answer using SIPSorcery.
        /// </summary>
        private async Task HandleWebRtcOfferAsync(ContractWebRtcSignalingMessage message)
        {
            Debug.WriteLine($"[WebRtcPeerService] Received offer from partition {message.FromPartitionId}");

            try
            {
                var connectionKey = message.FromPartitionId;
                
                // Create SIPSorcery RTCPeerConnection
                var rtcConfig = new RTCConfiguration
                {
                    iceServers = _iceServers ?? GetDefaultIceServers()
                };
                
                var rtcPeerConnection = new RTCPeerConnection(rtcConfig);
                
                // Create our WebRTC peer connection wrapper
                var webRtcConnection = new WebRtcPeerConnection
                {
                    SubtaskId = message.SubtaskId,
                    LocalPartitionId = message.ToPartitionId,
                    RemotePartitionId = message.FromPartitionId,
                    IsInitiator = false,
                    RtcPeerConnection = rtcPeerConnection,
                    ConnectionState = WebRtcConnectionState.Connecting
                };
                
                // Setup event handlers
                SetupPeerConnectionEventHandlers(webRtcConnection);
                
                // Store the connection
                _peerConnections[connectionKey] = webRtcConnection;
                
                // Parse and set remote description from offer
                var offerSdp = RTCSessionDescriptionInit.FromJSON(message.Payload);
                var setRemoteResult = rtcPeerConnection.setRemoteDescription(offerSdp);
                
                if (setRemoteResult != SetDescriptionResultEnum.OK)
                {
                    throw new InvalidOperationException($"Failed to set remote description: {setRemoteResult}");
                }
                
                // Create answer
                var answer = rtcPeerConnection.createAnswer();
                if (answer == null)
                {
                    throw new InvalidOperationException("Failed to create answer");
                }
                
                // Set local description
                var setLocalResult = rtcPeerConnection.setLocalDescription(answer);
                if (setLocalResult != SetDescriptionResultEnum.OK)
                {
                    throw new InvalidOperationException($"Failed to set local description: {setLocalResult}");
                }
                
                // Serialize answer to JSON
                var answerJson = answer.toJSON();
                
                if (_hubConnection is not null)
                {
                    await _hubConnection.InvokeAsync(
                        nameof(ITaskHubServer.SendWebRtcAnswer),
                        message.SubtaskId,
                        message.ToPartitionId,
                        message.FromPartitionId,
                        answerJson);

                    webRtcConnection.ConnectionState = WebRtcConnectionState.AnswerSent;
                    Debug.WriteLine($"[WebRtcPeerService] Sent answer to partition {message.FromPartitionId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error handling offer: {ex}");
                ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs
                {
                    PartitionId = message.FromPartitionId,
                    State = WebRtcConnectionState.Failed,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Handles incoming WebRTC answer using SIPSorcery.
        /// </summary>
        private async Task HandleWebRtcAnswerAsync(ContractWebRtcSignalingMessage message)
        {
            Debug.WriteLine($"[WebRtcPeerService] Received answer from partition {message.FromPartitionId}");

            try
            {
                if (_peerConnections.TryGetValue(message.FromPartitionId, out var webRtcConnection))
                {
                    var rtcPeerConnection = webRtcConnection.RtcPeerConnection;
                    if (rtcPeerConnection == null)
                    {
                        throw new InvalidOperationException("RTCPeerConnection is null");
                    }
                    
                    // Parse and set remote description from answer
                    var answerSdp = RTCSessionDescriptionInit.FromJSON(message.Payload);
                    var setRemoteResult = rtcPeerConnection.setRemoteDescription(answerSdp);
                    
                    if (setRemoteResult != SetDescriptionResultEnum.OK)
                    {
                        throw new InvalidOperationException($"Failed to set remote description: {setRemoteResult}");
                    }
                    
                    webRtcConnection.ConnectionState = WebRtcConnectionState.AnswerReceived;
                    Debug.WriteLine($"[WebRtcPeerService] Answer set, waiting for ICE connection with partition {message.FromPartitionId}");
                    
                    // Connection will be fully established when ICE completes (handled in event handlers)
                }
                else
                {
                    Debug.WriteLine($"[WebRtcPeerService] No peer connection found for partition {message.FromPartitionId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error handling answer: {ex}");
                ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs
                {
                    PartitionId = message.FromPartitionId,
                    State = WebRtcConnectionState.Failed,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Handles incoming ICE candidates using SIPSorcery.
        /// </summary>
        private Task HandleWebRtcIceCandidateAsync(ContractWebRtcSignalingMessage message)
        {
            Debug.WriteLine($"[WebRtcPeerService] Received ICE candidate from partition {message.FromPartitionId}");

            try
            {
                if (_peerConnections.TryGetValue(message.FromPartitionId, out var webRtcConnection))
                {
                    var rtcPeerConnection = webRtcConnection.RtcPeerConnection;
                    if (rtcPeerConnection == null)
                    {
                        Debug.WriteLine($"[WebRtcPeerService] RTCPeerConnection is null for partition {message.FromPartitionId}");
                        return Task.CompletedTask;
                    }
                    
                    // Parse ice candidate from JSON payload
                    var iceCandidate = JsonSerializer.Deserialize<IceCandidatePayload>(message.Payload);
                    if (iceCandidate == null)
                    {
                        Debug.WriteLine($"[WebRtcPeerService] Failed to parse ICE candidate");
                        return Task.CompletedTask;
                    }
                    
                    // Create RTCIceCandidateInit from the parsed data
                    var rtcIceCandidate = new RTCIceCandidateInit
                    {
                        candidate = iceCandidate.Candidate,
                        sdpMid = iceCandidate.SdpMid,
                        sdpMLineIndex = (ushort)(iceCandidate.SdpMLineIndex ?? 0)
                    };
                    
                    // Add ICE candidate to peer connection
                    rtcPeerConnection.addIceCandidate(rtcIceCandidate);
                    Debug.WriteLine($"[WebRtcPeerService] Added ICE candidate from partition {message.FromPartitionId}");
                }
                else
                {
                    Debug.WriteLine($"[WebRtcPeerService] No peer connection found for ICE candidate from partition {message.FromPartitionId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error handling ICE candidate: {ex}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles partition assignment notification.
        /// </summary>
        private async Task HandlePartitionAssignedAsync(ContractPartitionAssignment assignment)
        {
            Debug.WriteLine($"[WebRtcPeerService] Partition assigned: {assignment.PartitionId} (index {assignment.PartitionIndex}/{assignment.TotalPartitions})");

            _partitionAssignments[assignment.PartitionId] = assignment;

            // Raise event for partition execution handler
            PartitionAssigned?.Invoke(this, new PartitionAssignedEventArgs(assignment));

            // Initiate WebRTC connections with peers
            if (assignment.DownstreamPeer is not null && assignment.DownstreamPeer.IsInitiator)
            {
                await InitiateConnectionAsync(assignment.SubtaskId, assignment.PartitionId, assignment.DownstreamPeer);
            }
        }

        /// <summary>
        /// Handles partition ready notification (upstream partition has output ready).
        /// </summary>
        private Task HandlePartitionReadyAsync(ContractPartitionReadyNotification notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] Partition ready: {notification.PartitionId} with outputs: {string.Join(", ", notification.OutputTensorNames)}");

            PartitionReady?.Invoke(this, new PartitionReadyEventArgs(notification));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles notification that all partitions are ready for execution.
        /// </summary>
        private Task HandlePartitionsReadyAsync(ContractSubtaskPartitionsReadyPayload notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] All partitions ready for subtask {notification.SubtaskId}");

            PartitionsReady?.Invoke(this, new PartitionsReadyEventArgs(notification));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles notification that subgraph distribution is starting from parent peer.
        /// </summary>
        private Task HandleSubgraphDistributionStartAsync(ContractSubgraphDistributionStartPayload notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] Subgraph distribution starting for partition {notification.ChildPartitionId}, expected size: {notification.ExpectedSubgraphSizeBytes} bytes");
            
            // Start receiving subgraph
            _subgraphSerializer.StartReceiving(new OnnxSubgraphSerializer.SubgraphMetadata(
                notification.SubtaskId,
                notification.ChildPartitionId,
                0, // PartitionIndex - will be set when metadata arrives
                0, // TotalPartitions - will be set when metadata arrives
                notification.ExpectedSubgraphSizeBytes,
                0, // TotalChunks - will be set when metadata arrives
                string.Empty, // Checksum - will be set when metadata arrives
                new List<string>(), // InputTensorNames - will be set when metadata arrives
                new List<string>(), // OutputTensorNames - will be set when metadata arrives
                DateTime.UtcNow
            ));
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles subgraph transfer progress notification.
        /// </summary>
        private Task HandleSubgraphTransferProgressAsync(ContractSubgraphTransferProgressPayload notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] Subgraph transfer progress for partition {notification.ToPartitionId}: {notification.ProgressPercent}%");
            return Task.CompletedTask;
        }

        #region Helper Methods

        /// <summary>
        /// Gets the default ICE servers for WebRTC connections.
        /// </summary>
        private static RTCIceServer[] GetDefaultIceServers()
        {
            return new RTCIceServer[]
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun2.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun3.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun4.l.google.com:19302" }
            };
        }

        /// <summary>
        /// Sets up event handlers for the RTCPeerConnection.
        /// </summary>
        private void SetupPeerConnectionEventHandlers(WebRtcPeerConnection webRtcConnection)
        {
            var rtcPeerConnection = webRtcConnection.RtcPeerConnection;
            if (rtcPeerConnection == null) return;

            // Handle ICE candidate generation
            rtcPeerConnection.onicecandidate += (candidate) =>
            {
                if (candidate != null && _hubConnection != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var iceCandidatePayload = new
                            {
                                candidate = candidate.candidate,
                                sdpMid = candidate.sdpMid,
                                sdpMLineIndex = candidate.sdpMLineIndex
                            };
                            
                            var candidateJson = JsonSerializer.Serialize(iceCandidatePayload);
                            
                            await _hubConnection.InvokeAsync(
                                nameof(ITaskHubServer.SendWebRtcIceCandidate),
                                webRtcConnection.SubtaskId,
                                webRtcConnection.LocalPartitionId,
                                webRtcConnection.RemotePartitionId,
                                candidateJson);
                            
                            Debug.WriteLine($"[WebRtcPeerService] Sent ICE candidate to partition {webRtcConnection.RemotePartitionId}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WebRtcPeerService] Error sending ICE candidate: {ex.Message}");
                        }
                    });
                }
            };

            // Handle ICE connection state changes
            rtcPeerConnection.oniceconnectionstatechange += (state) =>
            {
                Debug.WriteLine($"[WebRtcPeerService] ICE connection state changed to {state} for partition {webRtcConnection.RemotePartitionId}");
                
                switch (state)
                {
                    case RTCIceConnectionState.connected:
                    case RTCIceConnectionState.completed:
                        webRtcConnection.ConnectionState = WebRtcConnectionState.Connected;
                        ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs
                        {
                            PartitionId = webRtcConnection.RemotePartitionId,
                            State = WebRtcConnectionState.Connected
                        });
                        
                        // Notify backend
                        _ = Task.Run(async () =>
                        {
                            await NotifyConnectionEstablishedAsync(
                                webRtcConnection.SubtaskId,
                                webRtcConnection.LocalPartitionId,
                                webRtcConnection.RemotePartitionId,
                                !webRtcConnection.IsInitiator);
                        });
                        break;
                        
                    case RTCIceConnectionState.failed:
                        webRtcConnection.ConnectionState = WebRtcConnectionState.Failed;
                        ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs
                        {
                            PartitionId = webRtcConnection.RemotePartitionId,
                            State = WebRtcConnectionState.Failed,
                            ErrorMessage = "ICE connection failed"
                        });
                        break;
                        
                    case RTCIceConnectionState.disconnected:
                    case RTCIceConnectionState.closed:
                        webRtcConnection.ConnectionState = WebRtcConnectionState.Closed;
                        ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs
                        {
                            PartitionId = webRtcConnection.RemotePartitionId,
                            State = WebRtcConnectionState.Closed
                        });
                        break;
                        
                    case RTCIceConnectionState.checking:
                        webRtcConnection.ConnectionState = WebRtcConnectionState.IceNegotiating;
                        break;
                }
            };
            
            // Handle data channel creation (for non-initiator)
            rtcPeerConnection.ondatachannel += (dataChannel) =>
            {
                Debug.WriteLine($"[WebRtcPeerService] Data channel received from partition {webRtcConnection.RemotePartitionId}: {dataChannel.label}");
                webRtcConnection.DataChannel = dataChannel;
                SetupDataChannelEventHandlers(webRtcConnection, dataChannel);
            };

            // Handle connection state changes
            rtcPeerConnection.onconnectionstatechange += (state) =>
            {
                Debug.WriteLine($"[WebRtcPeerService] Connection state changed to {state} for partition {webRtcConnection.RemotePartitionId}");
            };
        }

        /// <summary>
        /// Sets up event handlers for the RTCDataChannel.
        /// </summary>
        private void SetupDataChannelEventHandlers(WebRtcPeerConnection webRtcConnection, RTCDataChannel dataChannel)
        {
            dataChannel.onopen += () =>
            {
                Debug.WriteLine($"[WebRtcPeerService] Data channel opened for partition {webRtcConnection.RemotePartitionId}");
            };

            dataChannel.onclose += () =>
            {
                Debug.WriteLine($"[WebRtcPeerService] Data channel closed for partition {webRtcConnection.RemotePartitionId}");
            };

            dataChannel.onerror += (error) =>
            {
                Debug.WriteLine($"[WebRtcPeerService] Data channel error for partition {webRtcConnection.RemotePartitionId}: {error}");
            };

            dataChannel.onmessage += (dc, protocol, data) =>
            {
                Debug.WriteLine($"[WebRtcPeerService] Received {data.Length} bytes from partition {webRtcConnection.RemotePartitionId}");
                ProcessReceivedData(webRtcConnection.SubtaskId, webRtcConnection.RemotePartitionId, data);
            };
        }

        /// <summary>
        /// Processes received data from the data channel.
        /// </summary>
        private void ProcessReceivedData(Guid subtaskId, Guid fromPartitionId, byte[] data)
        {
            if (data.Length < 4)
            {
                Debug.WriteLine($"[WebRtcPeerService] Received data too short to determine type");
                return;
            }
            
            // First 4 bytes indicate the message type
            var messageType = BitConverter.ToInt32(data, 0);
            var payload = new byte[data.Length - 4];
            Array.Copy(data, 4, payload, 0, payload.Length);
            
            switch (messageType)
            {
                case 1: // Tensor chunk
                    ProcessReceivedTensorChunk(subtaskId, fromPartitionId, payload);
                    break;
                case 2: // Subgraph metadata
                    ProcessReceivedSubgraphMetadata(subtaskId, fromPartitionId, payload);
                    break;
                case 3: // Subgraph chunk
                    ProcessReceivedSubgraphChunk(subtaskId, fromPartitionId, payload);
                    break;
                default:
                    Debug.WriteLine($"[WebRtcPeerService] Unknown message type: {messageType}");
                    break;
            }
        }

        /// <summary>
        /// Processes a received tensor chunk.
        /// </summary>
        private void ProcessReceivedTensorChunk(Guid subtaskId, Guid fromPartitionId, byte[] chunkData)
        {
            var chunk = _tensorSerializer.DeserializeChunk(chunkData);
            if (chunk == null)
            {
                Debug.WriteLine($"[WebRtcPeerService] Failed to deserialize tensor chunk");
                return;
            }
            
            var bufferKey = (subtaskId, chunk.TensorId);
            var buffer = _tensorReceiveBuffers.GetOrAdd(bufferKey, _ => new TensorReceiveBuffer
            {
                TensorId = chunk.TensorId,
                TensorName = chunk.TensorName,
                TotalChunks = chunk.TotalChunks,
                DataType = chunk.DataType,
                Shape = chunk.Shape
            });
            
            buffer.ReceivedChunks[chunk.ChunkIndex] = chunk.Data;
            
            if (buffer.IsComplete)
            {
                // Reassemble tensor
                var totalSize = buffer.ReceivedChunks.Values.Sum(c => c.Length);
                var tensorData = new byte[totalSize];
                var offset = 0;
                
                for (int i = 0; i < buffer.TotalChunks; i++)
                {
                    if (buffer.ReceivedChunks.TryGetValue(i, out var chunkBytes))
                    {
                        Array.Copy(chunkBytes, 0, tensorData, offset, chunkBytes.Length);
                        offset += chunkBytes.Length;
                    }
                }
                
                // Remove from buffers
                _tensorReceiveBuffers.TryRemove(bufferKey, out _);
                
                // Raise event
                TensorReceived?.Invoke(this, new TensorReceivedEventArgs
                {
                    SubtaskId = subtaskId,
                    FromPartitionId = fromPartitionId,
                    TensorName = buffer.TensorName,
                    DataType = buffer.DataType,
                    Shape = buffer.Shape,
                    Data = tensorData
                });
                
                Debug.WriteLine($"[WebRtcPeerService] Tensor {buffer.TensorName} fully received ({tensorData.Length} bytes)");
            }
        }

        /// <summary>
        /// Processes received subgraph metadata.
        /// </summary>
        private void ProcessReceivedSubgraphMetadata(Guid subtaskId, Guid fromPartitionId, byte[] metadataBytes)
        {
            var metadata = _subgraphSerializer.DeserializeMetadata(metadataBytes);
            if (metadata == null)
            {
                Debug.WriteLine($"[WebRtcPeerService] Failed to deserialize subgraph metadata");
                return;
            }
            
            // Start receiving subgraph with metadata
            _subgraphSerializer.StartReceiving(metadata);
            Debug.WriteLine($"[WebRtcPeerService] Started receiving subgraph for partition {metadata.PartitionId}");
        }

        #endregion

        /// <summary>
        /// Sends a subgraph to a child peer partition via WebRTC data channel.
        /// </summary>
        public async Task SendSubgraphAsync(
            Guid subtaskId,
            Guid localPartitionId,
            Guid targetPartitionId,
            int targetPartitionIndex,
            byte[] subgraphBytes,
            List<string> inputTensorNames,
            List<string> outputTensorNames,
            CancellationToken cancellationToken = default)
        {
            Debug.WriteLine($"[WebRtcPeerService] Sending subgraph to partition {targetPartitionId} ({subgraphBytes.Length} bytes)");

            if (_hubConnection is null)
            {
                throw new InvalidOperationException("Hub connection not initialized");
            }

            // Require a WebRTC connection
            if (!_peerConnections.TryGetValue(targetPartitionId, out var peerConnection) ||
                peerConnection.ConnectionState != WebRtcConnectionState.Connected ||
                peerConnection.DataChannel == null)
            {
                throw new InvalidOperationException($"No active WebRTC connection to partition {targetPartitionId}");
            }

            try
            {
                // Create metadata
                var metadata = _subgraphSerializer.CreateMetadata(
                    subgraphBytes,
                    subtaskId,
                    targetPartitionId,
                    targetPartitionIndex,
                    _partitionAssignments.Count + 1, // Approximate total partitions
                    inputTensorNames,
                    outputTensorNames);

                // Send via WebRTC data channel
                await SendSubgraphViaWebRtcAsync(peerConnection, metadata, subgraphBytes, cancellationToken);

                // Report transfer complete
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.ReportSubgraphTransferProgress),
                    subtaskId,
                    localPartitionId,
                    targetPartitionId,
                    subgraphBytes.LongLength,
                    subgraphBytes.LongLength,
                    cancellationToken);

                Debug.WriteLine($"[WebRtcPeerService] Subgraph sent to partition {targetPartitionId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error sending subgraph: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Sends subgraph via direct WebRTC data channel.
        /// </summary>
        private async Task SendSubgraphViaWebRtcAsync(
            WebRtcPeerConnection peerConnection,
            OnnxSubgraphSerializer.SubgraphMetadata metadata,
            byte[] subgraphBytes,
            CancellationToken cancellationToken)
        {
            var dataChannel = peerConnection.DataChannel;
            if (dataChannel == null)
            {
                throw new InvalidOperationException("Data channel is not available");
            }

            // Send metadata first with message type prefix (2 = subgraph metadata)
            var metadataBytes = _subgraphSerializer.SerializeMetadata(metadata);
            var metadataMessage = PrependMessageType(2, metadataBytes);
            dataChannel.send(metadataMessage);
            
            Debug.WriteLine($"[WebRtcPeerService] Sent subgraph metadata ({metadataBytes.Length} bytes)");

            // Send chunks with message type prefix (3 = subgraph chunk)
            var chunks = _subgraphSerializer.CreateChunks(subgraphBytes, metadata.SubtaskId, metadata.PartitionId).ToList();
            var totalChunks = chunks.Count;
            var sentChunks = 0;
            
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var chunkBytes = _subgraphSerializer.SerializeChunk(chunk);
                var chunkMessage = PrependMessageType(3, chunkBytes);
                dataChannel.send(chunkMessage);
                
                sentChunks++;
                
                // Report progress periodically
                if (sentChunks % 10 == 0 || sentChunks == totalChunks)
                {
                    var progress = (long)((sentChunks * subgraphBytes.Length) / totalChunks);
                    Debug.WriteLine($"[WebRtcPeerService] Subgraph transfer progress: {sentChunks}/{totalChunks} chunks ({progress * 100 / subgraphBytes.Length}%)");
                }
                
                // Small delay to avoid overwhelming the data channel
                await Task.Delay(1, cancellationToken);
            }
            
            Debug.WriteLine($"[WebRtcPeerService] All {totalChunks} subgraph chunks sent");
        }

        /// <summary>
        /// Prepends a message type identifier to the data payload.
        /// </summary>
        private static byte[] PrependMessageType(int messageType, byte[] data)
        {
            var result = new byte[4 + data.Length];
            BitConverter.GetBytes(messageType).CopyTo(result, 0);
            data.CopyTo(result, 4);
            return result;
        }

        /// <summary>
        /// Processes received subgraph chunks (called by data channel handler).
        /// </summary>
        public void ProcessReceivedSubgraphChunk(Guid subtaskId, Guid partitionId, byte[] chunkData)
        {
            var chunk = _subgraphSerializer.DeserializeChunk(chunkData, subtaskId, partitionId);
            if (chunk is null)
            {
                Debug.WriteLine($"[WebRtcPeerService] Failed to deserialize subgraph chunk");
                return;
            }

            var ack = _subgraphSerializer.ProcessReceivedChunk(chunk);
            if (!ack.Success)
            {
                Debug.WriteLine($"[WebRtcPeerService] Chunk processing failed: {ack.ErrorMessage}");
            }

            // Check if transfer is complete
            if (chunk.IsLastChunk)
            {
                var (result, subgraphBytes) = _subgraphSerializer.CompleteTransfer(subtaskId, partitionId);
                if (result.Success && subgraphBytes is not null)
                {
                    Debug.WriteLine($"[WebRtcPeerService] Subgraph transfer complete for partition {partitionId}");
                    SubgraphReceived?.Invoke(this, new SubgraphReceivedEventArgs
                    {
                        SubtaskId = subtaskId,
                        PartitionId = partitionId,
                        SubgraphBytes = subgraphBytes,
                        IsValid = true
                    });
                }
                else
                {
                    Debug.WriteLine($"[WebRtcPeerService] Subgraph transfer failed: {result.ErrorMessage}");
                    SubgraphReceived?.Invoke(this, new SubgraphReceivedEventArgs
                    {
                        SubtaskId = subtaskId,
                        PartitionId = partitionId,
                        SubgraphBytes = Array.Empty<byte>(),
                        IsValid = false,
                        ErrorMessage = result.ErrorMessage
                    });
                }
            }
        }

        /// <summary>
        /// Initiates a WebRTC connection with a peer partition using SIPSorcery.
        /// </summary>
        private async Task InitiateConnectionAsync(Guid subtaskId, Guid localPartitionId, ContractWebRtcPeerInfo peerInfo)
        {
            Debug.WriteLine($"[WebRtcPeerService] Initiating connection from {localPartitionId} to {peerInfo.PartitionId}");

            try
            {
                // Create SIPSorcery RTCPeerConnection
                var rtcConfig = new RTCConfiguration
                {
                    iceServers = _iceServers ?? GetDefaultIceServers()
                };
                
                var rtcPeerConnection = new RTCPeerConnection(rtcConfig);
                
                // Create WebRTC peer connection wrapper
                var webRtcConnection = new WebRtcPeerConnection
                {
                    SubtaskId = subtaskId,
                    LocalPartitionId = localPartitionId,
                    RemotePartitionId = peerInfo.PartitionId,
                    IsInitiator = true,
                    RtcPeerConnection = rtcPeerConnection,
                    ConnectionState = WebRtcConnectionState.Connecting
                };
                
                // Setup event handlers
                SetupPeerConnectionEventHandlers(webRtcConnection);
                
                // Store the connection
                _peerConnections[peerInfo.PartitionId] = webRtcConnection;

                // Create data channel for tensor/subgraph transfer
                var dataChannelInit = new RTCDataChannelInit
                {
                    ordered = true,
                    maxRetransmits = 3
                };
                
                var dataChannel = await rtcPeerConnection.createDataChannel($"partition-{localPartitionId}-{peerInfo.PartitionId}", dataChannelInit);
                webRtcConnection.DataChannel = dataChannel;
                
                // Setup data channel event handlers
                SetupDataChannelEventHandlers(webRtcConnection, dataChannel);

                // Create offer
                var offer = rtcPeerConnection.createOffer();
                if (offer == null)
                {
                    throw new InvalidOperationException("Failed to create offer");
                }
                
                // Set local description
                var setLocalResult = rtcPeerConnection.setLocalDescription(offer);
                if (setLocalResult != SetDescriptionResultEnum.OK)
                {
                    throw new InvalidOperationException($"Failed to set local description: {setLocalResult}");
                }
                
                // Serialize offer to JSON
                var offerJson = offer.toJSON();

                if (_hubConnection is not null)
                {
                    await _hubConnection.InvokeAsync(
                        nameof(ITaskHubServer.SendWebRtcOffer),
                        subtaskId,
                        localPartitionId,
                        peerInfo.PartitionId,
                        offerJson);

                    webRtcConnection.ConnectionState = WebRtcConnectionState.OfferSent;
                    Debug.WriteLine($"[WebRtcPeerService] Sent offer to partition {peerInfo.PartitionId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error initiating connection: {ex}");
                
                if (_peerConnections.TryGetValue(peerInfo.PartitionId, out var conn))
                {
                    conn.ConnectionState = WebRtcConnectionState.Failed;
                }
                
                ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs
                {
                    PartitionId = peerInfo.PartitionId,
                    State = WebRtcConnectionState.Failed,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Notifies the backend that a WebRTC connection is established.
        /// </summary>
        private async Task NotifyConnectionEstablishedAsync(Guid subtaskId, Guid localPartitionId, Guid peerPartitionId, bool isUpstream)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.NotifyWebRtcConnected),
                    subtaskId,
                    localPartitionId,
                    peerPartitionId,
                    isUpstream);

                Debug.WriteLine($"[WebRtcPeerService] Notified connection established: {localPartitionId} <-> {peerPartitionId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error notifying connection: {ex}");
            }
        }

        /// <summary>
        /// Sends a tensor to a downstream partition via WebRTC data channel.
        /// </summary>
        public async Task SendTensorAsync(
            Guid subtaskId,
            Guid localPartitionId,
            Guid targetPartitionId,
            string tensorName,
            TensorSerializer.TensorDataType dataType,
            int[] shape,
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            Debug.WriteLine($"[WebRtcPeerService] Sending tensor {tensorName} ({data.Length} bytes) to partition {targetPartitionId}");

            // Require a WebRTC connection
            if (!_peerConnections.TryGetValue(targetPartitionId, out var peerConnection) ||
                peerConnection.ConnectionState != WebRtcConnectionState.Connected ||
                peerConnection.DataChannel == null)
            {
                throw new InvalidOperationException($"No active WebRTC connection to partition {targetPartitionId}");
            }

            // Serialize the tensor
            var serializedTensor = _tensorSerializer.SerializeTensor(subtaskId, tensorName, dataType, shape, data);

            // Send via WebRTC data channel
            await SendViaPeerConnectionAsync(peerConnection, serializedTensor, cancellationToken);

            // Report progress
            await ReportTransferProgressAsync(subtaskId, localPartitionId, targetPartitionId, tensorName, data.Length, data.Length, cancellationToken);
        }

        /// <summary>
        /// Sends tensor via direct WebRTC data channel.
        /// </summary>
        private async Task SendViaPeerConnectionAsync(
            WebRtcPeerConnection peerConnection,
            TensorSerializer.SerializedTensor tensor,
            CancellationToken cancellationToken)
        {
            var dataChannel = peerConnection.DataChannel;
            if (dataChannel == null)
            {
                throw new InvalidOperationException("Data channel is not available");
            }

            // Chunk the tensor for reliable delivery
            var chunks = _tensorSerializer.ChunkTensor(tensor).ToList();
            var totalChunks = chunks.Count;
            var sentChunks = 0;
            
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var chunkBytes = _tensorSerializer.SerializeChunk(chunk);
                
                // Prepend message type (1 = tensor chunk)
                var message = PrependMessageType(1, chunkBytes);
                dataChannel.send(message);
                
                sentChunks++;
                
                // Report progress periodically
                if (sentChunks % 10 == 0 || sentChunks == totalChunks)
                {
                    Debug.WriteLine($"[WebRtcPeerService] Tensor transfer progress: {sentChunks}/{totalChunks} chunks");
                }
                
                // Small delay to avoid overwhelming the data channel
                await Task.Delay(1, cancellationToken);
            }
            
            Debug.WriteLine($"[WebRtcPeerService] All {totalChunks} tensor chunks sent for {tensor.Name}");
        }

        /// <summary>
        /// Reports tensor transfer progress to the backend.
        /// </summary>
        private async Task ReportTransferProgressAsync(
            Guid subtaskId,
            Guid fromPartitionId,
            Guid toPartitionId,
            string tensorName,
            long bytesTransferred,
            long totalBytes,
            CancellationToken cancellationToken)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.ReportTensorTransferProgress),
                    subtaskId,
                    fromPartitionId,
                    toPartitionId,
                    tensorName,
                    bytesTransferred,
                    totalBytes,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error reporting transfer progress: {ex}");
            }
        }

        /// <summary>
        /// Reports that this partition is ready with output tensors.
        /// </summary>
        public async Task ReportPartitionReadyAsync(
            Guid subtaskId,
            Guid partitionId,
            string[] outputTensorNames,
            long outputTensorSizeBytes,
            CancellationToken cancellationToken = default)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.ReportPartitionReady),
                    subtaskId,
                    partitionId,
                    outputTensorNames,
                    outputTensorSizeBytes,
                    cancellationToken);

                Debug.WriteLine($"[WebRtcPeerService] Reported partition {partitionId} ready with outputs: {string.Join(", ", outputTensorNames)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error reporting partition ready: {ex}");
            }
        }

        /// <summary>
        /// Reports that this partition has completed execution.
        /// </summary>
        public async Task ReportPartitionCompletedAsync(
            Guid subtaskId,
            Guid partitionId,
            PartitionResultPayload result,
            CancellationToken cancellationToken = default)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.ReportPartitionCompleted),
                    subtaskId,
                    result,
                    cancellationToken);

                Debug.WriteLine($"[WebRtcPeerService] Reported partition {partitionId} completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error reporting partition completed: {ex}");
            }
        }

        /// <summary>
        /// Reports that this partition has failed.
        /// </summary>
        public async Task ReportPartitionFailedAsync(
            Guid subtaskId,
            Guid partitionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.ReportPartitionFailed),
                    subtaskId,
                    partitionId,
                    failureReason,
                    cancellationToken);

                Debug.WriteLine($"[WebRtcPeerService] Reported partition {partitionId} failed: {failureReason}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error reporting partition failed: {ex}");
            }
        }

        /// <summary>
        /// Reports partition execution progress.
        /// </summary>
        public async Task ReportPartitionProgressAsync(
            Guid subtaskId,
            Guid partitionId,
            int progress,
            CancellationToken cancellationToken = default)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    nameof(ITaskHubServer.ReportPartitionProgress),
                    subtaskId,
                    partitionId,
                    progress,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error reporting partition progress: {ex}");
            }
        }

        /// <summary>
        /// Cleans up connections for a completed subtask.
        /// </summary>
        public void CleanupSubtask(Guid subtaskId)
        {
            var connectionsToRemove = new List<Guid>();
            
            foreach (var kvp in _peerConnections)
            {
                if (kvp.Value.SubtaskId == subtaskId)
                {
                    connectionsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in connectionsToRemove)
            {
                if (_peerConnections.TryRemove(key, out var connection))
                {
                    connection.Dispose();
                }
            }

            // Remove partition assignments
            var assignmentsToRemove = new List<Guid>();
            foreach (var kvp in _partitionAssignments)
            {
                if (kvp.Value.SubtaskId == subtaskId)
                {
                    assignmentsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in assignmentsToRemove)
            {
                _partitionAssignments.TryRemove(key, out _);
            }

            Debug.WriteLine($"[WebRtcPeerService] Cleaned up {connectionsToRemove.Count} connections for subtask {subtaskId}");
        }

        public async ValueTask DisposeAsync()
        {
            _offerSubscription?.Dispose();
            _answerSubscription?.Dispose();
            _iceCandidateSubscription?.Dispose();
            _partitionAssignedSubscription?.Dispose();
            _partitionReadySubscription?.Dispose();
            _partitionsReadySubscription?.Dispose();
            _subgraphDistributionStartSubscription?.Dispose();
            _subgraphTransferProgressSubscription?.Dispose();

            foreach (var connection in _peerConnections.Values)
            {
                connection.Dispose();
            }

            _peerConnections.Clear();
            _partitionAssignments.Clear();
            _tensorReceiveBuffers.Clear();

            await Task.CompletedTask;
        }

        #region Internal Types

        /// <summary>
        /// WebRTC peer connection wrapper that holds SIPSorcery RTCPeerConnection and RTCDataChannel.
        /// </summary>
        private sealed class WebRtcPeerConnection : IDisposable
        {
            public Guid SubtaskId { get; init; }
            public Guid LocalPartitionId { get; init; }
            public Guid RemotePartitionId { get; init; }
            public bool IsInitiator { get; init; }
            public WebRtcConnectionState ConnectionState { get; set; }
            public RTCPeerConnection? RtcPeerConnection { get; set; }
            public RTCDataChannel? DataChannel { get; set; }

            public void Dispose()
            {
                try
                {
                    DataChannel?.close();
                    RtcPeerConnection?.close();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebRtcPeerConnection] Error during disposal: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Buffer for receiving tensor chunks.
        /// </summary>
        private sealed class TensorReceiveBuffer
        {
            public Guid TensorId { get; init; }
            public string TensorName { get; init; } = string.Empty;
            public int TotalChunks { get; init; }
            public TensorSerializer.TensorDataType DataType { get; init; }
            public int[] Shape { get; init; } = Array.Empty<int>();
            public ConcurrentDictionary<int, byte[]> ReceivedChunks { get; } = new();
            public bool IsComplete => ReceivedChunks.Count == TotalChunks;
        }
        
        /// <summary>
        /// Buffer for receiving subgraph chunks.
        /// </summary>
        private sealed class SubgraphReceiveBuffer
        {
            public Guid SubtaskId { get; init; }
            public Guid PartitionId { get; init; }
            public int TotalChunks { get; init; }
            public long TotalSize { get; init; }
            public ConcurrentDictionary<int, byte[]> ReceivedChunks { get; } = new();
            public bool IsComplete => ReceivedChunks.Count == TotalChunks;
        }
        
        /// <summary>
        /// Payload for deserializing ICE candidate from JSON.
        /// </summary>
        private sealed class IceCandidatePayload
        {
            public string Candidate { get; set; } = string.Empty;
            public string? SdpMid { get; set; }
            public int? SdpMLineIndex { get; set; }
        }

        #endregion
    }

    #region Event Args and DTOs

    /// <summary>
    /// Event args for when a partition is assigned to this device.
    /// Uses the shared contract PartitionAssignment type.
    /// </summary>
    public sealed class PartitionAssignedEventArgs : EventArgs
    {
        public ContractPartitionAssignment Assignment { get; }

        public PartitionAssignedEventArgs(ContractPartitionAssignment assignment)
        {
            Assignment = assignment;
        }
    }

    /// <summary>
    /// Event args for when a tensor is received from another partition.
    /// </summary>
    public sealed class TensorReceivedEventArgs : EventArgs
    {
        public Guid SubtaskId { get; init; }
        public Guid FromPartitionId { get; init; }
        public string TensorName { get; init; } = string.Empty;
        public TensorSerializer.TensorDataType DataType { get; init; }
        public int[] Shape { get; init; } = Array.Empty<int>();
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Event args for when all subtask partitions are ready.
    /// Uses the shared contract SubtaskPartitionsReadyPayload type.
    /// </summary>
    public sealed class PartitionsReadyEventArgs : EventArgs
    {
        public ContractSubtaskPartitionsReadyPayload Notification { get; }

        public PartitionsReadyEventArgs(ContractSubtaskPartitionsReadyPayload notification)
        {
            Notification = notification;
        }
    }

    /// <summary>
    /// Event args for when a partition is ready with output.
    /// Uses the shared contract PartitionReadyNotification type.
    /// </summary>
    public sealed class PartitionReadyEventArgs : EventArgs
    {
        public ContractPartitionReadyNotification Notification { get; }

        public PartitionReadyEventArgs(ContractPartitionReadyNotification notification)
        {
            Notification = notification;
        }
    }

    /// <summary>
    /// Event args for when a subgraph is received from parent peer.
    /// </summary>
    public sealed class SubgraphReceivedEventArgs : EventArgs
    {
        public Guid SubtaskId { get; init; }
        public Guid PartitionId { get; init; }
        public byte[] SubgraphBytes { get; init; } = Array.Empty<byte>();
        public bool IsValid { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// WebRTC connection state enumeration for tracking peer connection lifecycle.
    /// </summary>
    public enum WebRtcConnectionState
    {
        /// <summary>Initial state before any connection attempt.</summary>
        None,
        /// <summary>Connection attempt in progress.</summary>
        Connecting,
        /// <summary>WebRTC offer has been sent, waiting for answer.</summary>
        OfferSent,
        /// <summary>WebRTC offer received, creating answer.</summary>
        OfferReceived,
        /// <summary>WebRTC answer sent, waiting for ICE completion.</summary>
        AnswerSent,
        /// <summary>WebRTC answer received, waiting for ICE completion.</summary>
        AnswerReceived,
        /// <summary>ICE negotiation in progress.</summary>
        IceNegotiating,
        /// <summary>Connection fully established and ready for data transfer.</summary>
        Connected,
        /// <summary>Connection attempt failed.</summary>
        Failed,
        /// <summary>Connection has been closed.</summary>
        Closed
    }

    /// <summary>
    /// Event args for when a WebRTC connection state changes.
    /// </summary>
    public sealed class WebRtcConnectionStateChangedEventArgs : EventArgs
    {
        public Guid PartitionId { get; init; }
        public WebRtcConnectionState State { get; init; }
        public string? ErrorMessage { get; init; }
    }

    #endregion
}
