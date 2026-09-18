/**
 * The current browser's signed-in identity — a real Identity-service account,
 * reached via a real password login, not a stand-in. `token` is the JWT
 * Identity issued at login; every authenticated call attaches it as
 * `Authorization: Bearer <token>` (see `lib/api/browser-client.ts`), and the
 * Gateway is the only party that ever validates it — see `ICurrentUser`'s
 * remarks on the backend for why individual services never need to.
 *
 * `null` is a first-class state here, not a loading placeholder: unlike the
 * old guest-auto-provisioning stub, a viewer can genuinely be signed out, and
 * that is exactly what lets anonymous browsing exist as an intentional mode
 * rather than a race before a guest account finishes being created.
 */
export interface Viewer {
  userId: string;
  displayName: string;
  token: string;
}
