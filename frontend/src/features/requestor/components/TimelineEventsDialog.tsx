import { useMemo } from "react";
import { DialogShell } from "../../../shared/components/DialogShell";
import { DataTable } from "../../../shared/components/DataTable";
import type { SubtaskTimelineEventDto } from "../types";
import { getRelativeTime } from "../../../shared/utils/dateTime";
import { Clock, ScrollText } from "lucide-react";

interface TimelineEventsDialogProps {
  open: boolean;
  onDismiss: () => void;
  events: Array<SubtaskTimelineEventDto>;
  subtaskId: string;
}

const formatDate = (iso: string) => {
  return Number.isNaN(new Date(iso).getTime()) ? "—" : getRelativeTime(iso);
};

const formatMetadata = (metadataJson?: string | null) => {
  if (!metadataJson) return null;
  
  try {
    const metadata = JSON.parse(metadataJson);
    return JSON.stringify(metadata, null, 2);
  } catch {
    return metadataJson;
  }
};

export const TimelineEventsDialog = ({
  open,
  onDismiss,
  events,
  subtaskId,
}: TimelineEventsDialogProps) => {
  const columns = useMemo(
    () => [
      {
        key: "timestamp",
        header: "Timestamp",
        render: (event: SubtaskTimelineEventDto) => (
          <div className="flex flex-col gap-0.5">
            <span className="text-sm font-medium text-slate-700 dark:text-slate-300">
              {formatDate(event.createdAtUtc)}
            </span>
            <span className="text-xs text-slate-400 dark:text-slate-500" title={event.createdAtUtc}>
              {new Date(event.createdAtUtc).toLocaleString()}
            </span>
          </div>
        ),
      },
      {
        key: "eventType",
        header: "Event Type",
        render: (event: SubtaskTimelineEventDto) => (
          <span className="inline-flex items-center gap-1.5 rounded-md border border-indigo-200 bg-indigo-50 px-2 py-1 text-xs font-medium text-indigo-700 dark:border-indigo-900/50 dark:bg-indigo-950/50 dark:text-indigo-400">
            <Clock className="h-3 w-3" />
            {event.eventType}
          </span>
        ),
      },
      {
        key: "message",
        header: "Message",
        render: (event: SubtaskTimelineEventDto) => (
          <div className="max-w-md">
            {event.message ? (
              <span className="text-sm text-slate-700 dark:text-slate-300">{event.message}</span>
            ) : (
              <span className="text-xs text-slate-400 dark:text-slate-500">No message</span>
            )}
          </div>
        ),
      },
      {
        key: "metadata",
        header: "Metadata",
        render: (event: SubtaskTimelineEventDto) => {
          const formattedMetadata = formatMetadata(event.metadataJson);
          if (!formattedMetadata) {
            return <span className="text-xs text-slate-400">—</span>;
          }
          return (
            <details className="group">
              <summary className="cursor-pointer text-xs font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 dark:hover:text-indigo-300">
                View metadata
              </summary>
              <pre className="mt-2 max-w-md overflow-x-auto rounded bg-slate-50 p-2 text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-300">
                {formattedMetadata}
              </pre>
            </details>
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
      closeLabel="Close timeline events dialog"
      badgeIcon={<ScrollText className="h-3.5 w-3.5" />}
      badgeLabel="Timeline Events"
      title={`Timeline Events for Subtask`}
      helperText={`Subtask ID: ${subtaskId}`}
      containerClassName="max-w-6xl"
    >
      <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-700 dark:bg-slate-900">
        <DataTable
          data={events}
          columns={columns}
          keyExtractor={(event) => event.id}
          isLoading={false}
          isError={false}
          emptyMessage="No timeline events recorded for this subtask"
          errorMessage="Unable to load timeline events"
        />
      </div>
    </DialogShell>
  );
};