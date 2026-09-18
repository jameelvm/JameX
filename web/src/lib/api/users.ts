import { browserApiFetch } from "@/lib/api/browser-client";
import type { Viewer } from "@/types/viewer";

/** Shape of `UserDto` on the wire — see `JameX.Contracts.Dtos`. Only the fields the frontend needs. */
interface UserDto {
  userId: string;
  displayName: string;
}

/** Shape of Identity's `AuthResponse` on the wire — see `JameX.Identity.Contracts`. */
interface AuthResponseDto {
  token: string;
  user: UserDto;
}

/**
 * Registers a real account. Distinct from {@link logIn}: signup and login
 * share an email and password, but only signup also carries a display name
 * — see the identical remark on the backend's `LoginRequest`.
 * <para>
 * Deliberately does **not** log the new account in — Identity's `POST
 * /users` returns a plain `UserDto`, not a token, the same way a real
 * registration form usually asks you to log in as a separate step
 * afterwards rather than assuming the two are one action.
 * </para>
 */
export function signUp(
  email: string,
  displayName: string,
  password: string,
): Promise<UserDto> {
  return browserApiFetch<UserDto>("/users", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, displayName, password }),
  });
}

/** The one credential exchange in the system — see `UsersController.Login` on the backend. */
export async function logIn(email: string, password: string): Promise<Viewer> {
  const response = await browserApiFetch<AuthResponseDto>("/users/login", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });

  return {
    userId: response.user.userId,
    displayName: response.user.displayName,
    token: response.token,
  };
}
