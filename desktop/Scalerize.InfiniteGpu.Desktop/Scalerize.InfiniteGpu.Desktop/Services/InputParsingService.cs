using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    public sealed class InputParsingService
    {
        private static readonly Regex NpyHeaderRegex = new(@"'descr'\s*:\s*'(?<descr>[^']+)'[^']*'fortran_order'\s*:\s*(?<fortran>True|False)[^']*'shape'\s*:\s*\((?<shape>[^)]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".mpg", ".mpeg", ".gifv"
        };

        private static readonly HashSet<string> TransparencyImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".webp", ".gif", ".tif", ".tiff", ".apng"
        };

        private static readonly HashSet<string> TransparencyFormatNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "PNG", "GIF", "TIFF", "WEBP"
        };

        private readonly HttpClient _httpClient;
        private readonly TokenizerService _tokenizer;

        public InputParsingService(HttpClient httpClient, TokenizerService tokenizerService)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenizer = tokenizerService ?? throw new ArgumentNullException(nameof(tokenizerService));
        }

        /// <summary>
        /// Builds named ONNX values from input bindings in parametersJson.
        /// Looks in both training.inputs (preferred for training tasks) and inference.bindings (for inference tasks).
        /// </summary>
        public async Task<List<NamedOnnxValue>> BuildNamedInputsAsync(string? parametersJson, CancellationToken cancellationToken)
        {
            var inputs = new List<NamedOnnxValue>();

            if (string.IsNullOrWhiteSpace(parametersJson))
            {
                return inputs;
            }

            try
            {
                using var document = JsonDocument.Parse(parametersJson);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return inputs;
                }

                // First, try to read from training.inputs (for training tasks)
                if (root.TryGetProperty("training", out var trainingNode) && trainingNode.ValueKind == JsonValueKind.Object)
                {
                    if (trainingNode.TryGetProperty("inputs", out var trainingInputsNode) && trainingInputsNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var bindingNode in trainingInputsNode.EnumerateArray())
                        {
                            if (bindingNode.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!bindingNode.TryGetProperty("tensorName", out var tensorNameNode) ||
                                tensorNameNode.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(tensorNameNode.GetString()))
                            {
                                continue;
                            }

                            var tensorName = tensorNameNode.GetString()!.Trim();

                            var handled = await TryAddBinaryTensorAsync(tensorName, bindingNode, inputs, cancellationToken);
                            if (!handled)
                            {
                                Debug.WriteLine($"[InputParsingService] Failed to build binary tensor for training input '{tensorName}'.");
                            }
                        }
                    }
                }

                // Fallback: check inference.bindings for inference tasks or backward compatibility
                if (inputs.Count == 0 && root.TryGetProperty("inference", out var inferenceNode) && inferenceNode.ValueKind == JsonValueKind.Object)
                {
                    if (inferenceNode.TryGetProperty("bindings", out var bindingsNode) && bindingsNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var bindingNode in bindingsNode.EnumerateArray())
                        {
                            if (bindingNode.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!bindingNode.TryGetProperty("tensorName", out var tensorNameNode) ||
                                tensorNameNode.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(tensorNameNode.GetString()))
                            {
                                continue;
                            }

                            var tensorName = tensorNameNode.GetString()!.Trim();

                            var handled = await TryAddBinaryTensorAsync(tensorName, bindingNode, inputs, cancellationToken);
                            if (!handled)
                            {
                                Debug.WriteLine($"[InputParsingService] Failed to build binary tensor for input '{tensorName}'.");
                            }

                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse parametersJson: {ex}");
            }

            return inputs;
        }

        /// <summary>
        /// Builds named ONNX values from output bindings in parametersJson.
        /// For training, these represent the target/label values needed for loss computation.
        /// Looks in both training.outputs (preferred for training tasks) and inference.outputs (fallback).
        /// </summary>
        public async Task<List<NamedOnnxValue>> BuildNamedOutputsAsync(string? parametersJson, CancellationToken cancellationToken)
        {
            var outputs = new List<NamedOnnxValue>();

            if (string.IsNullOrWhiteSpace(parametersJson))
            {
                return outputs;
            }

            try
            {
                using var document = JsonDocument.Parse(parametersJson);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return outputs;
                }

                // First, try to read from training.outputs (for training tasks with target/label values)
                if (root.TryGetProperty("training", out var trainingNode) && trainingNode.ValueKind == JsonValueKind.Object)
                {
                    if (trainingNode.TryGetProperty("outputs", out var trainingOutputsNode) && trainingOutputsNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var outputNode in trainingOutputsNode.EnumerateArray())
                        {
                            if (outputNode.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!outputNode.TryGetProperty("tensorName", out var tensorNameNode) ||
                                tensorNameNode.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(tensorNameNode.GetString()))
                            {
                                continue;
                            }

                            var tensorName = tensorNameNode.GetString()!.Trim();

                            // Training outputs must have a fileUrl with actual target data
                            if (outputNode.TryGetProperty("fileUrl", out var fileUrlNode) &&
                                fileUrlNode.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(fileUrlNode.GetString()))
                            {
                                var handled = await TryAddBinaryTensorAsync(tensorName, outputNode, outputs, cancellationToken);
                                if (!handled)
                                {
                                    Debug.WriteLine($"[InputParsingService] Failed to build binary tensor for training output '{tensorName}'.");
                                }
                            }
                        }
                    }
                }

                // Fallback: check inference.outputs for backward compatibility
                // (in case outputs with fileUrl were stored there)
                if (outputs.Count == 0 && root.TryGetProperty("inference", out var inferenceNode) && inferenceNode.ValueKind == JsonValueKind.Object)
                {
                    if (inferenceNode.TryGetProperty("outputs", out var outputsNode) && outputsNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var outputNode in outputsNode.EnumerateArray())
                        {
                            if (outputNode.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!outputNode.TryGetProperty("tensorName", out var tensorNameNode) ||
                                tensorNameNode.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(tensorNameNode.GetString()))
                            {
                                continue;
                            }

                            var tensorName = tensorNameNode.GetString()!.Trim();

                            // Check if this output has a fileUrl (contains actual data for training targets)
                            if (outputNode.TryGetProperty("fileUrl", out var fileUrlNode) &&
                                fileUrlNode.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(fileUrlNode.GetString()))
                            {
                                var handled = await TryAddBinaryTensorAsync(tensorName, outputNode, outputs, cancellationToken);
                                if (!handled)
                                {
                                    Debug.WriteLine($"[InputParsingService] Failed to build binary tensor for output '{tensorName}'.");
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse parametersJson for outputs: {ex}");
            }

            return outputs;
        }

        private async Task<bool> TryAddBinaryTensorAsync(string tensorName, JsonElement bindingNode, List<NamedOnnxValue> inputs, CancellationToken cancellationToken)
        {
            if (!bindingNode.TryGetProperty("fileUrl", out var fileUrlNode) ||
                fileUrlNode.ValueKind != JsonValueKind.String)
            {
                Debug.WriteLine($"[InputParsingService] Binary binding '{tensorName}' is missing a fileUrl.");
                return false;
            }

            var fileUrlValue = fileUrlNode.GetString();
            if (string.IsNullOrWhiteSpace(fileUrlValue))
            {
                Debug.WriteLine($"[InputParsingService] Binary binding '{tensorName}' specifies an empty fileUrl.");
                return false;
            }

            if (!TryCreateUri(fileUrlValue, out var fileUri) || fileUri is null)
            {
                Debug.WriteLine($"[InputParsingService] Binary binding '{tensorName}' has an invalid fileUrl '{fileUrlValue}'.");
                return false;
            }

            byte[] payload;

            try
            {
                payload = await DownloadBinaryPayloadAsync(fileUri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to download binary payload for '{tensorName}' from '{fileUri}': {ex}");
                return false;
            }

            if (payload.Length == 0)
            {
                Debug.WriteLine($"[InputParsingService] Binary payload for '{tensorName}' is empty.");
                return false;
            }

            var extension = Path.GetExtension(fileUri.AbsolutePath);
            extension = string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant();

            NamedOnnxValue? namedValue = null;
            var handled = false;

            switch (extension)
            {
                case ".json":
                    handled = TryParseJsonFile(tensorName, payload, out namedValue);
                    break;
                case ".txt":
                    handled = TryParseTextFile(tensorName, payload, bindingNode, out namedValue);
                    break;
                case ".npy":
                    handled = TryParseNpyTensor(tensorName, payload, out namedValue);
                    break;
                case ".npz":
                    handled = TryParseNpzTensor(tensorName, payload, out namedValue);
                    break;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".tif":
                case ".tiff":
                case ".webp":
                case ".gif":
                    handled = TryParseImageOrVideoTensor(tensorName, payload, extension, out namedValue);
                    break;
                default:
                    if (VideoExtensions.Contains(extension))
                    {
                        handled = TryParseVideoTensor(tensorName, payload, extension, out namedValue);
                    }
                    else
                    {
                        handled = false;
                    }
                    break;
            }

            if (!handled)
            {
                if (extension.Length == 0 && TryParseNpyTensor(tensorName, payload, out var unnamedNpy))
                {
                    namedValue = unnamedNpy;
                    handled = true;
                }
                else if (TryParseImageOrVideoTensor(tensorName, payload, extension, out var fallbackImage))
                {
                    namedValue = fallbackImage;
                    handled = true;
                }
                else if (extension.Length == 0 &&
                         TryParseVideoTensor(tensorName, payload, extension, out var fallbackVideo))
                {
                    namedValue = fallbackVideo;
                    handled = true;
                }
                else if (VideoExtensions.Contains(extension))
                {
                    Debug.WriteLine($"[InputParsingService] Unable to decode video payload for input '{tensorName}' with container '{extension}'.");
                }
                else if (extension.Length > 0)
                {
                    Debug.WriteLine($"[InputParsingService] Unsupported binary payload extension '{extension}' for input '{tensorName}'.");
                }
            }

            if (handled && namedValue is not null)
            {
                inputs.Add(namedValue);
                return true;
            }

            return false;
        }

        private async Task<byte[]> DownloadBinaryPayloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private bool TryCreateUri(string? value, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                uri = absolute;
                return true;
            }

            return false;
        }

        private bool TryParseJsonFile(string tensorName, byte[] payload, out NamedOnnxValue value)
        {
            value = null!;
            try
            {
                var jsonString = Encoding.UTF8.GetString(payload);
                if (!string.IsNullOrWhiteSpace(jsonString) &&
                    TryParseJsonArray(jsonString, out var tensorElement) &&
                    TryBuildTensor(tensorName, tensorElement, out value))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse JSON file for input '{tensorName}': {ex}");
            }
            return false;
        }

        private bool TryParseTextFile(string tensorName, byte[] payload, JsonElement bindingNode, out NamedOnnxValue value)
        {
            value = null!;
            try
            {
                var text = Encoding.UTF8.GetString(payload);

                // Extract tokenization parameters from binding
                var maxLength = 512;
                var addSpecialTokens = true;
                var padding = false;

                if (bindingNode.TryGetProperty("maxLength", out var maxLengthNode) &&
                    maxLengthNode.ValueKind == JsonValueKind.Number)
                {
                    maxLength = maxLengthNode.GetInt32();
                }

                if (bindingNode.TryGetProperty("addSpecialTokens", out var specialTokensNode) &&
                    (specialTokensNode.ValueKind == JsonValueKind.True || specialTokensNode.ValueKind == JsonValueKind.False))
                {
                    addSpecialTokens = specialTokensNode.GetBoolean();
                }

                if (bindingNode.TryGetProperty("padding", out var paddingNode) &&
                    (paddingNode.ValueKind == JsonValueKind.True || paddingNode.ValueKind == JsonValueKind.False))
                {
                    padding = paddingNode.GetBoolean();
                }

                // Tokenize the text into token IDs
                var tokenIds = _tokenizer.Encode(text, maxLength, addSpecialTokens, padding);

                // Create tensor with shape [1, sequence_length] for batch size of 1
                var shape = new[] { 1, tokenIds.Length };
                var tensor = new DenseTensor<long>(tokenIds, shape);
                value = NamedOnnxValue.CreateFromTensor(tensorName, tensor);

                Debug.WriteLine($"[InputParsingService] Tokenized text file input '{tensorName}': {tokenIds.Length} tokens");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse text file for input '{tensorName}': {ex}");
            }
            return false;
        }

        private static bool TryParseJsonArray(string json, out JsonElement element)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    element = root.Clone();
                    return true;
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse JSON tensor payload: {ex}");
            }

            element = default;
            return false;
        }

        private static bool TryBuildTensor(string name, JsonElement element, out NamedOnnxValue value)
        {
            value = null!;
            if (!element.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var dtype = element.TryGetProperty("dtype", out var dtypeNode) && dtypeNode.ValueKind == JsonValueKind.String
                ? dtypeNode.GetString()?.Trim().ToLowerInvariant()
                : "float32";

            int[] dimensions;
            if (element.TryGetProperty("shape", out var shapeNode) && shapeNode.ValueKind == JsonValueKind.Array)
            {
                dimensions = BuildDimensions(shapeNode, dataNode.GetArrayLength());
            }
            else
            {
                dimensions = new[] { dataNode.GetArrayLength() };
            }

            try
            {
                switch (dtype)
                {
                    case "float":
                    case "float32":
                        {
                            var data = ParseFloatArray(dataNode);
                            dimensions = EnsureCompatibleDimensions(dimensions, data.Length);
                            value = NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dimensions));
                            return true;
                        }
                    case "int64":
                        {
                            var data = ParseLongArray(dataNode);
                            dimensions = EnsureCompatibleDimensions(dimensions, data.Length);
                            value = NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dimensions));
                            return true;
                        }
                    case "int32":
                        {
                            var data = ParseIntArray(dataNode);
                            dimensions = EnsureCompatibleDimensions(dimensions, data.Length);
                            value = NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data, dimensions));
                            return true;
                        }
                    default:
                        Debug.WriteLine($"[InputParsingService] Unsupported tensor dtype '{dtype}' for input '{name}'.");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to build tensor for input '{name}': {ex}");
                return false;
            }
        }

        private static bool TryParseNpyTensor(string tensorName, byte[] payload, out NamedOnnxValue value)
        {
            return TryParseNpyTensor(tensorName, new ReadOnlySpan<byte>(payload), out value);
        }

        private static bool TryParseNpyTensor(string tensorName, ReadOnlySpan<byte> payload, out NamedOnnxValue value)
        {
            value = null!;
            if (payload.Length < 10)
            {
                return false;
            }

            ReadOnlySpan<byte> magic = stackalloc byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };
            if (!payload[..magic.Length].SequenceEqual(magic))
            {
                return false;
            }

            var major = payload[6];
            int offset = 8;
            int headerLength;

            if (major <= 1)
            {
                if (payload.Length < offset + 2)
                {
                    return false;
                }

                headerLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
                offset += 2;
            }
            else
            {
                if (payload.Length < offset + 4)
                {
                    return false;
                }

                headerLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
                offset += 4;
            }

            if (headerLength <= 0 || payload.Length < offset + headerLength)
            {
                return false;
            }

            var headerText = Encoding.ASCII.GetString(payload.Slice(offset, headerLength));
            offset += headerLength;

            var headerMatch = NpyHeaderRegex.Match(headerText);
            if (!headerMatch.Success)
            {
                Debug.WriteLine($"[InputParsingService] Unable to parse NPY header for input '{tensorName}'.");
                return false;
            }

            var normalizedDescr = headerMatch.Groups["descr"].Value.Trim().ToLowerInvariant();
            var fortranOrder = headerMatch.Groups["fortran"].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
            if (fortranOrder)
            {
                Debug.WriteLine($"[InputParsingService] Fortran-ordered NPY tensors are not supported for input '{tensorName}'.");
                return false;
            }

            var shape = ParseNpyShape(headerMatch.Groups["shape"].Value);
            var dataSpan = payload[offset..];
            if (dataSpan.Length == 0)
            {
                Debug.WriteLine($"[InputParsingService] Empty NPY payload for input '{tensorName}'.");
                return false;
            }

            var elementSize = normalizedDescr switch
            {
                "<f4" or "|f4" or "float32" => 4,
                "<f8" or "|f8" or "float64" => 8,
                "<i1" or "|i1" => 1,
                "<i2" or "|i2" => 2,
                "<i4" or "|i4" => 4,
                "<i8" or "|i8" => 8,
                "<u1" or "|u1" => 1,
                "<u2" or "|u2" => 2,
                "<u4" or "|u4" => 4,
                "<u8" or "|u8" => 8,
                "|b1" => 1,
                _ => -1
            };

            if (elementSize <= 0)
            {
                Debug.WriteLine($"[InputParsingService] Unsupported NPY dtype '{normalizedDescr}' for input '{tensorName}'.");
                return false;
            }

            long elementCountLong;
            if (shape.Length == 0)
            {
                elementCountLong = dataSpan.Length / elementSize;
            }
            else
            {
                elementCountLong = shape.Aggregate(1L, (accumulator, dimension) => accumulator * dimension);
            }

            if (elementCountLong <= 0 || elementCountLong > int.MaxValue)
            {
                Debug.WriteLine($"[InputParsingService] Invalid element count in NPY payload for input '{tensorName}'.");
                return false;
            }

            var expectedByteCount = elementCountLong * elementSize;
            if (expectedByteCount > dataSpan.Length)
            {
                Debug.WriteLine($"[InputParsingService] NPY payload shorter than expected for input '{tensorName}'.");
                return false;
            }

            var slice = dataSpan[..(int)expectedByteCount];
            var elementCount = (int)elementCountLong;
            var resolvedShape = shape.Length == 0 ? new[] { elementCount } : shape;

            try
            {
                switch (normalizedDescr)
                {
                    case "<f4" or "|f4" or "float32":
                        {
                            var span = MemoryMarshal.Cast<byte, float>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<float>(data, resolvedShape));
                            return true;
                        }

                    case "<f8" or "|f8" or "float64":
                        {
                            var span = MemoryMarshal.Cast<byte, double>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<double>(data, resolvedShape));
                            return true;
                        }

                    case "<i1" or "|i1":
                        {
                            var data = new sbyte[elementCount];
                            for (var index = 0; index < elementCount; index++)
                            {
                                data[index] = unchecked((sbyte)slice[index]);
                            }

                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<sbyte>(data, resolvedShape));
                            return true;
                        }

                    case "<i2" or "|i2":
                        {
                            var span = MemoryMarshal.Cast<byte, short>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<short>(data, resolvedShape));
                            return true;
                        }

                    case "<i4" or "|i4":
                        {
                            var span = MemoryMarshal.Cast<byte, int>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<int>(data, resolvedShape));
                            return true;
                        }

                    case "<i8" or "|i8":
                        {
                            var span = MemoryMarshal.Cast<byte, long>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<long>(data, resolvedShape));
                            return true;
                        }

                    case "<u1" or "|u1":
                        {
                            var data = slice[..elementCount].ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<byte>(data, resolvedShape));
                            return true;
                        }

                    case "<u2" or "|u2":
                        {
                            var span = MemoryMarshal.Cast<byte, ushort>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<ushort>(data, resolvedShape));
                            return true;
                        }

                    case "<u4" or "|u4":
                        {
                            var span = MemoryMarshal.Cast<byte, uint>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<uint>(data, resolvedShape));
                            return true;
                        }

                    case "<u8" or "|u8":
                        {
                            var span = MemoryMarshal.Cast<byte, ulong>(slice);
                            if (span.Length != elementCount)
                            {
                                return false;
                            }

                            var data = span.ToArray();
                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<ulong>(data, resolvedShape));
                            return true;
                        }

                    case "|b1":
                        {
                            var data = new bool[elementCount];
                            for (var index = 0; index < elementCount; index++)
                            {
                                data[index] = slice[index] != 0;
                            }

                            value = NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<bool>(data, resolvedShape));
                            return true;
                        }

                    default:
                        Debug.WriteLine($"[InputParsingService] Unsupported NPY dtype '{normalizedDescr}' for input '{tensorName}'.");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to decode NPY payload for input '{tensorName}': {ex}");
                return false;
            }
        }

        private static bool TryParseNpzTensor(string tensorName, byte[] payload, out NamedOnnxValue value)
        {
            value = null!;
            try
            {
                using var stream = new MemoryStream(payload, writable: false);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    if (!entry.FullName.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var entryStream = entry.Open();
                    using var buffer = new MemoryStream();
                    entryStream.CopyTo(buffer);

                    if (TryParseNpyTensor(tensorName, buffer.ToArray(), out value))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse NPZ payload for input '{tensorName}': {ex}");
            }

            return false;
        }

        private static bool TryParseImageOrVideoTensor(string tensorName, byte[] payload, string extension, out NamedOnnxValue value)
        {
            value = null!;
            try
            {
                using var source = Image.Load<Rgba32>(payload);

                if (string.Equals(source.Metadata.DecodedImageFormat!.Name, "GIF", StringComparison.OrdinalIgnoreCase) && source.Frames.Count > 1)
                {
                    return TryBuildVideoTensor(tensorName, source.Clone(), out value);
                }

                var supportsTransparency = FormatSupportsTransparency(extension, source.Metadata.DecodedImageFormat);
                return TryBuildImageTensor(tensorName, source.Clone(), supportsTransparency, out value);
            }
            catch (UnknownImageFormatException)
            {
                Debug.WriteLine($"[InputParsingService] Payload for input '{tensorName}' is not a supported image format.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse image/video payload for input '{tensorName}': {ex}");
                return false;
            }
        }

        private static bool TryBuildImageTensor(string tensorName, Image<Rgba32> image, bool useAlphaChannel, out NamedOnnxValue value)
        {
            value = null!;
            using (image)
            {
                var width = image.Width;
                var height = image.Height;
                if (width <= 0 || height <= 0)
                {
                    return false;
                }

                var spatialSize = width * height;
                var channelCount = useAlphaChannel ? 4 : 3;
                var data = new float[channelCount * spatialSize];
                var redOffset = 0;
                var greenOffset = spatialSize;
                var blueOffset = spatialSize * 2;
                var alphaOffset = useAlphaChannel ? spatialSize * 3 : 0;

                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++)
                        {
                            var pixel = row[x];
                            var pixelIndex = (y * width) + x;
                            data[redOffset + pixelIndex] = pixel.R / 255f;
                            data[greenOffset + pixelIndex] = pixel.G / 255f;
                            data[blueOffset + pixelIndex] = pixel.B / 255f;
                            if (useAlphaChannel)
                            {
                                data[alphaOffset + pixelIndex] = pixel.A / 255f;
                            }
                        }
                    }
                });

                var tensor = new DenseTensor<float>(data, new[] { 1, channelCount, height, width });
                value = NamedOnnxValue.CreateFromTensor(tensorName, tensor);
                return true;
            }
        }

        private static bool TryBuildVideoTensor(string tensorName, Image<Rgba32> image, out NamedOnnxValue value)
        {
            value = null!;
            using (image)
            {
                var frameCount = image.Frames.Count;
                var width = image.Width;
                var height = image.Height;

                if (frameCount <= 0 || width <= 0 || height <= 0)
                {
                    return false;
                }

                const int channelCount = 3;
                var channelStride = height * width;
                var perFrameElementCount = channelCount * channelStride;
                var data = new float[frameCount * perFrameElementCount];

                for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    using var frame = image.Frames.CloneFrame(frameIndex);
                    using var rgbFrame = frame.CloneAs<Rgb24>();

                    var frameFloats = new float[perFrameElementCount];

                    rgbFrame.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < accessor.Height; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (var x = 0; x < row.Length; x++)
                            {
                                var pixel = row[x];
                                var pixelIndex = (y * width) + x;
                                frameFloats[pixelIndex] = pixel.R / 255f;
                                frameFloats[channelStride + pixelIndex] = pixel.G / 255f;
                                frameFloats[(2 * channelStride) + pixelIndex] = pixel.B / 255f;
                            }
                        }
                    });

                    Array.Copy(frameFloats, 0, data, frameIndex * perFrameElementCount, perFrameElementCount);
                }

                var tensor = new DenseTensor<float>(data, new[] { frameCount, channelCount, height, width });
                value = NamedOnnxValue.CreateFromTensor(tensorName, tensor);
                return true;
            }
        }

        private static bool TryParseVideoTensor(string tensorName, byte[] payload, string extension, out NamedOnnxValue value)
        {
            value = null!;
            var tempExtension = !string.IsNullOrWhiteSpace(extension) ? extension : ".mp4";
            var tempFilePath = Path.Combine(Path.GetTempPath(), $"infinitegpu_{Guid.NewGuid():N}{tempExtension}");

            try
            {
                File.WriteAllBytes(tempFilePath, payload);
                using var capture = new VideoCapture(tempFilePath);
                if (!capture.IsOpened())
                {
                    Debug.WriteLine($"[InputParsingService] Unable to open video payload for input '{tensorName}'.");
                    return false;
                }

                var frameFloatBuffers = new List<float[]>();
                var width = 0;
                var height = 0;
                var perFrameElementCount = 0;
                var channelStride = 0;
                const int channelCount = 3;

                while (true)
                {
                    using var frame = new Mat();
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        break;
                    }

                    using var rgbFrame = new Mat();
                    ColorConversionCodes conversionCode;
                    switch (frame.Channels())
                    {
                        case 1:
                            conversionCode = ColorConversionCodes.GRAY2RGB;
                            break;
                        case 3:
                            conversionCode = ColorConversionCodes.BGR2RGB;
                            break;
                        case 4:
                            conversionCode = ColorConversionCodes.BGRA2RGB;
                            break;
                        default:
                            Debug.WriteLine($"[InputParsingService] Unsupported channel count '{frame.Channels()}' in video frame for input '{tensorName}'.");
                            return false;
                    }

                    Cv2.CvtColor(frame, rgbFrame, conversionCode);

                    if (width == 0 && height == 0)
                    {
                        width = rgbFrame.Width;
                        height = rgbFrame.Height;

                        if (width <= 0 || height <= 0)
                        {
                            Debug.WriteLine($"[InputParsingService] Invalid frame dimensions in video payload for input '{tensorName}'.");
                            return false;
                        }

                        channelStride = width * height;
                        perFrameElementCount = channelCount * channelStride;
                    }
                    else if (rgbFrame.Width != width || rgbFrame.Height != height)
                    {
                        Debug.WriteLine($"[InputParsingService] Video frames have inconsistent dimensions for input '{tensorName}'.");
                        return false;
                    }

                    var frameFloats = new float[perFrameElementCount];
                    var indexer = rgbFrame.GetGenericIndexer<Vec3b>();

                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var pixel = indexer[y, x];
                            var pixelIndex = (y * width) + x;
                            frameFloats[pixelIndex] = pixel.Item0 / 255f;
                            frameFloats[channelStride + pixelIndex] = pixel.Item1 / 255f;
                            frameFloats[(2 * channelStride) + pixelIndex] = pixel.Item2 / 255f;
                        }
                    }

                    frameFloatBuffers.Add(frameFloats);
                }

                if (frameFloatBuffers.Count == 0)
                {
                    Debug.WriteLine($"[InputParsingService] No frames decoded from video payload for input '{tensorName}'.");
                    return false;
                }

                var frameCount = frameFloatBuffers.Count;
                var data = new float[frameCount * perFrameElementCount];

                for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    Array.Copy(frameFloatBuffers[frameIndex], 0, data, frameIndex * perFrameElementCount, perFrameElementCount);
                }

                var tensor = new DenseTensor<float>(data, new[] { frameCount, channelCount, height, width });
                value = NamedOnnxValue.CreateFromTensor(tensorName, tensor);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InputParsingService] Failed to parse video payload for input '{tensorName}': {ex}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                    // Ignored.
                }
            }
        }

        private static bool FormatSupportsTransparency(string extension, IImageFormat? format)
        {
            if (!string.IsNullOrWhiteSpace(extension) && TransparencyImageExtensions.Contains(extension))
            {
                return true;
            }

            var formatName = format?.Name;
            if (!string.IsNullOrWhiteSpace(formatName) && TransparencyFormatNames.Contains(formatName))
            {
                return true;
            }

            return false;
        }

        private static int[] ParseNpyShape(string shapeText)
        {
            if (string.IsNullOrWhiteSpace(shapeText))
            {
                return Array.Empty<int>();
            }

            var segments = shapeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dimensions = new List<int>(segments.Length);

            foreach (var segment in segments)
            {
                if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimension) && dimension > 0)
                {
                    dimensions.Add(dimension);
                }
            }

            return dimensions.ToArray();
        }

        private static float[] ParseFloatArray(JsonElement element)
        {
            var values = new List<float>();
            foreach (var item in element.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.Number:
                        values.Add((float)item.GetDouble());
                        break;
                    case JsonValueKind.String:
                        if (float.TryParse(item.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                        {
                            values.Add(parsed);
                        }
                        break;
                }
            }

            return values.ToArray();
        }

        private static long[] ParseLongArray(JsonElement element)
        {
            var values = new List<long>();
            foreach (var item in element.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.Number:
                        values.Add(item.GetInt64());
                        break;
                    case JsonValueKind.String:
                        if (long.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        {
                            values.Add(parsed);
                        }
                        break;
                }
            }

            return values.ToArray();
        }

        private static int[] ParseIntArray(JsonElement element)
        {
            var values = new List<int>();
            foreach (var item in element.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.Number:
                        values.Add(item.GetInt32());
                        break;
                    case JsonValueKind.String:
                        if (int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        {
                            values.Add(parsed);
                        }
                        break;
                }
            }

            return values.ToArray();
        }

        private static int[] BuildDimensions(JsonElement shapeNode, int elementCount)
        {
            var dimensions = shapeNode.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Number)
                .Select(item => item.GetInt32())
                .Where(dimension => dimension > 0)
                .ToArray();

            if (dimensions.Length == 0)
            {
                return new[] { elementCount };
            }

            var product = dimensions.Aggregate(1L, (current, dimension) => current * dimension);
            return product == elementCount ? dimensions : new[] { elementCount };
        }

        private static int[] EnsureCompatibleDimensions(int[] dimensions, int elementCount)
        {
            if (dimensions.Length == 0)
            {
                return new[] { elementCount };
            }

            var product = dimensions.Aggregate(1L, (current, dimension) => current * dimension);
            if (product != elementCount)
            {
                return new[] { elementCount };
            }

            return dimensions;
        }
    }
}