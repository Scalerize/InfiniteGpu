import { type JSX, useCallback, useMemo, useState } from "react";
import {
  CheckCircle2,
  Clock,
  FileDown,
  FolderTree,
  Loader2,
  Plus,
  Receipt,
  Square,
  UserCheck,
  XCircle,
} from "lucide-react";
import { NewTaskRequestDialog } from "./NewTaskRequestDialog";
import { SubtasksDialog } from "./SubtasksDialog";
import { DataTable } from "../../../shared/components/DataTable";
import { useMyTasksQuery } from "../queries/useMyTasksQuery";
import { cancelTask } from "../api";
import { appQueryClient } from "../../../shared/providers/queryClient";
import { invalidateMyTasksQueryKey } from "../queries/useMyTasksQuery";
import type { RequestorTaskDto } from "../types";
import { RequestorTaskStatus, RequestorTaskType } from "../types";
import { getRelativeTime } from "../../../shared/utils/dateTime";

const ACTIVE_REQUEST_STATUSES: RequestorTaskStatus[] = [
  RequestorTaskStatus.Pending,
  RequestorTaskStatus.Assigned,
  RequestorTaskStatus.InProgress,
];

const STATUS_BADGE_BY_STATUS: Record<
  RequestorTaskStatus,
  { bg: string; text: string; ring: string; icon: JSX.Element; label: string }
> = {
  [RequestorTaskStatus.Pending]: {
    bg: "bg-amber-50 dark:bg-amber-950/50",
    text: "text-amber-600 dark:text-amber-400",
    ring: "ring-amber-100 dark:ring-amber-900",
    icon: <Clock className="h-3.5 w-3.5" />,
    label: "Pending",
  },
  [RequestorTaskStatus.Assigned]: {
    bg: "bg-sky-50 dark:bg-sky-950/50",
    text: "text-sky-600 dark:text-sky-400",
    ring: "ring-sky-100 dark:ring-sky-900",
    icon: <UserCheck className="h-3.5 w-3.5" />,
    label: "Assigned",
  },
  [RequestorTaskStatus.InProgress]: {
    bg: "bg-indigo-50 dark:bg-indigo-950/50",
    text: "text-indigo-600 dark:text-indigo-400",
    ring: "ring-indigo-100 dark:ring-indigo-900",
    icon: <Loader2 className="h-3.5 w-3.5 animate-spin" />,
    label: "In progress",
  },
  [RequestorTaskStatus.Completed]: {
    bg: "bg-emerald-50 dark:bg-emerald-950/50",
    text: "text-emerald-600 dark:text-emerald-400",
    ring: "ring-emerald-100 dark:ring-emerald-900",
    icon: <CheckCircle2 className="h-3.5 w-3.5" />,
    label: "Completed",
  },
  [RequestorTaskStatus.Failed]: {
    bg: "bg-rose-50 dark:bg-rose-950/50",
    text: "text-rose-600 dark:text-rose-400",
    ring: "ring-rose-100 dark:ring-rose-900",
    icon: <XCircle className="h-3.5 w-3.5" />,
    label: "Failed",
  },
};

const resolveBadgeForStatus = (status: RequestorTaskStatus) =>
  status
    ? STATUS_BADGE_BY_STATUS[status]
    : STATUS_BADGE_BY_STATUS[RequestorTaskStatus.Pending];

interface RequestRecap {
  id: string;
  label: string;
  onnxFileName: string;
  onnxDownloadUrl: string;
  status: RequestorTaskStatus;
  startedAtUtc: string;
  startedAtLabel: string;
  duration: string;
  subtaskCount: number;
  cost: number;
  fillBindingsViaApi: boolean;
}

const TASK_TYPE_LABEL: Record<RequestorTaskType, string> = {
  [RequestorTaskType.Train]: "Training",
  [RequestorTaskType.Inference]: "Inference",
};

const EURO_FORMATTER = new Intl.NumberFormat(undefined, {
  style: "currency",
  currency: "EUR",
});

const formatCurrency = (value: number) =>
  EURO_FORMATTER.format(Number.isFinite(value) ? value : 0);

const parseDate = (iso: string) => {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? null : date;
};

const formatDurationFromSeconds = (durationSeconds: number) => {
  const totalSeconds = Math.max(0, Math.floor(durationSeconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return `${hours}h ${minutes.toString().padStart(2, "0")}m`;
  }

  if (minutes > 0) {
    return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  }

  return `${seconds}s`;
};

const formatStartedAtLabel = (iso: string) => {
  const date = parseDate(iso);
  if (!date) {
    return "—";
  }

  return getRelativeTime(date);
};

const resolveArtifactName = (url: string) => {
  if (!url) {
    return "Artifact";
  }

  try {
    const parsed = new URL(url);
    const segments = parsed.pathname.split("/").filter(Boolean);
    const lastSegment = decodeURIComponent(segments.at(-1) ?? "");
    return lastSegment || "Artifact";
  } catch {
    const sanitized = url.split("?")[0];
    const segments = sanitized.split("/").filter(Boolean);
    return decodeURIComponent((segments.at(-1) ?? sanitized) || "Artifact");
  }
};

const formatTaskLabel = (task: RequestorTaskDto) => {
  if (task.name && task.name.trim().length > 0) {
    return task.name.trim();
  }

  const baseLabel = TASK_TYPE_LABEL[task.type] ?? "Workload";
  return `${baseLabel} workload`;
};

export const RequestHistoryPanel = () => {
  const [newTaskDialogOpen, setNewTaskDialogOpen] = useState(false);
  const [subtasksDialogState, setSubtasksDialogState] = useState<{
    open: boolean;
    taskId: string;
    taskLabel: string;
  }>({ open: false, taskId: "", taskLabel: "" });
  const { data, isLoading, isError } = useMyTasksQuery();

  const handleCancelTask = useCallback(async (taskId: string) => {
    try {
      await cancelTask(taskId);
      appQueryClient.invalidateQueries({ queryKey: invalidateMyTasksQueryKey });
    } catch {
      // Silently fail – the UI will reflect current state on next poll
    }
  }, []);

  const requestRecaps = useMemo<RequestRecap[]>(() => {
    if (!data) {
      return [];
    }

    return [...data]
      .sort(
        (a, b) =>
          (parseDate(b.createdAt)?.getTime() ?? 0) -
          (parseDate(a.createdAt)?.getTime() ?? 0)
      )
      .map((task) => {
        const startedAtUtc = task.createdAt;
        return {
          id: task.id,
          label: formatTaskLabel(task),
          onnxFileName: resolveArtifactName(task.modelUrl),
          onnxDownloadUrl: task.modelUrl,
          status: task.status,
          startedAtUtc,
          startedAtLabel: formatStartedAtLabel(startedAtUtc),
          duration: formatDurationFromSeconds(task.durationSeconds ?? 0),
          subtaskCount: task.subtasksCount,
          cost: Number(task.estimatedCost ?? 0),
          fillBindingsViaApi: task.fillBindingsViaApi ?? false,
        };
      });
  }, [data]);

  const metrics = useMemo(() => {
    const activeStatuses = new Set<RequestorTaskStatus>(
      ACTIVE_REQUEST_STATUSES
    );
    const activeCount = requestRecaps.filter((recap) =>
      activeStatuses.has(recap.status)
    ).length;
    const totalCost = requestRecaps.reduce((sum, recap) => sum + recap.cost, 0);

    const now = Date.now();
    const tasksInLast24h = (data ?? []).filter((task) => {
      const created = parseDate(task.createdAt);
      return created ? now - created.getTime() <= 86_400_000 : false;
    });

    const completedLast24h = tasksInLast24h.filter(
      (task) => task.status === RequestorTaskStatus.Completed
    ).length;

    const successRateValue =
      tasksInLast24h.length > 0
        ? Math.round((completedLast24h / tasksInLast24h.length) * 100)
        : null;

    return {
      activeLabel: (activeCount !== null ? activeCount :  "—"),
      activeHelper:
        activeCount > 0
          ? "Jobs currently streaming subtasks to providers"
          : "No active workloads",
      successRateLabel:
        successRateValue !== null ? `${successRateValue}%` : "—",
      successHelper:
        tasksInLast24h.length > 0
          ? `${completedLast24h}/${tasksInLast24h.length} completed in last 24h`
          : "Awaiting workload completions",
      projectedCostLabel: formatCurrency(totalCost),
      projectedCostHelper:
        requestRecaps.length > 0
          ? "Aggregate estimated cost"
          : "Awaiting request activity",
    };
  }, [data, requestRecaps]);

  const tableColumns = useMemo(
    () => [
      {
        key: "request",
        header: "Request",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "max-w-xs px-6 py-4 align-top",
        render: (request: RequestRecap) => (
          <div className="flex flex-col gap-1">
            <span className="font-semibold text-slate-900 dark:text-slate-100">
              {request.label}
            </span>
            <span className="text-xs text-slate-400 dark:text-slate-500 font-mono truncate" title={request.id}>
              {request.id}
            </span>
          </div>
        ),
      },
      {
        key: "artifact",
        header: "ONNX artifact",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "px-6 py-4 align-top",
        render: (request: RequestRecap) => {
          const downloadAvailable =
            typeof request.onnxDownloadUrl === "string" &&
            request.onnxDownloadUrl.length > 0;

          return (
            <div className="flex flex-col gap-1">
              <span className="font-medium text-slate-700 dark:text-slate-300">
                {request.onnxFileName}
              </span>
              {downloadAvailable ? (
                <a
                  href={request.onnxDownloadUrl}
                  className="inline-flex items-center gap-1 text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300"
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  <FileDown className="h-3.5 w-3.5" />
                  Download artifact
                </a>
              ) : (
                <span className="text-xs text-slate-400 dark:text-slate-500">
                  Download link unavailable
                </span>
              )}
            </div>
          );
        },
      },
      {
        key: "state",
        header: "State",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "px-6 py-4 align-top",
        render: (request: RequestRecap) => {
          const { bg, text, ring, icon, label } = resolveBadgeForStatus(
            request.status
          );
          return (
            <span
              className={`text-nowrap inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs font-medium ${bg} ${text} ring-1 ${ring}`}
            >
              {icon}
              {label}
            </span>
          );
        },
      },
      {
        key: "started",
        header: "Started",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "px-6 py-4 align-top text-sm font-medium text-slate-700 dark:text-slate-300",
        render: (request: RequestRecap) => (
          <span title={request.startedAtUtc}>{request.startedAtLabel}</span>
        ),
      },
      {
        key: "duration",
        header: "Duration",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "px-6 py-4 align-top",
        render: (request: RequestRecap) => request.duration,
      },
      {
        key: "subtasks",
        header: "Subtasks",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "px-6 py-4 align-top",
        render: (request: RequestRecap) => (
          <button
            type="button"
            onClick={() =>
              setSubtasksDialogState({
                open: true,
                taskId: request.id,
                taskLabel: request.label,
              })
            }
            className="text-nowrap inline-flex items-center gap-2 rounded-md border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-medium text-slate-600 transition hover:border-indigo-200 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:border-indigo-700 dark:hover:bg-indigo-950/30 dark:hover:text-indigo-400"
          >
            <FolderTree className="h-3.5 w-3.5 text-indigo-500 dark:text-indigo-400" />
            {request.subtaskCount} subtasks
          </button>
        ),
      },
      {
        key: "cost",
        header: "Cost",
        headerClassName: "whitespace-nowrap px-6 py-3",
        cellClassName: "px-6 py-4 align-top font-semibold text-slate-900 dark:text-slate-100",
        render: (request: RequestRecap) => formatCurrency(request.cost),
      },
      {
        key: "actions",
        header: "Actions",
        headerClassName: "whitespace-nowrap px-6 py-3 text-right",
        cellClassName: "px-6 py-4 align-top text-right",
        render: (request: RequestRecap) => {
          const isActive =
            request.status === RequestorTaskStatus.Pending ||
            request.status === RequestorTaskStatus.Assigned ||
            request.status === RequestorTaskStatus.InProgress;

          if (request.fillBindingsViaApi && isActive) {
            return (
              <button
                type="button"
                onClick={() => handleCancelTask(request.id)}
                className="text-nowrap inline-flex items-center gap-1.5 rounded-lg border border-rose-200 bg-white px-3 py-1.5 text-xs font-semibold text-rose-600 shadow-sm transition hover:border-rose-300 hover:bg-rose-50 dark:border-rose-800 dark:bg-slate-800 dark:text-rose-400 dark:hover:border-rose-700 dark:hover:bg-rose-950/30"
              >
                <Square className="h-3.5 w-3.5" />
                Stop task
              </button>
            );
          }

          return (
            <span className="text-xs text-slate-400 dark:text-slate-500">—</span>
          );
        },
      },
    ],
    [setSubtasksDialogState, handleCancelTask]
  );

  return (
    <div className="space-y-6">
      <section className="grid gap-4 md:grid-cols-3">
        <MetricCard
          icon={<Loader2 className="h-5 w-5 text-indigo-500" />}
          label="Active requests"
          value={metrics.activeLabel.toString()}
          helper={metrics.activeHelper}
        />
        <MetricCard
          icon={<CheckCircle2 className="h-5 w-5 text-emerald-500" />}
          label="24h success rate"
          value={metrics.successRateLabel}
          helper={metrics.successHelper}
        />
        <MetricCard
          icon={<Receipt className="h-5 w-5 text-amber-500" />}
          label="Projected cost"
          value={metrics.projectedCostLabel}
          helper={metrics.projectedCostHelper}
        />
      </section>

      <section className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-700 dark:bg-slate-900">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-700">
          <div className="flex flex-col">
            <span className="text-sm font-semibold uppercase tracking-wide text-indigo-500 dark:text-indigo-400">
              Requests
            </span>
            <span className="text-sm text-slate-500 dark:text-slate-400">
              Auto-refreshing snapshot of your latest dispatches
            </span>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              className="inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-300 focus:ring-offset-2 dark:bg-indigo-700 dark:hover:bg-indigo-600 dark:focus:ring-indigo-900 dark:focus:ring-offset-slate-900"
              onClick={() => setNewTaskDialogOpen(true)}
            >
              <Plus className="h-4 w-4" />
              New request
            </button>
          </div>
        </div>

        <DataTable
          data={requestRecaps}
          columns={tableColumns}
          keyExtractor={(request) => request.id}
          isLoading={isLoading}
          isError={isError}
          emptyMessage="No task requests registered yet. Create a new request to get started."
          errorMessage="Unable to load request history. Please retry shortly."
        />
      </section>

      <NewTaskRequestDialog
        open={newTaskDialogOpen}
        onDismiss={() => setNewTaskDialogOpen(false)}
      />

      <SubtasksDialog
        open={subtasksDialogState.open}
        onDismiss={() =>
          setSubtasksDialogState({ open: false, taskId: "", taskLabel: "" })
        }
        taskId={subtasksDialogState.taskId}
        taskLabel={subtasksDialogState.taskLabel}
      />
    </div>
  );
};

const MetricCard = ({
  icon,
  label,
  value,
  helper,
}: {
  icon: JSX.Element;
  label: string;
  value: string;
  helper: string;
}) => (
  <div className="flex flex-col gap-4 rounded-xl border border-slate-200 bg-white p-5 shadow-sm transition hover:border-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:hover:border-indigo-700">
    <div className="flex items-center justify-between">
      <div className="flex h-10 w-10 items-center justify-center rounded-full bg-indigo-50 dark:bg-indigo-950/50">
        {icon}
      </div>
      <span className="text-xs font-medium uppercase text-slate-400 dark:text-slate-500">
        {label}
      </span>
    </div>
    <div className="space-y-1">
      <p className="text-2xl font-semibold text-slate-900 dark:text-slate-100">{value}</p>
      <p className="text-xs text-slate-500 dark:text-slate-400">{helper}</p>
    </div>
  </div>
);
