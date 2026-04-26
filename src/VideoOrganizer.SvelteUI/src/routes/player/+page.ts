import { redirect } from '@sveltejs/kit';

// /player was folded into /browse (Shuffle button + EditTagsPanel). Redirect
// any lingering bookmarks or tag-click links (/player?id=X) to the new home
// with the query string intact.
export function load({ url }: { url: URL }) {
  throw redirect(307, `/browse${url.search}`);
}
