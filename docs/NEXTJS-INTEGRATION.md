# Next.js Integration

Use `examples/nextjs-client/thai-id-agent-client.ts` from client components or server actions that can obtain a short-lived token.

Do not store card data, tokens, or reader responses in `localStorage` or `sessionStorage`. Do not place card data in URLs. Clear React state when closing UI, logging out, starting a new read, or unmounting.
