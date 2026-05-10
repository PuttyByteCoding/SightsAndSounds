// Re-parents the element to <body> on mount so a `position: fixed`
// modal renders relative to the viewport regardless of any ancestor
// stacking context, transform, or overflow. Without this, a modal
// nested under a parent with `z-index: N` (or `transform`, or
// `contain: paint`, …) gets clipped to that parent's stacking
// context — e.g. /browse's filter sidebar at z-20 paints on top of
// any modal opened from inside the player wrapper at z-10.
//
// On destroy we just `node.remove()`. Putting the node back into the
// original parent confused Svelte's own unmount of the surrounding
// `{#if show}` block (the modal stayed in body after show flipped to
// false). `node.remove()` is idempotent and works wherever the node
// is currently attached.
export function portal(node: HTMLElement) {
  document.body.appendChild(node);
  return {
    destroy() { node.remove(); }
  };
}
