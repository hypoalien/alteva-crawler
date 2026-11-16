import type { Metadata } from "next";
import type { ReactNode } from "react";
import Link from "next/link";
import { QueryProvider } from "@/components/query-provider";
import "./globals.css";

export const metadata: Metadata = {
  title: "Alteva Crawler",
  description: "Explore website pages, links, and domain link ratios.",
};

/**
 * Wraps every route with the common HTML shell, navigation, and shared query cache. Route
 * content arrives through children.
 */
export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body>
        <QueryProvider>
          <div className="mx-auto min-h-screen max-w-5xl px-6">
            <header className="flex items-center justify-between gap-4 border-b py-6">
              <Link href="/" className="font-semibold">
                Alteva Crawler
              </Link>
              <nav aria-label="Primary navigation">
                <Link
                  href="/history"
                  className="text-sm underline-offset-4 hover:underline"
                >
                  History
                </Link>
              </nav>
            </header>
            <main className="py-10 sm:py-14">{children}</main>
          </div>
        </QueryProvider>
      </body>
    </html>
  );
}
