"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";

/**
 * Creates one stable TanStack Query client for this mounted app. Children share its cache,
 * request deduplication, and conservative read retry defaults.
 */
export function QueryProvider({ children }: { children: ReactNode }) {
  // Lazy state initialization prevents every render from creating a new cache and losing query state.
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: { retry: 1, staleTime: 1000, refetchOnWindowFocus: false },
        },
      }),
  );
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
