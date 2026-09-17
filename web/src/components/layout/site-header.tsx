"use client";

import Link from "next/link";

import { useViewer } from "@/components/viewer/viewer-provider";

export function SiteHeader() {
  const { viewer, isLoading, error, resetViewer } = useViewer();

  return (
    <header className="sticky top-0 z-10 flex items-center gap-4 border-b border-neutral-200 bg-white px-4 py-2.5 sm:px-6">
      <Link href="/" className="shrink-0 text-xl font-bold tracking-tight">
        <span className="text-red-600">Jame</span>
        <span className="text-neutral-900">X</span>
      </Link>

      <form action="/search" className="mx-auto flex w-full max-w-xl flex-1">
        <input
          type="search"
          name="q"
          placeholder="Search"
          className="w-full rounded-l-full border border-neutral-300 px-4 py-1.5 text-sm text-neutral-900 focus:border-blue-500 focus:outline-none"
        />
        <button
          type="submit"
          aria-label="Search"
          className="flex items-center justify-center rounded-r-full border border-l-0 border-neutral-300 bg-neutral-50 px-4 hover:bg-neutral-100"
        >
          <svg
            aria-hidden
            viewBox="0 0 24 24"
            className="h-4.5 w-4.5 fill-none stroke-neutral-700 stroke-2"
          >
            <circle cx="11" cy="11" r="7" />
            <line x1="21" y1="21" x2="16.65" y2="16.65" />
          </svg>
        </button>
      </form>

      <div className="flex shrink-0 items-center gap-4 text-sm text-neutral-700">
        <Link
          href="/upload"
          className="rounded-full px-3 py-1.5 font-medium hover:bg-neutral-100"
        >
          Upload
        </Link>
        {isLoading && <span className="text-neutral-500">Signing in…</span>}
        {!isLoading && Boolean(error) && (
          <span className="text-neutral-500">Couldn&apos;t sign in</span>
        )}
        {!isLoading && viewer && (
          <button
            type="button"
            onClick={resetViewer}
            title="Switch to a new guest identity"
            className="rounded-full px-3 py-1.5 font-medium hover:bg-neutral-100"
          >
            {viewer.displayName}
          </button>
        )}
      </div>
    </header>
  );
}
