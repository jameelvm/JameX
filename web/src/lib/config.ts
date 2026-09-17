/**
 * Server-side only — deliberately not `NEXT_PUBLIC_*`. Used by Server
 * Component fetches (see `lib/api/client.ts`). In a real deployment this
 * could point at an internal address the browser cannot reach at all; today,
 * running the frontend natively against the same compose stack, it happens
 * to match {@link publicGatewayBaseUrl}.
 */
export const gatewayBaseUrl =
  process.env.GATEWAY_BASE_URL ?? "http://localhost:8080/api";

/**
 * Ships into the browser bundle — the only reason this needs the
 * `NEXT_PUBLIC_` prefix (see `lib/api/browser-client.ts`). Used by client
 * components that must call the Gateway directly, such as the viewer-identity
 * stub, before there is a session to render from the server.
 */
export const publicGatewayBaseUrl =
  process.env.NEXT_PUBLIC_GATEWAY_BASE_URL ?? "http://localhost:8080/api";
