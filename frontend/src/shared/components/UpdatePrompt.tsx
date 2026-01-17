import { useEffect, useState } from 'react';
import { Download, AlertTriangle } from 'lucide-react';
import { DesktopBridge } from '../services/DesktopBridge';

// Import the required desktop version from package.json
// This value should be updated when a new desktop version is required
const REQUIRED_DESKTOP_VERSION = '1.0.14.0';
const DOWNLOAD_URL = 'https://www.infinite-gpu.scalerize.fr/ScalerizeInfiniteGpuDesktopSetup.msi';

/**
 * Parses a version string into an array of numeric components.
 * Supports formats like "1.0.0.0", "1.0.0", "1.0"
 */
const parseVersion = (version: string): number[] => {
  return version.split('.').map((part) => {
    const num = parseInt(part, 10);
    return Number.isNaN(num) ? 0 : num;
  });
};

/**
 * Compares two version arrays.
 * Returns:
 *  -1 if a < b
 *   0 if a === b
 *   1 if a > b
 */
const compareVersions = (a: number[], b: number[]): number => {
  const maxLength = Math.max(a.length, b.length);
  
  for (let i = 0; i < maxLength; i++) {
    const aPart = a[i] ?? 0;
    const bPart = b[i] ?? 0;
    
    if (aPart < bPart) return -1;
    if (aPart > bPart) return 1;
  }
  
  return 0;
};

/**
 * Checks if the current desktop version is outdated compared to the required version.
 */
const isVersionOutdated = (currentVersion: string, requiredVersion: string): boolean => {
  const current = parseVersion(currentVersion);
  const required = parseVersion(requiredVersion);
  
  return compareVersions(current, required) < 0;
};

interface UpdatePromptState {
  isVisible: boolean;
  currentVersion: string | null;
  isLoading: boolean;
  isDownloading: boolean;
  downloadProgress: number | null;
}

export const UpdatePrompt = () => {
  const [state, setState] = useState<UpdatePromptState>({
    isVisible: false,
    currentVersion: null,
    isLoading: true,
    isDownloading: false,
    downloadProgress: null,
  });

  useEffect(() => {
    const checkVersion = async () => {
      // Only check if running in desktop app
      if (!DesktopBridge.isAvailable()) {
        setState((prev) => ({ ...prev, isLoading: false }));
        return;
      }

      try {
        const version = await DesktopBridge.getAssemblyVersion();
        
        if (version && isVersionOutdated(version, REQUIRED_DESKTOP_VERSION)) {
          setState((prev) => ({
            ...prev,
            isVisible: true,
            currentVersion: version,
            isLoading: false,
          }));
        } else {
          setState((prev) => ({
            ...prev,
            isVisible: false,
            currentVersion: version,
            isLoading: false,
          }));
        }
      } catch (error) {
        console.error('Failed to check desktop version:', error);
        setState((prev) => ({ ...prev, isLoading: false }));
      }
    };

    checkVersion();
  }, []);

  const handleDownload = async () => {
    if (state.isDownloading) return;

    setState((prev) => ({ ...prev, isDownloading: true, downloadProgress: 0 }));

    try {
      const response = await fetch(DOWNLOAD_URL);
      
      if (!response.ok) {
        throw new Error(`Download failed: ${response.status}`);
      }

      const contentLength = response.headers.get('content-length');
      const total = contentLength ? parseInt(contentLength, 10) : 0;
      
      const reader = response.body?.getReader();
      if (!reader) {
        throw new Error('Failed to get response reader');
      }

      const chunks: ArrayBuffer[] = [];
      let received = 0;

      while (true) {
        const { done, value } = await reader.read();
        
        if (done) break;
        
        chunks.push(value.buffer);
        received += value.length;
        
        if (total > 0) {
          const progress = Math.round((received / total) * 100);
          setState((prev) => ({ ...prev, downloadProgress: progress }));
        }
      }

      // Combine chunks into a blob
      const blob = new Blob(chunks, { type: 'application/octet-stream' });
      
      // Create a download link and trigger it
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'ScalerizeInfiniteGpuDesktopSetup.msi';
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);

      setState((prev) => ({ ...prev, isDownloading: false, downloadProgress: 100 }));
    } catch (error) {
      console.error('Download failed:', error);
      // Fallback to direct link if fetch fails (e.g., CORS issues)
      const link = document.createElement('a');
      link.href = DOWNLOAD_URL;
      link.download = 'ScalerizeInfiniteGpuDesktopSetup.msi';
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      setState((prev) => ({ ...prev, isDownloading: false, downloadProgress: null }));
    }
  };

  if (state.isLoading || !state.isVisible) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm">
      <div className="mx-4 w-full max-w-md rounded-lg bg-white p-6 shadow-xl dark:bg-slate-800">
        <div className="flex items-center gap-3">
          <div className="flex h-12 w-12 items-center justify-center rounded-full bg-red-100 dark:bg-red-900/30">
            <AlertTriangle className="h-6 w-6 text-red-600 dark:text-red-400" />
          </div>
          <div>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
              Update Required
            </h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              A mandatory update is available
            </p>
          </div>
        </div>

        <div className="mt-4 space-y-3">
          <div className="rounded-md bg-slate-50 p-3 dark:bg-slate-700/50">
            <div className="flex items-center justify-between text-sm">
              <span className="text-slate-600 dark:text-slate-300">Your version:</span>
              <span className="font-mono font-medium text-red-600 dark:text-red-400">
                {state.currentVersion}
              </span>
            </div>
            <div className="mt-1 flex items-center justify-between text-sm">
              <span className="text-slate-600 dark:text-slate-300">Required version:</span>
              <span className="font-mono font-medium text-emerald-600 dark:text-emerald-400">
                {REQUIRED_DESKTOP_VERSION}
              </span>
            </div>
          </div>

          <p className="text-sm text-slate-600 dark:text-slate-300">
            Your desktop application is outdated and must be updated to continue.
            Please download and install the latest version.
          </p>
        </div>

        <div className="mt-6 space-y-3">
          {state.isDownloading && state.downloadProgress !== null && (
            <div className="space-y-2">
              <div className="flex items-center justify-between text-xs text-slate-600 dark:text-slate-400">
                <span>Downloading...</span>
                <span>{state.downloadProgress}%</span>
              </div>
              <div className="h-2 w-full overflow-hidden rounded-full bg-slate-200 dark:bg-slate-700">
                <div
                  className="h-full rounded-full bg-indigo-600 transition-all duration-300 dark:bg-indigo-500"
                  style={{ width: `${state.downloadProgress}%` }}
                />
              </div>
            </div>
          )}
          <button
            type="button"
            onClick={handleDownload}
            disabled={state.isDownloading}
            className="flex w-full items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-indigo-400 dark:bg-indigo-700 dark:hover:bg-indigo-600 dark:disabled:bg-indigo-800"
          >
            {state.isDownloading ? (
              <>
                <div className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                Downloading...
              </>
            ) : (
              <>
                <Download className="h-4 w-4" />
                Download Update
              </>
            )}
          </button>
        </div>
      </div>
    </div>
  );
};
