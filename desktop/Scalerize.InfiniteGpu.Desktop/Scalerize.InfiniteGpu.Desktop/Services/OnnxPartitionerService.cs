using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;
using System.Linq;
using static Onnx.TensorShapeProto.Types;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    /// <summary>
    /// Represents a node in the partition graph
    /// Each node represents a parallelizable subpart of the ONNX model
    /// </summary>
    public class PartitionNode
    {
        /// <summary>
        /// Unique identifier for this partition node
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Input tensor names that this partition consumes
        /// </summary>
        public List<string> InputNames { get; set; } = new List<string>();

        /// <summary>
        /// Output tensor names that this partition produces
        /// </summary>
        public List<string> OutputNames { get; set; } = new List<string>();

        /// <summary>
        /// Estimated memory required by this partition in bytes
        /// </summary>
        public long EstimatedMemoryBytes { get; set; }

        /// <summary>
        /// References to previous partition nodes in the graph
        /// </summary>
        public List<PartitionNode> PreviousNodes { get; set; } = new List<PartitionNode>();

        /// <summary>
        /// References to next partition nodes in the graph
        /// </summary>
        public List<PartitionNode> NextNodes { get; set; } = new List<PartitionNode>();

        /// <summary>
        /// ONNX node indices included in this partition
        /// </summary>
        public List<int> NodeIndices { get; set; } = new List<int>();

        /// <summary>
        /// Execution level/stage - partitions at the same level can execute in parallel
        /// </summary>
        public int ExecutionLevel { get; set; }

        /// <summary>
        /// Range of ONNX node indices included in this partition (start index, inclusive)
        /// </summary>
        public int StartNodeIndex { get; set; }

        /// <summary>
        /// Range of ONNX node indices included in this partition (end index, exclusive)
        /// </summary>
        public int EndNodeIndex { get; set; }
    }

    /// <summary>
    /// Represents the partition graph result
    /// A directed acyclic graph (DAG) where each node represents a parallelizable subpart
    /// </summary>
    public class PartitionGraph
    {
        /// <summary>
        /// All nodes in the partition graph
        /// </summary>
        public List<PartitionNode> Nodes { get; set; } = new List<PartitionNode>();

        /// <summary>
        /// Root nodes (entry points) of the partition graph
        /// </summary>
        public List<PartitionNode> RootNodes { get; set; } = new List<PartitionNode>();

        /// <summary>
        /// Leaf nodes (exit points) of the partition graph
        /// </summary>
        public List<PartitionNode> LeafNodes { get; set; } = new List<PartitionNode>();

        /// <summary>
        /// Nodes grouped by execution level - all nodes at the same level can execute in parallel
        /// Key: execution level, Value: list of partition nodes at that level
        /// </summary>
        public Dictionary<int, List<PartitionNode>> ExecutionLevels { get; set; } = new Dictionary<int, List<PartitionNode>>();

        /// <summary>
        /// Maximum parallelism possible (max number of partitions at any execution level)
        /// </summary>
        public int MaxParallelism { get; set; }

        /// <summary>
        /// Indicates whether the model was partitioned into multiple subgraphs
        /// </summary>
        public bool IsPartitioned { get; set; }

        /// <summary>
        /// Total memory required by the entire model in bytes
        /// </summary>
        public long TotalModelMemoryBytes { get; set; }
    }

    /// <summary>
    /// Service responsible for partitioning ONNX models into parallelizable subgraphs
    /// based on memory constraints with balanced graph partitioning to minimize communication
    /// </summary>
    public class OnnxPartitionerService
    {
        private readonly OnnxParsingService _parsingService;
        private readonly OnnxSizeService _sizeService;
        private const long DEFAULT_MEMORY_THRESHOLD_BYTES = 3L * 1024 * 1024 * 1024;
        
        // Minimum partition gain threshold (as percentage of total model size)
        // If partitioning gain is less than this, don't partition
        private const double MIN_PARTITION_GAIN_THRESHOLD = 0.15; // 15%
        
        // Maximum allowed edge cut ratio (communication overhead)
        private const double MAX_EDGE_CUT_RATIO = 0.3; // 30%

        // DTO Usings
        using DeviceCandidateDto = InfiniteGPU.Contracts.Hubs.Payloads.DeviceCandidateDto;
        using SmartPartitionDto = InfiniteGPU.Contracts.Hubs.Payloads.SmartPartitionDto;
        using PartitionStatus = InfiniteGPU.Contracts.Hubs.Payloads.PartitionStatus;

        public OnnxPartitionerService(OnnxParsingService parsingService, OnnxSizeService sizeService)
        {
            _parsingService = parsingService ?? throw new ArgumentNullException(nameof(parsingService));
            _sizeService = sizeService ?? throw new ArgumentNullException(nameof(sizeService));
        }

        /// <summary>
        /// Creates a partition plan for the given model and available devices.
        /// </summary>
        public List<SmartPartitionDto> CreatePartitionPlan(
            Onnx.ModelProto model,
            List<NamedOnnxValue> inputs,
            List<DeviceCandidateDto> availableDevices,
            Guid subtaskId)
        {
            if (availableDevices == null || !availableDevices.Any())
            {
                throw new ArgumentException("No available devices provided", nameof(availableDevices));
            }

            // Determine memory threshold based on available devices
            // We'll use the average available memory to encourage balanced partitioning
            long memoryThresholdBytes = (long)availableDevices.Average(d => d.AvailableMemoryBytes);
            // Ensure we don't exceed the largest device (partition must fit)
            long maxDeviceMemory = availableDevices.Max(d => d.AvailableMemoryBytes);
            memoryThresholdBytes = Math.Min(memoryThresholdBytes, maxDeviceMemory);

            // Generate partition graph
            var graph = PartitionModel(model, inputs, memoryThresholdBytes);

            var partitionDtos = new List<SmartPartitionDto>();
            var sortedDevices = availableDevices.OrderByDescending(d => d.AvailableMemoryBytes).ToList();
            var deviceUsage = new Dictionary<Guid, long>(); // Track used memory per device

            foreach (var node in graph.Nodes)
            {
                var partitionId = Guid.NewGuid();
                
                // Find best device for this partition
                DeviceCandidateDto? assignedDevice = null;
                
                // Simple greedy assignment: find first device with enough REMAINING memory
                foreach (var device in sortedDevices)
                {
                    long usedMemory = deviceUsage.GetValueOrDefault(device.DeviceId, 0);
                    if (device.AvailableMemoryBytes - usedMemory >= node.EstimatedMemoryBytes)
                    {
                        assignedDevice = device;
                        deviceUsage[device.DeviceId] = usedMemory + node.EstimatedMemoryBytes;
                        break;
                    }
                }
                
                // If no device fits, we might need to fail or just assign to largest (and hope virtual memory handles it)
                if (assignedDevice == null)
                {
                    assignedDevice = sortedDevices.First(); // Fallback to largest
                    // Still track usage
                    deviceUsage[assignedDevice.DeviceId] = deviceUsage.GetValueOrDefault(assignedDevice.DeviceId, 0) + node.EstimatedMemoryBytes;
                }

                var dto = new SmartPartitionDto
                {
                    Id = partitionId,
                    SubtaskId = subtaskId,
                    PartitionIndex = node.Id,
                    Status = PartitionStatus.Pending,
                    EstimatedMemoryMb = node.EstimatedMemoryBytes / (1024 * 1024),
                    IsParentPeer = node.Id == 0, // Assume root node is parent peer for now
                    InputTensorNames = node.InputNames,
                    OutputTensorNames = node.OutputNames,
                    AssignedDeviceId = assignedDevice.DeviceId,
                    AssignedToUserId = assignedDevice.ProviderId,
                    CreatedAtUtc = DateTime.UtcNow
                    // Upstream/Downstream will be linked in a second pass or based on graph structure
                };
                
                partitionDtos.Add(dto);
            }

            // Link Upstream/Downstream partitions
            // Mapping from NodeId (int) to PartitionId (Guid)
            var nodeToPartitionId = partitionDtos.ToDictionary(p => p.PartitionIndex, p => p.Id);

            var linkedDtos = new List<SmartPartitionDto>();
            foreach (var dto in partitionDtos)
            {
                // Find node
                var node = graph.Nodes.First(n => n.Id == dto.PartitionIndex);
                
                // Resolve Upstream (Previous)
                Guid? upstreamId = null;
                if (node.PreviousNodes.Any())
                {
                    // For pipeline, we usually have one linear upstream. If multiple, pick first valid.
                    // Complex DAGs might need more robust handling in DTO (List of Upstream) or just pick primary.
                    upstreamId = nodeToPartitionId[node.PreviousNodes.First().Id];
                }

                // Resolve Downstream (Next)
                Guid? downstreamId = null;
                if (node.NextNodes.Any())
                {
                    downstreamId = nodeToPartitionId[node.NextNodes.First().Id];
                }

                // Create new DTO with links (SmartPartitionDto is immutable/init-only usually)
                // Actually it has init properties. We can use object initializer with `with` expression if C# version supports, or just recreate.
                // Assuming C# 9+ `with` works on classes if they are records, but this is a class.
                // Recreating for safety.

                linkedDtos.Add(new SmartPartitionDto
                {
                    Id = dto.Id,
                    SubtaskId = dto.SubtaskId,
                    PartitionIndex = dto.PartitionIndex,
                    Status = dto.Status,
                    EstimatedMemoryMb = dto.EstimatedMemoryMb,
                    IsParentPeer = dto.IsParentPeer,
                    InputTensorNames = dto.InputTensorNames,
                    OutputTensorNames = dto.OutputTensorNames,
                    AssignedDeviceId = dto.AssignedDeviceId,
                    AssignedToUserId = dto.AssignedToUserId,
                    CreatedAtUtc = dto.CreatedAtUtc,
                    UpstreamPartitionId = upstreamId,
                    DownstreamPartitionId = downstreamId
                });
            }

            return linkedDtos;
        }

        /// <summary>
        /// Represents a partition along a dynamic input dimension
        /// </summary>
        public class InputPartition
        {
            /// <summary>
            /// The input name being partitioned
            /// </summary>
            public required string InputName { get; set; }
            
            /// <summary>
            /// The dimension index being partitioned (e.g., 0 for batch dimension)
            /// </summary>
            public int DimensionIndex { get; set; }
            
            /// <summary>
            /// The dimension parameter name (e.g., "batch_size")
            /// </summary>
            public required string DimensionParam { get; set; }
            
            /// <summary>
            /// Number of partitions along this dimension
            /// </summary>
            public int PartitionCount { get; set; }
            
            /// <summary>
            /// Estimated memory per partition
            /// </summary>
            public long MemoryPerPartition { get; set; }
        }

        /// <summary>
        /// Attempts to partition the input along dynamic dimensions if total memory exceeds threshold
        /// </summary>
        /// <param name="model">The ONNX model</param>
        /// <param name="inputs">List of inputs with actual dimensions</param>
        /// <param name="memoryThresholdBytes">Memory threshold for triggering partitioning (default: 3GB)</param>
        /// <returns>InputPartition if partitioning is possible and beneficial, null otherwise</returns>
        public InputPartition? PartitionInput(
            Onnx.ModelProto model,
            List<NamedOnnxValue> inputs,
            long memoryThresholdBytes = DEFAULT_MEMORY_THRESHOLD_BYTES)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            
            if (inputs == null || inputs.Count == 0)
                return null;

            var inputDimensions = inputs
                .ToDictionary(x => x.Name, x => x.AsTensor<float>().Dimensions.ToArray());

            // Calculate total memory footprint including all downstream operations
            long totalMemory = _sizeService.CalculateModelMemorySize(model, inputDimensions);
            
            // Only partition if memory exceeds threshold
            if (totalMemory <= memoryThresholdBytes)
                return null;

            // Find dynamic dimensions in inputs
            foreach (var input in model.Graph.Input)
            {
                if (input.Type?.TensorType?.Shape == null)
                    continue;

                for (int dimIdx = 0; dimIdx < input.Type.TensorType.Shape.Dim.Count; dimIdx++)
                {
                    var dim = input.Type.TensorType.Shape.Dim[dimIdx];
                    
                    // Check if this is a dynamic dimension
                    if (!string.IsNullOrEmpty(dim.DimParam))
                    {
                        // Get actual dimension value from input
                        if (inputDimensions.TryGetValue(input.Name, out int[] actualDims) &&
                            dimIdx < actualDims.Length &&
                            actualDims[dimIdx] > 1)
                        {
                            // Calculate how many partitions we need
                            int partitionCount = (int)Math.Ceiling((double)totalMemory / memoryThresholdBytes);
                            
                            // Ensure partition count doesn't exceed the dimension size
                            partitionCount = Math.Min(partitionCount, actualDims[dimIdx]);
                            
                            if (partitionCount > 1)
                            {
                                // Calculate memory per partition
                                long memoryPerPartition = totalMemory / partitionCount;
                                
                                return new InputPartition
                                {
                                    InputName = input.Name,
                                    DimensionIndex = dimIdx,
                                    DimensionParam = dim.DimParam,
                                    PartitionCount = partitionCount,
                                    MemoryPerPartition = memoryPerPartition
                                };
                            }
                        }
                    }
                }
            }

            return null;
        }
        
        /// <summary>
        /// Metrics for evaluating partition quality
        /// </summary>
        private class PartitionMetrics
        {
            public int EdgeCuts { get; set; }
            public int TotalEdges { get; set; }
            public double EdgeCutRatio => TotalEdges > 0 ? (double)EdgeCuts / TotalEdges : 0;
            public double LoadBalanceFactor { get; set; }
            public long MaxPartitionSize { get; set; }
            public long MinPartitionSize { get; set; }
            public double BalanceRatio => MinPartitionSize > 0 ? (double)MaxPartitionSize / MinPartitionSize : 0;
        }

        /// <summary>
        /// Dependency information for ONNX nodes
        /// </summary>
        private class DependencyInfo
        {
            /// <summary>
            /// Map of tensor name to the node index that produces it
            /// </summary>
            public Dictionary<string, int> TensorProducers { get; set; } = new Dictionary<string, int>();

            /// <summary>
            /// Map of node index to list of node indices that depend on it
            /// </summary>
            public Dictionary<int, List<int>> NodeDependents { get; set; } = new Dictionary<int, List<int>>();

            /// <summary>
            /// Map of node index to list of node indices it depends on
            /// </summary>
            public Dictionary<int, List<int>> NodeDependencies { get; set; } = new Dictionary<int, List<int>>();
        }

        /// <summary>
        /// Partitions an ONNX model into parallelizable subgraphs if memory constraints require it
        /// Uses balanced graph partitioning to minimize edge cuts and communication overhead
        /// </summary>
        /// <param name="model">The ONNX model to partition</param>
        /// <param name="inputs">List of inputs (for resolving dynamic shapes like batch_size)</param>
        /// <param name="availableMemoryBytes">Available memory in bytes (default: 8GB)</param>
        /// <returns>Partition graph with nodes representing parallelizable subparts</returns>
        public PartitionGraph PartitionModel(
            Onnx.ModelProto model,
            List<NamedOnnxValue> inputs,
            long availableMemoryBytes = DEFAULT_MEMORY_THRESHOLD_BYTES)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (model.Graph == null)
            {
                throw new InvalidOperationException("Model graph is null");
            }

            if (availableMemoryBytes <= 0)
            {
                throw new ArgumentException("Available memory must be positive", nameof(availableMemoryBytes));
            }
             
            var inputDimensions = inputs
                 .ToDictionary(x => x.Name, x => x.AsTensor<float>().Dimensions.ToArray());

            var graph = new PartitionGraph();
             
            long totalMemoryBytes = _sizeService.CalculateModelMemorySize(model, inputDimensions);
            graph.TotalModelMemoryBytes = totalMemoryBytes;

            graph.IsPartitioned = true;
            bool partitioningBeneficial = BuildBalancedPartitionGraph(model, inputDimensions, availableMemoryBytes, graph);
           
            if (!partitioningBeneficial)
            {
                graph.IsPartitioned = false;
                graph.Nodes.Clear();
                graph.RootNodes.Clear();
                graph.LeafNodes.Clear();
                graph.ExecutionLevels.Clear();
                
                var singleNode = CreateSinglePartitionNode(model, inputDimensions);
                graph.Nodes.Add(singleNode);
                graph.RootNodes.Add(singleNode);
                graph.LeafNodes.Add(singleNode);
                graph.ExecutionLevels[0] = new List<PartitionNode> { singleNode };
                graph.MaxParallelism = 1;
            }

            return graph;
        }


        /// <summary>
        /// Creates a single partition node containing the entire model
        /// </summary>
        private PartitionNode CreateSinglePartitionNode(Onnx.ModelProto model, Dictionary<string, int[]> inputDimensions)
        {
            var node = new PartitionNode
            {
                Id = 0,
                InputNames = model.Graph.Input.Select(i => i.Name).ToList(),
                OutputNames = model.Graph.Output.Select(o => o.Name).ToList(),
                StartNodeIndex = 0,
                EndNodeIndex = model.Graph.Node.Count,
                EstimatedMemoryBytes = _sizeService.CalculateModelMemorySize(model, inputDimensions),
                ExecutionLevel = 0
            };

            return node;
        }

        /// <summary>
        /// Builds a balanced partition graph using advanced partitioning with edge cut minimization
        /// </summary>
        /// <returns>True if partitioning provides sufficient benefit, false otherwise</returns>
        private bool BuildBalancedPartitionGraph(
            Onnx.ModelProto model,
            Dictionary<string, int[]> inputDimensions,
            long availableMemoryBytes,
            PartitionGraph graph)
        {
            var nodes = model.Graph.Node.ToList();
            var tensorMemorySizes = BuildTensorMemoryMap(model.Graph, inputDimensions);

            // Build dependency graph to identify parallelizable branches
            var dependencyInfo = BuildDependencyInfo(model.Graph);

            // Create balanced partitions with edge cut minimization
            var partitionNodes = CreateBalancedPartitions(model, nodes, tensorMemorySizes, dependencyInfo, availableMemoryBytes);

            // If no partitions created or only one partition, return false
            if (partitionNodes.Count <= 1)
            {
                return false;
            }

            // Build graph connections between partition nodes
            ConnectPartitionNodes(partitionNodes, model.Graph);

            // Calculate partition quality metrics
            var metrics = CalculatePartitionMetrics(partitionNodes, dependencyInfo, graph.TotalModelMemoryBytes);

            // Assess if partitioning provides sufficient benefit
            bool isBeneficial = AssessPartitioningGain(metrics, partitionNodes.Count, graph.TotalModelMemoryBytes, availableMemoryBytes);

            if (!isBeneficial)
            {
                return false;
            }

            // Calculate execution levels for parallel execution
            AssignExecutionLevels(partitionNodes);

            // Populate the partition graph
            graph.Nodes = partitionNodes;
            graph.RootNodes = partitionNodes.Where(n => n.PreviousNodes.Count == 0).ToList();
            graph.LeafNodes = partitionNodes.Where(n => n.NextNodes.Count == 0).ToList();
            
            // Group by execution levels
            graph.ExecutionLevels = partitionNodes
                .GroupBy(n => n.ExecutionLevel)
                .ToDictionary(g => g.Key, g => g.ToList());
            
            graph.MaxParallelism = graph.ExecutionLevels.Values.Any() ? graph.ExecutionLevels.Values.Max(list => list.Count) : 0;

            return true;
        }

        /// <summary>
        /// Builds the partition graph by creating nodes and establishing connections
        /// </summary>
        private void BuildPartitionGraph(
            Onnx.ModelProto model,
            Dictionary<string, int[]> inputDimensions,
            long availableMemoryBytes,
            PartitionGraph graph)
        {
            var nodes = model.Graph.Node.ToList();
            var tensorMemorySizes = BuildTensorMemoryMap(model.Graph, inputDimensions);

            // Build dependency graph to identify parallelizable branches
            var dependencyInfo = BuildDependencyInfo(model.Graph);

            // Create partition nodes based on memory constraints AND parallelization opportunities
            var partitionNodes = CreateParallelPartitions(model, nodes, tensorMemorySizes, dependencyInfo, availableMemoryBytes);

            // Build graph connections between partition nodes
            ConnectPartitionNodes(partitionNodes, model.Graph);

            // Calculate execution levels for parallel execution
            AssignExecutionLevels(partitionNodes);

            // Populate the partition graph
            graph.Nodes = partitionNodes;
            graph.RootNodes = partitionNodes.Where(n => n.PreviousNodes.Count == 0).ToList();
            graph.LeafNodes = partitionNodes.Where(n => n.NextNodes.Count == 0).ToList();
            
            // Group by execution levels
            graph.ExecutionLevels = partitionNodes
                .GroupBy(n => n.ExecutionLevel)
                .ToDictionary(g => g.Key, g => g.ToList());
            
            graph.MaxParallelism = graph.ExecutionLevels.Values.Any() ? graph.ExecutionLevels.Values.Max(list => list.Count) : 0;
        }

        /// <summary>
        /// Creates balanced partitions using graph partitioning with edge cut minimization
        /// </summary>
        private List<PartitionNode> CreateBalancedPartitions(
            Onnx.ModelProto model,
            List<Onnx.NodeProto> nodes,
            Dictionary<string, long> tensorMemorySizes,
            DependencyInfo dependencyInfo,
            long availableMemoryBytes)
        {
            // Calculate optimal number of partitions based on memory constraints
            long totalMemory = _sizeService.CalculateModelMemorySize(model, new Dictionary<string, int[]>());
            int targetPartitionCount = Math.Max(2, (int)Math.Ceiling((double)totalMemory / availableMemoryBytes));
            
            // Use greedy balanced partitioning with edge cut minimization
            var partitions = GreedyBalancedPartitioning(
                model,
                nodes,
                tensorMemorySizes,
                dependencyInfo,
                targetPartitionCount,
                availableMemoryBytes);

            // Refine partitions using local optimization (Kernighan-Lin style)
            RefinePartitionsWithLocalOptimization(partitions, nodes, tensorMemorySizes, dependencyInfo);

            return partitions;
        }

        /// <summary>
        /// Greedy balanced partitioning algorithm that creates initial partitions
        /// </summary>
        private List<PartitionNode> GreedyBalancedPartitioning(
            Onnx.ModelProto model,
            List<Onnx.NodeProto> nodes,
            Dictionary<string, long> tensorMemorySizes,
            DependencyInfo dependencyInfo,
            int targetPartitionCount,
            long availableMemoryBytes)
        {
            var partitions = new List<PartitionNode>();
            var nodeToPartitionMap = new Dictionary<int, int>();
            var partitionNodeLists = new List<List<int>>();
            var partitionMemories = new List<long>();

            // Initialize partitions
            for (int i = 0; i < targetPartitionCount; i++)
            {
                partitionNodeLists.Add(new List<int>());
                partitionMemories.Add(0);
            }

            // Assign nodes to partitions using a balanced approach
            var processedNodes = new HashSet<int>();
            var availableNodes = new Queue<int>();

            // Start with root nodes
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!dependencyInfo.NodeDependencies.ContainsKey(i) ||
                    dependencyInfo.NodeDependencies[i].Count == 0)
                {
                    availableNodes.Enqueue(i);
                }
            }

            // Process nodes in topological order
            while (availableNodes.Count > 0 || processedNodes.Count < nodes.Count)
            {
                if (availableNodes.Count == 0)
                {
                    // Find next unprocessed node
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        if (!processedNodes.Contains(i))
                        {
                            availableNodes.Enqueue(i);
                            break;
                        }
                    }
                }

                if (availableNodes.Count == 0) break;

                int nodeIdx = availableNodes.Dequeue();
                if (processedNodes.Contains(nodeIdx)) continue;

                long nodeMemory = _sizeService.CalculateNodeMemory(nodes[nodeIdx], tensorMemorySizes);

                // Find best partition for this node (minimize edge cuts and balance load)
                int bestPartition = FindBestPartitionForNode(
                    nodeIdx,
                    nodes,
                    dependencyInfo,
                    nodeToPartitionMap,
                    partitionMemories,
                    nodeMemory,
                    availableMemoryBytes);

                // Assign node to partition
                partitionNodeLists[bestPartition].Add(nodeIdx);
                partitionMemories[bestPartition] += nodeMemory;
                nodeToPartitionMap[nodeIdx] = bestPartition;
                processedNodes.Add(nodeIdx);

                // Add dependent nodes to queue
                if (dependencyInfo.NodeDependents.TryGetValue(nodeIdx, out var dependents))
                {
                    foreach (var dependent in dependents)
                    {
                        if (!processedNodes.Contains(dependent) &&
                            dependencyInfo.NodeDependencies[dependent].All(d => processedNodes.Contains(d)))
                        {
                            availableNodes.Enqueue(dependent);
                        }
                    }
                }
            }

            // Create partition nodes from the assignments
            int partitionId = 0;
            foreach (var nodeList in partitionNodeLists)
            {
                if (nodeList.Count > 0)
                {
                    var partition = CreatePartitionFromNodes(
                        model,
                        nodes,
                        nodeList,
                        partitionId++,
                        tensorMemorySizes,
                        dependencyInfo);
                    partitions.Add(partition);
                }
            }

            return partitions;
        }

        /// <summary>
        /// Finds the best partition for a node by minimizing edge cuts and balancing load
        /// </summary>
        private int FindBestPartitionForNode(
            int nodeIdx,
            List<Onnx.NodeProto> nodes,
            DependencyInfo dependencyInfo,
            Dictionary<int, int> nodeToPartitionMap,
            List<long> partitionMemories,
            long nodeMemory,
            long availableMemoryBytes)
        {
            int bestPartition = 0;
            double bestScore = double.MinValue;

            for (int p = 0; p < partitionMemories.Count; p++)
            {
                // Check if partition has space
                if (partitionMemories[p] + nodeMemory > availableMemoryBytes)
                    continue;

                // Calculate score based on:
                // 1. Number of dependencies in this partition (minimize edge cuts)
                // 2. Load balance (prefer less loaded partitions)
                
                int dependenciesInPartition = 0;
                if (dependencyInfo.NodeDependencies.TryGetValue(nodeIdx, out var deps))
                {
                    dependenciesInPartition = deps.Count(d =>
                        nodeToPartitionMap.TryGetValue(d, out int depPartition) && depPartition == p);
                }

                // Normalize scores
                double edgeCutScore = (double)dependenciesInPartition / Math.Max(1, dependencyInfo.NodeDependencies.ContainsKey(nodeIdx) ? dependencyInfo.NodeDependencies[nodeIdx].Count : 1);
                double balanceScore = 1.0 - ((double)partitionMemories[p] / availableMemoryBytes);

                // Combined score (70% edge cut, 30% balance)
                double score = 0.7 * edgeCutScore + 0.3 * balanceScore;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPartition = p;
                }
            }

            return bestPartition;
        }

        /// <summary>
        /// Refines partitions using local optimization (Kernighan-Lin algorithm)
        /// </summary>
        private void RefinePartitionsWithLocalOptimization(
            List<PartitionNode> partitions,
            List<Onnx.NodeProto> nodes,
            Dictionary<string, long> tensorMemorySizes,
            DependencyInfo dependencyInfo)
        {
            // Local optimization to reduce edge cuts
            bool improved = true;
            int iterations = 0;
            const int maxIterations = 10;

            while (improved && iterations < maxIterations)
            {
                improved = false;
                iterations++;

                // Try swapping nodes between adjacent partitions
                for (int i = 0; i < partitions.Count - 1; i++)
                {
                    for (int j = i + 1; j < partitions.Count; j++)
                    {
                        // Calculate current edge cuts
                        int currentCuts = CalculateEdgeCutsBetweenPartitions(partitions[i], partitions[j], dependencyInfo);

                        // Try moving nodes from partition i to j and vice versa
                        if (TryOptimizePartitionPair(partitions[i], partitions[j], nodes, tensorMemorySizes, dependencyInfo))
                        {
                            int newCuts = CalculateEdgeCutsBetweenPartitions(partitions[i], partitions[j], dependencyInfo);
                            if (newCuts < currentCuts)
                            {
                                improved = true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Calculates edge cuts between two partitions
        /// </summary>
        private int CalculateEdgeCutsBetweenPartitions(PartitionNode p1, PartitionNode p2, DependencyInfo dependencyInfo)
        {
            int cuts = 0;
            var p1Nodes = new HashSet<int>(p1.NodeIndices);
            var p2Nodes = new HashSet<int>(p2.NodeIndices);

            foreach (var nodeIdx in p1.NodeIndices)
            {
                if (dependencyInfo.NodeDependents.TryGetValue(nodeIdx, out var dependents))
                {
                    cuts += dependents.Count(d => p2Nodes.Contains(d));
                }
            }

            foreach (var nodeIdx in p2.NodeIndices)
            {
                if (dependencyInfo.NodeDependents.TryGetValue(nodeIdx, out var dependents))
                {
                    cuts += dependents.Count(d => p1Nodes.Contains(d));
                }
            }

            return cuts;
        }

        /// <summary>
        /// Tries to optimize a pair of partitions by moving nodes
        /// </summary>
        private bool TryOptimizePartitionPair(
            PartitionNode p1,
            PartitionNode p2,
            List<Onnx.NodeProto> nodes,
            Dictionary<string, long> tensorMemorySizes,
            DependencyInfo dependencyInfo)
        {
            // Simple heuristic: try moving boundary nodes
            // This is a simplified version - full Kernighan-Lin is more complex
            return false; // Placeholder for now
        }

        /// <summary>
        /// Calculates partition quality metrics
        /// </summary>
        private PartitionMetrics CalculatePartitionMetrics(
            List<PartitionNode> partitions,
            DependencyInfo dependencyInfo,
            long totalModelSize)
        {
            var metrics = new PartitionMetrics();

            // Calculate edge cuts
            int totalEdges = 0;
            int edgeCuts = 0;

            foreach (var partition in partitions)
            {
                var partitionNodes = new HashSet<int>(partition.NodeIndices);

                foreach (var nodeIdx in partition.NodeIndices)
                {
                    if (dependencyInfo.NodeDependents.TryGetValue(nodeIdx, out var dependents))
                    {
                        foreach (var dependent in dependents)
                        {
                            totalEdges++;
                            if (!partitionNodes.Contains(dependent))
                            {
                                edgeCuts++;
                            }
                        }
                    }
                }
            }

            metrics.EdgeCuts = edgeCuts;
            metrics.TotalEdges = totalEdges;

            // Calculate load balance
            if (partitions.Count > 0)
            {
                metrics.MaxPartitionSize = partitions.Max(p => p.EstimatedMemoryBytes);
                metrics.MinPartitionSize = partitions.Min(p => p.EstimatedMemoryBytes);
                long avgSize = partitions.Sum(p => p.EstimatedMemoryBytes) / partitions.Count;
                metrics.LoadBalanceFactor = avgSize > 0 ? (double)metrics.MaxPartitionSize / avgSize : 1.0;
            }

            return metrics;
        }

        /// <summary>
        /// Assesses whether partitioning provides sufficient benefit
        /// </summary>
        private bool AssessPartitioningGain(
            PartitionMetrics metrics,
            int partitionCount,
            long totalModelSize,
            long availableMemoryBytes)
        {
            // Don't partition if only 1-2 partitions or excessive edge cuts
            if (partitionCount <= 1)
                return false;

            // Check if edge cut ratio is acceptable
            if (metrics.EdgeCutRatio > MAX_EDGE_CUT_RATIO)
                return false;

            // Calculate potential gain from parallelization
            // Gain = parallelism benefit - communication overhead
            double parallelismGain = Math.Min(partitionCount, 4) / 4.0; // Assume max 4-way parallelism benefit
            double communicationOverhead = metrics.EdgeCutRatio;
            double netGain = parallelismGain - communicationOverhead;

            // Only partition if net gain exceeds threshold
            if (netGain < MIN_PARTITION_GAIN_THRESHOLD)
                return false;

            // Check if load is reasonably balanced
            if (metrics.BalanceRatio > 3.0) // Max 3x imbalance
                return false;

            return true;
        }

        /// <summary>
        /// Builds dependency information for the ONNX graph
        /// </summary>
        private DependencyInfo BuildDependencyInfo(Onnx.GraphProto graph)
        {
            var info = new DependencyInfo();
            var nodes = graph.Node.ToList();

            // Mark initial inputs as produced
            foreach (var input in graph.Input)
            {
                info.TensorProducers[input.Name] = -1; // -1 indicates model input
            }

            // Build tensor producer map and node dependencies
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                info.NodeDependencies[i] = new List<int>();
                info.NodeDependents[i] = new List<int>();

                // Record outputs produced by this node
                foreach (var output in node.Output)
                {
                    if (!string.IsNullOrEmpty(output))
                    {
                        info.TensorProducers[output] = i;
                    }
                }

                // Record dependencies for this node
                foreach (var input in node.Input)
                {
                    if (!string.IsNullOrEmpty(input) && info.TensorProducers.TryGetValue(input, out int producerIdx))
                    {
                        if (producerIdx >= 0) // Not a model input
                        {
                            info.NodeDependencies[i].Add(producerIdx);
                            if (!info.NodeDependents.ContainsKey(producerIdx))
                            {
                                info.NodeDependents[producerIdx] = new List<int>();
                            }
                            info.NodeDependents[producerIdx].Add(i);
                        }
                    }
                }
            }

            return info;
        }

        /// <summary>
        /// Creates parallel-aware partitions by identifying independent subgraphs
        /// </summary>
        private List<PartitionNode> CreateParallelPartitions(
            Onnx.ModelProto model,
            List<Onnx.NodeProto> nodes,
            Dictionary<string, long> tensorMemorySizes,
            DependencyInfo dependencyInfo,
            long availableMemoryBytes)
        {
            var partitions = new List<PartitionNode>();
            var processedNodes = new HashSet<int>();
            var availableNodes = new HashSet<int>();
            int partitionId = 0;

            // Initialize with nodes that have no dependencies (can start immediately)
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!dependencyInfo.NodeDependencies.ContainsKey(i) || 
                    dependencyInfo.NodeDependencies[i].Count == 0)
                {
                    availableNodes.Add(i);
                }
            }

            // Process nodes in waves, creating parallel partitions
            while (availableNodes.Count > 0 || processedNodes.Count < nodes.Count)
            {
                // Find independent branches that can run in parallel
                var parallelBranches = IdentifyParallelBranches(
                    nodes, 
                    availableNodes, 
                    dependencyInfo, 
                    processedNodes,
                    tensorMemorySizes,
                    availableMemoryBytes);

                // Create a partition for each branch
                foreach (var branch in parallelBranches)
                {
                    if (branch.Count > 0)
                    {
                        var partition = CreatePartitionFromNodes(
                            model,
                            nodes,
                            branch,
                            partitionId++,
                            tensorMemorySizes,
                            dependencyInfo);
                        
                        partitions.Add(partition);

                        // Mark nodes as processed
                        foreach (var nodeIdx in branch)
                        {
                            processedNodes.Add(nodeIdx);
                            availableNodes.Remove(nodeIdx);
                        }
                    }
                }

                // Add newly available nodes (whose dependencies are now satisfied)
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!processedNodes.Contains(i) && !availableNodes.Contains(i))
                    {
                        // Check if all dependencies are processed
                        bool allDependenciesMet = dependencyInfo.NodeDependencies.ContainsKey(i) &&
                            dependencyInfo.NodeDependencies[i].All(dep => processedNodes.Contains(dep));
                        
                        if (allDependenciesMet || !dependencyInfo.NodeDependencies.ContainsKey(i))
                        {
                            availableNodes.Add(i);
                        }
                    }
                }

                // Safety check to prevent infinite loops
                if (availableNodes.Count == 0 && processedNodes.Count < nodes.Count)
                {
                    // Add remaining nodes as fallback
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        if (!processedNodes.Contains(i))
                        {
                            availableNodes.Add(i);
                        }
                    }
                }
            }

            return partitions;
        }

        /// <summary>
        /// Identifies independent branches that can execute in parallel
        /// </summary>
        private List<List<int>> IdentifyParallelBranches(
            List<Onnx.NodeProto> nodes,
            HashSet<int> availableNodes,
            DependencyInfo dependencyInfo,
            HashSet<int> processedNodes,
            Dictionary<string, long> tensorMemorySizes,
            long availableMemoryBytes)
        {
            var branches = new List<List<int>>();
            var assignedNodes = new HashSet<int>();

            foreach (var startNode in availableNodes.OrderBy(n => n))
            {
                if (assignedNodes.Contains(startNode))
                    continue;

                var branch = new List<int>();
                var branchMemory = 0L;
                var currentNode = startNode;
                
                // Grow branch while memory permits and no conflicts
                while (currentNode >= 0 && !assignedNodes.Contains(currentNode))
                {
                    long nodeMemory = _sizeService.CalculateNodeMemory(nodes[currentNode], tensorMemorySizes);
                    
                    if (branchMemory + nodeMemory > availableMemoryBytes && branch.Count > 0)
                    {
                        break; // Would exceed memory limit
                    }

                    branch.Add(currentNode);
                    assignedNodes.Add(currentNode);
                    branchMemory += nodeMemory;

                    // Try to extend branch with next dependent node
                    currentNode = -1;
                    if (dependencyInfo.NodeDependents.TryGetValue(branch[branch.Count - 1], out var dependents))
                    {
                        // Find a dependent that's available and not yet assigned
                        foreach (var dependent in dependents.OrderBy(d => d))
                        {
                            if (availableNodes.Contains(dependent) || 
                                (dependencyInfo.NodeDependencies[dependent].All(dep => 
                                    processedNodes.Contains(dep) || branch.Contains(dep))))
                            {
                                if (!assignedNodes.Contains(dependent))
                                {
                                    currentNode = dependent;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (branch.Count > 0)
                {
                    branches.Add(branch);
                }
            }

            // If no branches created but we have available nodes, create single-node branches
            if (branches.Count == 0 && availableNodes.Count > 0)
            {
                foreach (var node in availableNodes.Take(1))
                {
                    branches.Add(new List<int> { node });
                }
            }

            return branches;
        }

        /// <summary>
        /// Creates a partition from a list of node indices
        /// </summary>
        private PartitionNode CreatePartitionFromNodes(
            Onnx.ModelProto model,
            List<Onnx.NodeProto> nodes,
            List<int> nodeIndices,
            int partitionId,
            Dictionary<string, long> tensorMemorySizes,
            DependencyInfo dependencyInfo)
        {
            var partition = new PartitionNode
            {
                Id = partitionId,
                NodeIndices = nodeIndices,
                StartNodeIndex = nodeIndices.Min(),
                EndNodeIndex = nodeIndices.Max() + 1
            };

            // Calculate inputs and outputs
            var inputs = new HashSet<string>();
            var outputs = new HashSet<string>();
            var internalTensors = new HashSet<string>();
            long totalMemory = 0;

            foreach (var idx in nodeIndices)
            {
                var node = nodes[idx];
                
                // Track outputs
                foreach (var output in node.Output)
                {
                    if (!string.IsNullOrEmpty(output))
                    {
                        outputs.Add(output);
                        internalTensors.Add(output);
                    }
                }

                // Track inputs
                foreach (var input in node.Input)
                {
                    if (!string.IsNullOrEmpty(input) && !internalTensors.Contains(input))
                    {
                        inputs.Add(input);
                    }
                }

                // Calculate memory
                totalMemory += _sizeService.CalculateNodeMemory(node, tensorMemorySizes);
            }

            partition.InputNames = inputs.ToList();
            partition.OutputNames = outputs.ToList();
            partition.EstimatedMemoryBytes = totalMemory;

            return partition;
        }

        /// <summary>
        /// Assigns execution levels to partition nodes for parallel execution
        /// Uses topological sort to determine which partitions can run concurrently
        /// </summary>
        private void AssignExecutionLevels(List<PartitionNode> partitions)
        {
            // Calculate the longest path from root to each node (execution level)
            var levels = new Dictionary<PartitionNode, int>();
            
            // Initialize all nodes to level 0
            foreach (var partition in partitions)
            {
                levels[partition] = 0;
            }

            // Use BFS to assign levels based on dependencies
            var queue = new Queue<PartitionNode>(partitions.Where(p => p.PreviousNodes.Count == 0));
            var visited = new HashSet<PartitionNode>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited.Contains(current))
                    continue;
                
                visited.Add(current);

                // Calculate this node's level as max of all predecessor levels + 1
                int maxPredecessorLevel = -1;
                foreach (var predecessor in current.PreviousNodes)
                {
                    maxPredecessorLevel = Math.Max(maxPredecessorLevel, levels[predecessor]);
                }
                levels[current] = maxPredecessorLevel + 1;

                // Add successors to queue
                foreach (var successor in current.NextNodes)
                {
                    if (!visited.Contains(successor))
                    {
                        queue.Enqueue(successor);
                    }
                }
            }

            // Assign calculated levels to partitions
            foreach (var partition in partitions)
            {
                partition.ExecutionLevel = levels[partition];
            }
        }

        /// <summary>
        /// Connects partition nodes by establishing Previous/Next relationships
        /// </summary>
        private void ConnectPartitionNodes(List<PartitionNode> partitionNodes, Onnx.GraphProto graph)
        {
            // Build a map of which outputs are produced by which partitions
            var outputToPartitionMap = new Dictionary<string, PartitionNode>();

            foreach (var partition in partitionNodes)
            {
                foreach (var output in partition.OutputNames)
                {
                    outputToPartitionMap[output] = partition;
                }
            }

            // Connect partitions based on data dependencies
            foreach (var partition in partitionNodes)
            {
                foreach (var inputName in partition.InputNames)
                {
                    // Find which partition produces this input
                    if (outputToPartitionMap.TryGetValue(inputName, out var producerPartition))
                    {
                        // Connect the partitions
                        if (!partition.PreviousNodes.Contains(producerPartition))
                        {
                            partition.PreviousNodes.Add(producerPartition);
                        }

                        if (!producerPartition.NextNodes.Contains(partition))
                        {
                            producerPartition.NextNodes.Add(partition);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Builds a map of tensor names to their memory sizes
        /// Now uses the complete memory calculation that includes all intermediate tensors
        /// </summary>
        private Dictionary<string, long> BuildTensorMemoryMap(Onnx.GraphProto graph, Dictionary<string, int[]> inputDimensions)
        {
            // Use the comprehensive memory map builder from OnnxSizeService
            return _sizeService.BuildCompleteTensorMemoryMap(graph, inputDimensions);
        }
    }
}