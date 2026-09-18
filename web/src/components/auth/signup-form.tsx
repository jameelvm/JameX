"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";

import { useViewer } from "@/components/viewer/viewer-provider";
import { ApiError } from "@/lib/api/browser-client";

/** Real registration against Identity — see `UsersController.Create` on the backend. Signs the new account straight in afterward; see `ViewerProvider.signUp`'s remark on why that's two backend calls. */
export function SignupForm() {
  const router = useRouter();
  const { signUp } = useViewer();

  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setIsSubmitting(true);
    setError(null);

    try {
      await signUp(email, displayName, password);
      router.push("/");
    } catch (caught) {
      setError(
        caught instanceof ApiError && caught.status === 409
          ? "That email address is already registered."
          : caught instanceof ApiError && caught.status === 400
            ? "Check your email, name, and password (at least 8 characters)."
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
        <label htmlFor="signup-name" className="text-sm text-neutral-600">
          Name
        </label>
        <input
          id="signup-name"
          required
          autoComplete="name"
          maxLength={100}
          value={displayName}
          onChange={(event) => setDisplayName(event.target.value)}
          className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="signup-email" className="text-sm text-neutral-600">
          Email
        </label>
        <input
          id="signup-email"
          type="email"
          required
          autoComplete="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="signup-password" className="text-sm text-neutral-600">
          Password
        </label>
        <input
          id="signup-password"
          type="password"
          required
          minLength={8}
          autoComplete="new-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
        />
        <span className="text-xs text-neutral-500">At least 8 characters.</span>
      </div>

      <button
        type="submit"
        disabled={isSubmitting}
        className="self-start rounded-full bg-blue-600 px-5 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-neutral-200 disabled:text-neutral-400"
      >
        {isSubmitting ? "Creating account…" : "Sign up"}
      </button>

      <p className="text-sm text-neutral-600">
        Already have an account?{" "}
        <Link href="/login" className="font-medium text-blue-700 hover:text-blue-800">
          Sign in
        </Link>
      </p>
    </form>
  );
}
