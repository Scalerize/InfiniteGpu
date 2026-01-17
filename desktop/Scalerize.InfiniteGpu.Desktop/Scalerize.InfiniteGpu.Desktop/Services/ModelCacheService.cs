using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services;

/// <summary>
/// Manages a disk-based cache for downloaded ONNX models.
/// Models are stored in the user's local app data folder with the URL hash as filename.
/// </summary>
public sealed class ModelCacheService
{
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, string> _urlToFileMap = new();
    private readonly object _initLock = new();
    private bool _isInitialized;

    public ModelCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDirectory = Path.Combine(appData, "Scalerize.InfiniteGpu", "ModelCache");
    }

    /// <summary>
    /// Initializes the cache by scanning the cache directory for existing cached models.
    /// </summary>
    public void Initialize()
    {
        lock (_initLock)
        {
            if (_isInitialized)
                return;

            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }

                // Scan existing cache files and rebuild index
                var metadataFiles = Directory.GetFiles(_cacheDirectory, "*.meta");
                foreach (var metaFile in metadataFiles)
                {
                    try
                    {
                        var url = File.ReadAllText(metaFile, Encoding.UTF8);
                        var modelFile = Path.ChangeExtension(metaFile, ".onnx");
                        if (File.Exists(modelFile))
                        {
                            _urlToFileMap[url] = modelFile;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ModelCacheService] Failed to read metadata file {metaFile}: {ex.Message}");
                    }
                }

                Debug.WriteLine($"[ModelCacheService] Initialized with {_urlToFileMap.Count} cached models.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelCacheService] Initialization failed: {ex.Message}");
            }

            _isInitialized = true;
        }
    }

    /// <summary>
    /// Attempts to retrieve a model from the cache.
    /// </summary>
    /// <param name="modelUrl">The model URL to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model bytes if cached, null otherwise.</returns>
    public async Task<byte[]?> TryGetCachedModelAsync(string modelUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(modelUrl))
            return null;

        if (!_urlToFileMap.TryGetValue(modelUrl, out var filePath))
            return null;

        try
        {
            if (!File.Exists(filePath))
            {
                _urlToFileMap.TryRemove(modelUrl, out _);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            Debug.WriteLine($"[ModelCacheService] Cache hit for model: {modelUrl}");
            return bytes;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModelCacheService] Failed to read cached model: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Caches a downloaded model to disk.
    /// </summary>
    /// <param name="modelUrl">The model URL (used as key).</param>
    /// <param name="modelBytes">The model bytes to cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CacheModelAsync(string modelUrl, byte[] modelBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(modelUrl) || modelBytes.Length == 0)
            return;

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

            var hash = ComputeUrlHash(modelUrl);
            var modelPath = Path.Combine(_cacheDirectory, $"{hash}.onnx");
            var metaPath = Path.Combine(_cacheDirectory, $"{hash}.meta");

            await File.WriteAllBytesAsync(modelPath, modelBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(metaPath, modelUrl, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            _urlToFileMap[modelUrl] = modelPath;
            Debug.WriteLine($"[ModelCacheService] Cached model: {modelUrl} -> {modelPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModelCacheService] Failed to cache model: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the list of all cached model URLs for reporting to the backend.
    /// </summary>
    /// <returns>List of cached model URLs.</returns>
    public IReadOnlyList<string> GetCachedModelUrls()
    {
        return _urlToFileMap.Keys.ToList();
    }

    /// <summary>
    /// Checks if a specific model URL is cached.
    /// </summary>
    /// <param name="modelUrl">The model URL to check.</param>
    /// <returns>True if the model is cached, false otherwise.</returns>
    public bool IsCached(string modelUrl)
    {
        return _urlToFileMap.ContainsKey(modelUrl);
    }

    private static string ComputeUrlHash(string url)
    {
        var bytes = Encoding.UTF8.GetBytes(url);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
