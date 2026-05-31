import { useMemo, useState, type ReactNode } from "react";
import { DialogShell } from "../../../shared/components/DialogShell";
import { DataTable } from "../../../shared/components/DataTable";
import { TimelineEventsDialog } from "./TimelineEventsDialog";
import { useTaskSubtasksQuery } from "../queries/useTaskSubtasksQuery";
import type {
  SubtaskDto,
  SubtaskStatus,
  SubtaskTimelineEventDto,
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

const EURO_FORMATTER = new Intl.NumberFormat(undefined, {
  style: "currency",
  currency: "EUR",
});

const formatCurrency = (value: number) =>
  EURO_FORMATTER.format(Number.isFinite(value) ? value : 0);

const formatDate = (iso?: string | null) => {
  if (!iso) return "—";
  return Number.isNaN(new Date(iso).getTime()) ? "—" : getRelativeTime(iso);
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
