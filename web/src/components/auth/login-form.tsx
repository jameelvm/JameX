"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";

import { useViewer } from "@/components/viewer/viewer-provider";
import { ApiError } from "@/lib/api/browser-client";

/**
 * Real credentials against Identity's real login endpoint — see
 * `UsersController.Login` on the backend. There is deliberately one error
 * message for every failure reason (`ApiError` with status 401): telling an
 * attacker "no such email" versus "wrong password" narrows their search for
 * free, and a real user gets the same actionable advice either way.
 */
export function LoginForm() {
  const router = useRouter();
  const { login } = useViewer();

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setIsSubmitting(true);
    setError(null);

    try {
      await login(email, password);
      router.push("/");
    } catch (caught) {
      setError(
        caught instanceof ApiError && caught.status === 401
          ? "Incorrect email or password."
          : "Something went wrong. Try again.",
      );
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4">
      {error && <p className="text-sm text-red-600">{error}</p>}

      <div className="flex flex-col gap-1">
        <label htmlFor="login-email" className="text-sm text-neutral-600">
          Email
        </label>
        <input
          id="login-email"
          type="email"
          required
          autoComplete="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="login-password" className="text-sm text-neutral-600">
          Password
        </label>
        <input
          id="login-password"
          type="password"
          required
          autoComplete="current-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
        />
      </div>

      <button
        type="submit"
        disabled={isSubmitting}
        className="self-start rounded-full bg-blue-600 px-5 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-neutral-200 disabled:text-neutral-400"
      >
        {isSubmitting ? "Signing in…" : "Sign in"}
      </button>

      <p className="text-sm text-neutral-600">
        No account?{" "}
        <Link href="/signup" className="font-medium text-blue-700 hover:text-blue-800">
          Sign up
        </Link>
      </p>
    </form>
  );
}
