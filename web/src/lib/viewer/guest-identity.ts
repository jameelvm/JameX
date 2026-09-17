/**
 * A short, human-friendly label — "Guest4821" — never persisted server-side
 * (Identity's `DisplayName` column is a real field, but nothing downstream
 * needs this specific string to mean anything beyond "distinguish one guest
 * from another in the UI").
 */
export function generateGuestDisplayName(): string {
  const suffix = Math.floor(1000 + Math.random() * 9000);
  return `Guest${suffix}`;
}

/**
 * Identity requires a unique email per user and this browser has none to
 * offer, so one is manufactured. The `.invalid` TLD is reserved by RFC 2606
 * specifically for addresses that are never meant to receive mail — the
 * honest choice here, since nothing ever sends to it.
 */
export function generateGuestEmail(): string {
  return `guest-${crypto.randomUUID()}@jamex.invalid`;
}
