import { useEffect, useState } from 'react';
import { Download, X, AlertTriangle } from 'lucide-react';
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
  isDismissed: boolean;
}

export const UpdatePrompt = () => {
  const [state, setState] = useState<UpdatePromptState>({
    isVisible: false,
    currentVersion: null,
    isLoading: true,
    isDismissed: false,
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
          setState({
            isVisible: true,
            currentVersion: version,
            isLoading: false,
            isDismissed: false,
          });
        } else {
          setState({
            isVisible: false,
            currentVersion: version,
            isLoading: false,
            isDismissed: false,
          });
        }
      } catch (error) {
        console.error('Failed to check desktop version:', error);
        setState((prev) => ({ ...prev, isLoading: false }));
      }
    };

    checkVersion();
  }, []);

  const handleDismiss = () => {
    setState((prev) => ({ ...prev, isDismissed: true, isVisible: false }));
  };

  const handleDownload = () => {
    window.open(DOWNLOAD_URL, '_blank');
  };

  if (state.isLoading || !state.isVisible || state.isDismissed) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm">
      <div className="mx-4 w-full max-w-md rounded-lg bg-white p-6 shadow-xl dark:bg-slate-800">
        <div className="flex items-start justify-between">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-amber-100 dark:bg-amber-900/30">
              <AlertTriangle className="h-5 w-5 text-amber-600 dark:text-amber-400" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
                Update Available
              </h3>
              <p className="text-sm text-slate-500 dark:text-slate-400">
                A new version of InfiniteGPU is available
              </p>
            </div>
          </div>
          <button
            type="button"
            onClick={handleDismiss}
            className="rounded-md p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-700 dark:hover:text-slate-300"
            aria-label="Dismiss"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="mt-4 space-y-3">
          <div className="rounded-md bg-slate-50 p-3 dark:bg-slate-700/50">
            <div className="flex items-center justify-between text-sm">
              <span className="text-slate-600 dark:text-slate-300">Your version:</span>
              <span className="font-mono font-medium text-slate-900 dark:text-white">
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
            Please update your desktop application to continue using all features. 
            Some functionality may be limited until you update.
          </p>
        </div>

        <div className="mt-6 flex gap-3">
          <button
            type="button"
            onClick={handleDismiss}
            className="flex-1 rounded-md border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-700"
          >
            Later
          </button>
          <button
            type="button"
            onClick={handleDownload}
            className="flex flex-1 items-center justify-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-blue-700"
          >
            <Download className="h-4 w-4" />
            Download Update
          </button>
        </div>
      </div>
    </div>
  );
};
