import { Badge } from "@/components/ui/badge";
import type { JobStatus, PageStatus } from "@/lib/api";

// Keep badge and graph colors together so each lifecycle state has one visual meaning.
// Labels remain visible, and both light/dark themes use contrasting text and backgrounds.
export const statusStyles: Record<JobStatus | PageStatus, { badge: string; dot: string }> = {
  Pending: {
    badge: "border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-800 dark:bg-amber-950 dark:text-amber-300",
    dot: "border-amber-600 bg-amber-500 dark:border-amber-400 dark:bg-amber-400",
  },
  Running: {
    badge: "border-blue-200 bg-blue-50 text-blue-800 dark:border-blue-800 dark:bg-blue-950 dark:text-blue-300",
    dot: "border-blue-600 bg-blue-500 dark:border-blue-400 dark:bg-blue-400",
  },
  Processing: {
    badge: "border-blue-200 bg-blue-50 text-blue-800 dark:border-blue-800 dark:bg-blue-950 dark:text-blue-300",
    dot: "border-blue-600 bg-blue-500 dark:border-blue-400 dark:bg-blue-400",
  },
  Completed: {
    badge: "border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-800 dark:bg-emerald-950 dark:text-emerald-300",
    dot: "border-emerald-600 bg-emerald-500 dark:border-emerald-400 dark:bg-emerald-400",
  },
  Failed: {
    badge: "border-red-200 bg-red-50 text-red-800 dark:border-red-800 dark:bg-red-950 dark:text-red-300",
    dot: "border-red-600 bg-red-500 dark:border-red-400 dark:bg-red-400",
  },
  Skipped: {
    badge: "border-orange-200 bg-orange-50 text-orange-800 dark:border-orange-800 dark:bg-orange-950 dark:text-orange-300",
    dot: "border-orange-600 bg-orange-500 dark:border-orange-400 dark:bg-orange-400",
  },
  Canceled: {
    badge: "border-slate-200 bg-slate-100 text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300",
    dot: "border-slate-500 bg-slate-400 dark:border-slate-400 dark:bg-slate-400",
  },
};

/** Applies the shared lifecycle colors everywhere a job or page status is displayed. */
export function StatusBadge({ status }: { status: JobStatus | PageStatus }) {
  return <Badge variant="outline" className={statusStyles[status].badge}>{status}</Badge>;
}
