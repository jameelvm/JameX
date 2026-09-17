import Link from "next/link";

/**
 * Next.js's built-in 404 fallback renders outside this app's own styling, so
 * without this file `notFound()` (e.g. from the watch page, for a video that
 * does not exist or was deleted) drops the viewer onto a page with none of
 * this app's own typography or spacing — jarring, not just wrong-coloured.
 */
export default function NotFound() {
  return (
    <main className="mx-auto flex max-w-2xl flex-1 flex-col items-center justify-center gap-2 px-4 text-center">
      <h1 className="text-2xl font-semibold text-neutral-900">
        Video not found
      </h1>
      <p className="text-neutral-600">
        It may have been removed, or the link is incorrect.
      </p>
      <Link href="/" className="mt-2 text-sm text-blue-700 underline">
        Back home
      </Link>
    </main>
  );
}
