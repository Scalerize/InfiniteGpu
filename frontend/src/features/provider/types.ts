export type SubtaskStatus = 'Pending' | 'Assigned' | 'Executing' | 'Completed' | 'Failed';

export type ProviderTaskType = 'Train' | 'Inference';

export type PartitionStatus =
  | 'Pending'
  | 'DownloadingFullModel'
  | 'Partitioning'
  | 'DistributingSubgraphs'
  | 'WaitingForSubgraph'
  | 'ReceivingSubgraph'
  | 'WaitingForPeers'
  | 'WaitingForInput'
  | 'Executing'
  | 'Completed'
  | 'Failed';

export type SubgraphReceiveStatus =
  | 'NotApplicable'
  | 'Pending'
  | 'Receiving'
  | 'Received'
  | 'Failed';

export interface ResourceSpecification {
  gpuUnits: number;
  cpuCores: number;
  diskGb: number;
  networkGb: number;
  dataSizeGb: number;
}

export interface WebRtcPeerInfo {
  partitionId: string;
  deviceId: string;
  deviceConnectionId: string;
  partitionIndex: number;
  isInitiator: boolean;
}

export interface SmartPartitionDto {
  id: string;
  subtaskId: string;
  partitionIndex: number;
  totalPartitions: number;
  status: PartitionStatus;
  progress: number;
  deviceId?: string | null;
  deviceConnectionId?: string | null;
  onnxSubgraphBlobUri?: string | null;
  inputTensorNames: string[];
  outputTensorNames: string[];
  estimatedMemoryMb: number;
  estimatedComputeMs: number;
  assignedAtUtc?: string | null;
  completedAtUtc?: string | null;
  executionDurationMs?: number | null;
  errorMessage?: string | null;
  
  // Parent peer architecture fields
  isParentPeer: boolean;
  onnxFullModelBlobUri?: string | null;
  subgraphReceiveStatus?: SubgraphReceiveStatus;
  onnxSubgraphSizeBytes?: number | null;
}

export interface ProviderSubtaskDto {
  id: string;
  taskId: string;
  taskType: ProviderTaskType;
  status: SubtaskStatus;
  progress: number;
  parametersJson: string;
  estimatedEarnings: number;
  resourceRequirements: ResourceSpecification;
  createdAtUtc: string;
  durationSeconds?: number | null;
  costUsd?: number | null;
  
  // Smart partitioning support
  requiresPartitioning?: boolean;
  partitionCount?: number;
  partitionIndex?: number | null;
  partitionProgress?: number;
  
  // Parent peer architecture
  partitions?: SmartPartitionDto[];
  hasParentPeer?: boolean;
  parentPeerDeviceId?: string | null;
}

export interface ProviderSubtaskExecutionResult {
  subtaskId: string;
  completedAtUtc: string;
  outputSummary: string;
  artifacts: Record<string, unknown>;
}

export interface ProgressEventPayload {
  SubtaskId: string;
  AssignedProviderId: string;
  Progress: number;
  LastHeartbeatAtUtc?: string;
}

export interface SubtaskAcceptedEventPayload {
  SubtaskId: string;
  AssignedProviderId: string;
  Status: SubtaskStatus;
  AssignedAtUtc: string;
}

export interface SubtaskCompleteEventPayload {
  SubtaskId: string;
  AssignedProviderId: string;
  CompletedAtUtc: string;
}

export interface AvailableSubtasksChangedEventPayload {
  SubtaskId: string;
  Status: SubtaskStatus;
  AcceptedByProviderId?: string;
  TimestampUtc: string;
}

// Parent peer architecture event payloads
export interface ParentPeerElectedEventPayload {
  SubtaskId: string;
  ParentPartitionId: string;
  ParentDeviceId: string;
  TotalChildPeers: number;
  TimestampUtc: string;
}

export interface SubgraphDistributionStartEventPayload {
  SubtaskId: string;
  ParentPartitionId: string;
  ChildPartitionId: string;
  ChildPartitionIndex: number;
  ExpectedSubgraphSizeBytes: number;
  TimestampUtc: string;
}

export interface SubgraphTransferProgressEventPayload {
  SubtaskId: string;
  FromPartitionId: string;
  ToPartitionId: string;
  BytesTransferred: number;
  TotalBytes: number;
  ProgressPercent: number;
  TimestampUtc: string;
}

export interface SubgraphReceivedEventPayload {
  SubtaskId: string;
  PartitionId: string;
  SubgraphSizeBytes: number;
  IsValid: boolean;
  TimestampUtc: string;
}

export interface PartitionAssignmentEventPayload {
  PartitionId: string;
  SubtaskId: string;
  TaskId: string;
  PartitionIndex: number;
  TotalPartitions: number;
  IsParentPeer: boolean;
  OnnxFullModelBlobUri?: string | null;
  OnnxSubgraphBlobUri?: string | null;
  InputTensorNames: string[];
  OutputTensorNames: string[];
  ChildPeers?: WebRtcPeerInfo[];
  ParentPeer?: WebRtcPeerInfo | null;
}