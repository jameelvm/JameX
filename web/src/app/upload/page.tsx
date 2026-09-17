import type { Metadata } from "next";

import { UploadPageClient } from "@/components/upload/upload-page-client";

export const metadata: Metadata = { title: "Upload" };

export default function UploadPage() {
  return (
    <div className="mx-auto max-w-2xl px-4 py-8">
      <h1 className="mb-6 text-xl font-semibold text-neutral-900">
        Upload a video
      </h1>
      <UploadPageClient />
    </div>
  );
}
