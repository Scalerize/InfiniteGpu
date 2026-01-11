import { useMemo, useState, type ReactNode } from "react";
import { DialogShell } from "../../../shared/components/DialogShell";
import { DataTable } from "../../../shared/components/DataTable";
import { TimelineEventsDialog } from "./TimelineEventsDialog";
import { useTaskSubtasksQuery } from "../queries/useTaskSubtasksQuery";
import type {
  SubtaskDto,
  SubtaskStatus,
  SubtaskTimelineEventDto,
  PartitionDto,
  PartitionStatus,
} from "../types";
import { getRelativeTime } from "../../../shared/utils/dateTime";
import {
  CheckCircle2,
  Clock,
  Loader2,
  XCircle,
  FileDown,
  FileUp,
  Receipt,
  ListTree,
  Network,
  Layers,
  ArrowRight,
  Wifi,
  WifiOff,
} from "lucide-react";

interface SubtasksDialogProps {
  open: boolean;
  onDismiss: () => void;
  taskId: string;
  taskLabel: string;
}

const STATUS_BADGE_CONFIG: Record<
  SubtaskStatus,
  { bg: string; text: string; ring: string; icon: ReactNode; label: string }
> = {
  Pending: {
    bg: "bg-amber-50 dark:bg-amber-950/50",
    text: "text-amber-600 dark:text-amber-400",
    ring: "ring-amber-100 dark:ring-amber-900",
    icon: <Clock className="h-3 w-3" />,
    label: "Pending",
  },
  Assigned: {
    bg: "bg-sky-50 dark:bg-sky-950/50",
    text: "text-sky-600 dark:text-sky-400",
    ring: "ring-sky-100 dark:ring-sky-900",
    icon: <Clock className="h-3 w-3" />,
    label: "Assigned",
  },
  Executing: {
    bg: "bg-indigo-50 dark:bg-indigo-950/50",
    text: "text-indigo-600 dark:text-indigo-400",
    ring: "ring-indigo-100 dark:ring-indigo-900",
    icon: <Loader2 className="h-3 w-3 animate-spin" />,
    label: "In Progress",
  },
  Completed: {
    bg: "bg-emerald-50 dark:bg-emerald-950/50",
    text: "text-emerald-600 dark:text-emerald-400",
    ring: "ring-emerald-100 dark:ring-emerald-900",
    icon: <CheckCircle2 className="h-3 w-3" />,
    label: "Completed",
  },
  Failed: {
    bg: "bg-rose-50 dark:bg-rose-950/50",
    text: "text-rose-600 dark:text-rose-400",
    ring: "ring-rose-100 dark:ring-rose-900",
    icon: <XCircle className="h-3 w-3" />,
    label: "Failed",
  },
};

// Partition status badge configuration for smart partitioning visualization
const PARTITION_STATUS_BADGE_CONFIG: Record<
  PartitionStatus,
  { bg: string; text: string; ring: string; icon: ReactNode; label: string }
> = {
  Pending: {
    bg: "bg-slate-50 dark:bg-slate-950/50",
    text: "text-slate-600 dark:text-slate-400",
    ring: "ring-slate-100 dark:ring-slate-900",
    icon: <Clock className="h-2.5 w-2.5" />,
    label: "Queued",
  },
  Assigned: {
    bg: "bg-sky-50 dark:bg-sky-950/50",
    text: "text-sky-600 dark:text-sky-400",
    ring: "ring-sky-100 dark:ring-sky-900",
    icon: <Layers className="h-2.5 w-2.5" />,
    label: "Assigned",
  },
  Connecting: {
    bg: "bg-violet-50 dark:bg-violet-950/50",
    text: "text-violet-600 dark:text-violet-400",
    ring: "ring-violet-100 dark:ring-violet-900",
    icon: <Wifi className="h-2.5 w-2.5 animate-pulse" />,
    label: "Connecting",
  },
  WaitingForInput: {
    bg: "bg-amber-50 dark:bg-amber-950/50",
    text: "text-amber-600 dark:text-amber-400",
    ring: "ring-amber-100 dark:ring-amber-900",
    icon: <Clock className="h-2.5 w-2.5" />,
    label: "Waiting",
  },
  Executing: {
    bg: "bg-indigo-50 dark:bg-indigo-950/50",
    text: "text-indigo-600 dark:text-indigo-400",
    ring: "ring-indigo-100 dark:ring-indigo-900",
    icon: <Loader2 className="h-2.5 w-2.5 animate-spin" />,
    label: "Running",
  },
  StreamingOutput: {
    bg: "bg-cyan-50 dark:bg-cyan-950/50",
    text: "text-cyan-600 dark:text-cyan-400",
    ring: "ring-cyan-100 dark:ring-cyan-900",
    icon: <ArrowRight className="h-2.5 w-2.5 animate-pulse" />,
    label: "Streaming",
  },
  Completed: {
    bg: "bg-emerald-50 dark:bg-emerald-950/50",
    text: "text-emerald-600 dark:text-emerald-400",
    ring: "ring-emerald-100 dark:ring-emerald-900",
    icon: <CheckCircle2 className="h-2.5 w-2.5" />,
    label: "Done",
  },
  Failed: {
    bg: "bg-rose-50 dark:bg-rose-950/50",
    text: "text-rose-600 dark:text-rose-400",
    ring: "ring-rose-100 dark:ring-rose-900",
    icon: <XCircle className="h-2.5 w-2.5" />,
    label: "Failed",
  },
  Cancelled: {
    bg: "bg-gray-50 dark:bg-gray-950/50",
    text: "text-gray-600 dark:text-gray-400",
    ring: "ring-gray-100 dark:ring-gray-900",
    icon: <WifiOff className="h-2.5 w-2.5" />,
    label: "Cancelled",
  },
};

const EURO_FORMATTER = new Intl.NumberFormat(undefined, {
  style: "currency",
  currency: "EUR",
});

const formatCurrency = (value: number) =>
  EURO_FORMATTER.format(Number.isFinite(value) ? value : 0);

const formatDate = (iso?: string | null) => {
  if (!iso) return "—";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "—" : getRelativeTime(date);
};

const resolveArtifactName = (url?: string | null) => {
  if (!url) return null;

  try {
    const parsed = new URL(url);
    const segments = parsed.pathname.split("/").filter(Boolean);
    const lastSegment = decodeURIComponent(segments.at(-1) ?? "");
    return lastSegment || null;
  } catch {
    const sanitized = url.split("?")[0];
    const segments = sanitized.split("/").filter(Boolean);
    return decodeURIComponent(segments.at(-1) ?? "") || null;
  }
};

export const SubtasksDialog = ({
  open,
  onDismiss,
  taskId,
  taskLabel,
}: SubtasksDialogProps) => {
  const { data, isLoading, isError } = useTaskSubtasksQuery(taskId, open);
  const [timelineDialogState, setTimelineDialogState] = useState<{
    open: boolean;
    events: Array<SubtaskTimelineEventDto>;
    subtaskId: string;
  }>({ open: false, events: [], subtaskId: "" });

  const columns = useMemo(
    () => [
      {
        key: "started",
        header: "Started",
        render: (subtask: SubtaskDto) => (
          <span
            className="text-sm font-medium text-slate-700 dark:text-slate-300"
            title={subtask.startedAtUtc || subtask.createdAtUtc}
          >
            {formatDate(subtask.startedAtUtc || subtask.createdAtUtc)}
          </span>
        ),
      },
      {
        key: "state",
        header: "State",
        render: (subtask: SubtaskDto) => {
          const config = STATUS_BADGE_CONFIG[subtask.status];
          return (
            <span
              className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium ${config?.bg} ${config?.text} ring-1 ${config?.ring}`}
            >
              {config?.icon}
              {config?.label}
            </span>
          );
        },
      },
      {
        key: "partitions",
        header: "Partitions",
        render: (subtask: SubtaskDto) => {
          // Show partition pipeline if distributed
          if (!subtask.requiresPartitioning || !subtask.partitions?.length) {
            return (
              <span className="text-xs text-slate-400 dark:text-slate-500">
                Single device
              </span>
            );
          }

          const partitions = subtask.partitions;
          const completed = partitions.filter((p) => p.status === "Completed").length;
          const executing = partitions.filter((p) => p.status === "Executing" || p.status === "StreamingOutput").length;
          const failed = partitions.filter((p) => p.status === "Failed").length;

          return (
            <div className="flex flex-col gap-1.5">
              {/* Summary row */}
              <div className="flex items-center gap-1.5">
                <Network className="h-3.5 w-3.5 text-violet-500 dark:text-violet-400" />
                <span className="text-xs font-medium text-slate-700 dark:text-slate-300">
                  {completed}/{partitions.length} complete
                </span>
                {executing > 0 && (
                  <span className="text-xs text-indigo-600 dark:text-indigo-400">
                    ({executing} active)
                  </span>
                )}
                {failed > 0 && (
                  <span className="text-xs text-rose-600 dark:text-rose-400">
                    ({failed} failed)
                  </span>
                )}
              </div>

              {/* Pipeline visualization */}
              <div className="flex items-center gap-0.5">
                {partitions
                  .sort((a, b) => a.partitionIndex - b.partitionIndex)
                  .map((partition, index) => {
                    const partitionConfig = PARTITION_STATUS_BADGE_CONFIG[partition.status];
                    return (
                      <div key={partition.id} className="flex items-center">
                        {/* Partition node */}
                        <div
                          className={`inline-flex items-center gap-0.5 rounded px-1.5 py-0.5 text-[10px] font-medium ${partitionConfig?.bg} ${partitionConfig?.text} ring-1 ${partitionConfig?.ring}`}
                          title={`P${index + 1}: ${partition.status} (${partition.progress}%)`}
                        >
                          {partitionConfig?.icon}
                          <span>P{index + 1}</span>
                        </div>
                        {/* Arrow connector */}
                        {index < partitions.length - 1 && (
                          <ArrowRight className="h-2.5 w-2.5 text-slate-300 dark:text-slate-600 mx-0.5" />
                        )}
                      </div>
                    );
                  })}
              </div>

              {/* Progress bar */}
              <div className="h-1.5 w-full rounded-full bg-slate-100 dark:bg-slate-800 overflow-hidden">
                <div
                  className="h-full bg-gradient-to-r from-violet-500 to-indigo-500 transition-all duration-300"
                  style={{
                    width: `${(completed / partitions.length) * 100}%`,
                  }}
                />
              </div>
            </div>
          );
        },
      },
      {
        key: "source",
        header: "Source",
        render: (subtask: SubtaskDto) => {
          const isManual = subtask.assignedAtUtc !== null;
          return (
            <span className="text-sm text-slate-600 dark:text-slate-300">
              {isManual ? "Manual" : "API"}
            </span>
          );
        },
      },
      {
        key: "inputArtifact",
        header: "Input Artifact",
        render: (subtask: SubtaskDto) => {
          const artifacts = subtask.inputArtifacts ?? [];
          if (artifacts.length === 0) {
            return <span className="text-xs text-slate-400">—</span>;
          }
          return (
            <div className="flex flex-col gap-1">
              {artifacts.map((artifact, index) => {
                const artifactName = resolveArtifactName(artifact.fileUrl);
                const hasFile = artifact.fileUrl && artifactName;
                const hasText = artifact.payload && !artifact.fileUrl;

                return (
                  <div key={index} className="flex flex-col gap-0.5">
                    <span className="text-sm text-slate-700 dark:text-slate-300">
                      {artifact.tensorName}
                    </span>
                    {hasFile && (
                      <a
                        href={artifact.fileUrl!}
                        className="inline-flex items-center gap-1 text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300"
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        <FileDown className="h-3 w-3" />
                        {artifactName}
                      </a>
                    )}
                    {hasText && (
                      <details className="group">
                        <summary className="cursor-pointer text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300">
                          {artifact.payloadType === "Text"
                            ? "View text content"
                            : "View JSON content"}
                        </summary>
                        <pre className="mt-1 max-w-md overflow-x-auto rounded bg-slate-50 p-2 text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-300">
                          {artifact.payload}
                        </pre>
                      </details>
                    )}
                  </div>
                );
              })}
            </div>
          );
        },
      },
      {
        key: "outputArtifact",
        header: "Output Artifact",
        render: (subtask: SubtaskDto) => {
          const artifacts = subtask.outputArtifacts ?? [];
          if (artifacts.length === 0) {
            return <span className="text-xs text-slate-400">—</span>;
          }
          return (
            <div className="flex flex-col gap-1">
              {artifacts.map((artifact, index) => {
                const artifactName = resolveArtifactName(artifact.fileUrl);
                const hasFile = artifact.fileUrl && artifactName;
                const hasText = artifact.payload && !artifact.fileUrl;

                return (
                  <div key={index} className="flex flex-col gap-0.5">
                    <span className="text-sm text-slate-700 dark:text-slate-300">
                      {artifact.tensorName}
                      {artifact.fileFormat && (
                        <span className="ml-1 text-xs text-slate-500 dark:text-slate-400">
                          ({artifact.fileFormat})
                        </span>
                      )}
                    </span>
                    {hasFile && (
                      <a
                        href={artifact.fileUrl!}
                        className="inline-flex items-center gap-1 text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300"
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        <FileUp className="h-3 w-3" />
                        {artifactName}
                      </a>
                    )}
                    {hasText && (
                      <details className="group">
                        <summary className="cursor-pointer text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300">
                           {artifact.payloadType === "Text"
                            ? "View text content"
                            : "View JSON content"}
                        </summary>
                        <pre className="mt-1 max-w-md overflow-x-auto rounded bg-slate-50 p-2 text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-300">
                          {artifact.payload}
                        </pre>
                      </details>
                    )}
                  </div>
                );
              })}
            </div>
          );
        },
      },
      {
        key: "cost",
        header: "Cost",
        render: (subtask: SubtaskDto) => (
          <span className="text-sm font-semibold text-slate-900 dark:text-slate-100">
            {formatCurrency(subtask.costUsd ?? 0)}
          </span>
        ),
      },
      {
        key: "logs",
        header: "Logs",
        render: (subtask: SubtaskDto) => {
          const eventCount = subtask.timeline?.length ?? 0;
          if (eventCount === 0) {
            return <span className="text-xs text-slate-400">No events</span>;
          }
          return (
            <button
              type="button"
              onClick={() =>
                setTimelineDialogState({
                  open: true,
                  events: subtask.timeline ?? [],
                  subtaskId: subtask.id,
                })
              }
              className="inline-flex items-center gap-1.5 rounded-md border border-slate-200 bg-slate-50 px-2 py-1 text-xs font-medium text-slate-600 transition hover:border-indigo-200 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:border-indigo-700 dark:hover:bg-indigo-950/30 dark:hover:text-indigo-400"
            >
              <ListTree className="h-3 w-3 text-indigo-500 dark:text-indigo-400" />
              {eventCount} {eventCount === 1 ? "event" : "events"}
            </button>
          );
        },
      },
    ],
    []
  );

  return (
    <DialogShell
      open={open}
      onDismiss={onDismiss}
      closeLabel="Close subtasks dialog"
      badgeIcon={<Receipt className="h-3.5 w-3.5" />}
      badgeLabel="Subtasks"
      title={`Subtasks for ${taskLabel}`}
      helperText="View all subtasks that were executed as part of this task request"
      containerClassName="max-w-7xl"
    >
      <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-700 dark:bg-slate-900">
        <DataTable
          data={data ?? []}
          columns={columns}
          keyExtractor={(subtask) => subtask.id}
          isLoading={isLoading}
          isError={isError}
          emptyMessage="No subtasks found for this task"
          errorMessage="Unable to load subtasks. Please try again."
        />
      </div>

      <TimelineEventsDialog
        open={timelineDialogState.open}
        onDismiss={() =>
          setTimelineDialogState({ open: false, events: [], subtaskId: "" })
        }
        events={timelineDialogState.events}
        subtaskId={timelineDialogState.subtaskId}
      />
    </DialogShell>
  );
};
