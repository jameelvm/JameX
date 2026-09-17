import { apiFetch } from "@/lib/api/client";
import type { Comment } from "@/types/comment";
import type { PagedResult } from "@/types/api";

/**
 * The first page of top-level comments, fetched server-side alongside the
 * rest of the watch page — a viewer should see comments on first paint, not
 * wait for a client-side round trip after hydration. Everything past this
 * (more pages, replies, posting, editing, deleting) is necessarily
 * client-side interaction — see `lib/api/comments-client.ts`.
 */
export function getComments(
  videoId: string,
  page = 1,
  pageSize = 20,
): Promise<PagedResult<Comment>> {
  return apiFetch<PagedResult<Comment>>(
    `/videos/${videoId}/comments?page=${page}&pageSize=${pageSize}`,
    { cache: "no-store" },
  );
}
