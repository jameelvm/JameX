import { apiFetch } from "@/lib/api/client";
import type { SearchHit } from "@/types/search";

/**
 * See Search's `GET /search` — an AND-of-terms query over the DynamoDB
 * inverted index, hydrated with Catalog data server-side already (title,
 * thumbnail, duration). Unlike the home feed, `SearchHit` carries no
 * `channelId`, so results render without a channel name — an honest gap in
 * the wire contract, not something to paper over with an extra per-hit
 * fetch of the full video record just to get one field.
 */
export function searchVideos(query: string): Promise<SearchHit[]> {
  return apiFetch<SearchHit[]>(`/search?q=${encodeURIComponent(query)}`, {
    cache: "no-store",
  });
}
