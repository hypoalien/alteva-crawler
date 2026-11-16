import { Button } from "@/components/ui/button";

/**
 * Renders a readable, announced error and an optional retry action. The caller decides whether
 * retry refreshes a query or repeats a user action.
 */
export function ErrorNotice({
  error,
  onRetry,
}: {
  error: Error;
  onRetry?: () => void;
}) {
  return (
    <div className="space-y-3 rounded-lg border border-destructive/30 p-4">
      <p role="alert" className="text-sm text-destructive">
        {error.message}
      </p>
      {onRetry ? (
        <Button variant="outline" size="sm" onClick={onRetry}>
          Try again
        </Button>
      ) : null}
    </div>
  );
}
