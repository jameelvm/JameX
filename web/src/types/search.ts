/** One row of a search result — see Search's `GET /search`. Not paginated on the wire, unlike everything else, so no `PagedResult` wrapper here. */
export interface SearchHit {
  videoId: string;
  title: string;
  thumbnailUrl: string | null;
  durationSeconds: number;
  publishedAt: string | null;
  score: number;
  matchedOn: string;
}
