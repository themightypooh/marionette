# Changelog

What shipped in each Geppetto package revision on sbox.game.

Add lines to **Unreleased** as you go, under one of the five headings below.
They are not arbitrary: they are the boxes the changelist form on sbox.game
asks for, so `tools/changelist.sh` can hand you the text for each box instead
of guessing which one a line belongs in. Use the same five in every release.

- **Added** — something you can now do that you could not before.
- **Improved** — something that already worked and now works better.
- **Fixed** — something that was broken.
- **Removed** — something that is gone.
- **Known Issues** — something broken that is not fixed yet.

Write for whoever installed the package, not for whoever works on it. Trailing
file references in backticks are fine — `changelist.sh` strips them on the way
out, so they stay useful here and never reach the store page.

KEEP THE STORE-PAGE BULLETS SHORT. An entry here can be a paragraph — the file
has room to explain itself, and should. The form does not: every line you paste
becomes its own bullet, and a five-sentence bullet reads as a wall of text on a
package page. Split each entry into one-idea lines on the way out, and leave
behind anything only somebody working on the repo would feel.

When you publish a revision, rename **Unreleased** to its version, then run
`tools/changelist.sh <version>` and paste each block into its box on the site.
`tools/changelist.sh` with no argument prints Unreleased.

NOT EVERY REVISION EARNS A CHANGELIST. A publish that moved only tests, build
scripts or repo layout changed nothing an installed user can feel, and a
changelist saying so is noise on the package page. Those revisions are listed
below the sections, named, so it is clear they were considered rather than
forgotten.

## Unreleased

### Added
- A **material brush**. Press Material in the Paint workspace and drag on the model to
  lay a material onto faces, instead of picking them one at a time. The material is
  whatever is selected in the Materials browser, which the Paint workspace already opens
  -- click one there, brush it on, click another and keep going without leaving the model.
  `MaterialBrushSession.cs`, `EffigyViewport.MaterialBrush.cs`
- The brush outlines the faces it is about to take, because a material belongs to a whole
  face: on a coarse box a small ring still paints an entire side. Subdivide first if you
  want the edge to follow the brush.
- It makes the same edit dropping a material makes, so one Ctrl+Z is one dab, the slot is
  reused rather than multiplied, and a slot the brush swept the last face off is retired
  instead of being left named on nothing.
- Paint can cover instead of tinting. A **Blend** dropdown on the paint bar, beside the
  colour, radius and strength: **Tint** multiplies the paint into the material underneath
  so the surface shows through, **Replace** paints onto white so what you brushed is the
  colour that renders. Both are the same multiply -- what moves is the surface under it --
  which is why covering needed no shader in the end. A face you dropped a material on
  keeps that material either way; Blend only moves the slots that had nothing on them.
  `PaintFeature.cs`, `EffigyPaintBar.cs`

### Fixed
- The paint brush lands on a part you have not subdivided. Paint colours vertices, and the
  default brush was a twelfth of the model's diagonal -- on a 1-unit box that is 0.144, while
  the nearest corner to the middle of a face is 0.707 away, so the brush reached no vertex at
  all. It painted nothing and said nothing about it, which reads as the tool being broken
  rather than as the mesh being too coarse to hold the paint. The starting radius now clears
  the spacing between vertices. `PaintSession.cs`
- Entering Paint on a coarse body says so, and says what to do: a bare box has eight places
  for colour to land, so a stroke tints whole faces instead of following the cursor. Add a
  Subdivide above the Paint feature and the same brush gets as fine as the mesh.
- Dragging a multi-bone selection in Marionette no longer moves one bone twice as far as
  the rest. A group drag is applied to the selected bones that nothing above them is
  carrying -- but it only checked each bone's immediate parent, so selecting a bone and
  its *grandchild* while the bone between them stayed unselected let the grandchild
  through: it was transformed directly and carried by its grandparent at the same time.
  It reads as the gizmo misbehaving rather than as the selection being misread, which is
  why it survived. The rule now walks the whole chain. `BoneSelection.cs`
- Materials from the browser bind to something that exists. Most of the engine's own
  content ships compiled, and the asset browser names those `.vmat_c` -- so dropping one
  on a face, or using it for the whole part, wrote a reference nothing resolves and the
  face came back in the bright red missing-material shader. The source path is what goes
  in the document now. The browser's "bound" badge and the one-slot-per-material rule were
  wrong in the same way and by the same cause: a part wearing `oak.vmat` did not recognise
  `oak.vmat_c` as the material it already had, so it took a second slot for it.
  `MaterialDrop.cs`, `EffigyMaterialsPanel.cs`
- A compiled model arrives wearing a material instead of the bright red missing-material
  shader. Every material slot the mesh actually uses is now named in the model's remap
  list -- the ones you dropped a material on point at that material, and the rest point at
  `materials/default.vmat`. They used to point at nothing: the mesh calls an unbound slot
  `material_0`, no asset answers to that name, and red is what the engine shows for a
  material it cannot find. Since the geometry, UVs and skinning were all fine, the first
  thing anyone saw after their first export was a broken-looking model that was not broken.
  `VmdlMaterials.cs`
- Paint survives the compile, for the same reason. Paint is vertex colour, and vertex
  colour is a tint -- it needs a material underneath to multiply into, and an unbound slot
  had none, so a painted part compiled to red rather than to its paint. Bound to the
  default it renders AS the paint, which is what painting a part and exporting it should
  do. What still has not been checked is the far side of the compile in a real scene.

### Known Issues
- Animation clips have to be added again every time you open the tool. File → Animation
  Clips… builds the list that Compile .vmdl bakes in, and that list is not written to the
  .effigy -- close Effigy and it is empty next time, with nothing said about it. Saving it
  would mean a model file naming a .riganim that names a model, which is a loop the loader
  has to be taught to break, so it is a real decision rather than an oversight. Until it is
  made: add the clips in the same sitting you compile in. `EffigyWindow.cs`

## v367690 — 2026-09-05

### Added
- Soft bones are reachable. Select a bone, tick **Soft** in the Rig panel, and set its
  stiffness, damping, weight and cone. The solver behind them shipped a while ago and
  has been tested since, but nothing in the editor could ever put softness on a bone --
  the rig problems list would even warn about a soft bone with a zero cone that no
  amount of clicking could create. Soft bones draw blue in the viewport so you can see
  which ones are simulated. `EffigyRigPanel.cs`
- **Preview** on the Rig bar runs the solver live. Gravity pulls the soft bones off
  their pose so you can watch them sag and settle while you tune the numbers, and
  dragging a bone with the pose gizmo makes everything soft below it swing behind the
  drag. **Rest** puts them back when you want to judge a fresh value.
  `EffigyViewport.SoftPreview.cs`
- Rigs are saved in the .effigy file. Bones, their bind pose, their softness and which
  body is pinned to which bone all survive a save and reopen. They did not before --
  the skeleton lived on the rig panel, which no file format had ever heard of, so
  placing bones and reopening the part lost every one of them silently. A part with no
  rig is written exactly as it was before, byte for byte, and still opens in older
  builds. `StudioDocument.cs`, `PartStudio.cs`
- A workspace bar across the top: **CAD**, **Sculpt**, **Paint**, **Rig**. Effigy had
  grown four toolsets that all took turns on one tool bar, and the only thing that ever
  said which was showing was a word written small at the right-hand end. Now the part of
  the pipeline you are in is a control rather than something you infer: clicking Sculpt
  gets you sculpting, the way clicking Extrude gets you an extrude. Sketching still
  counts as CAD — you opened it from there and you finish it back there.
  `EffigyWorkspaceBar.cs`, `EffigyWindow.Workspaces.cs`
- Rigging has its own tool bar, so it works like every other part of the tool. Add Bone,
  Delete, Assign Body and Mirror sit on Bones and Bind stages instead of being buttons
  in a side panel — they were the only toolset that lived somewhere else. The Rig panel
  keeps its buttons; they run the same actions.
- Each workspace opens the panels it needs and puts the rest away. Paint brings up the
  material browser, Rig brings up the skeleton tree and gives it the whole right-hand
  side. Whatever you rearrange while you are in a workspace is what you come back to, so
  the layouts are a starting point rather than something that undoes your own arranging.
- A tool that cannot run yet is dimmed and says why when you hover it, instead of looking
  live and doing nothing. Assign Body, Mirror and Delete all need a bone selected.
  `EffigyStageBar.cs`
- The tool bar marks which tools will use what you have selected. Click a face and
  Fillet, Chamfer, Draft, Hole, Face Material, Extrude and Move Face each pick up a
  green mark down their left edge; click an edge and only the two blends do. It marks
  what applies rather than dimming what does not, because a tool that ignores your
  selection is not unavailable — you can still press Primitive with a face selected,
  and always could. `EffigyStageBar.cs`
- **Boolean**, on the Solid stage: union, subtract or intersect two bodies. The engine's boolean
  has been installed and working for a while -- Extrude's Remove and Hole both cut with it -- but
  it could only ever be reached by drawing a profile or drilling a hole. There was no way to point
  at two solids you already had and make them one, which is the first thing anybody tries in a
  modeller. Pick the tool body, pick the operation from the button's dropdown, and the tool is
  consumed by the cut the way a cutting tool should be; **Keep tool bodies** leaves it if you want
  to reuse it. `BooleanFeature.cs`
- A Boolean refuses rather than guesses. A body cannot be its own tool, an unpicked tool is an
  error instead of quietly meaning "every body", and a subtract that removes everything or a union
  of solids that never touch each say so rather than leaving you with a part that vanished.
- Painting. Press **Paint** and brush colour straight onto the model. The paint
  composes over whatever material the part already wears, so a part with a dropped
  material keeps that material everywhere you did not brush. Strokes are saved in the
  .effigy file and replayed whenever the model rebuilds, so paint follows the part
  through later edits instead of smearing when something upstream changes.
  `PaintFeature.cs`, `PaintSession.cs`, `PaintReplay.cs`
- Bones can be scaled in Marionette. The viewport's drag mode cycles Rotate, Move and
  Scale, and **E** still flips between the first two. The scale is one number rather
  than three, because that is all a Source 2 bone carries -- it shows in the Inspector
  and in the readout, is keyed like any other channel, and `RigAnimPlayerComponent`
  applies it at runtime. `RigViewport.cs`, `RigInspectorPanel.cs`
- Shift-click builds a selection of bones instead of replacing it. A group gets one
  gizmo at its centre, and dragging it moves every top-most bone in the group together
  while their children follow through the hierarchy the way they always do.
- A **Handle Size** slider on the rig bar. The bone dots and the areas you click to
  grab them both scale with it, so a dense hand rig can be shrunk until its fingers
  stop overlapping and a whole-body rig can be grown until it is easy to hit. The size
  is remembered between sessions.

### Improved
- The built-in Effigy tutorial builds a house rather than a lamp, and takes five steps
  to do it: box walls, a wedge roof, holes drilled for the windows and the door, then
  the export. The lamp asked you to sketch, revolve, shell, subdivide, unwrap and
  sculpt before you had made anything you could look at -- which is the whole tool,
  taught in the order the tool is written rather than the order somebody learning it
  can follow. Every step of the house is a shape you can see arrive.
  `EffigyTutorial.cs`, `EffigyTutorialPanel.cs`
- Only one thing can own a click in the viewport now. Sketching, sculpting, painting and
  the bone tool each used to shut down its own hand-kept list of the others on the way in,
  the lists disagreed, and nothing at all closed a paint before letting you place a bone —
  so both could be armed and one click tried to do two things. Every way in goes through
  one place. `EffigyWindow.Workspaces.cs`
- The pull handle is now a single arrow, pointing the way the face faces. It used to
  be three, and on one face two of them did nothing when dragged — correctly, since
  sliding a flat face within its own plane does not change the solid, but an arrow
  that does nothing reads as broken. Sliding a wall is Move Face's Translate mode.
  `EffigyViewport.FaceDrag.cs`

### Fixed
- Bones can be clicked in the viewport. They never could: every bone registered its
  click target into the same shared slot, so the hit test could tell you the cursor was
  over *a* bone but not over *which* one, and the click went nowhere. Each bone now has
  its own, the way the Marionette rig viewport has always done it. `EffigyViewport.cs`
- A bone's hit target is the bone you can see. The drawing sized itself off the bone's
  length while the target was a fixed radius, so the two agreed at exactly one bone
  length and drifted apart in both directions from there -- on a long bone most of what
  you could see did nothing, on a short one the target stuck out past the end. Both now
  come from the same number.
- The pose gizmo stops vanishing when you click the bone it belongs to. A selected bone
  had no hit target at all, so a click anywhere off the gizmo's arrows counted as
  clicking empty space and threw the selection away -- the gizmo was not failing to
  appear, it was being dismissed by the click aimed at it.
- Hovering a bone highlights the bone, rather than putting a blob at one end of it.
- Picking a bone in the Rig tree shows up in the viewport straight away -- it turns
  yellow and gets its pose gizmo. The selection had always worked; the viewport only
  repaints when asked and nobody was asking, so nothing on screen said so until you
  happened to move the mouse over the model.
- In the Rig workspace the model's faces are no longer selectable or highlighted, so a
  click meant for a bone cannot land on the wall of triangles behind it. The origin
  handle, the lamps and the face-drag arrow step aside there too.
- A rig edit marks the document unsaved. Placing bones, renaming one, or changing
  softness left the title bar showing no changes, so closing the window closed it
  cleanly without asking and the whole rig went with it. Harmless while a rig only
  lived in the window and there was nothing to save it into; a way to lose work as
  soon as rigs went into the file. `EffigyWindow.cs`
- Undo works on soft bones. Making a bone soft and pressing Ctrl+Z left it soft, and
  changing a stiffness twice in a row lost the first value -- the undo system compared
  two rigs without looking at their softness, so every soft edit looked to it like
  nothing had happened. `EffigyWindow.cs`
- An oversized fillet or chamfer is refused again, across the whole range where it
  should be. On a 2-unit cube any radius above 1.0 has eaten more of every face than
  the face had; between 1.0 and about 1.25 the part came back quietly self-intersecting
  instead of saying so, because the old check measured the volume of the finished body
  and a part folded exactly through its own middle still encloses a positive one. The
  check is now per-face and per-edge — an edge that has been shrunk past its own length
  and turned around — so it catches the fold where it happens rather than hoping it
  shows up in the total. The suggested radius the error offers is fixed by the same
  change, and no longer proposes a size inside the broken band. `EdgeBlend.cs`

### Known Issues
- Paint is as fine as the mesh it lands on. Vertex colours live one per vertex, so a
  bare box paints as a few colour blobs; add a Subdivide (or Sculpt) above the Paint
  feature and the same brush is as fine as the mesh.
- A painted model writes its colours into the DMX (the field names were read out of the
  compiler's own binary), and the whole chain up to that file is now checked on every test
  run — the strokes replay, they survive an edit to the feature underneath them, and they come
  back identical after a save and reopen. What is still unchecked is the far side of the
  compile: nobody has looked at how the engine's shader composites those colours. Check a
  painted model renders its paint before relying on it.
- Paint tints the material rather than covering it, which is what the engine's standard
  material does for free; paint that fully replaces the colour under it needs a shader.

## v367420 — 2026-09-04

### Added
- Faces of a part can be extruded. Click a face of anything on screen —
  a primitive, a boolean, something twenty features old — press Extrude,
  and it pulls. There is no sketch involved and none is asked for. Taper,
  Up to next, Through all, a second distance and New body all work from a
  face exactly as they do from a sketch profile. `SketchFeatures.cs`
- A plain pull is done by MOVING the face rather than by growing a boss on
  top of it, so the part stays a clean single solid you can still Shell
  afterwards. `FaceMove.cs`
- Move Face, a new tool on the Detail stage. Offset pushes each picked face
  along its own normal, so picking both sides of a wall makes it thicker or
  thinner. Translate moves them together along one direction, so the wall
  SLIDES and keeps its thickness — material added on one side and taken from
  the other. `FaceMove.cs`, `SolidFeatures.cs`
- Both refuse rather than guess: a face that is not flat, a face moving while
  a flat neighbour stays put, or a move far enough to turn the part inside
  out, each say which of those it was and what to do instead.
- The distance can be dragged instead of typed. Open Extrude or Move Face,
  pick the faces, and a set of arrows appears on them — turned to the face
  rather than to the world, so the blue one is always straight out. Pull it
  and the solid grows under the cursor, with the number in the panel
  counting up as it goes. It only ever sets the open tool's distance: no
  feature is created by dragging, and the arrows are gone the moment the
  tool is closed. `EffigyViewport.FaceDrag.cs`, `EffigyFeatureDialog.cs`
- The Profiles box in the Extrude panel picks faces. Press Extrude with
  nothing selected and the faces of your part are live straight away —
  hover one, click it, and that is your profile. Click it again to take it
  back off. No sketch has to exist and none is asked for. Sketch regions are
  still pickable in the same box at the same time, and a sketch in front of a
  face gets the click. `EffigyFeatureDialog.cs`, `EffigyViewport.Sketching.cs`
- Right-click a face of a part and every tool that can use that face is on the
  menu — Sketch, Fillet, Chamfer, Shell, Draft, Hole, Subdivide and Face
  Material — each opening already pointed at the face you clicked. Picking one
  is the same as selecting the face and pressing the button on the bar, so
  there is nothing new to learn. The menu's material entries are still there,
  under their own heading. `EffigyWindow.cs`, `EffigyViewport.Selection.cs`

### Improved
- The panel no longer says "No sketch yet — add a Sketch first" at a part
  that has faces you can point at. That message was painted whenever the
  document had no sketches, which is the normal state of a part built out of
  primitives — so it sent you off to draw a rectangle in order to pull the
  rectangle you were already pointing at. It now says what you can click, and
  the box stops being red once a face answers it. Revolve, Sweep and Loft
  still ask for a sketch, because a face is not something they can use.
  `EffigyFeatureDialog.cs`
- The line under the viewport that tells you what your selection is good for
  now asks the tools rather than reciting a list somebody typed. It had already
  drifted once — Subdivide learned to take faces and the sentence had to be
  edited by hand to admit it — and a tool that starts accepting a face now
  says so there by itself. `Feature.cs`, `EffigyWindow.cs`
- Subdivide asks which part you mean instead of taking the whole document. It
  read an empty selection as "everything", so one click could quadruple the
  triangle count of every part you had — including the ones off screen, and
  including a cage you were about to sculpt. Click a part in the Parts list and
  you get that part entire, or pick faces in the viewport and you get those.
  Subdividing a whole part is still there and still the one that smooths; it
  just will not guess which part.
- The grease pencil's colour picker is folded into the pen button itself
  instead of sitting next to it as a second control — click the pen to draw or
  put it down, open its dropdown to change colour. The eraser now works like
  the sketch Cut tool: hold the left button and drag through the notes you
  want gone, rather than clicking each one in turn. `EffigyWindow.cs`,
  `EffigyStageBar.cs`, `EffigyViewport.Notes.cs`
- The Edit menu is shorter and reads in groups instead of as one list of
  fifteen. The five sculpt-mask commands are behind a single "Sculpt Mask"
  submenu — they only do anything while a Sculpt feature is open, so they no
  longer sit in front of everyone else. The three "Normal Map:" entries were
  toggles whose only feedback was a status line that had already scrolled
  away; they move to Edit > Settings under "Normal map bake" as switches and a
  size dropdown you can actually read, and they are remembered between
  sessions now. `EffigyWindow.cs`, `EffigySettingsWindow.cs`

### Fixed
- Chamfered and filleted parts measured smaller than they are. The little
  triangles that cap each corner were being built inside-out, so they
  subtracted from the enclosed volume instead of adding to it — a chamfered
  1-unit box measured 0.811 against a true 0.883. Nothing looked wrong,
  because nothing was wrong to look at: the mesh was closed, valid and
  correctly shaped, and only the numbers taken off it were out. Those numbers
  are what collision hulls and physics are built from. `EdgeBlend.cs`

### Known Issues
- An oversized fillet or chamfer is not always refused any more. The check
  that catches "the blends have met through the middle" measures enclosed
  volume, and some of what it used to catch it was catching only because of
  the inside-out corners fixed above. On a 2-unit cube a fillet radius
  between about 1.0 and 1.25 now builds a part that is quietly
  self-intersecting instead of saying so. Below and above that band it still
  refuses correctly. `EdgeBlend.cs`
- A compiled model arrives with no material on it. Open one in Marionette, or
  drop it in a scene, and it renders in the bright red missing-material shader
  until you assign one by hand. The geometry, the UVs and the skinning are all
  fine — it is only the material reference that does not survive the compile —
  but the first thing anyone sees after their first export is a broken-looking
  model, which reads as the exporter having failed.

## v367389 — 2026-09-04

### Fixed
- A new feature added while the rollback bar sat at the end of the tree was
  never evaluated. It appeared in the tree, it was saved to the file, and it did
  nothing — the bar landed exactly on it rather than below it. Sketching on a
  face is where this showed: the plane is worked out when the sketch feature
  runs, so a sketch that never ran stayed on the global XY plane, and the face
  outline drawn on it collapsed to a single line lying flat through the model.
  Materials dropped on a face could go the same way.
- Exporting no longer overwrites the last thing you exported. Every part studio
  compiled to `models/effigy/export.vmdl` — one name for the whole project — so
  compiling the spatula replaced the grill, and anything already placed in a
  scene changed shape without a word. The exported `.vmdl`, `.obj`, `.dmx` and
  `.smd` now take the document's own name, and an unsaved studio is asked for
  one instead of being given a name that collides with the next.

### Added
- A grid switch and a spacing dropdown at the right-hand end of the sketch tool
  row, shown while a sketch is open. Both were already in Edit → Settings, which
  is the right home for setting up how the tool behaves and the wrong one for
  changing paper mid-drawing. They are the same two values, not a second copy —
  change one and the other follows.
- **View → Console** docks the editor's own console inside Effigy, along the
  bottom. It is the real one, not a copy — the same level filters, term filter,
  stack traces and command entry — so compile failures and anything you log
  show up without leaving the part you are working on.

### Improved
- Hovering a face lights up the whole face, whatever shape or size it is. A
  wall that a cut or a boolean left as many flat pieces used to light up one of
  those pieces — a triangle in the middle of it, a different triangle if you
  moved the mouse a hand's width — while the sketch grid covered the whole
  wall, so the highlight and the paper disagreed on screen at once. The
  highlight, the edge picker, the sketch outline, the material you drop on a
  face and the edges Fillet takes from one now all mean the same face.
- The seams inside such a wall are no longer offered as edges to pick. They are
  not edges of the part — rounding one does nothing — and on a heavily cut wall
  one was always within a few pixels of the cursor, which made the face
  underneath very hard to click at all.
- The grid on the face you are sketching on holds up at any size. It used to
  draw nothing at all once a face was large enough to want more lines than the
  cap allows; it now widens the spacing until the lines fit. It also thins out
  as the lines close up on screen instead of filling the face with solid
  colour, and fades away as the face turns edge-on, the way the reference
  planes already did.
- A `.effigy` part studio shows the model it builds in the asset browser, and
  in the inspector's preview panel, instead of the generic document icon every
  unrecognised file gets. It is the real thing, turning on the spot, wearing
  the materials you dropped on it. A studio the current build cannot read keeps
  the plain icon rather than putting an error in your console while you scroll
  a folder.
- Shipping is one command. `tools/ship.sh -m "what changed"` syncs, commits,
  tests, pushes, publishes the package, stamps this file with the revision that
  created, and prints the changelist text ready to paste. The paste is the only
  step left by hand, because the engine's package API can read changelists and
  has no method that writes one.

## v367360 — 2026-09-04

### Fixed
- Building against Geppetto no longer floods your compile with warnings. The
  editor assembly was compiling its own copy of four kernel files the game
  assembly already provides, so `Vec2`, `Xform`, `Skeleton` and `SoftBone` each
  existed twice — 1857 CS0436 warnings, and two types that read identically in
  source but will not substitute for each other across the game/editor line.
  Both assemblies now compile clean.

## v367356 — 2026-09-04

### Added
- Select first, then pick the tool. Click a face or a part in the viewport and
  the next feature you add starts already pointed at it, instead of making you
  choose again in the dialog. A face selection also tells the tool which part
  you meant, so filleting the thing you just clicked no longer rounds every
  part in the studio.
- Fillet and chamfer can round the edges you pick, not just every sharp edge on
  the part. Click near an edge in the viewport to add it, click again to drop
  it; leave the list empty and it behaves exactly as it did before. Picked
  edges are stored on the part, so they survive a save and a rebuild.
- Viewport lighting. Full bright is the default so faces stay readable while
  you model (Edit → Settings → Full bright); turn it off for a studio sun that
  matches a game scene. View → Add Point Light drops a lamp you can drag, and
  Delete removes the selected one. Lamps are viewport-only — they never export.
  (`EffigyViewport.Lights.cs`, `EffigySettingsWindow`)
- Double-clicking a `.effigy` file in the asset browser opens it in Effigy.
  Part studios now show up there like any other asset.
  (`EffigyPartStudioAsset`)

## v367329 — 2026-09-04

### Added
- Soft bones. A bone can carry stiffness, damping, weight and a cone, and the
  solver turns an animated pose into one with lag and swing in it. Written for
  the VR case where a controller reports a wrist and everything above it is
  invention — welded rigidly, the elbow pivots about the hand and reads as
  broken even though the hand is right. (`Effigy/Rig/SoftBone.cs`)
- Games can run the soft-bone solver at runtime, not just the editor. A
  four-file subset of the kernel ships to game assemblies — the arithmetic on
  `Vec3` and `Xform` and nothing that touches the filesystem, which the game
  sandbox would refuse anyway. (`Code/Effigy`)
- Animation clips bake into the compiled model, so what Effigy makes can be
  handed to AnimGraph. Author clips in Marionette, add them through
  File → Animation Clips…, and they are carried in on the next Compile .vmdl.
  Bones match by name and a mismatch is reported rather than silently dropping
  the clip. (`DmxAnimWriter`, `VmdlAnimation`)
- Copy and paste poses in Marionette. Copy takes every selected key, or the
  pose at the playhead, by bone name — and the clipboard outlives the clip, so
  you can copy idle's rest pose, open fire, and paste instead of re-posing.
- Play interaction clips in game without compiling a model.
  `RigAnimPlayerComponent` plays a `.riganim` on the character you already
  have. Playback runs to the last keyed frame rather than the full 900-frame
  canvas, and `NormalizedTime` is a 0..1 clock to tween a door or a lever
  against.
- Grease-pencil notes: annotations drawn over a part, stored on the document
  beside materials and hidden bodies so they survive a reopen. Deliberately
  outside the feature list, so no exporter can reach them — notes cannot appear
  in OBJ, DMX or the compiled vmdl. (`Effigy/Note.cs`, `Effigy/NoteSession.cs`)

### Fixed
- Exported animation no longer crumples the model. Every bone was written a
  quarter-turn out: the exporter built a bone's basis from the tool's own
  (Right, Forward, Up) naming, while an `Xform`'s columns are where the unit
  axes land, and the DMX writer read them back as the latter. Positions were
  correct in each parent's true frame, so the extra turn on a parent threw its
  children rather than simply tilting the model. (`ToXform`)

### Removed
- The "Marionette" menu this package used to add to your editor. Its only two
  entries rebuilt example clips belonging to Geppetto's own repo and meant
  nothing to anyone who installed the library to pose a model. Both are still
  there as the console commands `rig_build_sample` and `rig_build_wave`.

## v1 — 2026-09-02 (version 367036)

### Added
- First public release: two editor tools sharing one goal — make a usable,
  rigged, animated model without leaving the editor.
- **Effigy**, a parametric CAD modeller. Sketch on a plane or on the face of a
  solid, then extrude, revolve, sweep, loft, shell, bevel, mirror, pattern or
  subdivide it. Booleans cut for real through s&box's own PolygonMesh. The
  sketcher does lines, arcs, circles, ellipses and splines, finds closed
  regions rather than making you declare them, and edits in place with trim,
  extend, fillet and offset. A Levenberg–Marquardt solver handles seventeen
  constraint kinds and reports degrees of freedom, so an under-constrained
  sketch tells you what is loose instead of misbehaving. Everything sits in an
  ordered feature history with rollback and incremental rebuild, so changing a
  dimension near the bottom rebuilds what is above it.
- Rig and export from the same tool: a skeleton, auto-weighting smoothed across
  mesh adjacency, and a real skinned `.vmdl`. Sculpt and normal-bake are in
  there too, so detail can go onto a clean low-poly cage instead of into the
  topology.
- **Marionette**, a control-rig animator. Click a bone in the viewport and drag
  to rotate — the skeleton draws x-ray, so bones buried in the mesh stay
  clickable. Key it, move the playhead, pose again. One timeline lane per bone
  with the real interpolation curve drawn between keys, three easing modes, and
  undo in labelled steps where one drag is one step. Two-bone IK solves in
  closed form with rotation limits, so dragging a hand lets the elbow and
  shoulder follow without bending backwards. There is a first-person view
  framed off the model's own camera bone, reference props to pose against, and
  prop-attach events that spawn a model on a bone for a frame range.
- Clips save as `.riganim` and rigs as `.ctrlrig`, kept separate so several
  clips can share one rig. Constraints bake into keyframes rather than
  re-solving at playback, so a clip plays identically in game.

<!--
REVISIONS WITH NO CHANGELIST, and why - so a gap in the numbering reads as a
decision rather than an oversight. Each of these published real work; none of
it is visible to somebody who installed the package.

  367362  CHANGELOG restructured to match the changelist form's boxes.
  367359  tools/changelist.sh added; publish.sh waits for the version line.
  367358  Test samples write beside the suite instead of into the working
          directory, which had put 46 sample meshes into 367356's package.
  367334  Geppetto became its own repository; kernel, tests and tooling
          absorbed into it.
  367328  Wizard publish, same content as 367329.
-->
