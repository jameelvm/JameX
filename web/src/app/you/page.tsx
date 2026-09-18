import Link from "next/link";
import type { Metadata } from "next";

import { ApiError } from "@/lib/api/errors";
import { getMyVideos } from "@/lib/api/my-videos";
import { getServerAuthToken } from "@/lib/viewer/server-viewer";
import { VideoGrid } from "@/components/video/video-grid";
import type { PagedResult } from "@/types/api";
import type { VideoSummary } from "@/types/video";

export const metadata: Metadata = { title: "Your videos" };

function SignInPrompt() {
  return (
    <main className="mx-auto flex max-w-2xl flex-1 flex-col items-center justify-center gap-3 px-4 py-16 text-center">
      <h1 className="text-xl font-semibold text-neutral-900">Your videos</h1>
      <p className="text-sm text-neutral-600">Sign in to see the videos you&apos;ve uploaded.</p>
      <Link
        href="/login"
        className="rounded-full bg-blue-600 px-5 py-2 text-sm font-medium text-white hover:bg-blue-700"
      >
        Sign in
      </Link>
    </main>
  );
}

export default async function YourVideosPage() {
  const token = await getServerAuthToken();
  if (!token) return <SignInPrompt />;

  let feed: PagedResult<VideoSummary>;
  try {
    feed = await getMyVideos(token);
  } catch (error) {
    // An expired or otherwise rejected token cookie is indistinguishable
    // from "not signed in" here — a Server Component can't clear the
    // cookie mid-render, so the client-side session naturally sorts itself
    // out the next time `ViewerProvider` makes a call of its own and gets
    // the same 401 back.
    if (error instanceof ApiError && error.status === 401) return <SignInPrompt />;
    throw error;
  }

  return (
    <main className="mx-auto w-full max-w-[1800px] px-6 py-6">
      <h1 className="mb-4 text-xl font-semibold text-neutral-900">Your videos</h1>
      <VideoGrid
        videos={feed.items.map((video) => ({
          videoId: video.videoId,
          title: video.title,
          thumbnailUrl: video.thumbnailUrl,
          durationSeconds: video.durationSeconds,
          channelName: null,
          hideChannel: true,
          publishedAt: video.publishedAt,
          viewCount: video.viewCount,
          status: video.status,
        }))}
        emptyMessage="You haven't uploaded any videos yet."
      />
    </main>
  );
}
