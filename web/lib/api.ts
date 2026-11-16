export type JobStatus =
  | "Pending"
  | "Running"
  | "Completed"
  | "Failed"
  | "Canceled";
export type PageStatus =
  | "Pending"
  | "Processing"
  | "Completed"
  | "Failed"
  | "Skipped"
  | "Canceled";
export type CreateJobInput = { url: string; maxDepth?: number };
export type JobProgress = {
  total: number;
  pending: number;
  processing: number;
  completed: number;
  failed: number;
  skipped: number;
  canceled: number;
};
export type JobHistoryItem = {
  jobId: string;
  inputUrl: string;
  status: JobStatus;
  createdAt: string;
  completedAt: string | null;
};
export type JobSummary = JobHistoryItem & {
  maxDepth: number;
  maxPages: number;
  startedAt: string | null;
  failureReason: string | null;
  progress: JobProgress;
};
export type PageTreeNode = {
  url: string;
  finalUrl: string | null;
  depth: number;
  status: PageStatus;
  httpStatus: number | null;
  contentType: string | null;
  totalLinkCount: number;
  internalLinkCount: number;
  linkRatio: number | null;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  children: PageTreeNode[];
};
export type PagedResult<T> = {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
};

type ProblemDetails = {
  detail?: string;
  title?: string;
  errors?: Record<string, string[]>;
};

export class ApiError extends Error {
  /**
   * Keeps the HTTP status with a user-facing error message; status zero represents a connection
   * failure before an HTTP response.
   */
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/**
 * Calls the same-origin .NET API through the Next.js rewrite and returns typed JSON. Converts
 * Problem Details/network failures into ApiError while preserving canceled-read AbortErrors.
 */
async function request<T>(path: string, options?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api/jobs${path}`, {
      ...options,
      cache: "no-store",
    });
  } catch (error) {
    if (error instanceof Error && error.name === "AbortError") throw error;
    throw new ApiError(
      "Could not reach the crawl service. Please try again.",
      0,
    );
  }
  if (!response.ok) {
    const problem: ProblemDetails | null = await response
      .json()
      .catch(() => null);
    const validation = problem?.errors
      ? Object.values(problem.errors).flat().join(" ")
      : null;
    throw new ApiError(
      validation ||
        problem?.detail ||
        problem?.title ||
        `Request failed (${response.status}).`,
      response.status,
    );
  }
  return response.json() as Promise<T>;
}

/**
 * Starts a new crawl with POST and returns its public UUID. Repeating this request
 * intentionally creates another job; worker-task deduplication is a separate concern.
 */
export const createJob = (input: CreateJobInput) =>
  request<{ jobId: string }>("", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  });
/**
 * Requests durable cancellation and returns the authoritative job summary. The server treats
 * repeated cancellation as a no-op and rejects already finished jobs with HTTP 409.
 */
export const cancelJob = (id: string) =>
  request<JobSummary>(`/${encodeURIComponent(id)}/cancel`, { method: "POST" });
/**
 * Reads lifecycle, limits, timestamps, and progress for a public job UUID. TanStack Query
 * supplies signal to abort obsolete reads when the page changes.
 */
export const getJob = (id: string, signal?: AbortSignal) =>
  request<JobSummary>(`/${encodeURIComponent(id)}`, { signal });
/**
 * Reads the job's nested page results, including redirects, ratios, and per-page errors.
 * Callers enable this query when the job becomes terminal.
 */
export const getJobTree = (id: string, signal?: AbortSignal) =>
  request<PageTreeNode[]>(`/${encodeURIComponent(id)}/tree`, { signal });
/**
 * Reads a one-based page of newest-first history, including total count. Values are encoded in
 * the query string rather than stored only in component state.
 */
export const listJobs = (
  page: number,
  pageSize: number,
  signal?: AbortSignal,
) =>
  request<PagedResult<JobHistoryItem>>(`?page=${page}&pageSize=${pageSize}`, {
    signal,
  });
/**
 * Defines the shared UI polling/cancel rule: only Pending and Running jobs can still do work.
 * Missing or terminal status returns false.
 */
export const isActive = (status?: JobStatus) =>
  status === "Pending" || status === "Running";
/**
 * Converts a stored UTC timestamp to the browser's local date/time display. Missing lifecycle
 * timestamps use an em dash instead of an invalid date.
 */
export const formatTime = (value: string | null) =>
  value ? new Date(value).toLocaleString() : "—";
