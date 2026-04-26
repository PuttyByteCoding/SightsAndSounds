// Per-tag-group badge color. Hashes the group name into the existing
// badge-tag-* palette in app.css so the same group keeps the same hue
// across pages. Renaming a group changes its color, which is fine.

import { filterStore } from './filterStore.svelte';

const PALETTE = [
  'badge-tag-performer',         // Slate Blue
  'badge-tag-content-creator',   // Dusty Teal
  'badge-tag-composition',       // Muted Mauve
  'badge-tag-other',             // Warm Sage
  'badge-tag-flag',              // Dusty Rose
  'badge-tag-favorite'           // Steel Amber
];

export function tagBadgeClass(tagGroupName: string | null | undefined): string {
  const s = (tagGroupName ?? '').trim();
  if (!s) return 'badge-tag-content-creator';
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) - h + s.charCodeAt(i)) | 0;
  }
  return PALETTE[Math.abs(h) % PALETTE.length];
}

// Which filter slot a tag is in (if any). Used to override pill color.
export type FilterSlot = 'required' | 'optional' | 'excluded';
export function filterSlot(tagId: string): FilterSlot | null {
  const match = (t: { type: string; value: string }) => t.type === 'tag' && t.value === tagId;
  if (filterStore.required.some(match)) return 'required';
  if (filterStore.optional.some(match)) return 'optional';
  if (filterStore.excluded.some(match)) return 'excluded';
  return null;
}

// Dim filter-slot tints for pills. Uses Tailwind alpha modifiers on the
// daisy semantic palette: green (success) for Required, blue (info) for
// Optional, red (error) + line-through for Excluded.
export function filterSlotClass(slot: FilterSlot): string {
  switch (slot) {
    case 'required': return 'bg-success/20 text-success border-success/40';
    case 'optional': return 'bg-info/20 text-info border-info/40';
    case 'excluded': return 'bg-error/20 text-error border-error/40 line-through opacity-80';
  }
}

// Single source of truth for "what color is this tag pill?". If the tag is
// in any filter slot, the slot tint wins (group color is ignored). Else,
// the group's hashed palette color.
export function pillClass(tagId: string, tagGroupName: string | null | undefined): string {
  const slot = filterSlot(tagId);
  return slot ? filterSlotClass(slot) : tagBadgeClass(tagGroupName);
}
