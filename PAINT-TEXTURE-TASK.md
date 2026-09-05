# Task: move Effigy's paint from vertex colours back to a texture atlas

You are working in the **Geppetto** repo (`C:/Users/pooh/Documents/s&box projects/geppetto`), an
s&box editor package containing **Effigy** (parametric CAD modeller) and **Marionette** (rig
animator). C#, .NET, s&box editor assemblies.

## The goal

Effigy's Paint tool currently stores paint as **per-vertex colours**. Replace that with a
**per-texel texture atlas**, which is how it worked before and which must work again.

Do **not** remove the material brush (painting existing `.vmat` materials onto faces). It is
unrelated to colour painting and must keep working.

## Why vertex colours failed — do not reintroduce these

1. **Density.** Effigy is a CAD modeller; its output is low-poly by design. A 1×1×1 box has 8
   vertices, all at corners. Paint colours vertices inside the brush sphere, so paint landed on
   corners and spread across whole faces, nowhere near the cursor. Users had to add a `Subdivide`
   before painting anything.
2. **Accumulation.** `PaintReplay.DabColors` does `colors[vi] = SourceOver(...)` per dab with no
   per-stroke coverage cap. Holding the brush still keeps compositing, so colour "ticks up" like a
   sculpt brush instead of laying down like a paint brush. **The texture path must composite each
   stroke once** — keep a per-stroke coverage buffer and composite the stroke into the canvas at
   the end, rather than compositing every dab straight into the canvas.
3. **No shader read it.** Vertex colours go into the `Vertex.Color` (COLOR0) stream, which
   `complex.shader` ignores entirely (its model tint is a per-draw constant; its tint mask is a
   texture). This was worked around by binding `materials/default/vertex_color.vmat`. With a
   texture atlas this problem disappears — the paint is a texture on an ordinary material.

## What already exists — reuse it, do not rewrite

The texture path was written, then parked. It is still in the tree and marked `PARKED, NOT LIVE`:

- `Effigy/PaintCanvas.cs` — RGBA CPU canvas with a dirty rect. Complete.
- `Effigy/AtlasId.cs` — stable id for a mesh's UV layout, so a cached canvas is not reused against
  a re-unwrapped mesh. Complete.
- `Effigy/PaintReplay.cs` — `Replay(mesh, strokes, resolution)` returns a `PaintCanvas`; `Dab` and
  `PaintStroke` are the texel halves. Complete. (`ReplayColors`/`DabColors` are the vertex-colour
  halves — those become dead once you switch, remove them.)
- `Effigy/PngWriter.cs` — already writes RGBA (colour type 6).
- `Effigy.Tests/PaintCanvasTests.cs`, `Effigy.Tests/AtlasIdTests.cs` — ~350 lines, already passing.
- `Editor/EffigyEditor/EffigyPreview.cs` — has a `Build(mesh, material)` overload whose comment
  says "THIS IS THE PAINT-PREVIEW PATH": one material, `Material.CreateCopy` with the live canvas
  texture bound. Use it.

Reused unchanged:

- `Effigy/PaintStroke.cs` and its `.effigy` serialisation. A stroke is points + colour + radius +
  strength + falloff — independent of how it is rasterised. **Do not change the file format.**
- `Effigy/PaintSession.cs` stroke machinery: spacing, `MaxSamplesPerMove`, `MirrorX`, falloff,
  `BeginStroke`/`MoveTo`/`EndStroke`. Only the dab TARGET changes (texels, not vertices).
- `Effigy/MeshBVH.cs` — `FacesInRadius` is what the texel dab needs.

## What must be built

1. **A UV gate.** Texel paint needs UVs. A `PaintFeature` on a body with no usable UV layout must
   refuse at the door with a message naming `UV Project` (the existing feature, `Mode = Unwrap`),
   the way other Effigy tools refuse rather than guess. Do not auto-unwrap.
2. **Canvas persistence.** Strokes already persist and `PaintReplay.Replay` rebuilds the canvas
   from them, so prefer replaying on rebuild over storing the image. Key any cache on
   `AtlasId.Of(mesh)` **and** the sculpt topology id — a re-unwrap keeps topology and moves every
   island.
3. **The authoring chain, which does not exist at all.** On export/compile:
   canvas → PNG → a `.vtex` → a generated `.vmat` referencing it → bind that vmat to the face's
   material slot via the existing `MaterialDrop`/`PartStudio.MaterialNames` mechanism. Decide and
   document whether a painted part exports one atlas for the whole part or one per material slot.
4. **Live preview.** While painting, upload the canvas dirty rect to a texture and render through
   the `EffigyPreview.Build(mesh, material)` path. `PaintCanvas` already tracks the dirty rect
   precisely so the whole 1024² image is not re-uploaded per mouse-move.

## Hard constraints

- **Kernel mirroring.** `Effigy/` and `Editor/Effigy/` are byte-identical copies; `Code/Effigy/`
  holds a 4-file runtime subset (`Vec.cs`, `Xform.cs`, `Rig/Skeleton.cs`, `Rig/SoftBone.cs`) which
  must NOT be mirrored into the editor. Run `tools/sync-kernel.sh` and keep `KernelSyncTests`
  green. Kernel files must not reference engine types.
- **Tests.** `sh tools/test.sh` must end `0 failed`. It is currently 3221 passing. Add tests for
  every behaviour you change; a headless test must be able to reach the logic, which is why the
  kernel/editor split exists.
- **Do not break these**, all fixed recently and all load-bearing:
  - `VmdlMaterials` names *every* material slot the mesh uses in the remap list. An unnamed slot
    compiles to the missing-material shader (bright red). Unbound slots fall back to a real asset.
  - Material paths are stored as the **source** path. The asset browser reports `.vmat_c` for
    compiled content, and the engine appends `_c` itself, so storing `.vmat_c` makes it look for
    `.vmat_c_c` and renders red. See `MaterialDrop.AsSourcePath`.
  - Undo is recorded **once per stroke, before the stroke lands**, not per dab.
- **Comment style.** This codebase explains *why*, in prose, especially where a decision was
  non-obvious or a previous attempt failed. Match it. There are zero `TODO`s in the repo; do not
  add any.
- **CHANGELOG.md.** Add entries under the existing `Added` / `Improved` / `Fixed` / `Removed` /
  `Known Issues` headings in `## Unreleased`, written for someone who installed the package.

## Acceptance

- Paint a **bare 1×1×1 box** with no Subdivide: the mark appears under the cursor, at brush
  resolution, not spread across whole faces.
- Holding the button still does not keep darkening the same spot.
- Strokes survive save → close → reopen, and survive editing a feature below the Paint feature.
- The material brush and dropped materials still work and are unaffected.
- `sh tools/test.sh` ends `0 failed`.

## Useful context files

- `docs/dev/PAINTING.md` — the design record. §6 ("what a painted model exports as") is the
  decision you are reopening; it lists the texel argument explicitly.
- `docs/dev/README.md` — index, with a dated note on what is newer than the other docs.
