import { useEffect, useRef } from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { useAuthStore } from "../../features/auth/stores/authStore";
import { appQueryClient } from "../providers/queryClient";
import { API_BASE_URL } from "../utils/apiClient";
import { invalidateMyTasksQueryKey } from "../../features/requestor/queries/useMyTasksQuery";
import { invalidateDeviceSubtasksKey } from "../../features/provider/queries/useDeviceSubtasksQuery";
import { invalidateAvailableSubtasksKey } from "../../features/provider/queries/useAvailableSubtasksQuery";

/**
 * Connects to the backend SignalR `/taskhub` and listens for task/subtask
 * state-change events. When an event arrives the corresponding React-Query
 * cache keys are invalidated so the UI refreshes automatically.
 *
 * The hook manages the full connection lifecycle:
 *  – connects when the user is authenticated
 *  – for Provider users, joins the `Providers` group via `JoinAvailableTasks`
 *  – disconnects on logout or unmount
 */
export const useSignalRRefresh = () => {
  const user = useAuthStore((s) => s.user);
  const token = useAuthStore((s) => s.token);
  const connectionRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    if (!user || !token) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/taskhub`, {
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    connectionRef.current = connection;

    // ── Requestor events (received via User_{userId} group, auto-joined on connect) ──

    const invalidateRequestorTasks = () => {
      appQueryClient.invalidateQueries({ queryKey: [...invalidateMyTasksQueryKey] });
      // Also refresh any open subtasks dialog within the Requests page
      appQueryClient.invalidateQueries({ queryKey: ["task-subtasks"] });
    };

    connection.on("TaskUpdated", invalidateRequestorTasks);
    connection.on("TaskCompleted", invalidateRequestorTasks);
    connection.on("TaskFailed", invalidateRequestorTasks);

    // ── Provider events (received via Providers / Provider_{userId} groups) ──

    const invalidateProviderSubtasks = () => {
      appQueryClient.invalidateQueries({ queryKey: [...invalidateDeviceSubtasksKey] });
      appQueryClient.invalidateQueries({ queryKey: [...invalidateAvailableSubtasksKey] });
    };

    connection.on("OnAvailableSubtasksChanged", invalidateProviderSubtasks);
    connection.on("OnSubtaskAccepted", invalidateProviderSubtasks);
    connection.on("OnComplete", invalidateProviderSubtasks);
    connection.on("OnFailure", invalidateProviderSubtasks);

    // ── Start ──

    connection
      .start()
      .then(async () => {
        // Providers must explicitly join the Providers group to receive subtask events
        if (user.role === "Provider") {
          try {
            await connection.invoke("JoinAvailableTasks", user.id, user.role, null);
          } catch (err) {
            console.warn("[SignalR] Failed to join available tasks group:", err);
          }
        }
      })
      .catch((err) => {
        console.warn("[SignalR] Connection failed:", err);
      });

    // Re-join provider group on reconnect
    connection.onreconnected(async () => {
      if (user.role === "Provider") {
        try {
          await connection.invoke("JoinAvailableTasks", user.id, user.role, null);
        } catch (err) {
          console.warn("[SignalR] Re-join failed after reconnect:", err);
        }
      }
    });

    return () => {
      connectionRef.current = null;
      if (connection.state !== HubConnectionState.Disconnected) {
        connection.stop().catch(() => {});
      }
    };
  }, [user, token]);
};
