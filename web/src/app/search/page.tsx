import type { Metadata } from "next";

import { searchVideos } from "@/lib/api/search";
import { VideoGrid } from "@/components/video/video-grid";

interface SearchPageProps {
  searchParams: Promise<{ q?: string }>;
}

export async function generateMetadata({
  searchParams,
}: SearchPageProps): Promise<Metadata> {
  const { q } = await searchParams;
  return { title: q ? `${q} — search` : "Search" };
}

export default async function SearchPage({ searchParams }: SearchPageProps) {
  const { q } = await searchParams;
  const query = q?.trim() ?? "";
  const hits = query ? await searchVideos(query) : [];

  return (
    <main className="mx-auto w-full max-w-[1800px] px-6 py-6">
      <p className="mb-4 text-sm text-neutral-600">
        {query
          ? `Search results for "${query}"`
          : "Type something in the search bar above."}
      </p>
      <VideoGrid
        videos={hits.map((hit) => ({
          videoId: hit.videoId,
          title: hit.title,
          thumbnailUrl: hit.thumbnailUrl,
          durationSeconds: hit.durationSeconds,
          // The inverted index carries no channelId (see `SearchHit`), so a
          // search card never has a channel name to show — an honest gap,
          // not a fabricated one.
          channelName: null,
          publishedAt: hit.publishedAt,
          viewCount: null,
        }))}
        emptyMessage={
          query ? "No videos matched your search." : "Nothing to show yet."
        }
      />
    </main>
  );
}
