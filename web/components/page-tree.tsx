import { StatusBadge } from "@/components/status-badge";
import type { PageTreeNode } from "@/lib/api";

/**
 * Displays one page's requested/final URL, status, depth, HTTP outcome, ratio, and error. This
 * presentation component does not fetch data or follow links.
 */
function PageRow({ node }: { node: PageTreeNode }) {
  return (
    <span className="block min-w-0 flex-1 space-y-2 py-2">
      <span className="text-sm font-medium break-all">{node.url}</span>
      {node.finalUrl && node.finalUrl !== node.url ? (
        <span className="block text-xs break-all text-muted-foreground">
          Redirected to {node.finalUrl}
        </span>
      ) : null}
      <span className="flex flex-wrap items-center gap-x-3 gap-y-2 text-xs text-muted-foreground">
        <StatusBadge status={node.status} />
        <span>Depth {node.depth}</span>
        {node.httpStatus !== null ? <span>HTTP {node.httpStatus}</span> : null}
        <span>
          Ratio {node.linkRatio === null ? "—" : node.linkRatio.toFixed(6)} ·{" "}
          {node.internalLinkCount}/{node.totalLinkCount} links internal
        </span>
      </span>
      {node.error ? (
        <span className="block text-xs text-destructive">{node.error}</span>
      ) : null}
    </span>
  );
}

/**
 * Recursively renders the saved parent/child hierarchy with native expandable details. Expands
 * the root initially and respects the supported depth/page bounds.
 */
function TreeNode({ node, level }: { node: PageTreeNode; level: number }) {
  if (level > 5) return null;
  return (
    <li className="min-w-0">
      {node.children.length > 0 && level < 5 ? (
        <details open={level === 0} className="[&[open]>summary>svg]:rotate-90">
          <summary className="flex cursor-pointer list-none items-start gap-2 rounded-md px-2 hover:bg-muted/50 focus-visible:outline-2 focus-visible:outline-ring [&::-webkit-details-marker]:hidden">
            <svg
              viewBox="0 0 16 16"
              fill="none"
              aria-hidden="true"
              className="mt-3 size-4 shrink-0 text-muted-foreground transition-transform motion-reduce:transition-none"
            >
              <path
                d="m6 4 4 4-4 4"
                stroke="currentColor"
                strokeWidth="1.5"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
            <PageRow node={node} />
          </summary>
          <ul className="ml-3 space-y-2 border-l pl-3 sm:ml-5 sm:pl-5">
            {node.children.slice(0, 1000).map((child) => (
              <TreeNode key={child.url} node={child} level={level + 1} />
            ))}
          </ul>
        </details>
      ) : (
        <div className="pl-8 pr-2">
          <PageRow node={node} />
        </div>
      )}
    </li>
  );
}

/**
 * Renders the root nodes returned by the API, or an explicit empty state. Each page URL is
 * unique within the job and supplies its React key.
 */
export function PageTree({ nodes }: { nodes: PageTreeNode[] }) {
  if (nodes.length === 0)
    return (
      <p className="text-sm text-muted-foreground">No pages discovered.</p>
    );
  return (
    <ul className="space-y-3">
      {nodes.map((node) => (
        <TreeNode key={node.url} node={node} level={0} />
      ))}
    </ul>
  );
}
