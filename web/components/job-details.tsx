"use client";

import Link from "next/link";
import dynamic from "next/dynamic";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { ErrorNotice } from "@/components/error-notice";
import { PageTree } from "@/components/page-tree";
import { StatusBadge } from "@/components/status-badge";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cancelJob, formatTime, getJob, getJobTree, isActive } from "@/lib/api";

// Load the interactive graph only when its tab is opened; the default tree stays lightweight.
const PageGraph = dynamic(
  () => import("@/components/page-graph").then((module) => module.PageGraph),
  {
    ssr: false,
    loading: () => (
      <p role="status" className="py-8 text-sm text-muted-foreground">
        Loading graph…
      </p>
    ),
  },
);

/**
 * Loads one job, polls while active, and fetches its tree once terminal. Owns cancellation
 * feedback/cache updates and keeps processed counts separate from canceled pages.
 */
export function JobDetails({ jobId }: { jobId: string }) {
  const queryClient = useQueryClient();
  // Do not mark the UI canceled optimistically: the server may be unavailable or completion may win.
  const cancellation = useMutation({
    mutationFn: () => cancelJob(jobId),
    // Commit the returned summary to the cache, then refresh dependent tree/history queries.
    onSuccess: (result) => {
      queryClient.setQueryData(["job", jobId], result);
      void queryClient.invalidateQueries({ queryKey: ["job-tree", jobId] });
      void queryClient.invalidateQueries({ queryKey: ["jobs"] });
    },
    // Completion may win the race; fetch the authoritative state instead of claiming it was canceled.
    onError: () =>
      void queryClient.invalidateQueries({ queryKey: ["job", jobId] }),
  });
  const job = useQuery({
    queryKey: ["job", jobId],
    queryFn: ({ signal }) => getJob(jobId, signal),
    // Terminal jobs stop polling; cached details can still be reopened from history.
    refetchInterval: (query) =>
      isActive(query.state.data?.status) ? 1500 : false,
  });
  // Running jobs poll only their lightweight summary; a terminal state enables the larger results query.
  const tree = useQuery({
    queryKey: ["job-tree", jobId],
    queryFn: ({ signal }) => getJobTree(jobId, signal),
    enabled: !!job.data && !isActive(job.data.status),
  });
  if (job.isPending) return <p role="status">Loading crawl…</p>;
  if (!job.data)
    return (
      <ErrorNotice
        error={job.error ?? new Error("Crawl unavailable.")}
        onRetry={() => void job.refetch()}
      />
    );
  const data = job.data;
  const progress = data.progress;
  const finished = progress.completed + progress.failed + progress.skipped;
  const percentage =
    progress.total === 0 ? 0 : (finished / progress.total) * 100;
  const requestError = cancellation.error ?? job.error;

  return (
    <div className="space-y-6">
      <div className="space-y-3">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-3xl font-semibold tracking-tight">
            Crawl details
          </h1>
          <StatusBadge status={data.status} />
        </div>
        <p className="break-all text-muted-foreground">{data.inputUrl}</p>
        <p className="font-mono text-xs break-all text-muted-foreground">
          Job {data.jobId}
        </p>
      </div>
      {requestError ? (
        <ErrorNotice
          error={requestError}
          onRetry={() => {
            cancellation.reset();
            void job.refetch();
          }}
        />
      ) : null}
      {data.failureReason ? (
        <ErrorNotice error={new Error(data.failureReason)} />
      ) : null}
      {isActive(data.status) ? (
        <div>
          <Button
            variant="outline"
            disabled={cancellation.isPending}
            onClick={() => cancellation.mutate()}
          >
            {cancellation.isPending ? "Canceling…" : "Cancel crawl"}
          </Button>
          <p className="mt-2 text-sm text-muted-foreground">
            Stops unfinished work and keeps pages already collected.
          </p>
        </div>
      ) : null}
      {data.status === "Canceled" ? (
        <p role="status" className="text-sm">
          Crawl canceled. Previously collected pages remain available;
          unfinished pages were canceled.
        </p>
      ) : null}
      <Card>
        <CardHeader>
          <CardTitle>
            <h2>Progress</h2>
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="space-y-2">
            <p className="text-sm" role="status">
              {finished} of {progress.total} discovered pages finished
            </p>
            <Progress value={percentage} aria-label="Crawl progress" />
          </div>
          <dl className="grid grid-cols-2 gap-4 text-sm sm:grid-cols-6">
            {(
              [
                ["Pending", progress.pending],
                ["Processing", progress.processing],
                ["Completed", progress.completed],
                ["Failed", progress.failed],
                ["Skipped", progress.skipped],
                ["Canceled", progress.canceled],
              ] as const
            ).map(([label, count]) => (
              <div key={label}>
                <dt className="text-muted-foreground">{label}</dt>
                <dd className="mt-1 text-lg font-semibold tabular-nums">
                  {count}
                </dd>
              </div>
            ))}
          </dl>
          <dl className="grid gap-4 border-t pt-4 text-sm sm:grid-cols-3">
            {(
              [
                ["Created", data.createdAt],
                ["Started", data.startedAt],
                ["Completed", data.completedAt],
              ] as const
            ).map(([label, value]) => (
              <div key={label}>
                <dt className="text-muted-foreground">{label}</dt>
                <dd className="mt-1">
                  {value ? (
                    <time dateTime={value}>{formatTime(value)}</time>
                  ) : (
                    "—"
                  )}
                </dd>
              </div>
            ))}
          </dl>
          <p className="text-xs text-muted-foreground">
            Maximum depth {data.maxDepth} · Page limit {data.maxPages}
            {progress.total >= data.maxPages ? " · Page limit reached" : ""}
          </p>
          {data.status === "Completed" && progress.failed > 0 ? (
            <p className="text-sm">
              Finished with {progress.failed} failed page
              {progress.failed === 1 ? "" : "s"}. Details are shown below.
            </p>
          ) : null}
        </CardContent>
      </Card>
      {!isActive(data.status) ? (
        <Card>
          <CardHeader>
            <CardTitle>
              <h2>Discovered pages</h2>
            </CardTitle>
            <p className="text-sm text-muted-foreground">
              Ratio = distinct internal links ÷ all distinct HTTP(S) links.
              Explore pages as a tree or an interactive discovery graph.
            </p>
          </CardHeader>
          <CardContent>
            {tree.isPending ? (
              <p role="status">Loading pages…</p>
            ) : tree.error ? (
              <ErrorNotice
                error={tree.error}
                onRetry={() => void tree.refetch()}
              />
            ) : (
              <Tabs defaultValue="tree" className="gap-4">
                <TabsList aria-label="Discovered pages view">
                  <TabsTrigger
                    value="tree"
                    className="px-4 data-[state=active]:bg-background data-[state=active]:text-foreground"
                  >
                    Tree
                  </TabsTrigger>
                  <TabsTrigger
                    value="graph"
                    className="px-4 data-[state=active]:bg-background data-[state=active]:text-foreground"
                  >
                    Graph
                  </TabsTrigger>
                </TabsList>
                <TabsContent value="tree">
                  <PageTree nodes={tree.data ?? []} />
                </TabsContent>
                <TabsContent value="graph">
                  <PageGraph pages={tree.data ?? []} />
                </TabsContent>
              </Tabs>
            )}
          </CardContent>
        </Card>
      ) : (
        <p className="text-sm text-muted-foreground">
          Crawling in the background. Results will appear here when finished.
        </p>
      )}
      <div className="flex gap-5 text-sm">
        <Link href="/" className="underline underline-offset-4">
          Start another crawl
        </Link>
        <Link href="/history" className="underline underline-offset-4">
          View history
        </Link>
      </div>
    </div>
  );
}
