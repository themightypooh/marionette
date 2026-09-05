# Task: rebuild the arena — enclosed, tiered, huge

Repo: **Geppetto**. Effigy is the parametric CAD modeller. Build headlessly, like the existing
generators.

**Replace `Effigy.Tests/StadiumGen.cs`.** It exists and it is wrong for this: it builds a
horseshoe — three straight wedge sections with the front left open, one clean unbroken slope, plus
a floating canopy, a crown band and two pylons. The reference is the opposite of that in the ways
that matter. Keep its scaffolding (a `PartStudio` of ordinary editable features, the `--stadium`
entry point, the OBJ/DMX/VMDL/.effigy export, `VmdlMaterials.GroupList`), throw away its shape.

## What the reference actually is

A **fully enclosed rectangular bowl**. Not a horseshoe — seating runs continuously around all four
sides and through all four corners. No roof, no canopy: the bowl is open to the sky.

The seating is **stepped, not a smooth ramp**. You can see individual rows as terraces climbing
away from the field. That stepping is most of what sells the scale, and it is the single biggest
difference from what is there now.

The bowl is **very deep and steeply raked** — the stands rise far higher than the field is wide
looks, and read as a canyon around a small floor. A flat rectangular field slab sits at the bottom.

At the top of the stands there is a **parapet rim** — a plain wall band running the whole way
around. The reference hangs signage on it. **Do not model signs, screens, lights or banners.** The
rim itself is structure and should be there; everything mounted on it should not.

**Ignore the machine in the middle of the field entirely.**

## Build it in this order

Each step is ordinary Effigy features so the result stays editable. Work in the same headless style
`StadiumGen` already uses.

1. **Field slab.** A flat box at the origin. Everything else is positioned off its edges.
2. **One straight seating bank, as a sketched cross-section extruded along its length.** This is the
   important one and it is where the stepping comes from: sketch the bank's *profile* — a sawtooth
   of terrace steps climbing from the field edge up and outward to the rim — then extrude it the
   length of that side. A stepped profile extruded is enormously cheaper than patterning a row solid
   hundreds of times, and it gives real rows rather than a smooth wedge.
   - Use enough steps that they read as rows at a distance, not so many the mesh explodes. Start
     around 30–40 and look at it.
   - The back of the profile should close down to the ground so the bank is a solid, not a shell.
3. **The other three banks.** Mirror the long bank across the field's centre, then build the end
   bank the same way and mirror that. Prefer `MirrorFeature` over rebuilding — that is the whole
   point of having a feature history.
4. **Corner infills.** The four corners are the fiddly part. Each is the same stepped profile turned
   through 90°. Simplest honest answer: build one corner as its own part — the profile swept or
   lofted around the corner — and mirror it into the other three. If a swept corner fights you,
   a mitred straight section that meets both neighbours at 45° is acceptable and reads fine at
   this scale; say in a comment which you did and why.
5. **Parapet rim.** A plain band around the top outer edge of the bowl. A closed loop sketch
   extruded up, or a box loop — whichever is fewer features.
6. **Vomitories.** Cut stair gaps into the seating with a linear pattern of box cuts (boolean
   subtract) at intervals around the bowl. These break the terraces into blocks and are a big part
   of why a stadium looks like a stadium rather than a bowl.

## Scale — this is the point of the whole task

Build it at real size. s&box units are inches: a person is about **72 units** tall. So a seat row is
~32 units deep and ~16 units high, and a field 100m long is about **4000 units**.

Do not build a small stadium and plan to scale it up. Pick the field size first, in units, and
derive everything from it — bank depth, rise, step size, rim height — so the proportions hold and a
72-unit person standing on the field is a speck. That is what "capturing the scale" means here and
it is entirely a matter of the numbers you choose.

Put the key dimensions in named constants at the top with a comment giving each in metres as well,
the way `StadiumGen` already does with `W`, `B`, `F`, `D`, `H`.

## Name everything

**Every feature gets a real name, and so does every body.** A forty-feature stadium whose tree reads
`Box 1`, `Extrude 3`, `Mirror 2` is not an editable model, it is a lump with a history attached —
and "somebody could still edit this" is half the point of building it in Effigy rather than dumping
a mesh.

- `feature.Name = "..."` on every feature as you add it. Say what the thing IS, not what tool made
  it: `"Field slab"`, `"North bank profile"`, `"North bank"`, `"Bank mirror (south)"`,
  `"Corner infill NE"`, `"Parapet rim"`, `"Vomitory cuts (north)"`.
- `studio.BodyNames[body.Id] = "..."` for the parts that survive to the end, so the Parts list reads
  as a stadium too — `"Field"`, `"Bowl"`, `"Rim"`. Body ids are stable across rebuilds, which is
  exactly why the name is keyed on the id.
- Sketches get names as well. A sketch called `Sketch 4` that turns out to be the terrace profile is
  the one you will need to find again.

Do this AS YOU BUILD, not as a renaming pass at the end. The pass at the end never happens, and
you will have lost track of which `Extrude` was which by then.

## Traps

- **Do not Subdivide any of this.** Look at the cost table the suite prints: a box at level 6 is
  24,576 faces. A stadium is big flat surfaces — it needs no subdivision and will not survive it.
- **Booleans on a huge stepped mesh are the expensive operation here.** Cut the vomitories with as
  few boolean features as you can — one patterned cut per side rather than one per gap if that
  works.
- **Fillets will fight you at this scale.** An oversized radius is refused, and on stepped geometry
  it is rarely worth it. Leave the edges sharp.
- Check `report.HasErrors` after `studio.Rebuild()` and fail loudly, as the existing generators do.

## Done when

- `Effigy.Tests.exe --stadium <outDir>` writes the OBJ, DMX, VMDL and `.effigy` without errors.
- Opening the `.effigy` in Effigy shows a feature tree somebody could still edit — not one giant
  imported lump.
- The bowl is closed on all four sides, the seating is visibly stepped, and the field reads as small
  inside it.
- `sh tools/test.sh` ends `0 failed`.

Conventions as in `SCULPT-POLISH-TASK.md`.
