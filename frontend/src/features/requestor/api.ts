import { apiRequest } from "../../shared/utils/apiClient";
import type {
  RequestorTaskDto,
  RequestorTaskStatus,
} from "./types";

export const TaskUploadFileType = {
  Model: 0,
  Input: 1,
  Output: 2
} as const;
export type TaskUploadFileType = (typeof TaskUploadFileType)[keyof typeof TaskUploadFileType];

export const TaskType = {
  Train: 0,
  Inference: 1
} as const;
export type TaskType = (typeof TaskType)[keyof typeof TaskType];

export interface GenerateTaskUploadUrlRequest {
  taskId: string;
  subtaskId: string;
  inputName: string;
  fileExtension: string;
  fileType: TaskUploadFileType;
}

export interface GenerateTaskUploadUrlResponse {
  blobUri: string;
  uploadUri: string;
  expiresAtUtc: string;
}

export const generateTaskUploadUrl = (payload: GenerateTaskUploadUrlRequest) =>
  apiRequest<GenerateTaskUploadUrlResponse, GenerateTaskUploadUrlRequest>(
    "/api/tasks/upload-url",
    {
      method: "POST",
      body: payload,
    }
  );

export type InferencePayloadType = "Json" | "Text" | "Binary";

export interface CreateTaskRequestBody {
  taskId: string;
  type: TaskType;
  modelUrl: string;
  optimizerModelUrl?: string;
  checkpointUrl?: string;
  evalModelUrl?: string;
  fillBindingsViaApi: boolean;
  initialSubtaskId?: string;
  inference?: {
    bindings: Array<{
      tensorName: string;
      payloadType: InferencePayloadType;
      payload: string | null;
      fileUrl: string | null;
      maxLength?: number | null;
      padding?: boolean | null;
    }>;
    outputs?: Array<{
      tensorName: string;
      payloadType: InferencePayloadType;
      fileFormat?: string;
    }>;
  };
  training?: {
    inputs: Array<{
      tensorName: string;
      payloadType: InferencePayloadType;
      fileUrl: string | null;
    }>;
    outputs: Array<{
      tensorName: string;
      payloadType: InferencePayloadType;
      fileUrl: string | null;
    }>;
  };
}

export const createTask = (payload: CreateTaskRequestBody) =>
  apiRequest<void, CreateTaskRequestBody>("/api/tasks/create", {
    method: "POST",
    body: payload,
  });

export const getMyTasks = (status?: RequestorTaskStatus) => {
  const searchParams =
    typeof status === "number" ? `?status=${status}` : "";

  return apiRequest<Array<RequestorTaskDto>>(
    `/api/tasks/my-tasks${searchParams}`
  );
};

export const getTaskSubtasks = (taskId: string) => {
  return apiRequest<Array<import("./types").SubtaskDto>>(
    `/api/tasks/${taskId}/subtasks`
  );
};