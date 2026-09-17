import { browserApiFetch } from "@/lib/api/browser-client";
import type { Viewer } from "@/types/viewer";

/** Shape of `UserDto` on the wire — see `JameX.Contracts.Dtos`. Only the fields the viewer stub needs. */
interface UserDto {
  userId: string;
  displayName: string;
}

/**
 * Provisions a real Identity user to back a guest browser identity. Called
 * from the browser (see `ViewerProvider`), so this goes through
 * `browserApiFetch`, not the server-side client. `email` only exists because
 * Identity's schema requires one and enforces uniqueness — nothing here ever
 * reads it back; the viewer is identified by `userId` alone from this point
 * on.
 */
export async function createGuestUser(
  displayName: string,
  email: string,
): Promise<Viewer> {
  const user = await browserApiFetch<UserDto>("/users", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, displayName }),
  });

  return { userId: user.userId, displayName: user.displayName };
}
