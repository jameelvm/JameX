import type { Metadata } from "next";
import { Geist_Mono, Roboto } from "next/font/google";

import { SiteHeader } from "@/components/layout/site-header";
import { ViewerProvider } from "@/components/viewer/viewer-provider";
import "./globals.css";

// Roboto, not Geist — the actual typeface real video-platform chrome uses.
const roboto = Roboto({
  variable: "--font-roboto",
  subsets: ["latin"],
  weight: ["400", "500", "700"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: {
    default: "JameX",
    template: "%s · JameX",
  },
  description: "A YouTube clone built to internalise system design in practice.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      className={`${roboto.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="flex min-h-full flex-col bg-white text-neutral-900">
        <ViewerProvider>
          <SiteHeader />
          {children}
        </ViewerProvider>
      </body>
    </html>
  );
}
