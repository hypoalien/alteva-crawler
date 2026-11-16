"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { ErrorNotice } from "@/components/error-notice";
import { StatusBadge } from "@/components/status-badge";
import { formatTime, isActive, listJobs } from "@/lib/api";

/**
 * Reads the page number from the URL, loads newest-first history, and renders navigation.
 * Retains the previous page while loading and refreshes displayed active jobs until they
 * finish.
 */
export function HistoryList() {
  const params = useSearchParams();
  const router = useRouter();
  const requestedPage = Number(params.get("page") ?? 1);
  const page =
    Number.isInteger(requestedPage) &&
    requestedPage > 0 &&
    requestedPage <= 1_000_000
      ? requestedPage
      : 1;
  const pageSize = 10;
  const history = useQuery({
    queryKey: ["jobs", page],
    queryFn: ({ signal }) => listJobs(page, pageSize, signal),
    // Keep the old rows visible during navigation; the buttons disable while those rows are placeholders.
    placeholderData: keepPreviousData,
    // A job left on the history screen must not remain visibly Running after it finishes.
    refetchInterval: (query) =>
      query.state.data?.items.some((job) => isActive(job.status))
        ? 3000
        : false,
  });
  if (history.isPending) return <p role="status">Loading history…</p>;
  if (history.error)
    return (
      <ErrorNotice
        error={history.error}
        onRetry={() => void history.refetch()}
      />
    );
  const data = history.data;

  return (
    <div className="space-y-5">
      <Card>
        <CardContent>
          {data.items.length === 0 ? (
            <p className="py-5 text-sm text-muted-foreground">
              No crawls on this page.{" "}
              <Link href="/" className="underline">
                Start a crawl
              </Link>{" "}
              or return to an earlier page.
            </p>
          ) : (
            <ul className="divide-y">
              {data.items.map((job) => (
                <li key={job.jobId}>
                  <Link
                    href={`/jobs/${encodeURIComponent(job.jobId)}`}
                    className="block space-y-2 rounded-md py-5 hover:bg-muted/50 focus-visible:outline-2 focus-visible:outline-ring"
                  >
                    <div className="flex flex-wrap items-center gap-3">
                      <span className="min-w-0 flex-1 text-sm font-medium break-all">
                        {job.inputUrl}
                      </span>
                      <StatusBadge status={job.status} />
                    </div>
                    <p className="text-xs text-muted-foreground">
                      Created {formatTime(job.createdAt)} · Completed{" "}
                      {formatTime(job.completedAt)}
                    </p>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
      <nav
        aria-label="History pages"
        className="flex flex-wrap items-center justify-between gap-3"
      >
        <Button
          variant="outline"
          disabled={page <= 1 || history.isPlaceholderData}
          onClick={() => router.push(`/history?page=${page - 1}`)}
        >
          Previous
        </Button>
        <p className="text-sm text-muted-foreground" role="status">
          Page {page} · {data.total} crawls
        </p>
        <Button
          variant="outline"
          disabled={page * pageSize >= data.total || history.isPlaceholderData}
          onClick={() => router.push(`/history?page=${page + 1}`)}
        >
          Next
        </Button>
      </nav>
    </div>
  );
}
