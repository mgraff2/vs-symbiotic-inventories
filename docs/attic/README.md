# Attic

Retired code kept for possible revival. This project has no git history (it was never
`git init`-ed), so deletion is permanent unless a copy lands here first.

- `ShapeMask.cs.txt` / `ShapePacker.cs.txt` — the silhouette-packing "Shape" layout mode, cut
  2026-08-08 in favour of the two-mode design (floating jigsaw + left dock). Contains ten
  ASCII-authored silhouettes (slab, sea turtle, daisy, and eight designed by Fable: galleon,
  oak, owl, amphora, whale, mushroom, butterfly, castle keep), all machine-verified for equal
  row widths, capacity >= 150 slots, and a solid 8x4 region. To revive: drop both files back
  into `src/Core/Layout/` (renamed to `.cs`), re-add a `Shape` member to `LayoutMode`, and
  restore the dispatch branch in `SectionPacker.Pack`.
