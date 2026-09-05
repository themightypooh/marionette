using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

// Features that reshape solids after they exist, whatever made them - a primitive, an extrude, a
// revolve. Kept apart from SketchFeatures deliberately: these care about meshes, not about where
// the mesh came from, and separating them keeps two people working on the two halves out of each
// other's way.

/// <summary>
/// Hollow the selected bodies to a wall thickness. The "make a room" feature.
///
/// OpenFaces is an index list because there is no face selection in the kernel yet — a viewport
/// would set it by clicking. Left empty, the result is a sealed hollow solid, which is what you
/// want for something that only needs to be light rather than enterable.
/// </summary>
public sealed class ShellFeature : Feature
{
	public override string TypeName => "Shell";

	/// <summary>A picked face becomes an opening; a picked part is what gets hollowed.</summary>
	public override GeometryKind Accepts => GeometryKind.Face | GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Thickness = new( "Wall thickness", 0.1f, 0.0001f, unit: "u" );

	/// <summary>
	/// Face indices to leave open. Not an IParam — a viewport sets this by picking, and a numeric
	/// list in a dialog would be unusable.
	///
	/// These indices are applied to EVERY selected body, which only makes sense when one body is
	/// selected. That is the normal case for a room, and the alternative — per-body face sets —
	/// needs a selection model the kernel does not have yet.
	/// </summary>
	public readonly List<int> OpenFaces = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Thickness };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		// Shell everything before assigning anything. Feature.Run promises that a failed feature
		// leaves the bodies as they were, and mutating in place breaks that promise the moment the
		// third body of four throws — you get a half-shelled model and an error message.
		var shelled = new List<PolyMesh>( targets.Count );

		foreach ( var body in targets )
		{
			try
			{
				shelled.Add( ShellOperation.Shell( body.Mesh, Thickness.Clamped, OpenFaces ) );
			}
			catch ( ArgumentOutOfRangeException e )
			{
				Fail(
					"An opening names a face that is not on this body",
					e.Message,
					"Pick faces that exist on the selected body" );
			}
			catch ( InvalidOperationException e )
			{
				RefuseShell( e.Message, body.Mesh );
			}
		}

		for ( var i = 0; i < targets.Count; i++ )
			targets[i].Mesh = shelled[i];
	}

	void RefuseShell( string message, PolyMesh mesh )
	{
		if ( message.Contains( "pinch" ) )
		{
			Fail(
				"The opened faces pinch to a point",
				message,
				"Open faces that share an edge",
				"Leave a face between the openings" );
		}

		if ( message.Contains( "every face" ) )
		{
			Fail(
				"Cannot open every face — there would be nothing left",
				"Every face was marked as an opening, so the shell has no wall to keep.",
				"Leave at least one face closed" );
		}

		if ( message.Contains( "open mesh" ) )
		{
			Fail(
				"Cannot shell an open mesh",
				message,
				"Close the mesh first" );
		}

		var fit = SuggestThickness( mesh, Thickness.Clamped, OpenFaces );

		if ( fit > 0f )
		{
			var suggestion = FloorThousandths( fit );
			FailOn( "Wall thickness", suggestion,
				"This wall thickness does not fit this part",
				message,
				$"Reduce wall thickness to {suggestion:0.###}",
				"Open a face so the offset has room to move" );
		}

		FailOn( "Wall thickness",
			"This shell cannot be built",
			message,
			"Reduce the wall thickness",
			"Open a face so the offset has room to move" );
	}

	static float SuggestThickness( PolyMesh mesh, float size, List<int> open )
	{
		if ( size <= 0f )
			return 0f;

		if ( Fits( mesh, size, open ) )
			return size;

		var lo = 0.0001f;
		var hi = size;

		if ( !Fits( mesh, lo, open ) )
			return 0f;

		for ( var i = 0; i < 12; i++ )
		{
			var mid = ( lo + hi ) * 0.5f;

			if ( Fits( mesh, mid, open ) )
				lo = mid;
			else
				hi = mid;
		}

		return lo;
	}

	static bool Fits( PolyMesh mesh, float thickness, List<int> open )
	{
		try
		{
			ShellOperation.Shell( mesh, thickness, open );
			return true;
		}
		catch ( InvalidOperationException )
		{
			return false;
		}
		catch ( ArgumentOutOfRangeException )
		{
			return false;
		}
	}
}

/// <summary>
/// Flat chamfer along every edge sharper than the angle threshold — Onshape's Chamfer.
///
/// THE FIELD IS STILL CALLED `Width` AND THE LABEL IS "Distance". Those disagree on purpose. The
/// label is what Onshape calls the dimension and what anyone reading the dialog expects; the field
/// name is the key StudioDocument writes into a saved file, so renaming it would silently drop the
/// distance out of every document already on disk. A stale field name costs one comment. See
/// StudioDocument.StateFields.
/// </summary>
public sealed class ChamferFeature : Feature
{
	public override string TypeName => "Chamfer";

	/// <summary>Edges are what it blends, and a picked FACE means its boundary - "select the top,
	/// then Fillet" - so both count. See Feature.ApplyBlendEdges.</summary>
	public override GeometryKind Accepts => GeometryKind.Edge | GeometryKind.Face | GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Width = new( "Distance", 0.1f, 0.0001f, unit: "u" );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );

	/// <summary>Edges to cut. Empty means every edge sharper than the angle threshold, which is
	/// how this feature behaved before edges could be picked and how a Fillet with no selection
	/// still should.</summary>
	public List<EdgeRef> Edges = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Width, AngleThreshold };

	protected override void Execute( FeatureContext ctx )
	{
		CommitBlend( BlendBodies( ctx, Edges, ( mesh, keys ) =>
			keys is null
				? EdgeBlend.ChamferReport( mesh, Width.Clamped, AngleThreshold.Clamped )
				: EdgeBlend.ChamferReport( mesh, Width.Clamped, keys ) ), "Distance" );
	}
}

/// <summary>
/// Rounded fillet along every edge sharper than the angle threshold — Onshape's Fillet.
///
/// A SEPARATE FEATURE RATHER THAN A CHAMFER WITH SEGMENTS TURNED UP, because that is what it is to
/// the person using it, and because the dimension means something different: a chamfer's distance
/// is measured back along each face, a fillet's radius is the arc's own radius and the setback
/// follows from the angle the edge opens at. One control that means two things depending on
/// another control is the shape of a bad dialog. See EdgeBlend for the one algorithm underneath.
///
/// `Segments` has no Onshape counterpart because Onshape is a B-rep and stores the arc exactly.
/// This kernel is polygonal, so how finely the arc is cut into faces is a real authoring decision
/// and belongs in the dialog.
/// </summary>
public sealed class FilletFeature : Feature
{
	public override string TypeName => "Fillet";

	/// <summary>Edges are what it blends, and a picked FACE means its boundary - "select the top,
	/// then Fillet" - so both count. See Feature.ApplyBlendEdges.</summary>
	public override GeometryKind Accepts => GeometryKind.Edge | GeometryKind.Face | GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Radius = new( "Radius", 0.1f, 0.0001f, unit: "u" );
	public readonly IntParam Segments = new( "Segments", 4, 1, 16 );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );

	/// <summary>Edges to round. Empty means every edge sharper than the angle threshold — see
	/// ChamferFeature.Edges.</summary>
	public List<EdgeRef> Edges = new();

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Radius, Segments, AngleThreshold };

	protected override void Execute( FeatureContext ctx )
	{
		CommitBlend( BlendBodies( ctx, Edges, ( mesh, keys ) =>
			keys is null
				? EdgeBlend.FilletReport( mesh, Radius.Clamped, AngleThreshold.Clamped, Segments.Clamped )
				: EdgeBlend.FilletReport( mesh, Radius.Clamped, Segments.Clamped, keys ) ), "Radius" );
	}
}

/// <summary>
/// Re-project UVs across the selected bodies. Onshape has no equivalent because it does not care
/// about textures; every game-facing modeller needs one.
///
/// Placed as a feature rather than an export option on purpose: where it sits in the tree decides
/// what it sees. Before a bevel it projects the sharp cage and the chamfer strips inherit
/// interpolated UVs; after a bevel it projects the chamfers as their own faces.
/// </summary>
public sealed class UVProjectFeature : Feature
{
	public override string TypeName => "UV project";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Box", "Planar", "Unwrap" } );
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Scale = new( "Units per tile", 1f, 0.0001f, unit: "u" );

	/// <summary>How far a face may lean from its chart before it starts a new one. Only Unwrap has
	/// anything to say about it.</summary>
	public readonly FloatParam ChartAngle = new( "Chart angle", 66f, 1f, 179f, unit: "deg" );

	/// <summary>Gutter between islands. The bake bleeds its islands outward so seams do not glow
	/// under mipmapping, and without a gutter that bleed runs into the neighbour.</summary>
	public readonly FloatParam Margin = new( "Island margin", 0.01f, 0f, 0.2f );

	public override IReadOnlyList<IParam> Parameters => Mode.Value switch
	{
		"Planar" => new IParam[] { Bodies, Mode, Direction, Scale },
		"Unwrap" => new IParam[] { Bodies, Mode, ChartAngle, Margin },
		_ => new IParam[] { Bodies, Mode, Scale },
	};

	protected override void Execute( FeatureContext ctx )
	{
		if ( Mode.Value == "Planar" && Direction.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Direction",
				"Projection direction has no length",
				"A planar projection needs a direction, and this one is (0, 0, 0).",
				"Set Direction to the axis you want the texture to face" );
		}

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			// Unwrap is the only one of the three a BAKE can use. Box and planar tile on purpose and
			// overlap by construction, which is right for a texture and useless for a normal map -
			// see UVUnwrap and NormalBake.Measure.
			if ( Mode.Value == "Unwrap" )
			{
				var report = UVUnwrap.Unwrap( body.Mesh, ChartAngle.Clamped, Margin.Clamped );

				if ( report.SkippedFaces > 0 )
				{
					Warn(
						"Some faces could not be unwrapped",
						$"{report}. A face with no area has no direction to flatten onto.",
						"Check the body for degenerate faces" );
				}
			}
			else if ( Mode.Value == "Planar" )
			{
				UVProjection.PlanarProject( body.Mesh, Direction.Value, Scale.Clamped );
			}
			else
			{
				UVProjection.BoxProject( body.Mesh, Scale.Clamped );
			}
		}
	}
}


/// <summary>
/// Assigns a material slot to picked faces.
///
/// Faces have carried a material slot since the beginning and every exporter groups by it — OBJ
/// writes usemtl, SMD and DMX name a material per face — so a model has always been able to arrive
/// in ModelDoc with several slots to bind. What was missing was any way to say which faces. Extrude
/// puts its whole solid on one slot and nothing could change it afterwards, so in practice every
/// model was single-material whatever the exporters were prepared to do.
///
/// A FEATURE, NOT AN EDIT. Painting the mesh directly would be undone by the next rebuild, since
/// bodies are rebuilt from scratch every time. Sitting in the tree means the assignment is re-applied
/// after the geometry it paints is remade, and it can be rolled back, suppressed and reordered like
/// anything else.
///
/// Faces are held as FaceRefs, so the reference survives the rebuild that recreates them — the same
/// machinery a sketch drawn on a face uses, resolved through the same function so the two cannot
/// disagree about which face is meant.
/// </summary>
/// <summary>
/// Drill a hole where a face was picked.
///
/// CONVENIENCE, NOT CAPABILITY, and worth saying because it decides what belongs here. Holes already
/// work as inner loops of a profile and cuts already work through MeshBoolean; what was missing was
/// that nobody wants to draw two concentric circles and extrude them when the numbers they have are
/// "6mm clearance, 10mm head, 6 deep". So this is a parameterised shape and a dialog, and the shape
/// itself lives in HoleOperation.
///
/// IT NEEDS A BOOLEAN PROVIDER, like every other cut. Taking material away means recomputing the
/// surface, and the engine does that inside the s&amp;box editor. Headless, the suite installs a stub -
/// see MergeTests - so this feature can still be tested end to end without one.
/// </summary>
public sealed class HoleFeature : Feature
{
	public override string TypeName => "Hole";

	public override GeometryKind Accepts => GeometryKind.Face;

	/// <summary>Where the holes go. One per picked face, drilled along that face's own normal, so a
	/// hole rides its face the way a sketch does.</summary>
	public List<FaceRef> Faces = new();

	public readonly ChoiceParam Style = new( "Style", new[] { "Simple", "Counterbore", "Countersink" } );
	public readonly FloatParam Diameter = new( "Diameter", 0.25f, 0.0001f, unit: "u" );

	/// <summary>Zero means through everything. A through hole is built long enough to leave the body
	/// either side, because a tool that stops exactly at the far surface gives the boolean two
	/// coplanar faces and those are the ones that produce slivers.</summary>
	public readonly FloatParam Depth = new( "Depth (0 = through)", 0f, 0f, unit: "u" );

	public readonly FloatParam HeadDiameter = new( "Head diameter", 0.5f, 0.0001f, unit: "u" );
	public readonly FloatParam HeadDepth = new( "Head depth", 0.15f, 0.0001f, unit: "u" );
	public readonly FloatParam SinkAngle = new( "Countersink angle", 90f, 1f, 179f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 24, 6, 256 );

	public override IReadOnlyList<IParam> Parameters => Style.Value switch
	{
		"Counterbore" => new IParam[] { Style, Diameter, Depth, HeadDiameter, HeadDepth, Segments },
		"Countersink" => new IParam[] { Style, Diameter, Depth, HeadDiameter, SinkAngle, Segments },
		_ => new IParam[] { Style, Diameter, Depth, Segments },
	};

	protected override void Execute( FeatureContext ctx )
	{
		if ( Faces.Count == 0 )
		{
			Fail(
				"No faces picked - click where the holes should go",
				"A hole is drilled into a face along that face's own normal, and none have been chosen yet.",
				"Click a face in the viewport" );
		}

		if ( !MeshBoolean.Available )
		{
			Fail(
				"Drilling a hole needs the engine's boolean",
				"Taking material away means recomputing the surface, and no boolean provider is installed.",
				"Run this inside the s&box editor, where the provider is available" );
		}

		var style = Style.Index switch
		{
			1 => HoleStyle.Counterbore,
			2 => HoleStyle.Countersink,
			_ => HoleStyle.Simple,
		};

		var drilled = 0;
		var lost = 0;
		var separated = 0;
		var separatedBodies = new List<string>();

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var faceIndex ) )
			{
				lost++;
				continue;
			}

			var mesh = body.Mesh;
			var face = mesh.Faces[faceIndex];
			var normal = mesh.FaceNormal( face );

			if ( normal.LengthSquared < 1e-16f )
			{
				lost++;
				continue;
			}

			// INTO the solid, which is against the face's outward normal. Drilling along it would put
			// the tool entirely outside the body and cut nothing at all - and a boolean that removes
			// nothing succeeds, so this would look like the feature quietly not working.
			var into = -normal.Normal;
			var at = reference.Point;

			PolyMesh tool;

			try
			{
				tool = HoleOperation.Build( style, at, into, Diameter.Clamped, Depth.Clamped,
					HeadDiameter.Clamped, HeadDepth.Clamped, SinkAngle.Clamped,
					body.Mesh.BoundsDiagonal, Segments.Clamped );
			}
			catch ( InvalidOperationException e )
			{
				Fail( "This hole cannot be built", e.Message,
					"Make the head wider than the shaft",
					"Check the diameter and depth" );
				return;
			}

			body.Mesh = MeshBoolean.Apply( BooleanOp.Subtract, body.Mesh, tool );
			drilled++;

			// A hole drilled right through a thin wall can sever the part - a slot across a bar is
			// the obvious one. See Feature.SeparatePieces.
			var added = SeparatePieces( ctx, body );

			if ( added > 0 )
			{
				separated += added;

				if ( !separatedBodies.Contains( body.Name ) )
					separatedBodies.Add( body.Name );
			}
		}

		if ( drilled == 0 )
		{
			Fail(
				"None of the picked faces are on the model any more",
				$"All {Faces.Count} of them named geometry the features above this one no longer produce.",
				"Pick the faces again on the current model" );
		}

		// ONE DIAGNOSTIC PER FEATURE, so the two possible warnings cannot both be set and the second
		// would silently overwrite the first. Lost faces are the one to lead with - they mean the
		// feature did less than it was asked - and a separation that happened alongside is named in
		// the same message rather than dropped.
		if ( lost > 0 )
		{
			var also = separated > 0
				? $" Drilling also separated {Listed( separatedBodies )} into {separated} more part(s)."
				: "";

			Warn(
				$"{lost} of {Faces.Count} picked faces are no longer on the model",
				"They named geometry the features above this one no longer produce, so they were skipped." + also,
				"Pick them again on the current model" );

			return;
		}

		if ( separatedBodies.Count == 1 )
		{
			WarnSeparated( separated, separatedBodies[0] );
			return;
		}

		if ( separatedBodies.Count > 1 )
		{
			Warn(
				$"Drilling separated {separatedBodies.Count} parts",
				$"{Listed( separatedBodies )} each went all the way through, adding {separated} part(s) to the studio. "
					+ "Each original keeps its name and id and its largest piece.",
				"Reduce the depth if these were meant to be pockets",
				"Nothing to fix if separating the parts was the intent" );
		}
	}
}

/// <summary>
/// Move picked faces of a solid that already exists — push and pull on a finished part.
///
/// THE FEATURE THAT CLOSES THE "edit it afterwards" GAP. Every other tool here builds forwards: a
/// sketch becomes a prism, the prism gets a fillet, and changing the prism means going back to the
/// sketch. This one takes a face of whatever is on screen and moves it, with the walls around it
/// following, and it does not care whether that face came from a primitive, an extrude or a boolean.
///
/// TWO MODES, and the difference only shows on a pair of facing walls:
///
/// - **Offset** moves each face along its own normal, so a facing pair moves APART — the wall gets
///   thicker or thinner.
/// - **Translate** moves them all one way, so a facing pair SLIDES and keeps its thickness. That is
///   the one you want when a wall is in the wrong place rather than the wrong size.
///
/// See FaceMove for the solve, for why the two modes are one code path, and for the list of things
/// this refuses rather than approximating.
///
/// IT LIVES IN THE FEATURE TREE LIKE EVERYTHING ELSE, and that is the whole bet of direct editing
/// here: the faces are held as FaceRefs, so an upstream edit that destroys one makes this feature
/// say so out loud rather than reattaching itself to whatever is nearest. See FacePlane's header for
/// why the reference is geometric rather than an index, and DraftFeature for the same contract.
/// </summary>
public sealed class MoveFaceFeature : Feature
{
	public override string TypeName => "Move face";

	public override GeometryKind Accepts => GeometryKind.Face;

	/// <summary>The faces to move. Not an IParam, for the reason FaceMaterialFeature gives: a list
	/// of picked geometry has no generic control to render it.</summary>
	public List<FaceRef> Faces = new();

	/// <summary>
	/// Offset first because it is the one that needs no second answer — a distance and nothing else,
	/// which is what "push this face out a bit" means. Translate needs a direction as well.
	/// </summary>
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Offset", "Translate" } );

	public const int ModeOffset = 0;
	public const int ModeTranslate = 1;

	/// <summary>How far. NEGATIVE IS ALLOWED and is half the point: pushing a face into the solid is
	/// the same operation as pulling it out, and making that a separate direction to type would be
	/// two controls for one number.</summary>
	public readonly FloatParam Distance = new( "Distance", 0.25f, unit: "u" );

	/// <summary>Which way, in Translate mode. Ignored in Offset mode, where each face has a
	/// direction of its own already.</summary>
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 0, 0, 1 ) );

	public override IReadOnlyList<IParam> Parameters => Mode.Index == ModeTranslate
		? new IParam[] { Mode, Distance, Direction }
		: new IParam[] { Mode, Distance };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Faces.Count == 0 )
		{
			Fail(
				"No faces picked - click the faces to move",
				"This moves faces of a part that already exists, and none have been chosen yet.",
				"Click a face of the part in the viewport, then press Move Face" );
		}

		if ( Mode.Index == ModeTranslate && Direction.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Direction",
				"The direction has no length",
				"Translate moves the faces along one direction, and this one is (0, 0, 0).",
				"Set a direction, or switch to Offset and let each face use its own normal" );
		}

		// Grouped by body so one call moves every face picked on it. Moving them one at a time would
		// solve a shared corner once per face it belongs to, and the second solve would be against a
		// mesh the first had already moved - the same reason DraftFeature groups.
		var byBody = new Dictionary<Body, List<int>>();
		var lost = 0;

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var faceIndex ) )
			{
				lost++;
				continue;
			}

			if ( !byBody.TryGetValue( body, out var list ) )
				byBody[body] = list = new List<int>();

			if ( !list.Contains( faceIndex ) )
				list.Add( faceIndex );
		}

		if ( byBody.Count == 0 )
		{
			Fail(
				"None of the picked faces are on the model any more",
				$"All {Faces.Count} of them named geometry that the features above this one no longer produce.",
				"Pick the faces again on the current model",
				"Move this feature back below the edit that changed them" );
		}

		var moved = new List<(Body Body, PolyMesh Mesh)>();

		// Every body solved before anything is assigned, so a failure on the third of four leaves the
		// model as it was rather than half moved - the same promise ShellFeature and Draft make.
		foreach ( var (body, faces) in byBody )
		{
			try
			{
				moved.Add( (body, Solve( body.Mesh, faces )) );
			}
			catch ( InvalidOperationException e )
			{
				FailOn( Mode.Index == ModeTranslate ? "Direction" : "Distance",
					"That move cannot be made exactly",
					e.Message,
					"Use a smaller distance",
					"Select the whole flat surface rather than part of it" );
			}
		}

		foreach ( var (body, mesh) in moved )
			body.Mesh = mesh;

		if ( lost > 0 )
		{
			Warn(
				$"{lost} of {Faces.Count} picked faces are no longer on the model",
				"They named geometry the features above this one no longer produce, so they were skipped.",
				"Pick them again on the current model" );
		}
	}

	PolyMesh Solve( PolyMesh mesh, List<int> faces ) => Mode.Index == ModeTranslate
		? FaceMove.Translate( mesh, faces, Direction.Value.Normal * Distance.Value )
		: FaceMove.Offset( mesh, faces, Distance.Value );
}

/// <summary>
/// Taper picked faces of a solid that already exists, so the part can leave a mould.
///
/// Extrude's Taper covers a face being MADE; this covers one that is already there, which by the
/// time you need it is usually twenty features back with fillets and cuts on top of it. See
/// DraftOperation for the method and for why a face looking straight along the pull cannot be
/// drafted at all.
/// </summary>
public sealed class DraftFeature : Feature
{
	public override string TypeName => "Draft";

	public override GeometryKind Accepts => GeometryKind.Face;

	/// <summary>The faces to taper. Not an IParam, for the reason FaceMaterialFeature gives: a list
	/// of picked geometry has no generic control to render it.</summary>
	public List<FaceRef> Faces = new();

	public readonly Vec3Param Pull = new( "Pull direction", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Angle = new( "Draft angle", 3f, -88f, 88f, unit: "deg" );

	/// <summary>
	/// Where the parting line sits, measured along the pull from the origin.
	///
	/// One number rather than a point, because the plane is always perpendicular to the pull - a
	/// neutral plane at any other angle is not a parting line, it is two different drafts.
	/// </summary>
	public readonly FloatParam Neutral = new( "Neutral plane", 0f, unit: "u" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Pull, Angle, Neutral };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Faces.Count == 0 )
		{
			Fail(
				"No faces picked - click the faces to taper",
				"A draft leans faces away from a parting line, and none have been chosen yet.",
				"Click the walls of the part in the viewport" );
		}

		if ( Pull.Value.LengthSquared < 1e-12f )
		{
			FailOn( "Pull direction",
				"The pull direction has no length",
				"A draft leans faces relative to the direction the part is pulled, and this one is (0, 0, 0).",
				"Set the pull to the axis the mould opens along" );
		}

		// Grouped by body so one call drafts every face picked on it: drafting them one at a time
		// would move a shared vertex once per face it belongs to, and the corner between two drafted
		// walls would lean twice as far as either.
		var byBody = new Dictionary<Body, List<int>>();
		var lost = 0;

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var faceIndex ) )
			{
				lost++;
				continue;
			}

			if ( !byBody.TryGetValue( body, out var list ) )
				byBody[body] = list = new List<int>();

			if ( !list.Contains( faceIndex ) )
				list.Add( faceIndex );
		}

		if ( byBody.Count == 0 )
		{
			Fail(
				"None of the picked faces are on the model any more",
				$"All {Faces.Count} of them named geometry that the features above this one no longer produce.",
				"Pick the faces again on the current model",
				"Move this feature back below the edit that changed them" );
		}

		var neutral = Pull.Value.Normal * Neutral.Value;
		var drafted = new List<(Body Body, PolyMesh Mesh)>();

		// Every body drafted before anything is assigned, so a failure on the third of four leaves
		// the model as it was rather than half tapered - the same promise ShellFeature makes.
		foreach ( var (body, faces) in byBody )
		{
			try
			{
				drafted.Add( (body, DraftOperation.Draft( body.Mesh, faces, neutral, Pull.Value, Angle.Clamped )) );
			}
			catch ( InvalidOperationException e )
			{
				RefuseDraft( e.Message, body, faces, neutral );
			}
		}

		foreach ( var (body, mesh) in drafted )
			body.Mesh = mesh;

		if ( lost > 0 )
		{
			Warn(
				$"{lost} of {Faces.Count} picked faces are no longer on the model",
				"They named geometry the features above this one no longer produce, so they were skipped.",
				"Pick them again on the current model" );
		}
	}

	void RefuseDraft( string message, Body body, List<int> faces, Vec3 neutral )
	{
		// "Inside out" and "collapsed" are both an angle that is too big, and the useful answer to
		// both is the largest one that still works - measured, not guessed.
		if ( message.Contains( "inside out" ) || message.Contains( "collapses" ) )
		{
			var largest = DraftOperation.LargestAngle( body.Mesh, faces, neutral, Pull.Value, Angle.Clamped );

			FailOn( "Draft angle", largest,
				$"A draft of {Angle.Clamped}deg turns this part inside out",
				$"{message} The faces are not deep enough either side of the neutral plane to lean that far.",
				$"Use {largest:0.##}deg or less",
				"Move the neutral plane closer to the middle of the wall" );
		}

		if ( message.Contains( "straight along the pull" ) )
		{
			FailOn( "Pull direction",
				"These faces cannot be drafted along this pull",
				message + " A face's draft is a lean of its own normal, and a normal parallel to the pull has nothing to lean.",
				"Pick the walls rather than the top and bottom",
				"Set the pull to an axis the picked faces run along" );
		}

		Fail( "This draft cannot be applied", message, "Try a smaller angle" );
	}
}

public sealed class FaceMaterialFeature : Feature
{
	public override string TypeName => "Face material";

	public override GeometryKind Accepts => GeometryKind.Face;

	/// <summary>The faces to paint. Not an IParam: a list of picked geometry has no generic control
	/// to render it, the way a float or a choice does, and the dialog builds it a selection box of
	/// its own.</summary>
	public List<FaceRef> Faces = new();

	public readonly IntParam Material = new( "Material slot", 1, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Material };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Faces.Count == 0 )
		{
			Fail(
				"No faces picked — click the faces to assign this material to.",
				"A face-material feature paints faces, and none have been chosen yet.",
				"Click faces in the viewport to assign this material" );
		}

		var painted = 0;
		var lost = 0;

		foreach ( var reference in Faces )
		{
			if ( !FacePlane.TryResolveFace( ctx.Bodies, reference, out var body, out var faceIndex ) )
			{
				lost++;
				continue;
			}

			// THE WHOLE SURFACE, not the one n-gon the reference resolved to. A wall a boolean has
			// returned as fragments is one face to look at and one face to click, and the viewport
			// lights all of it - painting a single fragment would turn a third of a wall red and
			// leave the rest as it was, which is the highlight promising something the click does
			// not deliver. FaceSurface stops at a neighbour already on another slot, so this can
			// never eat an assignment somebody else made.
			var surface = FaceSurface.FromFace( body.Mesh, faceIndex );

			if ( surface.IsEmpty )
				body.Mesh.Faces[faceIndex].Material = Material.Clamped;
			else
				foreach ( var index in surface.Faces )
					body.Mesh.Faces[index].Material = Material.Clamped;

			painted++;
		}

		// Losing SOME faces is a warning: an upstream edit that removes one face out of twelve is
		// ordinary, and failing the feature over it would blank the other eleven. Losing ALL of them
		// means the geometry moved out from under the whole assignment, which is worth stopping for
		// rather than leaving a feature in the tree that silently does nothing.
		if ( painted == 0 )
		{
			Fail(
				$"None of the {Faces.Count} picked face(s) still exist — the geometry changed underneath them. Pick them again.",
				$"All {Faces.Count} stored face(s) failed to resolve against the bodies as they are now.",
				"Pick the faces again on the current geometry" );
		}

		if ( lost > 0 )
		{
			Warn(
				$"{lost} of {Faces.Count} picked faces no longer exist and were skipped.",
				$"{painted} face(s) still painted; {lost} could not be found after an upstream edit.",
				"Pick the missing faces again if they still matter" );
		}
	}
}
