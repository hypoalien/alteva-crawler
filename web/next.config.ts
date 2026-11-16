import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  turbopack: {
    root: __dirname,
  },
  /**
   * Forwards browser /api requests to .NET at build time. Keeping requests same-origin avoids
   * making the browser know Docker service names or requiring cross-origin API setup.
   */
  async rewrites() {
    // Rewrites are compiled at build time. The browser always uses its own /api origin.
    const api = (process.env.API_BASE_URL ?? "http://localhost:5080").replace(/\/$/, "");
    return [{ source: "/api/:path*", destination: `${api}/api/:path*` }];
  },
};

export default nextConfig;
