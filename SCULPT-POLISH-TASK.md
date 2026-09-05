# Task: three pieces of sculpt polish

Repo: **Geppetto** (`C:/Users/pooh/Documents/s&box projects/geppetto`) — an s&box editor package
containing **Effigy** (parametric CAD modeller) and **Marionette** (rig animator). C#, .NET.

Sculpt is the best-tested subsystem here: ~2,180 lines of kernel against ~2,564 lines of tests. Its
**editor** half is only 244 lines, and that is the whole problem — the machinery is well ahead of
the UI. All three tasks below expose or complete something that already works underneath. None of
them should need new sculpting maths.

Do these three and stop. They are independent of each other and can land as three commits.

---

## 1. Expose brush falloff (smallest, do it first)

`BrushFalloff { Smooth, Linear, Sharp, Constant }` is declared in `Editor/Effigy/Brush.cs`, carried
as `SculptSession.Falloff` (`Editor/Effigy/SculptSession.cs:132`), and used by the kernel on every
sample. **There is no UI for it anywhere in the editor.** Sharp versus Smooth is the difference
between a crease and a mound, so this is live capability nobody can reach.

Add a **Falloff** dropdown to `Editor/EffigyEditor/EffigySculptBar.cs`, beside Radius and Strength.

- Copy the ComboBox idiom already used in `Editor/EffigyEditor/EffigyPaintBar.cs` (its `Blend`
  dropdown) — including **the `_refreshing` guard**. `ComboBox.AddItem` takes a `selected:` flag and
  fires its `onSelected` callback while you are populating it, so without the guard, refreshing the
  bar writes the value straight back and marks the document changed for a control nobody touched.
- The bar is `FixedWidth = 460f` and the row does not wrap — **widen it** or the last control clips.
- Falloff is a brush setting, not a document edit, so it goes through the bar's existing `Changed`
  action (viewport redraw). It must NOT mark the document dirty.

Populate from `Enum.GetValues<BrushFalloff>()` rather than a hand-typed list, so a new falloff kind
appears without editing the bar.

---

## 2. Ctrl-drag inverts the brush

Every sculpting tool inverts on Ctrl — Draw carves in, Inflate deflates, Flatten pushes out. Here
the **only** way to sculpt inward is to type a negative number into the Strength box. It works
(`Brush.cs:181`, `next = pos + n * weight`, and `SculptSession.Strength` is deliberately unclamped)
but nobody will ever find it.

**There is already an exact pattern to copy.** `SculptSession` handles mask erasing like this:

```csharp
// SculptSession.cs:546 and :550
mask.Paint( _working, _bvh, point, Radius, Erasing ? -Strength : Strength, Falloff );
```

Do the same thing for the brush:

- Add `public bool Inverted;` to `SculptSession` beside `Erasing` (`SculptSession.cs:169`), with a
  comment saying why it is a session flag rather than a negative Strength: the number in the bar
  must not flip while a modifier is held.
- Apply it where the sample is built — `SculptSession.cs:556`,
  `stroke.Samples.Add( new BrushSample( point, normal, Radius, Strength, direction ) )` — as
  `Inverted ? -Strength : Strength`.
- Drive it from the viewport in `Editor/EffigyEditor/EffigyViewport.Sculpting.cs`: set it from the
  Ctrl modifier **at the moment a stroke begins**, and leave it alone for the rest of that stroke.
  Reading the modifier every frame means letting go of Ctrl mid-stroke reverses the brush halfway
  through a gesture.
- Draw the brush ring differently while inverted (a different colour, or a minus in the middle) so
  the state is visible before the click rather than after it. The ring is drawn in the same file.
- **Grab is directional and Smooth has no sign** — decide and write down in a comment what invert
  means for each of the six kinds rather than letting them do something arbitrary. Smooth inverted
  is conventionally "sharpen", which this kernel does not have; leaving Smooth unaffected and
  saying so is a fine answer.

---

## 3. Sculpt hotkeys

`HandleSculptKey` in `Editor/EffigyEditor/EffigyViewport.Sculpting.cs:220` currently handles exactly
two keys: `X` (symmetry) and `M` (mask). Sculpting is two-handed and everything else needs a trip to
the toolbar.

Add, in that same switch:

- **`1`–`6`** select the six brushes, in the order they appear on the stage bar (see
  `BuildSculptStages` in `Editor/EffigyEditor/EffigyWindow.cs:1246`): Draw, Smooth, Inflate, Grab,
  Flatten, Pinch. Keep the two orders tied together in code rather than typing the list twice — the
  stage bar already builds them in order via a `BrushTool(...)` helper.
- **`[` and `]`** shrink and grow the radius. Multiply rather than add (radius is world-space and
  parts vary hugely in size); something like ×0.8 and ×1.25, clamped at the same `1e-4f` floor the
  bar uses. Repeat-friendly — holding the key should keep changing it.

The stage bar's checked state and the floating bar's numbers must both follow, or the UI will
disagree with the tool. `SculptSettingsChanged` is the existing signal for that — the current
handler already raises it, and the bar's `Refresh()` is what reads the session back.

---

## Repo conventions — these are enforced

- **Kernel mirroring.** `Effigy/` and `Editor/Effigy/` are **byte-identical copies**. `Brush.cs` and
  `SculptSession.cs` live in both — edit one and run `tools/sync-kernel.sh`, or edit both.
  `KernelSyncTests` fails the build otherwise. `Editor/EffigyEditor/` is editor-only and NOT
  mirrored. Kernel files must not reference engine types (`Sandbox.*`, `Editor.*`).
- **Tests.** `sh tools/test.sh` must end `0 failed`. It is currently **3221 passing**. Task 2 is
  kernel logic and must get a test — invert flips the sign of the displacement, and does not disturb
  Strength itself. Tasks 1 and 3 are editor-only and cannot be tested headlessly; do not contort the
  code to make them testable.
- **Undo.** Sculpt does NOT use the studio undo stack — it has its own sparse `BrushUndo` inside
  `SculptSession`, and `EffigyWindow.Undo()` routes to `StepSculptHistory` while a sculpt session is
  open. Do not add `RecordUndo()` calls to sculpt paths.
- **Comment style.** This codebase explains *why* in prose, especially where a decision was
  non-obvious or a previous attempt failed. Match the surrounding files. There are zero `TODO`s in
  the repo — do not add any.
- **CHANGELOG.md.** Add entries under the existing `Added` / `Improved` / `Fixed` headings in
  `## Unreleased`, written for someone who installed the package, not for someone reading the repo.
- **XML doc comments** must escape `s&box` as `s&amp;box` — a bare `&` is an XML entity and warns.

## Acceptance

- A Falloff dropdown sits on the sculpt bar, changes the brush, survives a bar refresh without
  marking the document changed, and does not clip.
- Holding Ctrl and dragging carves inward with Draw; the ring shows the inverted state; letting go
  of Ctrl mid-stroke does not reverse that stroke; the Strength box never changes on its own.
- `1`–`6` pick brushes and the toolbar highlights the one picked; `[` and `]` resize the brush and
  the bar's Radius number follows.
- `sh tools/test.sh` ends `0 failed`.

## Do not touch

Paint is being reworked in parallel (`PAINT-TEXTURE-TASK.md`) — `PaintSession`, `PaintReplay`,
`PaintCanvas`, `PaintFeature`, `EffigyPaintBar` and `EffigyPreview`. Sculpt and paint meet only at
the workspace bar, so there is no reason to go near any of it.
