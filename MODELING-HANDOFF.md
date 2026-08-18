# Modeling tool — session handoff

## Status, 2026-08-18

**Phase one (parametric CAD) is done.** Primitives, feature tree, sketcher, bevel, shell, mirror,
array, UV projection all work and are tested — 417 checks in `Effigy.Tests`. Boolean subtract is
the one gap, deliberately deferred (see "Not here yet" in `Effigy/README.md`).

**Phase two (bones) is next, and it does not wait on s&box.** Skeleton, auto-weighting
(`SkinBinder`), and SMD export (`SmdWriter`) already exist in the kernel — what's missing is a
`SkeletonFeature` in the feature tree and end-to-end tests proving a rigged model round-trips
through rebuild/rebind/export. This is picked over sculpt (formerly phase two) because there's
more hands-on experience with rigging than sculpting — see `Effigy/README.md`'s "Not here yet"
for the reordered plan. Sculpt (Catmull-Clark exists; brushes and multires don't) is now phase
three.

**s&box editor integration (phase four) is a separate, parallel track — not a blocker for the
above.** Its viability rests on two unanswered questions (open questions #1 and #2 below), which
need a local machine with s&box installed; this repo is developed from a cloud container that
can't reach `sbox.game` or run the editor. A PC is being set up for exactly this check. Until that
comes back, phase four stays "unverified leads only" and phases two/three proceed regardless —
OBJ (static) and SMD (rigged) export both work today independent of whatever s&box's own mesh
tools turn out to support.

A second s&box tool: **make a usable, rigged 3D model without leaving the editor**, the same thesis
as Marionette applied to meshes instead of animation.

Not a prop builder. The intended pipeline runs end to end:

```
CAD  →  subdivide  →  sculpt  →  bake to cage  →  add bones  →  Marionette animates it
```

Every stage feeds the next, and **the low-poly cage the CAD stage produces is the spine of the whole
thing.** It carries the UVs, it receives the baked sculpt detail, it is what gets skinned, and it is
what Marionette ends up posing. Nothing downstream works if the cage is not clean, which is what
justifies starting parametric — see the decision section.

An earlier draft of this document framed the goal as static hard-surface props. That was wrong, and
the correction matters: it changes the export format and adds a whole rigging stage.

---

## How to not waste this session

**Almost nothing here was read from engine source. Treat every API shape below as a lead, not a
fact.**

This session ran in a cloud container. The engine source that Marionette's handoff points at —

```
C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\tools\Code
C:\Program Files (x86)\Steam\steamapps\common\sbox\editor\
```

— was not reachable, and neither was the API browser (`sbox.game` and `sboxcool.com` are both
blocked by the container's egress proxy; GitHub is not). So findings came from Facepunch's docs
repo (`github.com/Facepunch/sbox-docs`, read directly) and from web search snippets.

That is exactly the failure mode Marionette's handoff was written to prevent. The provenance
column in the tables below is load-bearing — **anything marked "search snippet" has the same
standing as a guess.** Read the shipped source before writing against it.

---

## The decision: parametric first, then sculpt on top

Parametric modelling **first**, with sculpting layered over it — not sculpting as the starting
point. The reasoning, shortest form:

- **A sculpt-first model cannot be rigged.** It has no clean topology and no UVs, and getting them
  means retopology and unwrapping — the two hardest jobs in the pipeline and exactly the two a
  non-modeller cannot do. Starting parametric means they never come up, because the CAD stage
  produces rig-ready topology as a side effect.
- **The failure modes are not symmetrical.** A mediocre parametric model is a plain shape that
  works. A mediocre sculpt-first model is a shapeless blob nothing downstream can consume.
- **Quads deform correctly when skinned; triangles pinch at joints.** This is the same quad
  requirement Catmull-Clark imposes, arriving a second time from a different direction — which is a
  good sign the constraint is real rather than an artefact of one algorithm.
- **UVs nearly come free.** Planar/box projection per face cluster is cheap on parametric geometry
  and impossible to do well on a raw sculpt. Those UVs are what the normal-map bake later needs.
- **Collision comes free from parametric history.** A model known to be a union of N convex
  primitives *is* its own physics representation.

Sculpting from nothing is the better demo and the worse tool. Sculpting *on top of a parametric
base* is the actual goal, and the parametric stage is what makes every later stage possible.

---

## Confirmed

Everything in this table came from Facepunch's own documentation, read directly out of the docs
repo.

| Finding | Where |
|---|---|
| ModelDoc imports **DMX, SMD, FBX, OBJ, VOX**. OBJ is on the list. | `docs/editor/model-editor.md` |
| ModelDoc's *Export As…* writes OBJ and FBX, including skinned meshes | same |
| A model that isn't fully static needs at least an `AnimBindPose` node, or morph targets and IK data silently break | same |
| **Max 4 weight influences per vertex.** Extra weights are culled and normalised automatically, which the docs call "far from ideal" — plan skinning around it from the start | same |
| Creating bones in ModelDoc is a last resort, for rigid props needing one attachment. Bones belong in the source mesh | same |
| `citizen.vmdl` ships as a readable source file at `sbox\addons\citizen\Assets\models\citizen\citizen.vmdl` | same |
| Scene Mapping mode (`M`) ships Primitive, Vertex, Edge, Face, Texture, Vertex Paint and **Displacement** tools. Displacement is described as *"Sculpt and displace vertices to create organic shapes."* | `docs/editor/mapping/index.md` |
| The Texture tool already does per-face material assignment plus UV align/scale/rotate | same |
| Hammer is slated for removal once scene mesh editing replaces it | s&box forums / VDC |
| Mounts synthesise models at `.vmdl` paths from arbitrary bytes via `ResourceLoader<T>` + `ResourceType.Model` | `docs/game-mounts/creating-mounts.md` |
| `AssetSystem.CreateResource` takes an **absolute** path | Marionette, `RigSampleBuilder.cs:148` |

## Unverified — leads only

| Lead | Provenance | Why it matters |
|---|---|---|
| `Model.Builder.AddMesh( mesh ).AddCollisionMesh( positions, indices ).Create()` | third-party wiki via search snippet, **not** Facepunch | the live-preview path; if the signature is wrong the whole viewport plan shifts |
| `Editor.MeshEditor.PrimitiveBuilder` exists | API-browser search snippet | would mean primitive generation is already written |
| `PolygonMesh` exists and has a `.Vertices` list | API-browser search snippet, plus a release note about it writing world-space properties into JSON and breaking prefab diffing | the single biggest unknown — see below |
| `EditorMeshComponent` is the scene-mesh component | search snippet | ditto |
| `CreateResource` accepts `"vmdl"` | **pure guess** | `vmdl` is an engine type, not a `GameResource`. May well refuse |
| The `.vmdl` KV3 node schema for `RenderMeshFile` | **pure guess** | needed to emit a vmdl at all; `citizen.vmdl` answers it in one read |

---

## The two facts that shape the tool

### 1. The export path is a source mesh plus a .vmdl — and OBJ is only half of it

`.vmdl` is a text KV3 source file that references a source mesh, so export means writing both:

```
tool document  →  thing.<mesh>  +  thing.vmdl (RenderMeshFile → thing.<mesh>)  →  compile
```

**Which mesh format depends on whether the model has bones**, and the pipeline says it eventually
does:

| Format | Bones? | Notes |
|---|---|---|
| **OBJ** | **no** | Plain text, ~60 lines to write. Positions, UVs, normals, material groups, nothing else. Written and working |
| **SMD** | **yes** | Plain text, carries skeleton and per-vertex weights. ModelDoc calls it *"technically deprecated, but usable"*. The natural next target |
| **DMX** | yes | Valve's current format (version 22?). The proper answer if SMD's deprecation ever bites |
| **FBX** | yes | Binary. Supported, and by far the most work to write |

So OBJ is the static-geometry path and a debugging convenience — it is what makes a kernel result
openable in Blender or ModelDoc today. **It is not the destination.** The day the rigging stage
lands, the export layer needs an SMD writer; that is a rewrite of the export layer only, not of the
kernel.

Two constraints from the ModelDoc docs that only matter once there are bones, and both matter a
lot then:

- **A model that is not fully static needs at least an `AnimBindPose` node**, or morph targets and
  IK data silently break.
- **No more than 4 weight influences per vertex.** Extra weights are culled and normalised
  automatically, which the docs describe as far from ideal — so the skinning stage has to cap at
  four deliberately rather than discovering it later.

`Model.Builder` still handles the live model in the tool's viewport; only the file export needs a
format that carries a skeleton.

### 2. Facepunch already shipped most of a mesh editor

Scene Mapping mode has primitives, vertex/edge/face editing, per-face materials with UV controls,
and vertex paint. **The gap is not "edit meshes in the editor" — it's that mapping meshes are not
props.** There is no clean path from "I blocked this out" to "reusable `.vmdl` with collision and
materials".

If `PolygonMesh` turns out to be usable from tool code, the tool collapses to two things worth
building:

- the modelling operations mapping lacks — bevel, subdivision surface, mirror, array, boolean
- the asset-ification step — OBJ/vmdl export, collision from the primitive history, sane material slots

That is a fraction of the work of a CAD kernel or a sculpt engine, and it is the piece nobody has.
**Whether `PolygonMesh` is reachable is therefore the question that decides the tool's size.**

---

## CAD → subdivide → sculpt: this is the plan

**Correction to an earlier draft of this document, which claimed subdivide-then-sculpt does not
work. It does. The earlier claim conflated two different things:**

| | Moves vertices | Undercuts / overhangs |
|---|---|---|
| **Displacement** (Source-style, and s&box's mapping Displacement tool) | along normals only, heightfield on a fixed grid | **no**, at any subdivision level |
| **Sculpting on a subdivided mesh** (ZBrush subdivision levels, Mudbox, Blender multires) | freely, in 3D | **yes** |

The first genuinely cannot make an ear. The second is the industry-standard workflow and is what
this tool should do. Do not let the earlier version of this section talk anyone out of it.

### Why starting from CAD is what makes the sculpt usable

This is the important part. A sculpt-first model is unusable as a game asset because it has no
low-poly version and no UVs, and getting them means retopology and unwrapping. In a subdivide-and-
sculpt pipeline **the base cage is already the low-poly, with the UVs assigned at CAD time.** So:

```
parametric base (quads, UV'd)  →  subdivide 3-4 levels  →  sculpt the dense mesh
        └────────────── bake detail to a normal map ──────────────┘
                    ship the cage + the normal map
```

No retopo step, because the parametric stage produced clean topology as a side effect. That closes
the exact objection that ruled out sculpting-first, and it is why the two halves belong in one tool
rather than two.

### What it takes

- **Quad-dominant output from the CAD stage.** Catmull-Clark needs quads; general boolean output is
  triangle soup and subdivides badly. Keep primitives quad-based and lean on grouping with
  subtract-only-where-needed — which is already the phase-one scope, so this constrains nothing that
  wasn't already constrained. (Triangle regions can fall back to Loop subdivision.)
- **A half-edge mesh and Catmull-Clark subdivision**, levels 0–4, switchable up and down.
- **Brushes** — grab, inflate, smooth, pinch, flatten, clay — with a falloff radius and an octree or
  BVH for hit-testing once past ~100k verts.
- **Multires displacement deltas**, storing the sculpt per level so the base cage stays editable
  underneath it. This is the hard part and the one that decides whether the tool feels good or feels
  like a trap. Without it, sculpting is a one-way door and the parametric history dies the moment a
  brush touches the mesh.

### Real limits, so they aren't surprises

- Detail only goes where polygons were allocated. A fine crease in a large flat area needs density
  there, at CAD time.
- Hard pulls stretch triangles thin and detail quality degrades with them.
- No new protrusion from nothing, no merging separate forms, no new holes. Substantial deformation
  yes; topology change no.
- 4× vertex count per level. A 500-triangle base at level 4 is ~128k — fine in C#.

### The SDF route, for the record

A single signed-distance field with CSG primitives and sculpt brushes both writing into it, meshed
with dual contouring, gives live-editable parametric *and* sculpting with sharp edges preserved.
It is the better end state and a much larger build — weeks to months for the meshing alone. Multires
gets most of the benefit for a fraction of the work. Revisit only if the live-both-ways property
turns out to be the actual point.

---

## Phase one — the parametric base

Much of this now exists in `Effigy/`, engine-free and with 417 checks behind it. See that
folder's README for the design decisions; the status here is what remains.

1. **Parametric primitives** — box, plane, cylinder, quad sphere, wedge, tube. **Done.**
   Quad-dominant output is a hard requirement, not a nicety: skinning needs it in phase two.
2. **Feature tree** — ordered history, rollback, incremental rebuild, self-describing parameters.
   **Done**, modelled on Onshape's Part Studio.
3. **Sketcher** — planes, lines, arcs, circles, closed-region finding, extrude, revolve. **Done.**
   The constraint solver is not: coordinates are typed rather than derived.
4. **Modifiers** — array, mirror, bevel (flat chamfer by angle threshold with skin-weight passthrough)
   and shell are **done**.
5. **Boolean subtract**, which also unlocks profiles with holes. Not started.
6. **Planar/box-projection auto-UV per face cluster.** **Done** — `UVProjectFeature` re-projects box
   or planar per selected bodies.
7. **Export** — OBJ works for static geometry. Collision from the primitive list rather than from 
   triangles is not built.

Live tree throughout: change any number, the model rebuilds.

## Phase two — bones and rigged export

Bones come before sculpt because you have bone experience and can iterate faster. Sculpt is deferred
until phase three.

1. **Skeleton in the kernel** — bones, hierarchy, orientation, bind pose
2. **Skinning weights**, **capped at 4 influences per vertex** because the compiler culls beyond that
3. **Auto-weighting** so a first result is not hand-painted; heat diffusion or bone-glow
4. **Weight painting** to fix what auto-weighting gets wrong
5. **Export as SMD** (or DMX), with an `AnimBindPose` node in the `.vmdl`

Skinning targets the cage produced by phase one. This is the same cage that sculpting (phase three)
and editor integration (phase four) will later use — it has to survive every stage between.

## Phase three — subdivide and sculpt

Deferred until phase two ships. The cage from phase one is what this operates on.

1. Catmull-Clark subdivision, levels 0–4, switchable — **done, in `Effigy/CatmullClark.cs`**
2. Brushes — grab, inflate, smooth, pinch, flatten, clay — with BVH hit-testing
3. Multires displacement deltas, so the base cage survives underneath the sculpt
4. Normal-map bake from the dense mesh down onto the cage

Step 3 is the one that makes the pipeline work rather than merely run. Without multires the sculpt
consumes the cage, and with the cage gone there is nothing to rig.

## Phase four — editor integration

Nothing to build here until phases 1–3 are solid. Marionette already poses and keyframes a skinned
model; the handover is a `.vmdl` with a skeleton — no new integration, no shared format beyond the
one s&box already reads.

Phase one is worth shipping alone. Phase two (rigging) ships a complete static→rigged→posed pipeline
separate from the editor. Phases three and four are optimizations and integration on top.

---

## Open questions, in priority order

1. **Reflection-dump `Editor.MeshEditor.PolygonMesh` and `EditorMeshComponent`** from a throwaway
   `[ConCmd]` — the technique that found `LocalTransform`. Constructible from tool code? Exposes
   faces and edges? Is subdivide or bevel already in there? **Does it hold n-gons/quads, or does it
   triangulate on the way in?** That last one decides whether phase two can build on it at all, since
   Catmull-Clark needs quads. *This question determines everything else.*
2. **Open `citizen.vmdl` in a text editor.** Confirm KV3, copy the `RenderMeshFile` node shape.
   One read answers the whole export format question.
3. **Does `AssetSystem.CreateResource( "vmdl", abs )` work**, or does it only accept `GameResource`
   types? If it refuses, find how ModelDoc itself writes a vmdl.
4. **Does the mapping toolbar already have "create model from selection"?** If yes, scope shrinks
   again and the tool may reduce to modifiers alone.
5. Confirm the real `Model.Builder` signature against shipped source before building the viewport on it.
6. **Does ModelDoc still import SMD cleanly, bones and weights included?** The docs call it
   deprecated but usable. This gates phase three, and if the answer is no the export layer needs
   DMX instead — a much bigger write. Worth answering early even though the rigging stage is far
   off, because it is cheap now and expensive to discover late.

Questions 1 and 2 are both cheap and both gate everything. Do them first, in that order.

---

## Environment notes

- `sbox.game` and `sboxcool.com` are **blocked by the cloud container's egress proxy**. GitHub is
  reachable, so `github.com/Facepunch/sbox-docs` is the usable docs route from a cloud session.
  API-browser lookups have to happen on a local machine.
- The `sbox` MCP server referenced in Marionette's handoff attaches to whatever project the editor
  has open — local only, and the fastest way to answer questions 1–4.
