using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    public class OnnxRuntimeService
    {
        private const string ExecutionProviderId = "InfiniteGpu";
        private bool _initialized;
        private readonly SemaphoreSlim _initializationLock = new(1, 1);

        public async Task<OnnxInferenceResult> ExecuteOnnxModelAsync(
            byte[] model,
            IReadOnlyList<NamedOnnxValue> inputs,
            CancellationToken cancellationToken = default)
        {
            if (model is null || model.Length == 0)
            {
                throw new ArgumentException("Model buffer cannot be null or empty.", nameof(model));
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var sessionOptions = new SessionOptions();
            sessionOptions.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MAX_PERFORMANCE);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var session = new InferenceSession(model, sessionOptions);
                using var results = session.Run(inputs);

                var outputs = new List<OnnxInferenceOutput>(results.Count);
                foreach (var output in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outputs.Add(ConvertOutput(output));
                }

                return new OnnxInferenceResult(outputs);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnnxRuntimeService] Model execution failed: {ex}");
                throw;
            }
        }

        public async Task<OnnxInferenceResult> ExecuteTrainingSessionAsync(
            byte[] trainingModel,
            byte[] optimizerModel,
            byte[] checkpoint,
            byte[] evalModel,
            IReadOnlyList<NamedOnnxValue> inputs,
            IReadOnlyList<NamedOnnxValue> targetOutputs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Create a temporary directory for this training session
            var tempDir = Path.Combine(Path.GetTempPath(), "InfiniteGpu", "Training", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            var trainingModelPath = Path.Combine(tempDir, "training_model.onnx");
            var optimizerModelPath = Path.Combine(tempDir, "optimizer_model.onnx");
            var checkpointPath = Path.Combine(tempDir, "checkpoint.ckpt");
            var evalModelPath = Path.Combine(tempDir, "eval_model.onnx");
            var newCheckpointPath = Path.Combine(tempDir, "checkpoint_updated.ckpt");

            // Lists to track disposable FixedBufferOnnxValues
            var fixedInputs = new List<FixedBufferOnnxValue>();
            var fixedOutputs = new List<FixedBufferOnnxValue>();

            try
            {
                // Write artifacts to temp files
                await File.WriteAllBytesAsync(trainingModelPath, trainingModel, cancellationToken);
                await File.WriteAllBytesAsync(optimizerModelPath, optimizerModel, cancellationToken);
                await File.WriteAllBytesAsync(checkpointPath, checkpoint, cancellationToken);
                await File.WriteAllBytesAsync(evalModelPath, evalModel, cancellationToken);

                using var sessionOptions = new SessionOptions();
                
                // Load checkpoint state from file
                using var checkpointState = CheckpointState.LoadCheckpoint(checkpointPath);
                
                // Create training session from files
                using var trainingSession = new TrainingSession(checkpointState, trainingModelPath, optimizerModelPath);
                
                // Convert NamedOnnxValue to FixedBufferOnnxValue for inputs
                foreach (var input in inputs)
                {
                    var fixedValue = ConvertToFixedBufferOnnxValue(input);
                    if (fixedValue != null)
                    {
                        fixedInputs.Add(fixedValue);
                    }
                }
                
                // Convert NamedOnnxValue to FixedBufferOnnxValue for outputs (target/label values)
                foreach (var output in targetOutputs)
                {
                    var fixedValue = ConvertToFixedBufferOnnxValue(output);
                    if (fixedValue != null)
                    {
                        fixedOutputs.Add(fixedValue);
                    }
                }
                
                // Reset gradients before training step
                trainingSession.LazyResetGrad();
                
                // Run training step with inputs and outputs as FixedBufferOnnxValue collections
                // TrainStep runs the forward pass, computes loss, and backpropagates gradients
                trainingSession.TrainStep(fixedInputs, fixedOutputs);
                
                // Run optimizer step to update weights based on computed gradients
                trainingSession.OptimizerStep();
                
                // Save updated checkpoint to new file
                CheckpointState.SaveCheckpoint(checkpointState, newCheckpointPath);
                
                // Read updated checkpoint back to memory
                var newCheckpointBytes = await File.ReadAllBytesAsync(newCheckpointPath, cancellationToken);
                
                var resultOutputs = new List<OnnxInferenceOutput>
                {
                    new OnnxInferenceOutput("updated_weights", "byte[]", new[] { newCheckpointBytes.Length }, newCheckpointBytes)
                };

                return new OnnxInferenceResult(resultOutputs);
            }
            finally
            {
                // Dispose all FixedBufferOnnxValues
                foreach (var fixedInput in fixedInputs)
                {
                    fixedInput?.Dispose();
                }
                foreach (var fixedOutput in fixedOutputs)
                {
                    fixedOutput?.Dispose();
                }
                
                // Cleanup temp directory
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// Converts a NamedOnnxValue to a FixedBufferOnnxValue for use with TrainingSession.TrainStep
        /// </summary>
        private static FixedBufferOnnxValue? ConvertToFixedBufferOnnxValue(NamedOnnxValue namedValue)
        {
            var value = namedValue.Value;
            
            return value switch
            {
                DenseTensor<float> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<double> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<long> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<int> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<short> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<byte> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<sbyte> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<ushort> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<uint> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<ulong> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                DenseTensor<bool> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<float> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<double> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<long> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<int> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<short> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<byte> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<sbyte> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<ushort> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<uint> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<ulong> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                Tensor<bool> tensor => FixedBufferOnnxValue.CreateFromTensor(tensor),
                _ => throw new NotSupportedException($"Unsupported tensor type for training: {value?.GetType()?.Name ?? "null"}")
            };
        }

        public async Task<bool> InitializeOnnxRuntimeAsync()
        {
            if (_initialized)
            {
                return true;
            }

            await _initializationLock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_initialized)
                {
                    return true;
                }

                EnvironmentCreationOptions envOptions = new()
                {
                    logId = ExecutionProviderId,
                    logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };

                using var ortEnv = OrtEnv.CreateInstanceWithOptions(ref envOptions);

                var catalog = ExecutionProviderCatalog.GetDefault();
                await catalog.EnsureAndRegisterCertifiedAsync();

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnnxRuntimeService] Failed to initialize ONNX Runtime: {ex}");
                return false;
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        private static OnnxInferenceOutput ConvertOutput(DisposableNamedOnnxValue value)
        {
            var elementType = DescribeElementType(value);
            var dimensions = TryGetDimensions(value);
            var data = ExtractFullData(value);

            return new OnnxInferenceOutput(value.Name, elementType, dimensions, data);
        }

        private static string DescribeElementType(DisposableNamedOnnxValue value)
        {
            try
            {
                var tensorElementType = value.ElementType;
                if (tensorElementType != TensorElementType.DataTypeMax)
                {
                    return tensorElementType.ToString();
                }
            }
            catch
            {
                // ElementType may throw for non-tensor outputs, ignore and fall back.
            }

            var runtimeType = value.Value?.GetType();
            if (runtimeType is not null)
            {
                return runtimeType.Name;
            }

            return "unknown";
        }

        private static int[]? TryGetDimensions(DisposableNamedOnnxValue value)
        {
            return value.Value switch
            {
                Tensor<float> tensor => tensor.Dimensions.ToArray(),
                Tensor<double> tensor => tensor.Dimensions.ToArray(),
                Tensor<long> tensor => tensor.Dimensions.ToArray(),
                Tensor<int> tensor => tensor.Dimensions.ToArray(),
                Tensor<bool> tensor => tensor.Dimensions.ToArray(),
                Tensor<string> tensor => tensor.Dimensions.ToArray(),
                _ => null
            };
        }

        private static object? ExtractFullData(DisposableNamedOnnxValue value)
        {
            return value.Value switch
            {
                Tensor<float> tensor => tensor.ToArray(),
                Tensor<double> tensor => tensor.ToArray(),
                Tensor<long> tensor => tensor.ToArray(),
                Tensor<int> tensor => tensor.ToArray(),
                Tensor<bool> tensor => tensor.ToArray(),
                Tensor<string> tensor => tensor.ToArray(),
                float[] array => array,
                double[] array => array,
                long[] array => array,
                int[] array => array,
                bool[] array => array,
                string[] array => array,
                IEnumerable<float> enumerable => enumerable.ToArray(),
                IEnumerable<double> enumerable => enumerable.ToArray(),
                IEnumerable<long> enumerable => enumerable.ToArray(),
                IEnumerable<int> enumerable => enumerable.ToArray(),
                IEnumerable<bool> enumerable => enumerable.ToArray(),
                IEnumerable<string> enumerable => enumerable.ToArray(),
                _ => value.Value
            };
        }


        public ExecutionProviderDevice GetExecutionProvider()
        {
            var catalog = ExecutionProviderCatalog.GetDefault();
            var ep = catalog.FindAllProviders().First();
            switch (ep.Name)
            {
                case "NvTensorRtRtxExecutionProvider":
                    return ExecutionProviderDevice.Gpu; 
                case "OpenVINOExecutionProvider":
                case "QNNExecutionProvider":
                case "VitisAIExecutionProvider":
                    return ExecutionProviderDevice.Npu; 
                case "CPUExecutionProvider":
                    return ExecutionProviderDevice.Cpu; 
                default:
                    return ExecutionProviderDevice.Cpu; 
            }
        }

    }

    public sealed record OnnxInferenceResult(IReadOnlyList<OnnxInferenceOutput> Outputs);

    public sealed record OnnxInferenceOutput(string Name, string ElementType, int[]? Dimensions, object? Data);
}