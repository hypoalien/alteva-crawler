"use client";

import { memo, useMemo, useState } from "react";
import {
  Background,
  Controls,
  Handle,
  Position,
  ReactFlow,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { Button } from "@/components/ui/button";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { StatusBadge } from "@/components/status-badge";
import type { PageStatus, PageTreeNode } from "@/lib/api";
import { buildPageGraph, type CrawlGraphNode } from "@/lib/page-graph";
import { cn } from "@/lib/utils";

/** Uses the same neutral/completed and red/failed vocabulary as the page status badges. */
function nodeColor(status: PageStatus) {
  if (status === "Failed") return "border-destructive bg-destructive";
  if (status === "Completed") return "border-foreground bg-foreground";
  return "border-muted-foreground bg-background";
}

/** A keyboard-focusable page with a full-URL tooltip; selecting it pins details below the graph. */
const PageNode = memo(function PageNode({
  data,
  selected,
}: NodeProps<CrawlGraphNode>) {
  const { page, inspect } = data;
  return (
    <>
      <Handle
        type="target"
        position={Position.Left}
        isConnectable={false}
        className="!pointer-events-none !left-1/2 !top-1/2 !opacity-0"
      />
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="ghost"
            size="icon"
            className="nodrag nopan size-10 rounded-full hover:bg-transparent"
            aria-label={`Inspect ${page.url}`}
            aria-pressed={selected}
            onClick={() => inspect(page)}
            onFocus={() => inspect(page)}
          >
            <span
              className={cn(
                "block size-5 rounded-full border-2 transition-shadow motion-reduce:transition-none",
                page.depth === 0 &&
                  "size-8 border-4 border-background ring-2 ring-foreground",
                nodeColor(page.status),
                selected && "outline-2 outline-offset-4 outline-ring",
              )}
            />
          </Button>
        </TooltipTrigger>
        <TooltipContent
          side="top"
          sideOffset={8}
          className="block max-w-[min(24rem,calc(100vw-2rem))] space-y-1 p-3"
        >
          <p className="break-all font-medium">{page.url}</p>
          <p>
            {page.status} · Depth {page.depth} · Ratio{" "}
            {page.linkRatio?.toFixed(6) ?? "—"}
          </p>
        </TooltipContent>
      </Tooltip>
      <Handle
        type="source"
        position={Position.Right}
        isConnectable={false}
        className="!pointer-events-none !left-1/2 !top-1/2 !opacity-0"
      />
    </>
  );
});

const nodeTypes = { page: PageNode };
const edgeOptions = {
  type: "straight",
  style: { stroke: "var(--muted-foreground)", strokeWidth: 1, opacity: 0.4 },
};

/** Shows the complete saved discovery hierarchy with pan/zoom and touch/keyboard page inspection. */
export function PageGraph({ pages }: { pages: PageTreeNode[] }) {
  const [selectedPage, setSelectedPage] = useState<PageTreeNode | null>(null);
  const graph = useMemo(() => buildPageGraph(pages, setSelectedPage), [pages]);
  // Resolve selection against current results so a refreshed tree never shows stale page metadata.
  const current =
    graph.nodes.find((node) => node.id === selectedPage?.url)?.data.page ??
    pages[0];
  const nodes = useMemo(
    () =>
      graph.nodes.map((node) => ({
        ...node,
        selected: node.id === current?.url,
      })),
    [graph.nodes, current?.url],
  );

  if (!pages.length)
    return (
      <p className="text-sm text-muted-foreground">No pages discovered.</p>
    );

  return (
    <div className="overflow-hidden rounded-lg border">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-3 text-xs text-muted-foreground">
        <span>
          {nodes.length} pages · {graph.edges.length} discovery connections
        </span>
        <div
          className="flex items-center gap-3"
          aria-label="Page status legend"
        >
          {(["Completed", "Failed", "Other"] as const).map((status) => (
            <span key={status} className="flex items-center gap-1.5">
              <span
                aria-hidden="true"
                className={cn(
                  "size-2 rounded-full border",
                  nodeColor(status === "Other" ? "Skipped" : status),
                )}
              />
              {status}
            </span>
          ))}
        </div>
      </div>
      <div
        className="page-graph h-[400px] bg-muted/20 sm:h-[480px]"
        aria-label="Page discovery graph"
      >
        <TooltipProvider delayDuration={150}>
          <ReactFlow<CrawlGraphNode>
            nodes={nodes}
            edges={graph.edges}
            nodeTypes={nodeTypes}
            defaultEdgeOptions={edgeOptions}
            nodeOrigin={[0.5, 0.5]}
            nodesDraggable={false}
            nodesConnectable={false}
            nodesFocusable={false}
            edgesFocusable={false}
            elementsSelectable={false}
            onNodeClick={(_, node) => setSelectedPage(node.data.page)}
            deleteKeyCode={null}
            minZoom={0.01}
            maxZoom={3}
            fitView
            fitViewOptions={{ padding: 0.2, minZoom: 0.01, maxZoom: 1.25 }}
            zoomOnScroll
            preventScrolling
          >
            <Background gap={24} size={1} color="var(--border)" />
            <Controls showInteractive={false} />
          </ReactFlow>
        </TooltipProvider>
      </div>
      <div className="space-y-3 border-t p-4">
        <p className="text-xs text-muted-foreground">
          Drag to pan. Scroll, pinch, or use + / − to zoom. Hover for a URL; select a
          page for details. Connections show which page discovered each URL. The
          larger node is the starting page.
        </p>
        {current ? (
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <StatusBadge status={current.status} />
              <span className="text-xs text-muted-foreground">
                Depth {current.depth}
                {current.httpStatus !== null
                  ? ` · HTTP ${current.httpStatus}`
                  : ""}
              </span>
            </div>
            <a
              href={current.url}
              target="_blank"
              rel="noopener noreferrer"
              className="block break-all text-sm font-medium underline underline-offset-4"
            >
              {current.url}
            </a>
            {current.finalUrl && current.finalUrl !== current.url ? (
              <p className="break-all text-xs text-muted-foreground">
                Redirected to {current.finalUrl}
              </p>
            ) : null}
            <p className="text-xs text-muted-foreground">
              Ratio {current.linkRatio?.toFixed(6) ?? "—"} ·{" "}
              {current.internalLinkCount}/{current.totalLinkCount} links
              internal
            </p>
            {current.error ? (
              <p className="break-all text-xs text-destructive">
                {current.error}
              </p>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  );
}
