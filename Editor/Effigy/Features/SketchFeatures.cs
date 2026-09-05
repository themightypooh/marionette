using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Holds a sketch and publishes it for later features to consume. Produces no geometry itself,
/// exactly like Onshape's Sketch feature.
///
/// Downstream features reference this by feature Id rather than holding the Sketch object, so
/// editing the sketch and rebuilding flows through automatically — there is no second reference to
/// keep in step.
/// </summary>
public sealed class SketchFeature : Feature
{
	public override string TypeName => "Sketch";

	/// <summary>A picked face is the plane it draws on - that is the cube-then-sketch-on-its-face
	/// flow, and it is the only thing a sketch takes from a selection.</summary>
	public override GeometryKind Accepts => GeometryKind.Face;

	public Sketch Sketch = new();

	public readonly ChoiceParam Plane = new( "Plane", new[] { "Top (XY)", "Front (XZ)", "Right (YZ)" } );
	public readonly FloatParam PlaneOffset = new( "Offset", 0f, unit: "u" );

	/// <summary>
	/// A face of an existing body to sketch on, instead of one of the three global planes.
	///
	/// Stored as geometry (a point and a normal) rather than a face index, and re-found on every
	/// rebuild — see FaceRef for why an index would silently attach itself to a different face the
	/// moment anything upstream changed.
	/// </summary>
	public FaceRef? Face;

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Plane, PlaneOffset };

	protected override void Execute( FeatureContext ctx )
	{
		var basePlane = ResolveBasePlane( ctx );

		Sketch.Plane = PlaneOffset.Value == 0f ? basePlane : basePlane.Offset( PlaneOffset.Value );

		// Constraints are intent; points are what everything downstream reads. Solving here, before
		// the sketch is published, is what makes the two agree — profile finding, extrude and
		// revolve all see coordinates that already satisfy the rules, and none of them needs to
		// know a solver exists. A sketch with no constraints costs one comparison.
		if ( Sketch.Constraints.Count > 0 )
		{
			var solve = Sketch.Solve();

			if ( !solve.Converged )
			{
				// A warning rather than an error, deliberately: the points are left at the solver's
				// best attempt, which is still a drawable sketch. Failing the feature would blank
				// the model the moment a sketch became momentarily over-constrained mid-edit.
				Warning = $"Sketch constraints did not fully solve — residual {solve.Residual:0.###e0} "
					+ $"after {solve.Iterations} iterations. The geometry is the closest fit found.";
			}
			else if ( solve.RedundantConstraints > 0 )
			{
				Warning = $"{solve.RedundantConstraints} constraint(s) repeat something already "
					+ "implied by the others. Harmless, but removing them makes the sketch easier "
					+ "to reason about.";
			}
		}

		ctx.Sketches[Id] = Sketch;

		// Publish what this sketch is growing out of, so a consumer can add to that body instead of
		// starting a new one. Cleared when there is no face, or a sketch moved back onto a global
		// plane would keep merging into whatever it used to sit on.
		if ( Face is { } attached )
			ctx.SketchHostBodies[Id] = attached.BodyId;
		else
			ctx.SketchHostBodies.Remove( Id );
	}

	SketchPlane ResolveBasePlane( FeatureContext ctx )
	{
		if ( Face is not { } face )
		{
			return Plane.Index switch
			{
				0 => SketchPlane.XY,
				1 => SketchPlane.XZ,
				2 => SketchPlane.YZ,
				_ => SketchPlane.XY
			};
		}

		if ( !FacePlane.TryResolve( ctx.Bodies, face, out var resolved ) )
		{
			Fail(
				"The face this sketch was placed on is gone — nothing at that point faces that way "
				+ "any more. Move the sketch to another face, or back to one of the global planes.",
				"The stored face reference no longer matches any face of any body at this point in the tree.",
				"Move the sketch to another face",
				"Switch the sketch back to one of the global planes" );
		}

		return resolved;
	}
}

/// <summary>Shared plumbing for the features that turn a sketch profile into a solid.</summary>
public abstract class SketchConsumingFeature : Feature
{
	public readonly ChoiceParam Sketch = new( "Sketch", new[] { "" } );

	/// <summary>Feature id of the SketchFeature to consume. Empty means "the most recent one",
	/// which is what you want while there is only one sketch in the tree.</summary>
	public string SketchFeatureId = "";

	/// <summary>
	/// What <see cref="SketchFeatureId"/> reads while the feature is waiting to be pointed at a
	/// profile, and nothing should be built from it yet.
	///
	/// NOT THE EMPTY STRING, which already means something else and has to keep meaning it: unset is
	/// "the most recent sketch", which is what a feature built in code - or loaded out of a document
	/// saved before this existed - relies on. A feature a toolbar has just made needs the opposite.
	/// Adding an Extrude used to pull the last sketch up by the default distance the instant the
	/// button was pressed, before anyone had said which profile they meant; that solid appearing out
	/// of nowhere is what this exists to stop.
	///
	/// A string no real id can collide with - ids are generated, never typed - so the distinction
	/// survives being saved and loaded like any other reference.
	/// </summary>
	public const string AwaitingPick = "(awaiting pick)";

	/// <summary>Waiting to be pointed at a sketch, so it builds nothing and says why.</summary>
	public bool IsAwaitingPick => SketchFeatureId == AwaitingPick;

	/// <summary>The profile, and one region of it where a region was picked. NOT Face - a mesh face
	/// is not something any of these can consume today, which is exactly the gap §6.3 fills.</summary>
	public override GeometryKind Accepts => GeometryKind.SketchRegion;

	/// <summary>
	/// Closed regions this feature builds from, as points inside them in the consumed sketch's
	/// plane coordinates. Empty means every region of that sketch, which is the old default.
	///
	/// POINTS RATHER THAN INDICES, deliberately. Profiles have no identity of their own — they
	/// are re-found from the curve graph on every rebuild, and their order is whatever order the
	/// walk happens to discover them in. "Region 2" would silently come to mean a different face
	/// the moment a curve was added upstream. A point inside the region is stable under every edit
	/// that does not destroy the region itself, and it is also exactly what a click in the viewport
	/// already gives us.
	///
	/// Several points because Extrude (and Revolve, Sweep) take any number of coplanar closed
	/// faces, not a sketch as a whole and not only one face of it. A seed that falls in a
	/// coplanar neighbour's exclusive face is still built — the plane is the filter, not the
	/// Sketch feature the click happened to land on first.
	/// </summary>
	public List<Vec2> RegionSeeds = new();

	/// <summary>
	/// The one-seed case. Setting it replaces the list, so existing tests and a click that
	/// names a single face still read as a single pick. Empty list (every region) comes back
	/// as null, matching the old field.
	/// </summary>
	public Vec2? RegionSeed
	{
		get => RegionSeeds.Count == 0 ? null : RegionSeeds[0];
		set
		{
			RegionSeeds.Clear();

			if ( value is { } seed )
				RegionSeeds.Add( seed );
		}
	}

	/// <summary>
	/// What the result does to the model: start a new part, or become part of the one it grows out
	/// of, or cut into it. Onshape calls this Result and puts New / Add / Remove / Intersect in it.
	///
	/// AUTO IS THE DEFAULT AND IT IS THE INTERESTING ONE. Extruding three bosses off the same block
	/// used to leave four separate parts in the list, which is not what "I built this up out of four
	/// extrudes" means to anyone. Auto adds to the body whose face the sketch was drawn on, and
	/// starts a new part when the sketch is on a global plane instead. So building on something
	/// keeps one part, and sketching in space starts another, with no parameter to set for either.
	///
	/// AUTO NEVER REMOVES. Adding and removing look identical from the geometry — the same profile
	/// pulled the same distance off the same face — so there is nothing for Auto to read that would
	/// tell them apart, and a rule that guessed would eventually guess a hole into someone's part.
	/// Removing is always asked for.
	///
	/// Add and Remove are also not two flavours of one thing. Add merges the meshes and leaves the
	/// interface between them uncut, which is cheap and right for a boss standing on a face. There
	/// is no equivalent shortcut for a cut: taking material away means genuinely recomputing the
	/// surface, so Remove goes through MeshBoolean and needs a provider installed — the engine's,
	/// inside the s&amp;box editor. Intersect is not offered because nothing has asked for it yet.
	/// </summary>
	public readonly ChoiceParam Result = new( "Result",
		new[] { "Auto", "New body", "Add to the body it grows from", "Remove from the body it cuts into" } );

	/// <summary>Index into Result for the cut. Named rather than written as a bare 3 at each use,
	/// since these options are also the dropdown a user reads and reordering them is a live
	/// possibility.</summary>
	protected const int ResultRemove = 3;

	/// <summary>Index into Result for a body of its own, named for the same reason ResultRemove
	/// is.</summary>
	protected const int ResultNewBody = 1;

	/// <summary>Which sketch feature this consumes, resolved the same way ResolveSketch resolves
	/// the sketch itself. Both have to agree about what "the most recent one" means, so they read
	/// the same dictionary in the same order.</summary>
	protected string ResolveSketchId( FeatureContext ctx )
	{
		// Nothing has been picked, so there is no id to report - not "the most recent one".
		if ( IsAwaitingPick || ctx.Sketches.Count == 0 )
			return null;

		return string.IsNullOrEmpty( SketchFeatureId ) ? ctx.Sketches.Keys.Last() : SketchFeatureId;
	}

	protected Sketch ResolveSketch( FeatureContext ctx )
	{
		if ( IsAwaitingPick )
		{
			Fail(
				"Pick the sketch profile this builds from",
				"This feature has just been added and has not been pointed at a sketch yet, so there is "
					+ "nothing for it to build.",
				"Click a sketch in the viewport - its filled face, or one of its curves",
				"Or choose one from the Sketch box in the panel" );
		}

		if ( ctx.Sketches.Count == 0 )
		{
			Fail(
				"There is no sketch to use — add a Sketch feature first",
				"This feature builds from a sketch profile, and the tree has none at this point.",
				"Add a Sketch feature and draw a closed region",
				"Move this feature below an existing Sketch in the tree" );
		}

		if ( string.IsNullOrEmpty( SketchFeatureId ) )
			return ctx.Sketches.Values.Last();

		if ( !ctx.Sketches.TryGetValue( SketchFeatureId, out var sketch ) )
		{
			Fail(
				$"Sketch '{SketchFeatureId}' is not available at this point in the tree",
				"The sketch this feature was pointed at has been deleted, suppressed, or sits below this feature, so it has not run yet.",
				"Pick a sketch that sits above this feature",
				"Move this feature below the sketch it should consume" );
		}

		return sketch;
	}

	/// <summary>
	/// Put a built solid into the model: as its own body, or merged into the one it grows from.
	///
	/// WHAT MERGING IS AND IS NOT. The two meshes are combined into one body. It is not a boolean
	/// union — nothing cuts the interface between them, so the face the boss stands on is still in
	/// there, now on the inside where it is never seen. For what this is for, that is the right
	/// trade: the part list reads as one part, the render and every exporter are correct, and none
	/// of it waits on a robust CSG. What it costs is that the merged mesh is non-manifold along
	/// that interface, so the operations that need clean topology — shell especially — will refuse
	/// it rather than produce something wrong. That refusal is the honest failure and it is why
	/// merging is not silently forced on features that never asked for it.
	/// </summary>
	protected void Emit( FeatureContext ctx, PolyMesh mesh ) => Emit( ctx, mesh, null );

	/// <summary>
	/// The same, for a solid built from a FACE rather than a sketch.
	///
	/// The host is known outright here — the face belongs to a body, there is nothing to infer — so
	/// Auto adds to that body instead of starting a new part, and Add and Remove never reach
	/// ResolveTarget's "more than one body and nothing says which" refusal. New body still means new
	/// body: it was asked for explicitly.
	/// </summary>
	protected void Emit( FeatureContext ctx, PolyMesh mesh, Body host )
	{
		var target = host is not null && Result.Index != ResultNewBody ? host : ResolveTarget( ctx );

		if ( target is null )
		{
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
			return;
		}

		if ( Result.Index == ResultRemove )
		{
			// ASKED BEFORE THE ENGINE IS, because the engine cannot tell these two apart. A tool
			// that misses the target entirely and a tool that genuinely defeats the boolean both
			// come back as one refusal, and its text sends you looking at the adapter.
			//
			// Bounding boxes only, so this never rejects a cut that would have worked: two boxes
			// that do not overlap contain no solids that do. Two that DO overlap may still hold
			// solids that miss each other, and that case is left to the engine — a conservative
			// check that is always right about what it refuses beats an exact one that guesses.
			RefuseIfItMisses( target.Mesh, mesh );

			// The built solid is the TOOL here, not the result: it is the shape of the hole, and
			// what stays in the studio is the target with that shape taken out of it. Replacing the
			// mesh rather than the Body keeps the body's id, which everything built on this part is
			// holding — a cut must not invalidate the face a later sketch sits on.
			target.Mesh = MeshBoolean.Apply( BooleanOp.Subtract, target.Mesh, mesh );

			// A cut is allowed to go all the way through and leave two solids where there was one.
			// Nothing downstream expects that of a Body, so it is settled here rather than being
			// discovered later as a part list that disagrees with the screen.
			WarnSeparated( SeparatePieces( ctx, target ), target.Name );
			return;
		}

		MeshTransform.Append( target.Mesh, mesh );
	}

	/// <summary>
	/// Refuse a cut whose tool solid does not reach the body at all, and say which way it went
	/// wrong rather than that it went wrong.
	///
	/// The overwhelmingly common cause is direction — see ExtrudeFeature.DirectionSign — so the
	/// message names the axis it missed along and how far short it fell. "It did not work" sends
	/// someone to read the boolean adapter; "the cut sits 0.4 above the material" does not.
	/// </summary>
	protected static void RefuseIfItMisses( PolyMesh target, PolyMesh tool )
	{
		if ( target is null || tool is null || target.VertexCount == 0 || tool.VertexCount == 0 )
			return;

		Extent( target, out var targetMin, out var targetMax );
		Extent( tool, out var toolMin, out var toolMax );

		var gapX = Gap( targetMin.x, targetMax.x, toolMin.x, toolMax.x );
		var gapY = Gap( targetMin.y, targetMax.y, toolMin.y, toolMax.y );
		var gapZ = Gap( targetMin.z, targetMax.z, toolMin.z, toolMax.z );

		var worst = MathF.Max( gapX, MathF.Max( gapY, gapZ ) );

		// STRICTLY NEGATIVE, not merely non-positive. A gap of exactly zero is the two solids
		// touching on a plane, which is precisely what a cut extruded the wrong way off a face
		// looks like — it sits ON the material with zero volume in common, and subtracting it
		// removes nothing. Treating "touching" as "overlapping" is what let the original bug
		// through this check on its first run.
		if ( worst < -1e-6f )
			return;

		var axis = worst == gapX ? "X" : worst == gapY ? "Y" : "Z";

		var how = worst > 1e-6f
			? $"it clears the part by {worst:0.###} along {axis}"
			: $"it only touches the part along {axis}, enclosing none of it";

		Fail(
			$"This cut does not reach into the part — {how}, so there is nothing to take away. "
			+ "A profile drawn on a face extrudes into that face by default; check Flip direction, "
			+ "or increase the distance.",
			how,
			"Check Flip direction",
			"Increase the distance" );
	}

	/// <summary>How far two spans are apart. Zero or negative means they overlap.</summary>
	static float Gap( float aMin, float aMax, float bMin, float bMax ) =>
		MathF.Max( bMin - aMax, aMin - bMax );

	static void Extent( PolyMesh mesh, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
	}

	/// <summary>The body this result acts on — merges into, or cuts into — or null to start a new
	/// one.</summary>
	Body ResolveTarget( FeatureContext ctx )
	{
		if ( Result.Index == 1 )
			return null;

		var host = ResolveSketchId( ctx ) is { } id && ctx.SketchHostBodies.TryGetValue( id, out var bodyId )
			? ctx.Bodies.FirstOrDefault( b => b.Id == bodyId )
			: null;

		if ( host is not null )
			return host;

		// Auto with no host starts a new part. Add and Remove were asked for explicitly, so they
		// have to find something to act on.
		if ( Result.Index is not (2 or ResultRemove) )
			return null;

		// One body in the studio is unambiguous, so use it — that is the sketch-drawn-on-a-global-
		// plane-over-the-only-part case, which is how most cuts get drawn. More than one and there
		// is no way to tell which was meant, so say so instead of picking: a cut landing on the
		// wrong part is worse than a cut that did not happen.
		if ( ctx.Bodies.Count == 1 )
			return ctx.Bodies[0];

		var verb = Result.Index == ResultRemove ? "remove from" : "add to";

		if ( ctx.Bodies.Count == 0 )
		{
			Fail(
				$"There is no body to {verb}. Set Result to New body, or draw the sketch on a face of an existing part.",
				"Result is set to act on an existing part, and the studio has none.",
				"Set Result to New body",
				"Add a Primitive or extrude a sketch first" );
		}

		Fail(
			$"There is more than one body and nothing says which to {verb}. Draw the sketch on a face of the part you mean, or set Result to New body.",
			$"The studio has {ctx.Bodies.Count} bodies and this sketch is not attached to any of them.",
			"Draw the sketch on a face of the part you mean",
			"Set Result to New body" );

		return null;
	}

	/// <summary>
	/// The regions this feature builds from: every closed region in the sketch, or the ones
	/// under <see cref="RegionSeeds"/> when faces have been picked.
	///
	/// Instance rather than static because of the seeds. A missing seed region throws by name
	/// rather than falling back to "all regions" — silently extruding the whole sketch because the
	/// face you picked stopped existing is exactly the kind of thing you notice three features
	/// later.
	/// </summary>
	protected List<Profile> ResolveProfiles( Sketch sketch, FeatureContext ctx )
	{
		var others = ctx.Sketches.Values.Where( s => !ReferenceEquals( s, sketch ) ).ToList();

		if ( RegionSeeds.Count == 0 )
		{
			// Overlap lenses are extra faces for a click in the middle. Building every region
			// must not also build the lens on top of the two wholes that already contain it.
			return FindProfiles( sketch, others ).Where( p => !p.FromOverlap ).ToList();
		}

		var all = ProfileFinder.Find( sketch, others ).Profiles;
		AddExclusiveNeighborRegions( sketch, others, all );

		var picked = new List<Profile>();

		foreach ( var seed in RegionSeeds )
		{
			// Smallest containing region wins, same rule the viewport pick uses. Two overlapping
			// wholes both contain a click in the lens; the lens is smaller, so that is the one
			// that was meant. Taking every match used to extrude both wholes from that click.
			var region = ProfileFinder.SmallestContaining( all, seed );

			if ( region is null )
			{
				Fail(
					"A selected region no longer exists — the sketch changed underneath it. "
					+ "Pick the regions again, or clear the selection to use every closed region.",
					"A RegionSeeds point no longer falls inside any closed profile of this sketch or a coplanar neighbour.",
					"Pick the regions again",
					"Clear the selection to use every closed region" );
			}

			if ( !picked.Contains( region ) )
				picked.Add( region );
		}

		return picked;
	}

	/// <summary>
	/// Closed faces that live on a coplanar neighbour and miss this sketch entirely. Profile
	/// finding leaves those with their own sketch on purpose; a seed stored in this sketch's
	/// UV can still name them, because the pick filter is the plane.
	/// </summary>
	static void AddExclusiveNeighborRegions( Sketch sketch, List<Sketch> others, List<Profile> into )
	{
		foreach ( var guest in others )
		{
			if ( guest is null || !SketchArrangement.Coplanar( sketch.Plane, guest.Plane ) )
				continue;

			var found = ProfileFinder.Find( guest, others.Append( sketch ) );

			foreach ( var profile in found.Profiles )
			{
				var projected = ProfileFinder.Project( profile, guest.Plane, sketch.Plane );

				if ( HostAlreadyHas( into, projected ) )
					continue;

				into.Add( projected );
			}
		}
	}

	/// <summary>True when every interior sample of <paramref name="guest"/> already sits in
	/// some host face — the same region reported twice, or a lens both sketches own. One
	/// sample outside the host is enough to keep the guest: overlapping equal-area squares
	/// share a centroid that would otherwise look like a duplicate.</summary>
	static bool HostAlreadyHas( List<Profile> host, Profile guest )
	{
		var any = false;

		foreach ( var sample in InteriorSamples( guest ) )
		{
			if ( !guest.Contains( sample ) )
				continue;

			any = true;

			if ( !host.Any( p => p.Contains( sample ) ) )
				return false;
		}

		return any;
	}

	static IEnumerable<Vec2> InteriorSamples( Profile profile )
	{
		if ( profile.Outer.Count == 0 )
			yield break;

		var c = InteriorOf( profile );
		yield return c;

		foreach ( var v in profile.Outer )
			yield return v + (c - v) * 0.2f;
	}

	static Vec2 InteriorOf( Profile profile )
	{
		if ( profile.Outer.Count == 0 )
			return Vec2.Zero;

		var sum = Vec2.Zero;

		foreach ( var p in profile.Outer )
			sum += p;

		return sum / profile.Outer.Count;
	}

	List<Profile> FindProfiles( Sketch sketch, IEnumerable<Sketch> neighbors )
	{
		var found = ProfileFinder.Find( sketch, neighbors );

		// ProfileFinder reports what it could not make sense of - a point where three curves meet,
		// for instance, which it will not guess at.
		//
		// This used to THROW whenever there was any such note, even with perfectly good regions
		// alongside it, so a single stray line left anywhere in a sketch failed every feature that
		// read it and there was no way to proceed but to hunt the stray down. Silently ignoring
		// them is the opposite mistake - that extruded one arbitrary sub-loop and looked like it
		// had worked. So: build from what did close, and say plainly what was skipped.
		if ( found.Warnings.Count > 0 && found.Profiles.Count > 0 )
		{
			Warning = $"Built from {found.Profiles.Count} closed region"
				+ (found.Profiles.Count == 1 ? "" : "s")
				+ $"; ignored: {string.Join( "; ", found.Warnings )}";
		}

		if ( found.Profiles.Count == 0 )
		{
			Fail(
				found.OpenChains > 0
					? "The sketch has no closed region — its curves do not join up"
					: "The sketch has no closed region",
				found.OpenChains > 0
					? $"The sketch has {found.OpenChains} open chain(s) and no loop that could be a solid."
					: "There are curves, but none of them enclose an area.",
				"Join the curves into a closed loop",
				"Draw a rectangle or circle to start" );
		}

		// Holes used to be refused HERE, for every consumer at once, on the grounds that capping
		// around one was "really the same problem as a boolean subtract". That was wrong: it is a
		// 2D triangulation problem, and ear clipping has been in the kernel for a while. Extrude
		// handles holes now; Revolve does not, and says so itself. The refusal belongs with whoever
		// cannot do it rather than in the shared path, or one feature's limit keeps standing in for
		// everyone's.
		return found.Profiles;
	}
}

/// <summary>
/// Extrudes sketch profiles into solids along the sketch plane's normal. Onshape's Extrude.
///
/// Caps are emitted as single n-gons rather than triangle fans. Catmull-Clark turns an n-gon into
/// n clean quads, so a hexagonal boss subdivides properly; a fan would leave a high-valence hub in
/// the middle of every face that puckers the moment anyone sculpts near it.
/// </summary>
public sealed class ExtrudeFeature : SketchConsumingFeature
{
	public override string TypeName => "Extrude";

	/// <summary>
	/// Faces of solids that already exist, pulled instead of a sketch profile.
	///
	/// THE THING THAT WAS MISSING. Select a face of a part built entirely from primitives, press
	/// Extrude, and this used to answer "no sketch yet — add a Sketch first": a part with a dozen
	/// bodies and no sketches sending you off to draw one in order to pull a face you were already
	/// pointing at. A face is a profile — it is a closed planar loop, which is the only thing a
	/// prism ever needed — so the whole of taper, termination, second distance and Result works from
	/// one unchanged.
	///
	/// A face selection WINS over the sketch when both are set. It is the more specific answer and
	/// it is the one that was just clicked.
	/// </summary>
	public List<FaceRef> Faces = new();

	public override GeometryKind Accepts => GeometryKind.SketchRegion | GeometryKind.Face;

	/// <summary>
	/// Where the extrude stops.
	///
	/// Blind is a typed distance and is what it has always done. The other two ask the model instead:
	/// UP TO NEXT stops at the first thing in the way, and THROUGH ALL goes past everything. Neither
	/// needs a boolean — both are questions about DISTANCE, answered by a raycast, and the solid they
	/// produce is an ordinary prism. That is worth saying because "up to face" sits next to "cut" in
	/// every CAD tool and reads like it must need one.
	/// </summary>
	public readonly ChoiceParam Termination = new( "Termination",
		new[] { "Blind", "Up to next", "Through all" } );

	public readonly FloatParam Distance = new( "Distance", 1f, unit: "u" );
	public readonly BoolParam Symmetric = new( "Symmetric", false );
	public readonly BoolParam Flip = new( "Flip direction", false );

	/// <summary>
	/// How far it also goes the OTHER way. Zero means one-sided, which is what it has always been.
	///
	/// Onshape calls this the second end position and gives it its own depth rather than a symmetric
	/// checkbox, because the two are not the same question: symmetric splits ONE distance in half,
	/// while this is genuinely independent — a boss 3 up and 1 down is a thing you cannot ask a
	/// symmetric extrude for at all. Symmetric wins when both are set, since it is the simpler
	/// intent and silently doubling up would be worse than ignoring one.
	/// </summary>
	public readonly FloatParam SecondDistance = new( "Second distance", 0f, 0f, unit: "u" );

	/// <summary>
	/// Draft angle, in degrees. Positive narrows toward the far end.
	///
	/// A moulded or cast part needs draft to come out of its tool, and a game asset usually wants it
	/// for the same reason it wants a bevel: a face that leans catches light instead of reading as a
	/// flat slab. It costs no boolean — the far cap is the near one offset by distance × tan(angle),
	/// and every wall leans by exactly that angle because both its ends are that far apart.
	/// </summary>
	public readonly FloatParam Taper = new( "Taper", 0f, -89f, 89f, unit: "deg" );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	/// <summary>
	/// Which way the extrude actually travels: +1 along the sketch plane's normal, -1 against it.
	///
	/// FLIP IS NOT THE WHOLE ANSWER, and that is the fix this method exists for. A sketch on a face
	/// takes that face's OUTWARD normal (FacePlane.Capture reads mesh.FaceNormal straight off the
	/// mesh), so the default direction points away from the solid. For a boss that is exactly right
	/// — it grows off the part. For a cut it is exactly backwards: the tool solid ends up floating
	/// outside the material, touching it on one plane and enclosing no common volume with it, and
	/// subtracting something that is not there removes nothing.
	///
	/// The engine's boolean reports that as "these two solids could not be combined - they may not
	/// overlap", which is true and reads like an adapter fault. It cost a session.
	///
	/// So a Remove whose sketch sits on a face of the body it is cutting defaults to travelling INTO
	/// that body, and Flip still means what it always meant: the other way from the sensible default.
	/// That is Onshape's behaviour too — picking Remove there points the arrow into the material.
	///
	/// Deliberately NOT applied when the sketch is on a global plane. The outward normal of a face
	/// says which way the material lies; a free-standing plane's normal says nothing of the kind, so
	/// there is nothing to infer from and guessing would be worse than the honest default.
	/// </summary>
	float DirectionSign( FeatureContext ctx )
	{
		var sign = Flip.Value ? -1f : 1f;

		return CutsIntoItsHostFace( ctx ) ? -sign : sign;
	}

	/// <summary>Removing material, through a sketch drawn on a face of the very body being cut.</summary>
	bool CutsIntoItsHostFace( FeatureContext ctx )
	{
		if ( Result.Index != ResultRemove )
			return false;

		if ( ResolveSketchId( ctx ) is not { } id || !ctx.SketchHostBodies.TryGetValue( id, out var bodyId ) )
			return false;

		// The host has to still be there. ResolveTarget falls back to "the only body" when it is
		// not, and that body is not necessarily the one the normal was measured against.
		return ctx.Bodies.Any( b => b.Id == bodyId );
	}

	public override IReadOnlyList<IParam> Parameters => Termination.Index == 0
		? new IParam[] { Sketch, Termination, Distance, SecondDistance, Symmetric, Flip, Taper, Result, Material }
		: new IParam[] { Sketch, Termination, Flip, Taper, Result, Material };

	/// <summary>
	/// What an extrude is asked for once in twenty.
	///
	/// The distance, the direction and which sketch is what an extrude IS, and those four rows were
	/// arriving buried under three that are draft angle, a second end position and a material slot
	/// — each of them left at its default nearly every time. Onshape folds the same three away
	/// behind a disclosure for the same reason. Result is not here: it is not advanced, it moved
	/// out of the dialog entirely and lives on the ADD/REMOVE strip over the viewport.
	/// </summary>
	public override IReadOnlyList<IParam> AdvancedParameters =>
		new IParam[] { SecondDistance, Taper, Material };

	protected override void Execute( FeatureContext ctx )
	{
		// A picked FACE wins over the sketch: it is the more specific answer and it is the one that
		// was just clicked. See the Faces field for what used to happen instead.
		if ( Faces.Count > 0 )
		{
			ExecuteFromFaces( ctx );
			return;
		}

		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch, ctx );

		var sign = DirectionSign( ctx );

		var reach = Termination.Index == 0
			? Distance.Value
			: MeasuredDistance( ctx, sketch.Plane.Origin, sketch.Plane.Normal * sign,
				SampleOrigins( sketch, profiles ) );

		if ( MathF.Abs( reach ) < 1e-6f )
		{
			FailOn( "Distance",
				"Distance cannot be zero",
				"An extrude with no distance produces no solid.",
				"Enter a distance greater than zero" );
		}

		var distance = reach * sign;

		var (near, far) = Ends( distance );

		foreach ( var profile in profiles )
		{
			var mesh = BuildPrism( sketch.Plane, profile, near, far, Taper.Clamped, Material.Clamped );
			Emit( ctx, mesh );
		}
	}

	/// <summary>
	/// Pull the picked faces rather than a sketch.
	///
	/// THE SPLIT BETWEEN A MOVE AND A PRISM, which is the one genuinely surprising thing here.
	/// Pulling a whole face straight out and ADDING it looks like it should build a prism and merge
	/// it on. It must not. Emit's append path deliberately does not cut the interface between the
	/// two meshes — right for a boss standing on a face — so the original face would stay buried
	/// inside the solid as a coincident double surface. That is non-manifold, and ShellFeature would
	/// then correctly refuse the part. MOVING the face reaches the same shape with fewer polygons and
	/// a clean topology, so that is what a plain pull does.
	///
	/// Everything else is a genuine prism, built and merged exactly the way a sketch profile is:
	/// a cut, a taper, a second distance, a symmetric pull, or a new body. Those all want a separate
	/// solid, and a cut needs one by definition.
	/// </summary>
	void ExecuteFromFaces( FeatureContext ctx )
	{
		var byBody = new Dictionary<Body, List<int>>();
		var lost = 0;

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var index ) )
			{
				lost++;
				continue;
			}

			if ( !byBody.TryGetValue( body, out var list ) )
				byBody[body] = list = new List<int>();

			if ( !list.Contains( index ) )
				list.Add( index );
		}

		if ( byBody.Count == 0 )
		{
			Fail(
				"None of the picked faces are on the model any more",
				$"All {Faces.Count} of them named geometry that the features above this one no longer produce.",
				"Pick the faces again on the current model",
				"Move this feature back below the edit that changed them" );
		}

		if ( IsAPlainPull )
			PullFaces( byBody );
		else
			PrismFromFaces( ctx, byBody );

		if ( lost > 0 )
		{
			Warn(
				$"{lost} of {Faces.Count} picked faces are no longer on the model",
				"They named geometry the features above this one no longer produce, so they were skipped.",
				"Pick them again on the current model" );
		}
	}

	/// <summary>A whole face, a blind distance, and material added to the body it belongs to —
	/// see ExecuteFromFaces for why this one case is a move rather than a prism.</summary>
	bool IsAPlainPull =>
		Termination.Index == 0
		&& !Symmetric.Value
		&& MathF.Abs( SecondDistance.Value ) < 1e-6f
		&& MathF.Abs( Taper.Clamped ) < 1e-6f
		&& Result.Index is 0 or 2;

	void PullFaces( Dictionary<Body, List<int>> byBody )
	{
		var distance = Distance.Value * (Flip.Value ? -1f : 1f);

		if ( MathF.Abs( distance ) < 1e-6f )
		{
			FailOn( "Distance",
				"Distance cannot be zero",
				"An extrude with no distance produces no solid.",
				"Enter a distance greater than zero" );
		}

		var pulled = new List<(Body Body, PolyMesh Mesh)>();

		// Solved before anything is assigned, so a failure on the third of four leaves the model as
		// it was rather than half pulled.
		foreach ( var (body, faces) in byBody )
		{
			try
			{
				pulled.Add( (body, FaceMove.Offset( body.Mesh, faces, distance )) );
			}
			catch ( InvalidOperationException e )
			{
				FailOn( "Distance",
					"That face cannot be pulled this far",
					e.Message,
					"Use a smaller distance",
					"Select the whole flat surface rather than part of it" );
			}
		}

		foreach ( var (body, mesh) in pulled )
			body.Mesh = mesh;
	}

	/// <summary>
	/// The face as an ordinary profile, put through the same prism builder a sketch goes through.
	///
	/// FACES HERE ARE ALWAYS SIMPLE LOOPS, so there is no hole handling to write: CoplanarMerge holds
	/// the invariant that a surface with n holes is n + 1 faces, never one face carrying holes.
	///
	/// The direction is the face's own OUTWARD normal, and a cut travels the other way — into the
	/// material — for exactly the reason DirectionSign gives for a sketch drawn on a face. Here the
	/// host body is known directly rather than looked up through ctx.SketchHostBodies, so it needs no
	/// guessing at all.
	/// </summary>
	void PrismFromFaces( FeatureContext ctx, Dictionary<Body, List<int>> byBody )
	{
		var sign = Flip.Value ? -1f : 1f;

		if ( Result.Index == ResultRemove )
			sign = -sign;

		// EVERY PROFILE READ BEFORE ANY OF THEM IS EMITTED, and it has to be that way round. A
		// Remove replaces the target's mesh outright, so a face index taken from the old mesh means
		// something else — or nothing — by the time the second face of the same body is reached.
		var jobs = new List<(Body Body, SketchPlane Plane, Profile Profile)>();

		foreach ( var (body, faces) in byBody )
		{
			foreach ( var index in faces )
			{
				var face = body.Mesh.Faces[index];
				var plane = FacePlane.FromPointAndNormal(
					body.Mesh.FaceCentroid( face ), body.Mesh.FaceNormal( face ) );

				var profile = new Profile();

				foreach ( var corner in face.Indices )
					profile.Outer.Add( plane.ToPlane( body.Mesh.Positions[corner] ) );

				jobs.Add( (body, plane, profile) );
			}
		}

		foreach ( var (body, plane, profile) in jobs )
		{
			var reach = Termination.Index == 0
				? Distance.Value
				: MeasuredDistance( ctx, plane.Origin, plane.Normal * sign, SampleOrigins( profile, plane ) );

			if ( MathF.Abs( reach ) < 1e-6f )
			{
				FailOn( "Distance",
					"Distance cannot be zero",
					"An extrude with no distance produces no solid.",
					"Enter a distance greater than zero" );
			}

			var (near, far) = Ends( reach * sign );

			Emit( ctx, BuildPrism( plane, profile, near, far, Taper.Clamped, Material.Clamped ), body );
		}
	}

	/// <summary>SampleOrigins for a single profile that is not in a sketch — same points, same
	/// reason: the centroid alone reads one point of the target and calls it the answer.</summary>
	static IEnumerable<Vec3> SampleOrigins( Profile profile, SketchPlane plane )
	{
		var centroid = Vec2.Zero;

		foreach ( var p in profile.Outer )
			centroid += p;

		centroid /= profile.Outer.Count;

		yield return plane.ToWorld( centroid );

		foreach ( var p in profile.Outer )
			yield return plane.ToWorld( p + (centroid - p) * 0.05f );
	}

	/// <summary>
	/// Where the two caps sit, measured from the plane the profile lies in.
	///
	/// Three ways to place them, in priority order: symmetric splits the one distance, a second
	/// distance runs back the other way from the plane, and otherwise it starts at the plane. Flip
	/// mirrors all of it, which is why `second` is applied against the sign of `distance` rather
	/// than against the plane's normal directly.
	/// </summary>
	(float Near, float Far) Ends( float distance )
	{
		var second = MathF.Abs( SecondDistance.Value );

		if ( Symmetric.Value )
			return (-distance * 0.5f, distance * 0.5f);

		if ( second > 1e-6f )
			return (distance >= 0f ? -second : second, distance);

		return (0f, distance);
	}

	/// <summary>
	/// How far the extrude reaches when the model is what decides, rather than a typed number.
	///
	/// Rays are cast from inside the profile along the extrude direction and the NEAREST hit wins.
	/// Nearest rather than furthest because the solid has to stop at the first thing in the way; a
	/// further hit is something the first surface is already hiding.
	///
	/// THE CAP STAYS FLAT, and that is the honest limitation of doing this without a boolean. A real
	/// "up to face" trims the new solid against the target surface, so a boss meeting an angled face
	/// ends in a matching slope. This ends flat, at the nearest point of contact — exactly right when
	/// the target is parallel to the sketch, and short of it by a visible gap when it is not. Visible
	/// rather than silent, and warned about besides: if the sample rays disagree about the distance,
	/// the target is not parallel and the feature says so.
	/// </summary>
	float MeasuredDistance( FeatureContext ctx, Vec3 origin, Vec3 direction, IEnumerable<Vec3> samples )
	{
		// Everything already built. A sketch drawn on a face of one of these starts ON it, which is
		// what the epsilon below is for.
		var targets = ctx.Bodies.Where( b => b.Mesh is { FaceCount: > 0 } ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				Termination.Index == 1
					? "Up to next needs something to stop at, and there is nothing else in the studio yet."
					: "Through all needs something to pass through, and there is nothing else in the studio yet.",
				"This termination measures against bodies already in the studio, and there are none.",
				"Add a Primitive or extrude a sketch first",
				"Switch termination to a blind distance" );
		}

		if ( Termination.Index == 2 )
			return ThroughAll( targets, origin, direction );

		var nearest = float.MaxValue;
		var furthest = 0f;
		var hits = 0;

		foreach ( var sample in samples )
		{
			if ( MeshRaycast.Raycast( targets, sample, direction ) is not { } hit )
				continue;

			// A sketch ON a face starts flush with it, and that face is not something to stop at —
			// it is where the extrude begins. Same for a face being pulled: the ray leaves from the
			// face itself.
			if ( hit.Hit.Distance < 1e-4f )
				continue;

			nearest = MathF.Min( nearest, hit.Hit.Distance );
			furthest = MathF.Max( furthest, hit.Hit.Distance );
			hits++;
		}

		if ( hits == 0 )
		{
			Fail(
				"Up to next found nothing in the way — no face lies ahead of this profile. Use a blind "
				+ "distance, or flip the direction.",
				"Every ray from the profile missed every body in the studio.",
				"Flip the direction",
				"Switch termination to a blind distance" );
		}

		if ( furthest - nearest > 1e-3f )
		{
			Warning = $"The face ahead is not parallel to this profile: it is between {nearest:0.###} and "
				+ $"{furthest:0.###} away. The extrude stops flat at the nearest point, so it will not "
				+ "meet the far side.";
		}

		return nearest;
	}

	/// <summary>Far enough to clear everything: the furthest any target reaches along the direction,
	/// plus a margin. A prism that stops exactly on a surface is a coplanar face waiting to confuse
	/// whatever consumes it next.</summary>
	static float ThroughAll( List<Body> targets, Vec3 origin, Vec3 direction )
	{
		var reach = 0f;

		foreach ( var body in targets )
		{
			foreach ( var p in body.Mesh.Positions )
				reach = MathF.Max( reach, Vec3.Dot( p - origin, direction ) );
		}

		if ( reach <= 0f )
		{
			Fail(
				"Through all found nothing ahead of this profile — everything is behind it. Flip the direction.",
				"Every body in the studio sits on the other side of the sketch plane.",
				"Flip the direction" );
		}

		// Ten percent past the last thing it has to clear, and never less than a whole unit, so a
		// tiny model does not end up with a margin too small to matter.
		return reach + MathF.Max( reach * 0.1f, 1f );
	}

	/// <summary>
	/// Points inside the profile to cast from: its centroid plus each corner pulled toward that
	/// centroid.
	///
	/// The corners matter. Casting from the centroid alone reads one point of the target and calls
	/// it the answer, which is how a profile that overhangs an edge — or sits over a hole — measures
	/// against something it barely touches. Pulling each corner inward keeps every ray inside the
	/// material rather than balanced on its boundary.
	/// </summary>
	static IEnumerable<Vec3> SampleOrigins( Sketch sketch, List<Profile> profiles )
	{
		foreach ( var profile in profiles )
		{
			var loop = profile.Outer;
			var centroid = Vec2.Zero;

			foreach ( var p in loop )
				centroid += p;

			centroid /= loop.Count;

			yield return sketch.Plane.ToWorld( centroid );

			foreach ( var p in loop )
				yield return sketch.Plane.ToWorld( p + (centroid - p) * 0.05f );
		}
	}

	/// <summary>
	/// Outer loop arrives counter-clockwise in plane coordinates, which fixes every winding
	/// question: the far cap keeps that order, the near cap reverses, and the side quads run
	/// bottom edge then up. Verified by the enclosed-volume test rather than by inspection.
	///
	/// HOLES COST EXACTLY TWO THINGS. Each hole loop gets walls of its own, built by the same code
	/// as the outer ones — and because ProfileFinder hands holes back wound the opposite way, those
	/// walls face into the hole with no sign handling anywhere. And the caps can no longer be single
	/// n-gons, because a face with a hole in it is not a polygon; they are triangulated around the
	/// holes instead.
	///
	/// That cap is a real tradeoff and worth stating plainly. This kernel prefers n-gons because
	/// Catmull-Clark turns one into n clean quads, and a triangulated cap subdivides worse — the
	/// README's whole argument about quads applies. A holed profile has no n-gon available, so the
	/// choice is a triangulated cap or no feature at all, and a plate with bolt holes is hard
	/// surface that rarely gets subdivided anyway. Profiles WITHOUT holes are untouched and still
	/// get their single n-gon.
	/// </summary>
	static PolyMesh BuildPrism( SketchPlane plane, Profile profile, float near, float far, float taper, int material )
	{
		var mesh = new PolyMesh();

		// A negative extrusion puts the "far" cap behind the "near" one and flips the solid inside
		// out. Ordering them here means the rest of the function never has to think about sign.
		var (low, high) = near <= far ? (near, far) : (far, near);

		// Outer first, then each hole — the same order Triangulate.WithHoles indexes them in, which
		// is what lets its triples map straight onto these vertices.
		var loops = new List<List<Vec2>> { profile.Outer };
		loops.AddRange( profile.Holes );

		// TAPER IS APPLIED FROM THE START OF THE SWEEP, so the whole solid is one frustum and every
		// wall leans by exactly the angle asked for, whichever way the extrude runs.
		//
		// The alternative is worth naming rather than dismissing: measuring draft from the SKETCH
		// PLANE would make a symmetric extrude draft away from that plane in both directions, which
		// is what a moulded part with a parting line down its middle actually wants. Onshape does
		// that. This does the simpler thing, because one consistent lean is easier to reason about
		// and is what a game asset usually wants; if a parting-line draft is ever needed, it belongs
		// as its own option rather than as a hidden difference in what Symmetric means.
		var drawn = high - low;
		var inset = taper == 0f ? 0f : drawn * MathF.Tan( taper * MathF.PI / 180f );

		var highLoops = loops;

		if ( inset != 0f )
		{
			highLoops = new List<List<Vec2>>( loops.Count );

			foreach ( var loop in loops )
			{
				if ( !LoopOffset.TryOffset( loop, inset, out var offsetLoop, out var error ) )
				{
					FailOn( "Taper",
						$"A taper of {taper:0.##} degrees over {drawn:0.###} does not fit this profile: {error}. "
						+ "Use a shallower angle, a shorter distance, or a profile without such a narrow neck.",
						$"The offset over {drawn:0.###} at {taper:0.##}° inverts or collapses a loop.",
						"Use a shallower angle",
						"Shorten the distance",
						"Widen the narrow neck of the profile" );
				}

				highLoops.Add( offsetLoop );
			}
		}

		// Where each loop's low ring starts. Its high ring follows immediately after it.
		var lowStart = new int[loops.Count];
		var highStart = new int[loops.Count];

		for ( var index = 0; index < loops.Count; index++ )
		{
			lowStart[index] = mesh.Positions.Count;

			foreach ( var p in loops[index] )
				mesh.AddVertex( plane.ToWorld( p ) + plane.Normal * low );

			highStart[index] = mesh.Positions.Count;

			foreach ( var p in highLoops[index] )
				mesh.AddVertex( plane.ToWorld( p ) + plane.Normal * high );
		}

		for ( var index = 0; index < loops.Count; index++ )
			AddWalls( mesh, loops[index], lowStart[index], highStart[index], material );

		AddCaps( mesh, profile, loops, highLoops, lowStart, highStart, material );

		return mesh;
	}

	/// <summary>One loop's side wall. Cumulative perimeter drives U so the texture does not stretch
	/// on long edges.</summary>
	static void AddWalls( PolyMesh mesh, List<Vec2> loop, int lowStart, int highStart, int material )
	{
		var n = loop.Count;
		var perimeter = 0f;
		var distances = new float[n + 1];

		for ( var i = 0; i < n; i++ )
		{
			var a = loop[i];
			var b = loop[(i + 1) % n];
			distances[i] = perimeter;
			perimeter += MathF.Sqrt( (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y) );
		}

		distances[n] = perimeter;

		for ( var i = 0; i < n; i++ )
		{
			var j = (i + 1) % n;
			var u0 = perimeter > 0f ? distances[i] / perimeter : 0f;
			var u1 = perimeter > 0f ? distances[i + 1] / perimeter : 1f;

			mesh.AddFace(
				new[] { lowStart + i, lowStart + j, highStart + j, highStart + i },
				new[] { new Vec2( u0, 0 ), new Vec2( u1, 0 ), new Vec2( u1, 1 ), new Vec2( u0, 1 ) },
				material );
		}
	}

	/// <summary>
	/// Top and bottom. Caps use plane coordinates directly as UVs, so a face keeps the proportions
	/// it was drawn with instead of being squashed into a unit square.
	/// </summary>
	static void AddCaps( PolyMesh mesh, Profile profile, List<List<Vec2>> loops, List<List<Vec2>> highLoops,
		int[] lowStart, int[] highStart, int material )
	{
		if ( !profile.HasHoles )
		{
			var loop = profile.Outer;
			var n = loop.Count;

			var topIndices = new int[n];
			var topUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				topIndices[i] = highStart[0] + i;

				// The TAPERED position, so a drafted face's texture follows the face it is on rather
				// than the shape it was drawn from.
				topUVs[i] = highLoops[0][i];
			}

			mesh.AddFace( topIndices, topUVs, material );

			var bottomIndices = new int[n];
			var bottomUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				bottomIndices[i] = lowStart[0] + n - 1 - i;
				bottomUVs[i] = loop[n - 1 - i];
			}

			mesh.AddFace( bottomIndices, bottomUVs, material );
			return;
		}

		// Flatten the loops into the one list WithHoles indexes against, so a triple it returns can
		// be read as "the nth point, counting outer first then each hole in turn".
		var flat = new List<Vec2>();
		var loopOf = new List<int>();
		var withinLoop = new List<int>();

		for ( var index = 0; index < loops.Count; index++ )
		{
			for ( var i = 0; i < loops[index].Count; i++ )
			{
				flat.Add( loops[index][i] );
				loopOf.Add( index );
				withinLoop.Add( i );
			}
		}

		// The top's own loops, which differ from the bottom's under taper. Triangulated separately
		// rather than reusing the bottom's triples: an inset loop can need a different bridge, and
		// forcing the bottom's onto it is how a tapered cap ends up with crossed triangles.
		var tapered = new List<Vec2>();

		foreach ( var loop in highLoops )
			tapered.AddRange( loop );

		var bottomTriangles = Triangulate.WithHoles( loops[0], loops.Skip( 1 ).Cast<IReadOnlyList<Vec2>>().ToList() );
		var topTriangles = ReferenceEquals( highLoops, loops )
			? bottomTriangles
			: Triangulate.WithHoles( highLoops[0], highLoops.Skip( 1 ).Cast<IReadOnlyList<Vec2>>().ToList() );

		if ( bottomTriangles.Count == 0 || topTriangles.Count == 0 )
		{
			Fail(
				$"This profile's {profile.Holes.Count} hole(s) could not be capped — the loops may cross each other. "
				+ "Check that every inner loop lies fully inside the outer one.",
				$"Ear clipping returned no triangles for a profile with {profile.Holes.Count} hole(s).",
				"Check that every inner loop lies fully inside the outer one",
				"Redraw the hole so it does not cross the outer loop" );
		}

		// TWO N-GONS WHERE THE SPLITTER WILL GIVE THEM, triangles where it will not.
		//
		// A cap with a hole in it cannot be one face - a face is one loop of corners and this has
		// two boundaries - but two is available, and two is what someone means when they say a
		// washer's end should not be a pile of triangles. Same reasoning as the cut that leaves a
		// hole in an existing face, and now the same code: SplitWithHoles hands back the ring
		// WithHoles was about to clip, cut in two instead.
		//
		// Each cap is asked separately. Under taper the top's loops are inset copies of the
		// bottom's, so one can split cleanly while the other cannot, and a cap that falls back to
		// triangles is coarse rather than wrong.
		var topLoops = Triangulate.SplitWithHoles( highLoops[0], highLoops.Skip( 1 ).Cast<IReadOnlyList<Vec2>>().ToList() );
		var bottomLoops = ReferenceEquals( highLoops, loops )
			? topLoops
			: Triangulate.SplitWithHoles( loops[0], loops.Skip( 1 ).Cast<IReadOnlyList<Vec2>>().ToList() );

		if ( topLoops is not null )
		{
			foreach ( var loop in topLoops )
				mesh.AddFace( loop.Select( High ).ToArray(), loop.Select( i => tapered[i] ).ToArray(), material );
		}
		else
		{
			foreach ( var (a, b, c) in topTriangles )
			{
				mesh.AddFace(
					new[] { High( a ), High( b ), High( c ) },
					new[] { tapered[a], tapered[b], tapered[c] },
					material );
			}
		}

		// The bottom is the same surface seen from the other side, so it is wound backwards.
		if ( bottomLoops is not null )
		{
			foreach ( var loop in bottomLoops )
			{
				var reversed = Enumerable.Reverse( loop ).ToList();

				mesh.AddFace( reversed.Select( Low ).ToArray(), reversed.Select( i => flat[i] ).ToArray(), material );
			}
		}
		else
		{
			foreach ( var (a, b, c) in bottomTriangles )
			{
				mesh.AddFace(
					new[] { Low( c ), Low( b ), Low( a ) },
					new[] { flat[c], flat[b], flat[a] },
					material );
			}
		}

		int High( int flatIndex ) => highStart[loopOf[flatIndex]] + withinLoop[flatIndex];
		int Low( int flatIndex ) => lowStart[loopOf[flatIndex]] + withinLoop[flatIndex];
	}
}

/// <summary>
/// Revolves sketch profiles about an axis lying in the sketch plane. Onshape's Revolve.
///
/// Points sitting ON the axis are the awkward case — every revolved copy of them lands in the same
/// place. Rather than special-casing that, construction runs through the vertex welder, so those
/// copies collapse to one vertex, the quad next to them degenerates to a triangle, and a profile
/// touching the axis produces a proper closed solid. A full revolution closes the same way: the
/// last ring welds onto the first.
/// </summary>
public sealed class RevolveFeature : SketchConsumingFeature
{
	public override string TypeName => "Revolve";

	/// <summary>
	/// Where the axis comes from.
	///
	/// THE TYPED AXIS WAS A RELIABLE ERROR, AND IS STILL THE DEFAULT. Custom runs through the sketch
	/// origin along X, and the sketch origin is exactly where people draw - so the first press of
	/// Revolve on a normal sketch hits a profile straddling its own axis and refuses. Correct, and a
	/// terrible first impression: the tool looks broken rather than unfinished, and the fix was to
	/// type numbers into two Vec3 boxes in sketch coordinates, which nobody can do from memory.
	///
	/// A lathe profile is revolved about one of its own edges essentially always, so the editor
	/// creates new Revolves on "Profile's left edge" - see EffigyWindow.NewFeature. It is NOT the
	/// default here, and that is deliberate: a ChoiceParam serialises its INDEX, and a document
	/// saved before this parameter existed has no line for it and loads on whatever index 0 is. If
	/// index 0 were an edge mode, every revolve in every saved file would quietly move to a
	/// different axis on the next open. Custom sits at 0 so those files rebuild exactly as they were.
	///
	/// The order is a promise for the same reason. Append, never reorder.
	/// </summary>
	public readonly ChoiceParam AxisMode = new( "Axis", new[]
	{
		"Custom", "Profile's left edge", "Profile's right edge", "Profile's bottom edge",
		"Profile's top edge", "Sketch X axis", "Sketch Y axis",
	} );

	public readonly Vec3Param AxisPoint = new( "Axis through (sketch coords)", Vec3.Zero );
	public readonly Vec3Param AxisDirection = new( "Axis direction (sketch coords)", new Vec3( 1, 0, 0 ) );
	public readonly FloatParam Angle = new( "Angle", 360f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	/// <summary>Index into AxisMode for the hand-typed axis. Zero so an old document, which has no
	/// AxisMode line at all, loads with the axis it was saved with.</summary>
	public const int AxisCustom = 0;

	/// <summary>What a NEW revolve should use, which is not what an old one defaults to. See AxisMode.</summary>
	public const int AxisProfileLeftEdge = 1;

	public override IReadOnlyList<IParam> Parameters => AxisMode.Index == AxisCustom
		? new IParam[] { Sketch, AxisMode, AxisPoint, AxisDirection, Angle, Segments, Result, Material }
		: new IParam[] { Sketch, AxisMode, Angle, Segments, Result, Material };

	/// <summary>The slot, for the same reason Extrude folds it away — it is answered by the
	/// Materials panel far more often than it is answered here.</summary>
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	/// <summary>
	/// The axis in sketch coordinates, from whichever mode is chosen.
	///
	/// The edge modes put the axis TANGENT to the profile - touching it, never through it - which is
	/// the one placement that is always legal, because a profile cannot straddle a line it only
	/// touches. That is what makes the default press work instead of refusing.
	/// </summary>
	(Vec2 Point, Vec2 Direction) ResolveAxis( List<Profile> profiles )
	{
		if ( AxisMode.Index == AxisCustom )
			return (new Vec2( AxisPoint.Value.x, AxisPoint.Value.y ),
				new Vec2( AxisDirection.Value.x, AxisDirection.Value.y ));

		if ( AxisMode.Value == "Sketch X axis" )
			return (Vec2.Zero, new Vec2( 1, 0 ));

		if ( AxisMode.Value == "Sketch Y axis" )
			return (Vec2.Zero, new Vec2( 0, 1 ));

		var min = new Vec2( float.MaxValue, float.MaxValue );
		var max = new Vec2( float.MinValue, float.MinValue );

		foreach ( var profile in profiles )
		{
			foreach ( var p in profile.Outer )
			{
				min = new Vec2( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ) );
				max = new Vec2( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ) );
			}
		}

		return AxisMode.Value switch
		{
			"Profile's right edge" => (new Vec2( max.x, 0 ), new Vec2( 0, 1 )),
			"Profile's bottom edge" => (new Vec2( 0, min.y ), new Vec2( 1, 0 )),
			"Profile's top edge" => (new Vec2( 0, max.y ), new Vec2( 1, 0 )),
			_ => (new Vec2( min.x, 0 ), new Vec2( 0, 1 )),
		};
	}

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch, ctx );

		// PAST A FULL TURN THE SWEEP OVERLAPS ITSELF, and the overlap welds: the result comes back
		// with edges shared by four or more faces and no error reported. Only exactly +-360 is
		// treated as a full revolution, so 720 was never going to mean "twice round" - it meant
		// "a broken mesh, quietly".
		if ( MathF.Abs( Angle.Value ) > 360f + 1e-3f )
			FailOn( "Angle",
				$"A revolve cannot exceed a full turn ({Angle.Value} degrees) — past 360 the sweep passes through itself.",
				$"{Angle.Value:0.###} degrees is more than one turn, and the extra overlap welds into a non-manifold solid.",
				"Set Angle to 360 or less" );

		if ( MathF.Abs( Angle.Value ) < 1e-4f )
		{
			FailOn( "Angle",
				"Angle cannot be zero",
				"A revolve with no angle produces no solid.",
				"Enter an angle greater than zero" );
		}

		var plane = sketch.Plane;
		var (axis2d, axisDir2d) = ResolveAxis( profiles );

		// The axis is authored in sketch coordinates and lifted into world space, so it moves with
		// the plane like everything else in the sketch.
		var axisOrigin = plane.ToWorld( axis2d );
		var axisDir = plane.XAxis * axisDir2d.x + plane.YAxis * axisDir2d.y;

		if ( axisDir.LengthSquared < 1e-12f )
		{
			FailOn( "Axis direction",
				"Axis direction cannot be zero",
				"A revolve needs an axis to spin around, and this one has no length.",
				"Set Axis to a non-zero direction in the sketch plane",
				"Or pick one of the profile's own edges from the Axis dropdown" );
		}

		_resolvedAxis = axis2d;
		_resolvedAxisDirection = axisDir2d;

		var full = MathF.Abs( MathF.Abs( Angle.Value ) - 360f ) < 1e-3f;

		foreach ( var profile in profiles )
		{
			// Every loop, not just the outer one. A hole straddling the axis is as meaningless as an
			// outer loop doing it, and for the same reason — each half sweeps the same surface.
			RejectIfCrossingAxis( profile.Outer );

			foreach ( var hole in profile.Holes )
				RejectIfCrossingAxis( hole );

			var mesh = BuildRevolve( plane, profile, axisOrigin, axisDir,
				Angle.Value, Segments.Clamped, full, Material.Clamped );

			OrientOutward( mesh );

			Emit( ctx, mesh );
		}
	}

	/// <summary>
	/// A profile straddling the axis is rejected, the way Onshape rejects it.
	///
	/// It is not merely unsupported, it is meaningless: each half sweeps the same solid, so every
	/// face is generated twice with opposite winding. The result passes a casual look, encloses
	/// zero volume, and welds vertices that should have stayed apart. Catching it here gives the
	/// user the real reason instead of a mesh that is quietly nonsense.
	/// </summary>
	/// <summary>The axis Execute settled on, so the straddle check tests what was actually used
	/// rather than what was typed. They differ in every mode but Custom.</summary>
	Vec2 _resolvedAxis;
	Vec2 _resolvedAxisDirection = new( 1, 0 );

	void RejectIfCrossingAxis( List<Vec2> loop )
	{
		var a = _resolvedAxis;
		var d = _resolvedAxisDirection;
		var length = MathF.Sqrt( d.x * d.x + d.y * d.y );

		if ( length < 1e-9f )
			return;

		var minSide = float.MaxValue;
		var maxSide = float.MinValue;

		foreach ( var p in loop )
		{
			// 2D cross product: signed perpendicular distance from the axis line.
			var side = (d.x * (p.y - a.y) - d.y * (p.x - a.x)) / length;
			minSide = MathF.Min( minSide, side );
			maxSide = MathF.Max( maxSide, side );
		}

		const float eps = 1e-5f;

		if ( minSide < -eps && maxSide > eps )
		{
			// Name the numbers. The default axis runs through the sketch origin, and people draw
			// around the origin, so this is the FIRST thing most Revolves hit - a message that just
			// says "move it" leaves you guessing which way and how far.
			Fail(
				$"The profile crosses the axis of revolution - it reaches {MathF.Abs( minSide ):0.###} "
				+ $"one side and {maxSide:0.###} the other. Move the axis at least {MathF.Abs( minSide ):0.###} "
				+ "so the whole profile sits on one side of it, or move the profile.",
				$"The profile reaches {MathF.Abs( minSide ):0.###} past the axis on one side and {maxSide:0.###} on the other, so each half would sweep the same solid twice.",
				$"Move the axis at least {MathF.Abs( minSide ):0.###} so the whole profile sits on one side",
				"Move the profile off the axis" );
		}
	}

	/// <summary>
	/// Flip every face if the finished solid encloses negative volume.
	///
	/// Whether a sweep comes out inside-out depends on the axis direction, the sign of the angle,
	/// and which side of the axis the profile sits on. Enumerating those cases invites getting one
	/// of them wrong silently; measuring the result instead is one cheap pass and is correct for
	/// all of them. Safe because a revolve is always closed — wrapped when full, capped when not.
	/// </summary>
	static void OrientOutward( PolyMesh mesh )
	{
		var volume = 0f;

		foreach ( var f in mesh.Faces )
			volume += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		if ( volume >= 0f )
			return;

		foreach ( var f in mesh.Faces )
		{
			Array.Reverse( f.Indices );
			Array.Reverse( f.UVs );
		}
	}

	/// <summary>
	/// Sweep a profile around the axis.
	///
	/// HOLES COST THE SAME TWO THINGS THEY COST AN EXTRUDE. Every loop sweeps, not just the outer
	/// one — and because ProfileFinder hands holes back wound the opposite way, the hole's quads come
	/// out facing into the hole with no sign handling anywhere. And a partial revolution's two end
	/// caps stop being single n-gons, because a face with a hole in it is not a polygon; they are
	/// triangulated around the holes instead, exactly as BuildPrism's are, with the same tradeoff
	/// against subdivision quality and the same guarantee that unholed profiles keep their n-gons.
	///
	/// A FULL revolution needs no caps at all, so a holed profile revolved all the way round pays
	/// nothing for its holes beyond the extra sweep.
	/// </summary>
	static PolyMesh BuildRevolve(
		SketchPlane plane, Profile profile, Vec3 axisOrigin, Vec3 axisDir,
		float angleDegrees, int segments, bool full, int material )
	{
		var mesh = new PolyMesh();
		var weld = new VertexWelder( mesh );

		// Outer first, then each hole — the order Triangulate.WithHoles indexes them in, so its
		// triples map straight onto the rings built below.
		var loops = new List<List<Vec2>> { profile.Outer };
		loops.AddRange( profile.Holes );

		var rings = segments;
		var step = angleDegrees / segments * MathF.PI / 180f;

		// ring[loop][k][i] is loop point i rotated by k steps. A full turn reuses ring 0 as the last
		// ring, which the welder achieves on its own by landing on identical positions.
		var ring = new int[loops.Count][][];

		for ( var li = 0; li < loops.Count; li++ )
		{
			var loop = loops[li];
			ring[li] = new int[rings + 1][];

			for ( var k = 0; k <= rings; k++ )
			{
				ring[li][k] = new int[loop.Count];
				var xform = Xform.RotateAbout( axisOrigin, axisDir, step * k );

				for ( var i = 0; i < loop.Count; i++ )
					ring[li][k][i] = weld.Add( xform.TransformPoint( plane.ToWorld( loop[i] ) ) );
			}
		}

		for ( var li = 0; li < loops.Count; li++ )
		{
			var n = loops[li].Count;

			for ( var k = 0; k < rings; k++ )
			{
				for ( var i = 0; i < n; i++ )
				{
					var j = (i + 1) % n;

					var quad = new[] { ring[li][k][i], ring[li][k][j], ring[li][k + 1][j], ring[li][k + 1][i] };

					var uvs = new[]
					{
						new Vec2( k / (float)rings, i / (float)n ),
						new Vec2( k / (float)rings, (i + 1) / (float)n ),
						new Vec2( (k + 1) / (float)rings, (i + 1) / (float)n ),
						new Vec2( (k + 1) / (float)rings, i / (float)n )
					};

					AddNonDegenerate( mesh, quad, uvs, material );
				}
			}
		}

		// A partial revolution is open at both ends and needs capping; a full one is already
		// closed, and adding caps would leave two faces buried inside the solid.
		if ( full )
			return mesh;

		if ( !profile.HasHoles )
		{
			var loop = profile.Outer;
			var n = loop.Count;

			var startCap = new int[n];
			var startUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				startCap[i] = ring[0][0][n - 1 - i];
				startUVs[i] = loop[n - 1 - i];
			}

			AddNonDegenerate( mesh, startCap, startUVs, material );

			var endCap = new int[n];
			var endUVs = new Vec2[n];

			for ( var i = 0; i < n; i++ )
			{
				endCap[i] = ring[0][rings][i];
				endUVs[i] = loop[i];
			}

			AddNonDegenerate( mesh, endCap, endUVs, material );

			return mesh;
		}

		// Flatten the loops into the single list WithHoles indexes against, so a triple it returns
		// reads as "the nth point, counting outer first then each hole in turn".
		var flat = new List<Vec2>();
		var loopOf = new List<int>();
		var withinLoop = new List<int>();

		for ( var li = 0; li < loops.Count; li++ )
		{
			for ( var i = 0; i < loops[li].Count; i++ )
			{
				flat.Add( loops[li][i] );
				loopOf.Add( li );
				withinLoop.Add( i );
			}
		}

		var triangles = Triangulate.WithHoles( profile.Outer, profile.Holes.Cast<IReadOnlyList<Vec2>>().ToList() );

		if ( triangles.Count == 0 )
		{
			Fail(
				$"This profile's {profile.Holes.Count} hole(s) could not be capped — the loops may cross each other. "
				+ "Check that every inner loop lies fully inside the outer one.",
				$"Ear clipping returned no triangles for a profile with {profile.Holes.Count} hole(s).",
				"Check that every inner loop lies fully inside the outer one",
				"Redraw the hole so it does not cross the outer loop" );
		}

		// Two n-gons per cap where the splitter will give them - see AddCaps, which does the same
		// thing for an extrude and explains why a hole makes two faces rather than one.
		var capLoops = Triangulate.SplitWithHoles( profile.Outer, profile.Holes.Cast<IReadOnlyList<Vec2>>().ToList() );

		if ( capLoops is not null )
		{
			foreach ( var loop in capLoops )
			{
				// The two caps are the same surface seen from opposite sides, so one is wound
				// backwards.
				var reversed = Enumerable.Reverse( loop ).ToList();

				AddNonDegenerate( mesh, reversed.Select( i => At( 0, i ) ).ToArray(),
					reversed.Select( i => flat[i] ).ToArray(), material );

				AddNonDegenerate( mesh, loop.Select( i => At( rings, i ) ).ToArray(),
					loop.Select( i => flat[i] ).ToArray(), material );
			}

			return mesh;
		}

		foreach ( var (a, b, c) in triangles )
		{
			// The two caps are the same surface seen from opposite sides, so one is wound backwards.
			AddNonDegenerate( mesh,
				new[] { At( 0, c ), At( 0, b ), At( 0, a ) },
				new[] { flat[c], flat[b], flat[a] }, material );

			AddNonDegenerate( mesh,
				new[] { At( rings, a ), At( rings, b ), At( rings, c ) },
				new[] { flat[a], flat[b], flat[c] }, material );
		}

		return mesh;

		int At( int k, int flatIndex ) => ring[loopOf[flatIndex]][k][withinLoop[flatIndex]];
	}

	/// <summary>
	/// Add a face, dropping repeated indices first and skipping it entirely if fewer than three
	/// remain.
	///
	/// This is what makes a profile touching the axis work. Those points weld to a single vertex,
	/// so the quad beside them arrives as (a, a, b, c) — collapsing it gives the triangle the
	/// geometry actually wants, and a fully degenerate face disappears instead of becoming a
	/// zero-area sliver that breaks normals downstream.
	/// </summary>
	static void AddNonDegenerate( PolyMesh mesh, int[] indices, Vec2[] uvs, int material )
	{
		var keptIndices = new List<int>( indices.Length );
		var keptUVs = new List<Vec2>( indices.Length );

		for ( var i = 0; i < indices.Length; i++ )
		{
			// Compare against the previous kept corner, and wrap for the last one, so runs of
			// duplicates collapse whether they are adjacent or straddle the end of the face.
			if ( keptIndices.Count > 0 && keptIndices[^1] == indices[i] )
				continue;

			keptIndices.Add( indices[i] );
			keptUVs.Add( uvs[i] );
		}

		while ( keptIndices.Count > 1 && keptIndices[0] == keptIndices[^1] )
		{
			keptIndices.RemoveAt( keptIndices.Count - 1 );
			keptUVs.RemoveAt( keptUVs.Count - 1 );
		}

		if ( keptIndices.Count < 3 )
			return;

		mesh.AddFace( keptIndices.ToArray(), keptUVs.ToArray(), material );
	}
}
