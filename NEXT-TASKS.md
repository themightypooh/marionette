# Three more Effigy tasks

Repo: **Geppetto** (`C:/Users/pooh/Documents/s&box projects/geppetto`) — an s&box editor package
containing **Effigy** (parametric CAD modeller) and **Marionette** (rig animator).

These are independent of each other and of `PAINT-TEXTURE-TASK.md` / `SCULPT-POLISH-TASK.md`. Do
them one at a time, one commit each. **Task 3 must wait until the paint rework has landed** — the
other two can be done at any point.

The repo conventions in `SCULPT-POLISH-TASK.md` under "Repo conventions" apply to all three: kernel
mirroring between `Effigy/` and `Editor/Effigy/`, `sh tools/test.sh` ending `0 failed`, prose
comments that say *why*, no `TODO`s, CHANGELOG entries under the existing headings, and `s&amp;box`
in XML doc comments.

The editor's MCP bridge is at `http://127.0.0.1:7269/mcp` (see `.mcp.json`). Start with
`editor_status` and confirm it says `Project: geppetto` — there is more than one s&box editor on
this machine and the bridge belongs to whichever bound the port.

---

## 1. Material brush: sample a material, and work across bodies

The material brush (`Editor/EffigyEditor/EffigyViewport.MaterialBrush.cs`,
`Editor/Effigy/MaterialBrushSession.cs`, `Editor/Effigy/Features/MaterialDrop.cs`) drags an existing
material onto faces. It works, and two things about it are unfinished.

**a. Alt-click to sample the material under the cursor.** Every paint tool has an eyedropper and
this one has none, so reusing a material already on the part means going back to the Materials
browser and finding it. Alt-click should read the face's material slot, resolve it through
`PartStudio.MaterialNames`, and make that the loaded material — then carry on brushing. If the face
is on an unbound slot, say so rather than silently loading nothing.

The brush gets its material from the Materials browser selection
(`EffigyMaterialsPanel.SelectedMaterial`), so sampling has to either move that selection or
introduce a separate "loaded material" the bar shows. **Prefer moving the browser's selection** —
one place naming the current material is what the design already chose, and two would drift.

**b. One body at a time is a door refusal that should not be needed.**
`EffigyWindow.EnterMaterialBrush` refuses when `_studio.Bodies.Count != 1`, because
`MaterialBrushSession` builds one BVH over one mesh and "which body did the ray hit" is a question
it cannot answer. A part with two bodies is completely ordinary, so this refuses the tool on plenty
of real documents.

Fix it by building a session per body and raycasting each, taking the nearest hit — the same thing
the ordinary face picker already does. Keep the dab itself per-body, because `MaterialDrop.Brush`
takes one `Body`.

**Do not change:** a dab is one undo step, recorded at the press (`MaterialStrokeStarted`), and
re-dabbing faces already on the slot must keep reporting no change. There are tests for both in
`Effigy.Tests/MaterialDropTests.cs`.

---

## 2. Show the UV layout

There is **no UV visualisation anywhere in Effigy** — no island view, no checker preview, no way to
see a seam. `UVUnwrap` produces an `UnwrapReport` (charts, faces, skipped faces, scale) whose
`ToString` is written for a human, and the only thing that ever reads it is a warning about
degenerate faces. `NormalBake.Measure` returns `UVCoverage` (covered texels, overlapping texels,
faces outside the square) and nothing shows that either.

This matters more than it used to: once paint is a texture, a bad unwrap is something the user sees
smeared across their model with no way to find out why.

Build the cheap 80% rather than a UV editor:

- **A checker overlay.** A viewport toggle that renders the part with a checker material instead of
  its own, so stretching and seams are visible on the model. This is how everyone diagnoses UVs and
  it needs no new window. The engine ships dev checker materials — look under
  `materials/dev/` in the engine's `core` folder rather than authoring one.
- **Report what the unwrap did.** Surface `UnwrapReport` in the UV Project feature's panel or the
  status prompt — "12 charts, 340 faces, 1.4 units per UV unit" — instead of discarding it.
- **Say when the UVs are unusable**, using `NormalBake.Measure`: significant overlap, or faces
  outside the 0..1 square. Box and planar projection overlap *by construction* and that is correct
  for tiling, so the warning must only appear where it matters — a bake or a paint — not on every
  box projection.

A dockable window showing the islands themselves is a bigger job and is NOT part of this task. If
you want to leave a foothold for it, put the island geometry behind a method that returns it rather
than drawing it inline.

---

## 3. Teach the new tools in the tutorial — AFTER paint lands

`Editor/EffigyEditor/EffigyTutorial.cs` walks five steps: box walls, a wedge roof, holes for the
windows and door, then the export. It teaches nothing about **Paint**, **Boolean**, or the
**material brush** — which between them are most of what the last two releases added and most of
what the store page advertises.

Extend the house so it ends up looking like a house:

- A **Boolean** step — the two solids the tutorial already builds (walls and roof) are exactly a
  union, so it costs one step and teaches the tool on geometry that is already there.
- A **material** step — brush or drop a material onto the walls.
- A **paint** step — colour something, once the paint rework has landed and painting a plain box
  works without extra setup. **Do not write this step until that is true**; a tutorial step that
  does not work is worse than a missing one.

Read the file first. It is careful about what a step is allowed to check: `EffigyTutorialState`
exposes only questions about the SHAPE of the document (`HasClean<T>()`, `SolidCount`,
`RolledBackAndForward`), deliberately, because a check that tests a proxy ticks itself off on the
wrong day. Its comment explains this and names the three times that lesson was learned. Add to
that vocabulary rather than reaching around it, and keep steps checkable by "a thing exists" rather
than "you typed the right number".

`EffigyToolTarget` is a small enum of the tools a step may point at — currently `Primitive` and
`Hole`. Extend it for the tools you add; the comment explains why it is not the window's own
`ToolKind`.
