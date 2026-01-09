using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    public sealed class OutputParsingService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private readonly HttpClient _httpClient;
        private readonly TokenizerService _tokenizer;

        public OutputParsingService(HttpClient httpClient, TokenizerService tokenizerService)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenizer = tokenizerService ?? throw new ArgumentNullException(nameof(tokenizerService));
        }

        public async Task<List<object>> ProcessOutputsAsync(
            Guid taskId,
            Guid subtaskId,
            string parametersJson,
            IReadOnlyList<OnnxInferenceOutput> rawOutputs,
            string authToken,
            CancellationToken cancellationToken)
        {
            var outputSettings = ExtractOutputSettings(parametersJson);
            var processedOutputs = new List<object>();

            foreach (var output in rawOutputs)
            {
                var tensorName = output.Name;
                var outputData = output.Data;

                if (outputData == null)
                {
                    continue;
                }

                var setting = outputSettings?.FirstOrDefault(s =>
                    string.Equals(s.TensorName, tensorName, StringComparison.OrdinalIgnoreCase));


                if (setting == null)
                {
                    continue;
                }

                var fileFormat = setting.FileFormat ?? "npy";
                var binaryData = await SerializeOutputToBinaryAsync(outputData, output.Dimensions, fileFormat, cancellationToken);
                var uploadUrl = await GenerateOutputUploadUrlAsync(taskId, subtaskId, tensorName, authToken, fileFormat, cancellationToken);
                await UploadBinaryDataAsync(uploadUrl.UploadUri, binaryData, cancellationToken);
                processedOutputs.Add(new { tensorName, fileUrl = uploadUrl.BlobUri, payloadType = "Binary", format = fileFormat });

            }

            return processedOutputs;
        }

        private async Task<OutputUploadUrlResponse> GenerateOutputUploadUrlAsync(Guid taskId, Guid subtaskId, string tensorName, string authToken, string fileExtension, CancellationToken cancellationToken)
        {
            var request = new
            {
                taskId = taskId.ToString(),
                subtaskId = subtaskId.ToString(),
                inputName = tensorName,
                fileExtension,
                fileType = 2
            };

            var requestJson = JsonSerializer.Serialize(request, SerializerOptions);
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_httpClient.BaseAddress + "api/tasks/upload-url"));
            httpRequest.Content = content;
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var uploadResponse = JsonSerializer.Deserialize<OutputParsingService.OutputUploadUrlResponse>(responseJson, SerializerOptions);

            if (uploadResponse == null)
            {
                throw new InvalidOperationException("Failed to deserialize upload URL response");
            }

            return uploadResponse;
        }

        public List<OutputSettingPayload>? ExtractOutputSettings(string parametersJson)
        {
            try
            {
                using var document = JsonDocument.Parse(parametersJson);
                if (document.RootElement.TryGetProperty("inference", out var inference) &&
                    inference.TryGetProperty("outputs", out var outputs))
                {
                    return JsonSerializer.Deserialize<List<OutputSettingPayload>>(outputs.GetRawText(), SerializerOptions);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputParsingService] Failed to extract output settings: {ex}");
            }

            return null;
        }

        private string ConvertOutputToText(object output)
        {
            if (output == null)
            {
                return string.Empty;
            }

            try
            {
                long[] tokenIds = output switch
                {
                    long[] longArray => longArray,
                    int[] intArray => intArray.Select(i => (long)i).ToArray(),
                    float[] floatArray => floatArray.Select(f => (long)Math.Round(f)).ToArray(),
                    double[] doubleArray => doubleArray.Select(d => (long)Math.Round(d)).ToArray(),
                    _ => null
                };

                if (tokenIds != null && tokenIds.Length > 0)
                {
                    // Detokenize the token IDs back to text
                    var decodedText = _tokenizer.Decode(tokenIds, skipSpecialTokens: true);
                    Debug.WriteLine($"[OutputParsingService] Detokenized {tokenIds.Length} tokens to text: {decodedText.Substring(0, Math.Min(50, decodedText.Length))}...");
                    return decodedText;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputParsingService] Failed to detokenize output: {ex}");
            }

            // Fallback to string representation
            return output.ToString() ?? string.Empty;
        }

        private async Task<byte[]> SerializeOutputToBinaryAsync(object output, int[]? dimensions, string fileFormat, CancellationToken cancellationToken)
        {
            var normalizedFormat = fileFormat.ToLowerInvariant().TrimStart('.');

            return normalizedFormat switch
            {
                "json" => SerializeToJson(output, dimensions),
                "txt" => SerializeToText(output),
                "npy" => SerializeToNpy(output, dimensions),
                "npz" => SerializeToNpz(output, dimensions),
                "png" or "jpg" or "jpeg" or "webp" or "bmp" or "tiff" or "tif" => await SerializeToImageAsync(output, dimensions, normalizedFormat, cancellationToken),
                _ => SerializeToNpy(output, dimensions) // Default to npy
            };
        }

        private byte[] SerializeToJson(object output, int[]? dimensions)
        {
            try
            {
                // Create a JSON object with data, dtype, and shape
                var jsonObject = new
                {
                    data = output,
                    shape = dimensions ?? Array.Empty<int>(),
                    dtype = output switch
                    {
                        float[] => "float32",
                        double[] => "float64",
                        int[] => "int32",
                        long[] => "int64",
                        bool[] => "bool",
                        _ => "object"
                    }
                };

                var json = JsonSerializer.Serialize(jsonObject, SerializerOptions);
                return Encoding.UTF8.GetBytes(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputParsingService] Failed to serialize to JSON: {ex}");
                throw new InvalidOperationException("Unable to serialize output to JSON", ex);
            }
        }

        private byte[] SerializeToText(object output)
        {
            try
            {
                var text = ConvertOutputToText(output);
                return Encoding.UTF8.GetBytes(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputParsingService] Failed to serialize to text: {ex}");
                throw new InvalidOperationException("Unable to serialize output to text", ex);
            }
        }

        private byte[] SerializeToNpy(object output, int[]? dimensions)
        {
            // Extract tensor data
            if (!TryExtractTensorData(output, dimensions, out var data, out var shape, out var dtype))
            {
                throw new InvalidOperationException("Unable to extract tensor data from output");
            }

            using var ms = new MemoryStream();

            // Write NPY magic number
            ms.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' });

            // Version 1.0
            ms.WriteByte(1);
            ms.WriteByte(0);

            // Build header
            var shapeStr = string.Join(", ", shape);
            var header = $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': ({shapeStr},), }}";

            // Pad to make total header length (including length field) a multiple of 64
            var headerBytes = Encoding.ASCII.GetBytes(header);
            var totalHeaderLen = headerBytes.Length + 10; // 6 (magic) + 2 (version) + 2 (len field) = 10
            var padding = (64 - (totalHeaderLen % 64)) % 64;
            var paddedHeader = header + new string(' ', padding) + '\n';
            var paddedHeaderBytes = Encoding.ASCII.GetBytes(paddedHeader);

            // Write header length (little-endian uint16)
            var lenBytes = BitConverter.GetBytes((ushort)paddedHeaderBytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(lenBytes);
            }
            ms.Write(lenBytes);

            // Write header
            ms.Write(paddedHeaderBytes);

            // Write data
            ms.Write(data);

            return ms.ToArray();
        }

        private byte[] SerializeToNpz(object output, int[]? dimensions)
        {
            var npyData = SerializeToNpy(output, dimensions);

            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("arr_0.npy");
                using var entryStream = entry.Open();
                entryStream.Write(npyData);
            }

            return ms.ToArray();
        }

        private async Task<byte[]> SerializeToImageAsync(object output, int[]? dimensions, string format, CancellationToken cancellationToken)
        {
            if (!TryExtractTensorData(output, dimensions, out var data, out var shape, out _))
            {
                throw new InvalidOperationException("Unable to extract tensor data for image conversion");
            }

            // Expect shape [C, H, W] or [1, C, H, W] or [H, W, C]
            int height, width, channels;

            if (shape.Length == 3)
            {
                // Try [C, H, W] first
                if (shape[0] <= 4)
                {
                    channels = shape[0];
                    height = shape[1];
                    width = shape[2];
                }
                else
                {
                    // Try [H, W, C]
                    height = shape[0];
                    width = shape[1];
                    channels = shape[2];
                }
            }
            else if (shape.Length == 4 && shape[0] == 1)
            {
                // [1, C, H, W]
                channels = shape[1];
                height = shape[2];
                width = shape[3];
            }
            else
            {
                throw new InvalidOperationException($"Unsupported tensor shape for image conversion: [{string.Join(", ", shape)}]");
            }

            if (channels < 1 || channels > 4)
            {
                throw new InvalidOperationException($"Unsupported channel count: {channels}");
            }

            // Convert byte array to float array
            var floatData = ConvertToFloatArray(data);

            using var image = new Image<Rgba32>(width, height);

            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < width; x++)
                    {
                        var pixelIndex = y * width + x;
                        byte r = 0, g = 0, b = 0, a = 255;

                        if (channels == 1)
                        {
                            var val = ClampAndScale(floatData[pixelIndex]);
                            r = g = b = val;
                        }
                        else if (channels >= 3)
                        {
                            var spatialSize = height * width;
                            r = ClampAndScale(floatData[pixelIndex]);
                            g = ClampAndScale(floatData[spatialSize + pixelIndex]);
                            b = ClampAndScale(floatData[2 * spatialSize + pixelIndex]);

                            if (channels == 4)
                            {
                                a = ClampAndScale(floatData[3 * spatialSize + pixelIndex]);
                            }
                        }

                        row[x] = new Rgba32(r, g, b, a);
                    }
                }
            });

            using var ms = new MemoryStream();

            switch (format)
            {
                case "png":
                    await image.SaveAsPngAsync(ms, cancellationToken);
                    break;
                case "jpg" or "jpeg":
                    await image.SaveAsJpegAsync(ms, cancellationToken);
                    break;
                case "webp":
                    await image.SaveAsWebpAsync(ms, cancellationToken);
                    break;
                case "bmp":
                    await image.SaveAsBmpAsync(ms, cancellationToken);
                    break;
                case "tiff" or "tif":
                    await image.SaveAsTiffAsync(ms, cancellationToken);
                    break;
                default:
                    await image.SaveAsPngAsync(ms, cancellationToken);
                    break;
            }

            return ms.ToArray();
        }

        private bool TryExtractTensorData(object output, int[]? dimensions, out byte[] data, out int[] shape, out string dtype)
        {
            data = Array.Empty<byte>();
            shape = dimensions ?? Array.Empty<int>();
            dtype = "<f4";

            try
            {
                // Handle strongly-typed arrays directly
                switch (output)
                {
                    case float[] floatArray:
                        data = new byte[floatArray.Length * sizeof(float)];
                        Buffer.BlockCopy(floatArray, 0, data, 0, data.Length);
                        dtype = "<f4";
                        if (dimensions == null || dimensions.Length == 0)
                        {
                            shape = new[] { floatArray.Length };
                        }
                        return true;

                    case double[] doubleArray:
                        data = new byte[doubleArray.Length * sizeof(double)];
                        Buffer.BlockCopy(doubleArray, 0, data, 0, data.Length);
                        dtype = "<f8";
                        if (dimensions == null || dimensions.Length == 0)
                        {
                            shape = new[] { doubleArray.Length };
                        }
                        return true;

                    case int[] intArray:
                        data = new byte[intArray.Length * sizeof(int)];
                        Buffer.BlockCopy(intArray, 0, data, 0, data.Length);
                        dtype = "<i4";
                        if (dimensions == null || dimensions.Length == 0)
                        {
                            shape = new[] { intArray.Length };
                        }
                        return true;

                    case long[] longArray:
                        data = new byte[longArray.Length * sizeof(long)];
                        Buffer.BlockCopy(longArray, 0, data, 0, data.Length);
                        dtype = "<i8";
                        if (dimensions == null || dimensions.Length == 0)
                        {
                            shape = new[] { longArray.Length };
                        }
                        return true;

                    case bool[] boolArray:
                        // Convert bool array to byte array (bool is 1 byte in .NET)
                        data = new byte[boolArray.Length];
                        for (int i = 0; i < boolArray.Length; i++)
                        {
                            data[i] = boolArray[i] ? (byte)1 : (byte)0;
                        }
                        dtype = "|b1";
                        if (dimensions == null || dimensions.Length == 0)
                        {
                            shape = new[] { boolArray.Length };
                        }
                        return true;

                    case string[] stringArray:
                        // For strings, we'll serialize as UTF-8 and store as object array
                        // NumPy string dtype is more complex, so we'll use a simple approach
                        var maxLength = stringArray.Max(s => Encoding.UTF8.GetByteCount(s ?? ""));
                        dtype = $"|S{maxLength}";

                        using (var ms = new MemoryStream())
                        {
                            foreach (var str in stringArray)
                            {
                                var bytes = Encoding.UTF8.GetBytes(str ?? "");
                                var paddedBytes = new byte[maxLength];
                                Array.Copy(bytes, paddedBytes, Math.Min(bytes.Length, maxLength));
                                ms.Write(paddedBytes);
                            }
                            data = ms.ToArray();
                        }

                        if (dimensions == null || dimensions.Length == 0)
                        {
                            shape = new[] { stringArray.Length };
                        }
                        return true;

                    default:
                        Debug.WriteLine($"[OutputParsingService] Unsupported output type: {output.GetType().Name}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputParsingService] Failed to extract tensor data: {ex}");
            }

            return false;
        }

        private float[] ConvertToFloatArray(byte[] data)
        {
            var floatCount = data.Length / sizeof(float);
            var floats = new float[floatCount];
            Buffer.BlockCopy(data, 0, floats, 0, data.Length);
            return floats;
        }

        private byte ClampAndScale(float value)
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            return (byte)(clamped * 255f);
        }

        private async Task UploadBinaryDataAsync(string uploadUri, byte[] data, CancellationToken cancellationToken)
        {
            using var content = new ByteArrayContent(data);
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri);
            request.Content = content;

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        public sealed class OutputSettingPayload
        {
            public string TensorName { get; init; } = string.Empty;
            public string PayloadType { get; init; } = "Json";
            public string? FileFormat { get; init; }
        }

        public sealed class OutputUploadUrlResponse
        {
            public string BlobUri { get; init; } = string.Empty;
            public string UploadUri { get; init; } = string.Empty;
            public DateTime ExpiresAtUtc { get; init; }
        }
    }
}