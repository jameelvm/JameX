/**
 * Must match `HeaderCurrentUser.HeaderName` on the backend
 * (`JameX.ServiceDefaults`) exactly — this is how every service recognises
 * the caller, in lieu of a real auth session.
 */
export const VIEWER_HEADER_NAME = "X-JameX-User";
