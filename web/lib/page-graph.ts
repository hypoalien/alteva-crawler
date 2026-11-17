import type { Edge, Node } from "@xyflow/react";
import type { PageTreeNode } from "@/lib/api";

export type CrawlGraphNode = Node<
  {
    page: PageTreeNode;
    inspect: (page: PageTreeNode) => void;
  },
  "page"
>;

/**
 * Places the saved discovery tree on depth rings. Descendant leaf counts allocate each branch
 * its angular space; every API page becomes one node and every parent/child pair one edge.
 * This is a visual layout only: it neither invents outgoing links nor schedules crawl work.
 */
export function buildPageGraph(
  pages: PageTreeNode[],
  inspect: (page: PageTreeNode) => void,
) {
  const nodes: CrawlGraphNode[] = [];
  const edges: Edge[] = [];
  const weights = new Map<string, number>();
  let maxLevel = 0;
  const rootLevel = pages.length > 1 ? 1 : 0;

  /** Counts leaves once so branch spacing stays linear in the number of discovered pages. */
  function measure(page: PageTreeNode, level: number): number {
    maxLevel = Math.max(maxLevel, level);
    const weight = Math.max(
      1,
      page.children.reduce((sum, child) => sum + measure(child, level + 1), 0),
    );
    weights.set(page.url, weight);
    return weight;
  }

  const totalWeight = pages.reduce(
    (sum, page) => sum + measure(page, rootLevel),
    0,
  );
  const radii: number[] = [0];
  for (let level = 1; level <= maxLevel; level++) {
    // A branch's sector can be narrow even on a sparse ring. Reserve space for the smallest
    // leaf-sized sector so neighboring hit targets never cover another page's center.
    radii[level] = Math.max(
      radii[level - 1] + 160,
      (totalWeight * 48) / (2 * Math.PI),
    );
  }

  /** Keeps each subtree in its own sector and connects it to its actual saved parent. */
  function place(
    page: PageTreeNode,
    level: number,
    start: number,
    sweep: number,
    parent?: string,
  ) {
    const angle = start + sweep / 2;
    const radius = radii[level];
    nodes.push({
      id: page.url,
      type: "page",
      position: { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius },
      data: { page, inspect },
      ariaLabel: `${page.url}, ${page.status}, depth ${page.depth}`,
    });
    if (parent)
      edges.push({
        id: JSON.stringify([parent, page.url]),
        source: parent,
        target: page.url,
      });
    let cursor = start;
    for (const child of page.children) {
      const childSweep =
        (sweep * weights.get(child.url)!) / weights.get(page.url)!;
      place(child, level + 1, cursor, childSweep, page.url);
      cursor += childSweep;
    }
  }

  let cursor = -Math.PI / 2;
  for (const page of pages) {
    const sweep = (2 * Math.PI * weights.get(page.url)!) / totalWeight;
    place(page, rootLevel, cursor, sweep);
    cursor += sweep;
  }
  return { nodes, edges };
}
