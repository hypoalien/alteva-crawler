import { Badge } from "@/components/ui/badge";
import type { JobStatus, PageStatus } from "@/lib/api";

/**
 * Maps job/page lifecycle states onto a shared visual badge: failure is destructive, completion
 * is prominent, and other states use a neutral style.
 */
export function StatusBadge({ status }: { status: JobStatus | PageStatus }) {
  const variant =
    status === "Failed"
      ? "destructive"
      : status === "Completed"
        ? "default"
        : "secondary";
  return <Badge variant={variant}>{status}</Badge>;
}
