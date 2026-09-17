import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  images: {
    remotePatterns: [
      {
        // The nginx edge/CDN tier — must match Storage:CdnBaseUrl in every
        // backend service's config. Thumbnails and posters are served from
        // here, never from the app's own origin.
        protocol: "http",
        hostname: "localhost",
        port: "8090",
        pathname: "/media/**",
      },
    ],
  },
};

export default nextConfig;
