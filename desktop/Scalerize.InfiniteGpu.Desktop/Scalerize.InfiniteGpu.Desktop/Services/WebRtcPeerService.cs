using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    /// <summary>
    /// Manages WebRTC peer connections for distributed partition execution.
    /// Handles signaling through SignalR and data channel communication for tensor transfer.
    /// </summary>
    /// <remarks>
    /// Note: This implementation provides the signaling and coordination layer.
    /// Actual WebRTC data channels would require a native WebRTC library like
    /// WebRTC.NET, libdatachannel, or similar. The tensor transfer falls back
    /// to SignalR relay when native WebRTC is not available.
    /// </remarks>
    public sealed class WebRtcPeerService : IAsyncDisposable
    {
        private const string WebRtcOfferEvent = "OnWebRtcOffer";
        private const string WebRtcAnswerEvent = "OnWebRtcAnswer";
        private const string WebRtcIceCandidateEvent = "OnWebRtcIceCandidate";
        private const string PartitionAssignedEvent = "OnPartitionAssigned";
        private const string PartitionReadyEvent = "OnPartitionReady";
        private const string SubtaskPartitionsReadyEvent = "OnSubtaskPartitionsReady";
        private const string SubgraphDistributionStartEvent = "OnSubgraphDistributionStart";
        private const string SubgraphTransferProgressEvent = "OnSubgraphTransferProgress";
        private const string SubgraphReceivedEvent = "OnSubgraphReceived";

        private readonly TensorSerializer _tensorSerializer;
        private readonly OnnxSubgraphSerializer _subgraphSerializer;
        private readonly ConcurrentDictionary<Guid, PeerConnection> _peerConnections = new();
        private readonly ConcurrentDictionary<Guid, PartitionAssignment> _partitionAssignments = new();
        private readonly ConcurrentDictionary<Guid, TensorReceiveBuffer> _tensorReceiveBuffers = new();
        
        private HubConnection? _hubConnection;
        private IDisposable? _offerSubscription;
        private IDisposable? _answerSubscription;
        private IDisposable? _iceCandidateSubscription;
        private IDisposable? _partitionAssignedSubscription;
        private IDisposable? _partitionReadySubscription;
        private IDisposable? _partitionsReadySubscription;
        private IDisposable? _subgraphDistributionStartSubscription;
        private IDisposable? _subgraphTransferProgressSubscription;

        public event EventHandler<PartitionAssignedEventArgs>? PartitionAssigned;
        public event EventHandler<TensorReceivedEventArgs>? TensorReceived;
        public event EventHandler<PartitionsReadyEventArgs>? PartitionsReady;
        public event EventHandler<PartitionReadyEventArgs>? PartitionReady;
        public event EventHandler<SubgraphReceivedEventArgs>? SubgraphReceived;

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

            _offerSubscription = _hubConnection.On<WebRtcSignalingMessage>(
                WebRtcOfferEvent, HandleWebRtcOfferAsync);

            _answerSubscription = _hubConnection.On<WebRtcSignalingMessage>(
                WebRtcAnswerEvent, HandleWebRtcAnswerAsync);

            _iceCandidateSubscription = _hubConnection.On<WebRtcSignalingMessage>(
                WebRtcIceCandidateEvent, HandleWebRtcIceCandidateAsync);

            _partitionAssignedSubscription = _hubConnection.On<PartitionAssignment>(
                PartitionAssignedEvent, HandlePartitionAssignedAsync);

            _partitionReadySubscription = _hubConnection.On<PartitionReadyNotification>(
                PartitionReadyEvent, HandlePartitionReadyAsync);

            _partitionsReadySubscription = _hubConnection.On<PartitionsReadyNotification>(
                SubtaskPartitionsReadyEvent, HandlePartitionsReadyAsync);
                
            _subgraphDistributionStartSubscription = _hubConnection.On<SubgraphDistributionStartNotification>(
                SubgraphDistributionStartEvent, HandleSubgraphDistributionStartAsync);
                
            _subgraphTransferProgressSubscription = _hubConnection.On<SubgraphTransferProgressNotification>(
                SubgraphTransferProgressEvent, HandleSubgraphTransferProgressAsync);
        }

        /// <summary>
        /// Handles incoming WebRTC offer and creates an answer.
        /// </summary>
        private async Task HandleWebRtcOfferAsync(WebRtcSignalingMessage message)
        {
            Debug.WriteLine($"[WebRtcPeerService] Received offer from partition {message.FromPartitionId}");

            try
            {
                // Create/update peer connection for this partition pair
                var connectionKey = message.FromPartitionId; // Use sender's ID as key
                var peerConnection = _peerConnections.GetOrAdd(connectionKey, _ => new PeerConnection
                {
                    SubtaskId = message.SubtaskId,
                    LocalPartitionId = message.ToPartitionId,
                    RemotePartitionId = message.FromPartitionId,
                    IsInitiator = false
                });

                // In a real implementation, we would:
                // 1. Create RTCPeerConnection
                // 2. Set remote description from offer
                // 3. Create answer
                // 4. Set local description
                // 5. Send answer back

                // For now, generate a placeholder answer for the signaling flow
                var answer = new
                {
                    type = "answer",
                    sdp = GeneratePlaceholderSdp("answer"),
                    connectionEstablished = true
                };

                var answerJson = JsonSerializer.Serialize(answer);

                if (_hubConnection is not null)
                {
                    await _hubConnection.InvokeAsync(
                        "SendWebRtcAnswer",
                        message.SubtaskId,
                        message.ToPartitionId,    // We are the receiver, so ToPartitionId is our local partition
                        message.FromPartitionId,   // Send answer back to the initiator
                        answerJson);

                    peerConnection.ConnectionState = PeerConnectionState.AnswerSent;
                    Debug.WriteLine($"[WebRtcPeerService] Sent answer to partition {message.FromPartitionId}");

                    // Simulate connection established
                    await NotifyConnectionEstablishedAsync(message.SubtaskId, message.ToPartitionId, message.FromPartitionId, isUpstream: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error handling offer: {ex}");
            }
        }

        /// <summary>
        /// Handles incoming WebRTC answer.
        /// </summary>
        private async Task HandleWebRtcAnswerAsync(WebRtcSignalingMessage message)
        {
            Debug.WriteLine($"[WebRtcPeerService] Received answer from partition {message.FromPartitionId}");

            try
            {
                if (_peerConnections.TryGetValue(message.FromPartitionId, out var peerConnection))
                {
                    // In a real implementation, we would:
                    // 1. Set remote description from answer
                    // 2. Complete ICE negotiation

                    peerConnection.ConnectionState = PeerConnectionState.Connected;
                    Debug.WriteLine($"[WebRtcPeerService] Connection established with partition {message.FromPartitionId}");

                    // Notify that connection is established
                    await NotifyConnectionEstablishedAsync(message.SubtaskId, message.ToPartitionId, message.FromPartitionId, isUpstream: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error handling answer: {ex}");
            }
        }

        /// <summary>
        /// Handles incoming ICE candidates.
        /// </summary>
        private Task HandleWebRtcIceCandidateAsync(WebRtcSignalingMessage message)
        {
            Debug.WriteLine($"[WebRtcPeerService] Received ICE candidate from partition {message.FromPartitionId}");

            // In a real implementation, we would add the ICE candidate to the peer connection
            // For now, we simulate successful ICE negotiation

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles partition assignment notification.
        /// </summary>
        private async Task HandlePartitionAssignedAsync(PartitionAssignment assignment)
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
        private Task HandlePartitionReadyAsync(PartitionReadyNotification notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] Partition ready: {notification.PartitionId} with outputs: {string.Join(", ", notification.OutputTensorNames)}");

            PartitionReady?.Invoke(this, new PartitionReadyEventArgs(notification));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles notification that all partitions are ready for execution.
        /// </summary>
        private Task HandlePartitionsReadyAsync(PartitionsReadyNotification notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] All partitions ready for subtask {notification.SubtaskId}");

            PartitionsReady?.Invoke(this, new PartitionsReadyEventArgs(notification));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles notification that subgraph distribution is starting from parent peer.
        /// </summary>
        private Task HandleSubgraphDistributionStartAsync(SubgraphDistributionStartNotification notification)
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
        private Task HandleSubgraphTransferProgressAsync(SubgraphTransferProgressNotification notification)
        {
            Debug.WriteLine($"[WebRtcPeerService] Subgraph transfer progress for partition {notification.ToPartitionId}: {notification.ProgressPercent}%");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a subgraph to a child peer partition via WebRTC.
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

                // Check if we have a direct WebRTC connection
                if (_peerConnections.TryGetValue(targetPartitionId, out var peerConnection) &&
                    peerConnection.ConnectionState == PeerConnectionState.Connected)
                {
                    // Send via WebRTC data channel
                    await SendSubgraphViaWebRtcAsync(peerConnection, metadata, subgraphBytes, cancellationToken);
                }
                else
                {
                    // Fallback: Send via SignalR relay (chunked)
                    await SendSubgraphViaSignalRRelayAsync(subtaskId, localPartitionId, targetPartitionId, metadata, subgraphBytes, cancellationToken);
                }

                // Report transfer complete
                await _hubConnection.InvokeAsync(
                    "ReportSubgraphTransferProgress",
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
            PeerConnection peerConnection,
            OnnxSubgraphSerializer.SubgraphMetadata metadata,
            byte[] subgraphBytes,
            CancellationToken cancellationToken)
        {
            // Send metadata first
            var metadataBytes = _subgraphSerializer.SerializeMetadata(metadata);
            // In real implementation: peerConnection.DataChannel.Send(metadataBytes);

            // Send chunks
            foreach (var chunk in _subgraphSerializer.CreateChunks(subgraphBytes, metadata.SubtaskId, metadata.PartitionId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var chunkBytes = _subgraphSerializer.SerializeChunk(chunk);
                // In real implementation: peerConnection.DataChannel.Send(chunkBytes);
                
                await Task.Delay(1, cancellationToken); // Small delay to avoid overwhelming
            }
        }

        /// <summary>
        /// Sends subgraph via SignalR relay (fallback).
        /// </summary>
        private async Task SendSubgraphViaSignalRRelayAsync(
            Guid subtaskId,
            Guid localPartitionId,
            Guid targetPartitionId,
            OnnxSubgraphSerializer.SubgraphMetadata metadata,
            byte[] subgraphBytes,
            CancellationToken cancellationToken)
        {
            if (_hubConnection is null) return;

            // Send metadata
            var metadataBytes = _subgraphSerializer.SerializeMetadata(metadata);
            var metadataBase64 = Convert.ToBase64String(metadataBytes);
            
            await _hubConnection.InvokeAsync(
                "RelaySubgraphMetadata",
                subtaskId,
                localPartitionId,
                targetPartitionId,
                metadataBase64,
                cancellationToken);

            // Send chunks
            var chunks = _subgraphSerializer.CreateChunks(subgraphBytes, subtaskId, targetPartitionId).ToList();
            var totalChunks = chunks.Count;
            var sentChunks = 0;

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkBytes = _subgraphSerializer.SerializeChunk(chunk);
                var chunkBase64 = Convert.ToBase64String(chunkBytes);

                await _hubConnection.InvokeAsync(
                    "RelaySubgraphChunk",
                    subtaskId,
                    localPartitionId,
                    targetPartitionId,
                    chunk.ChunkIndex,
                    chunk.TotalChunks,
                    chunkBase64,
                    cancellationToken);

                sentChunks++;

                // Report progress periodically
                if (sentChunks % 10 == 0 || sentChunks == totalChunks)
                {
                    var progress = (sentChunks * subgraphBytes.Length) / totalChunks;
                    await _hubConnection.InvokeAsync(
                        "ReportSubgraphTransferProgress",
                        subtaskId,
                        localPartitionId,
                        targetPartitionId,
                        (long)progress,
                        (long)subgraphBytes.Length,
                        cancellationToken);
                }
            }
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
        /// Initiates a WebRTC connection with a peer partition.
        /// </summary>
        private async Task InitiateConnectionAsync(Guid subtaskId, Guid localPartitionId, WebRtcPeerInfo peerInfo)
        {
            Debug.WriteLine($"[WebRtcPeerService] Initiating connection from {localPartitionId} to {peerInfo.PartitionId}");

            var peerConnection = new PeerConnection
            {
                SubtaskId = subtaskId,
                LocalPartitionId = localPartitionId,
                RemotePartitionId = peerInfo.PartitionId,
                IsInitiator = true,
                ConnectionState = PeerConnectionState.Connecting
            };

            _peerConnections[peerInfo.PartitionId] = peerConnection;

            try
            {
                // In a real implementation, we would:
                // 1. Create RTCPeerConnection
                // 2. Create data channel
                // 3. Create offer
                // 4. Set local description
                // 5. Send offer through SignalR

                var offer = new
                {
                    type = "offer",
                    sdp = GeneratePlaceholderSdp("offer")
                };

                var offerJson = JsonSerializer.Serialize(offer);

                if (_hubConnection is not null)
                {
                    await _hubConnection.InvokeAsync(
                        "SendWebRtcOffer",
                        subtaskId,
                        localPartitionId,
                        peerInfo.PartitionId,
                        offerJson);

                    peerConnection.ConnectionState = PeerConnectionState.OfferSent;
                    Debug.WriteLine($"[WebRtcPeerService] Sent offer to partition {peerInfo.PartitionId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRtcPeerService] Error initiating connection: {ex}");
                peerConnection.ConnectionState = PeerConnectionState.Failed;
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
                    "NotifyWebRtcConnected",
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
        /// Sends a tensor to a downstream partition.
        /// Falls back to SignalR relay if direct WebRTC is not available.
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

            // Serialize the tensor
            var serializedTensor = _tensorSerializer.SerializeTensor(subtaskId, tensorName, dataType, shape, data);

            // Check if we have a direct connection
            if (_peerConnections.TryGetValue(targetPartitionId, out var peerConnection) &&
                peerConnection.ConnectionState == PeerConnectionState.Connected &&
                peerConnection.DataChannel is not null)
            {
                // Send via WebRTC data channel
                await SendViaPeerConnectionAsync(peerConnection, serializedTensor, cancellationToken);
            }
            else
            {
                // Fallback: Send via SignalR relay (chunked)
                await SendViaSignalRRelayAsync(subtaskId, localPartitionId, targetPartitionId, serializedTensor, cancellationToken);
            }

            // Report progress
            await ReportTransferProgressAsync(subtaskId, localPartitionId, targetPartitionId, tensorName, data.Length, data.Length, cancellationToken);
        }

        /// <summary>
        /// Sends tensor via direct WebRTC data channel.
        /// </summary>
        private async Task SendViaPeerConnectionAsync(
            PeerConnection peerConnection,
            TensorSerializer.SerializedTensor tensor,
            CancellationToken cancellationToken)
        {
            // Chunk the tensor for reliable delivery
            foreach (var chunk in _tensorSerializer.ChunkTensor(tensor))
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var chunkBytes = _tensorSerializer.SerializeChunk(chunk);
                
                // In a real implementation, send via RTCDataChannel
                // peerConnection.DataChannel.Send(chunkBytes);
                
                // Small delay to avoid overwhelming the data channel
                await Task.Delay(1, cancellationToken);
            }
        }

        /// <summary>
        /// Sends tensor via SignalR relay (fallback when WebRTC is not available).
        /// </summary>
        private async Task SendViaSignalRRelayAsync(
            Guid subtaskId,
            Guid localPartitionId,
            Guid targetPartitionId,
            TensorSerializer.SerializedTensor tensor,
            CancellationToken cancellationToken)
        {
            if (_hubConnection is null)
            {
                throw new InvalidOperationException("Hub connection not initialized");
            }

            var chunks = _tensorSerializer.ChunkTensor(tensor).ToList();
            var totalChunks = chunks.Count;
            var sentChunks = 0;

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkBytes = _tensorSerializer.SerializeChunk(chunk);
                var base64Data = Convert.ToBase64String(chunkBytes);

                // Send chunk via SignalR
                await _hubConnection.InvokeAsync(
                    "RelayTensorChunk",
                    subtaskId,
                    localPartitionId,
                    targetPartitionId,
                    tensor.Name,
                    chunk.ChunkIndex,
                    chunk.TotalChunks,
                    base64Data,
                    cancellationToken);

                sentChunks++;

                // Report progress periodically
                if (sentChunks % 10 == 0 || sentChunks == totalChunks)
                {
                    var progress = (sentChunks * tensor.TotalSize) / totalChunks;
                    await ReportTransferProgressAsync(subtaskId, localPartitionId, targetPartitionId, tensor.Name, progress, tensor.TotalSize, cancellationToken);
                }
            }
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
                    "ReportTensorTransferProgress",
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
                    "ReportPartitionReady",
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
            string? resultJson,
            CancellationToken cancellationToken = default)
        {
            if (_hubConnection is null) return;

            try
            {
                await _hubConnection.InvokeAsync(
                    "ReportPartitionCompleted",
                    subtaskId,
                    partitionId,
                    resultJson,
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
                    "ReportPartitionFailed",
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
                    "ReportPartitionProgress",
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

        private static string GeneratePlaceholderSdp(string type)
        {
            // Placeholder SDP for signaling flow simulation
            return $"v=0\r\no=- {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na={type}\r\n";
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

        private enum PeerConnectionState
        {
            None,
            Connecting,
            OfferSent,
            OfferReceived,
            AnswerSent,
            Connected,
            Failed,
            Closed
        }

        private sealed class PeerConnection : IDisposable
        {
            public Guid SubtaskId { get; init; }
            public Guid LocalPartitionId { get; init; }
            public Guid RemotePartitionId { get; init; }
            public bool IsInitiator { get; init; }
            public PeerConnectionState ConnectionState { get; set; }
            
            // In a real implementation, this would be an RTCDataChannel
            public object? DataChannel { get; set; }

            public void Dispose()
            {
                // Clean up native WebRTC resources
            }
        }

        private sealed class TensorReceiveBuffer
        {
            public Guid TensorId { get; init; }
            public string TensorName { get; init; } = string.Empty;
            public int TotalChunks { get; init; }
            public ConcurrentDictionary<int, byte[]> ReceivedChunks { get; } = new();
            public bool IsComplete => ReceivedChunks.Count == TotalChunks;
        }

        #endregion
    }

    #region Event Args and DTOs

    public sealed class PartitionAssignedEventArgs : EventArgs
    {
        public PartitionAssignment Assignment { get; }

        public PartitionAssignedEventArgs(PartitionAssignment assignment)
        {
            Assignment = assignment;
        }
    }

    public sealed class TensorReceivedEventArgs : EventArgs
    {
        public Guid SubtaskId { get; init; }
        public Guid FromPartitionId { get; init; }
        public string TensorName { get; init; } = string.Empty;
        public TensorSerializer.TensorDataType DataType { get; init; }
        public int[] Shape { get; init; } = Array.Empty<int>();
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    public sealed class PartitionsReadyEventArgs : EventArgs
    {
        public PartitionsReadyNotification Notification { get; }

        public PartitionsReadyEventArgs(PartitionsReadyNotification notification)
        {
            Notification = notification;
        }
    }

    public sealed class PartitionReadyEventArgs : EventArgs
    {
        public PartitionReadyNotification Notification { get; }

        public PartitionReadyEventArgs(PartitionReadyNotification notification)
        {
            Notification = notification;
        }
    }

    public sealed class PartitionAssignment
    {
        public Guid PartitionId { get; init; }
        public Guid SubtaskId { get; init; }
        public Guid TaskId { get; init; }
        public int PartitionIndex { get; init; }
        public int TotalPartitions { get; init; }
        public string OnnxSubgraphBlobUri { get; init; } = string.Empty;
        public List<string> InputTensorNames { get; init; } = new();
        public List<string> OutputTensorNames { get; init; } = new();
        public string? ExecutionConfigJson { get; init; }
        public WebRtcPeerInfo? UpstreamPeer { get; init; }
        public WebRtcPeerInfo? DownstreamPeer { get; init; }
        
        // Parent peer architecture fields
        /// <summary>
        /// True if this device is the parent peer responsible for downloading
        /// the full model and distributing subgraphs.
        /// </summary>
        public bool IsParentPeer { get; init; }
        
        /// <summary>
        /// URI to the full ONNX model in blob storage (only set for parent peer).
        /// </summary>
        public string? OnnxFullModelBlobUri { get; init; }
        
        /// <summary>
        /// Information about the parent peer (for child peers to receive subgraphs).
        /// </summary>
        public WebRtcPeerInfo? ParentPeer { get; init; }
        
        /// <summary>
        /// List of child peers (for parent peer to distribute subgraphs to).
        /// </summary>
        public List<WebRtcPeerInfo>? ChildPeers { get; init; }
    }

    public sealed class WebRtcPeerInfo
    {
        public Guid PartitionId { get; init; }
        public Guid DeviceId { get; init; }
        public string DeviceConnectionId { get; init; } = string.Empty;
        public int PartitionIndex { get; init; }
        public bool IsInitiator { get; init; }
    }

    public sealed class WebRtcSignalingMessage
    {
        public Guid SubtaskId { get; init; }
        public Guid FromPartitionId { get; init; }
        public Guid ToPartitionId { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; }
    }

    public sealed class PartitionReadyNotification
    {
        public Guid SubtaskId { get; init; }
        public Guid PartitionId { get; init; }
        public int PartitionIndex { get; init; }
        public List<string> OutputTensorNames { get; init; } = new();
        public long OutputTensorSizeBytes { get; init; }
        public DateTime CompletedAtUtc { get; init; }
    }

    public sealed class PartitionsReadyNotification
    {
        public Guid SubtaskId { get; init; }
        public Guid PartitionId { get; init; }
        public int PartitionIndex { get; init; }
        public int TotalPartitions { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    /// <summary>
    /// Notification sent to child peers when parent peer starts distributing subgraphs.
    /// </summary>
    public sealed class SubgraphDistributionStartNotification
    {
        public Guid SubtaskId { get; init; }
        public Guid ParentPartitionId { get; init; }
        public Guid ChildPartitionId { get; init; }
        public int ChildPartitionIndex { get; init; }
        public long ExpectedSubgraphSizeBytes { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    /// <summary>
    /// Notification for subgraph transfer progress updates.
    /// </summary>
    public sealed class SubgraphTransferProgressNotification
    {
        public Guid SubtaskId { get; init; }
        public Guid FromPartitionId { get; init; }
        public Guid ToPartitionId { get; init; }
        public long BytesTransferred { get; init; }
        public long TotalBytes { get; init; }
        public int ProgressPercent => TotalBytes > 0 ? (int)((BytesTransferred * 100) / TotalBytes) : 0;
        public DateTime TimestampUtc { get; init; }
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

    #endregion
}
