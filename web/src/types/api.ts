/** Mirrors `JameX.Contracts.Dtos.PagedResult<T>` — every paginated list endpoint uses this envelope. */
export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  hasMore: boolean;
}
