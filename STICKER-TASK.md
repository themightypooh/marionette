# Task: sticker placement — stamp an image onto the model

Repo: **Geppetto** (`C:/Users/pooh/Documents/s&box projects/geppetto`), an s&box editor package.
Effigy is the parametric CAD modeller; this is a paint tool.

> **BLOCKED UNTIL `PAINT-TEXTURE-TASK.md` HAS LANDED.** A sticker is a dab whose brush is an image
> instead of a solid colour, so it composites into the paint canvas. While paint is per-vertex
> colour there is nowhere for it to go — a sticker on eight vertices is not a sticker. Do not start
> this until painting a plain box puts colour under the cursor at brush resolution.

## The idea

Click a spot on the model, and an image lands on the surface there — a decal, a logo, a warning
label. Then it can be moved, turned and resized until it looks right.

## Format is already solved — do not write a decoder

`Sandbox.Bitmap` is Skia-backed and is already used in this repo (see
`Editor/RigControlEditor/MarionetteIcon.cs`, which builds one from an SVG string and converts it
with `Pixmap.FromBitmap`). Skia decodes PNG, JPEG and TGA, so the tool should accept whatever the
asset browser will show it and lean on that.

**PNG with alpha is the format to document and expect**, because transparency is the entire point
of a sticker; a JPEG one is a rectangle of background.

**Verify the pixel-access call before building on it.** This repo can currently only *write* PNG
(`Effigy/PngWriter.cs` has no reader), so nothing here has read image pixels before. Confirm how to
get RGBA bytes out of a `Bitmap` or a loaded `Texture` in this engine version and write down what
you found, rather than assuming an API shape.

## The design that fits what is already here

**A sticker is a stroke, not baked pixels.** `PaintStroke` records what was painted and
`PaintReplay` replays it into the canvas on every rebuild — that is why paint survives editing a
feature underneath it. A sticker must work the same way: store a record — image asset path, the
surface point it was placed at, its rotation, its size, maybe an opacity — and replay it. Baking
the pixels in at placement time would make it unmovable and would lose it on the next rebuild.

Put it beside the strokes on `PaintFeature`, serialised through `StudioDocument` the way `Strokes`
already is. A never-stickered feature must serialise to nothing at all, the same "null until first
use" idiom `Strokes` follows.

**Placement is a decal projection.** Project the image along the surface normal at the hit point
into UV space, then composite it into `PaintCanvas` source-over. Two things to get right:

- **It must follow the surface across faces**, not stop at the first one. The projection is against
  UVs, so a sticker spanning a chart boundary lands in two islands — that is correct and is what
  per-corner UVs are for.
- **Refuse rather than guess where it cannot land.** A face at a grazing angle to the projection,
  or a sticker larger than the chart it sits in, should say so. Effigy's tools refuse and explain
  rather than producing quiet nonsense; match that.

**Editing after placement.** The last placed sticker stays selected with a gizmo — drag to move,
a ring to rotate, a corner to scale. Committing on release, and one undo step per placement or
adjustment, recorded before the change lands (see how the material brush does this with
`MaterialStrokeStarted`; note paint records undo in `OnPaintStrokeFinished` *before* the stroke
joins the list).

## Scope — what NOT to build

- No sticker library, no packs, no browser of its own. Pick the image the way the material brush
  picks a material: from an asset the editor already lists.
- No layer stack, no reordering. Stickers composite in the order they were placed, like strokes.
- No projection modes beyond "along the surface normal at the point you clicked".

## Acceptance

- Place a PNG with transparency on a plain box: it appears where clicked, transparent where the
  image is transparent, at a sensible default size.
- Move, rotate and resize it; each is one Ctrl+Z.
- Save, close, reopen: the sticker is still there, still editable, not baked.
- Edit a feature below the Paint feature and rebuild: the sticker is still on the surface.
- A sticker survives the compile onto the exported model.
- `sh tools/test.sh` ends `0 failed`.

## Conventions

As in `SCULPT-POLISH-TASK.md` — kernel mirroring between `Effigy/` and `Editor/Effigy/`, tests
green, prose comments saying *why*, no `TODO`s, CHANGELOG entries under the existing headings,
`s&amp;box` escaped in XML doc comments. The placement maths belongs in the kernel where a headless
test can reach it; only the gizmo and the asset picker need the editor.
