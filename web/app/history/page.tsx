import { Suspense } from "react";
import { HistoryList } from "@/components/history-list";

/**
 * Renders the history heading and a loading boundary around HistoryList, whose pagination comes
 * from the browser URL.
 */
export default function HistoryPage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-semibold tracking-tight">Crawl history</h1>
        <p className="mt-2 text-muted-foreground">
          Previous crawls, newest first.
        </p>
      </div>
      <Suspense fallback={<p role="status">Loading history…</p>}>
        <HistoryList />
      </Suspense>
    </div>
  );
}
