/**
 * The current browser's stand-in identity. There is no login system in this
 * project — auth is explicitly out of scope (see `CLAUDE.md`) — so this is
 * not "the logged-in user" in the usual sense, just a real Identity-service
 * user id this browser was assigned once and keeps reusing, sent as
 * `X-JameX-User` on interactive calls the same way any authenticated
 * request would be.
 */
export interface Viewer {
  userId: string;
  displayName: string;
}
