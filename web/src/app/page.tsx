import { getVideoFeed } from "@/lib/api/videos";
import { VideoGrid } from "@/components/video/video-grid";

const PAGE_SIZE = 24;

export default async function Home() {
  const feed = await getVideoFeed(1, PAGE_SIZE);

  return (
    <main className="mx-auto w-full max-w-[1800px] px-6 py-6">
      <VideoGrid
        videos={feed.items}
        emptyMessage="No videos yet — upload one to see it here."
      />
    </main>
  );
}
