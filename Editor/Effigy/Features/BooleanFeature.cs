using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Combine two solids: Union, Subtract, Intersect.
///
/// WHY THIS DID NOT EXIST FOR SO LONG, given the machinery did. MeshBoolean has been installed and
/// working since the engine provider landed, and two features already used it — Extrude's Remove
/// and Hole both call Subtract. But both of them build their own tool solid and cut with it, so
/// the boolean was only ever reachable by drawing a profile or drilling a hole. There was no way
/// to point at two bodies you already had and say "these are one thing now", which is the first
/// thing anybody tries in a solid modeller and the one thing the tool bar did not offer.
///
/// TARGETS AND TOOLS, NOT A AND B. Subtract is not symmetric and Union is, so a single flat list
/// of bodies cannot express all three operations — "subtract these" has to say which side the hole
/// goes in. Union ignores the split and folds everything named on either side into one body, which
/// is the honest reading rather than a special case: with a symmetric operation there is nothing
/// for the distinction to mean.
///
/// THE TOOL IS CONSUMED BY DEFAULT, which is what Onshape does and what the word means — a cutting
/// tool that survives the cut leaves you with the offcut sitting inside the hole it just made, and
/// the next feature has to know to ignore it. KeepTools is there because a tool reused for six
/// cuts is a real pattern, and because a body that vanishes with no way to get it back is worse
/// than one you have to tidy up.
///
/// THE TARGET KEEPS ITS ID. Everything downstream is holding it — a sketch on one of its faces, a
/// later feature's body selection — and a boolean must not invalidate that. This is the same rule
/// SeparatePieces follows for the offcuts of a cut, and for the same reason.
/// </summary>
public sealed class BooleanFeature : Feature
{
	public override string TypeName => "Boolean";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly ChoiceParam Operation = new( "Operation", new[] { "Union", "Subtract", "Intersect" } );

	/// <summary>What is being cut, or the first of the things being merged.</summary>
	public readonly BodySelectionParam Targets = new( "Target" );

	/// <summary>What is doing the cutting. Must be named explicitly — see Execute.</summary>
	public readonly BodySelectionParam Tools = new( "Tool" );

	public readonly BoolParam KeepTools = new( "Keep tool bodies", false );

	public override IReadOnlyList<IParam> Parameters =>
		Operation.Index == 0
			? new IParam[] { Operation, Targets, Tools }
			: new IParam[] { Operation, Targets, Tools, KeepTools };

	BooleanOp Op => Operation.Index switch
	{
		1 => BooleanOp.Subtract,
		2 => BooleanOp.Intersect,
		_ => BooleanOp.Union,
	};

	protected override void Execute( FeatureContext ctx )
	{
		if ( ctx.Bodies.Count < 2 )
		{
			Fail(
				"A boolean needs two solids",
				ctx.Bodies.Count == 0
					? "This studio has no bodies yet, so there is nothing to combine."
					: "This studio has one body, and a boolean combines two.",
				"Add a Primitive, or extrude a sketch, so there are two solids to combine" );
		}

		// TOOLS MUST BE NAMED, and this is the one place a BodySelectionParam's "empty means all"
		// default is actively wrong. Everywhere else that default is the sane reading of an
		// unmade choice — Shell every body, subdivide the whole part. Here it would mean "cut
		// this with everything, itself included", which is not a thing anybody meant, and the
		// operation is destructive enough that guessing is the wrong instinct.
		if ( Tools.BodyIds.Count == 0 )
		{
			FailOn( Tools.Label,
				"No tool body is chosen",
				Op == BooleanOp.Union
					? "A union needs to know which bodies to merge, and none have been picked."
					: $"A {OpName( Op )} needs to know which body is doing the cutting, and none has been picked.",
				"Click a body in the Parts list, or in the viewport, to use it as the tool" );
		}

		var tools = ctx.Bodies.Where( Tools.Matches ).ToList();

		if ( tools.Count == 0 )
		{
			FailOn( Tools.Label,
				"The tool body is gone",
				"The body chosen as the tool is no longer in the studio — a feature above this one "
					+ "may have deleted, renamed or consumed it.",
				"Pick a tool body that still exists" );
		}

		// TARGETS DEFAULT TO EVERYTHING THAT IS NOT A TOOL, rather than to everything. The plain
		// reading of "subtract this peg" in a studio holding a block and a peg is "from the
		// block", and taking the default literally would ask the peg to be subtracted from itself.
		var targets = Targets.BodyIds.Count > 0
			? ctx.Bodies.Where( Targets.Matches ).ToList()
			: ctx.Bodies.Where( b => !Tools.Matches( b ) ).ToList();

		var overlap = targets.Where( t => tools.Contains( t ) ).ToList();

		if ( overlap.Count > 0 )
		{
			FailOn( Targets.Label,
				"A body cannot be its own tool",
				$"{Describe( overlap )} is picked as both the target and the tool, and a solid "
					+ "cut by itself leaves nothing behind.",
				"Pick a different body as the tool" );
		}

		if ( targets.Count == 0 )
		{
			FailOn( Targets.Label,
				"Nothing is left to act on",
				"Every body in the studio is picked as a tool, so there is no target for the "
					+ "operation to change.",
				"Leave at least one body out of the tool selection" );
		}

		if ( Op == BooleanOp.Union )
			Unite( ctx, targets, tools );
		else
			CutOrKeep( ctx, targets, tools );
	}

	/// <summary>
	/// Fold every named body into the first of them.
	///
	/// The FIRST IN DOCUMENT ORDER, not the first in the selection, so a union of the same three
	/// bodies produces the same surviving id however they happened to be clicked. A rebuild that
	/// renamed its own output depending on click order would be a downstream reference that breaks
	/// when you reselect.
	/// </summary>
	void Unite( FeatureContext ctx, List<Body> targets, List<Body> tools )
	{
		var all = ctx.Bodies.Where( b => targets.Contains( b ) || tools.Contains( b ) ).ToList();

		if ( all.Count < 2 )
		{
			Fail(
				"A union needs two solids",
				"Only one body is named on either side, and merging a body with itself does nothing.",
				"Pick a second body to merge with" );
		}

		var kept = all[0];

		foreach ( var other in all.Skip( 1 ) )
		{
			kept.Mesh = MeshBoolean.Apply( BooleanOp.Union, kept.Mesh, other.Mesh );
			ctx.Bodies.Remove( other );
		}

		// A union of solids that do not touch is a legal boolean and produces a mesh in two
		// pieces. That is not an error — a handle and a lid really are one part in some designs —
		// but the Parts list has to be told, or the studio quietly holds one body that looks like
		// two and every later face pick is ambiguous about which piece it meant.
		if ( MeshSplit.PieceCount( kept.Mesh ) > 1 )
		{
			Warn(
				"The merged bodies do not touch",
				$"{kept.Name} is now one body in {MeshSplit.PieceCount( kept.Mesh )} separate pieces, "
					+ "because the solids that were merged do not overlap.",
				"Move them so they intersect, if they were meant to become one solid" );
		}
	}

	/// <summary>Subtract or intersect: every tool applied to every target, in order.</summary>
	void CutOrKeep( FeatureContext ctx, List<Body> targets, List<Body> tools )
	{
		var op = Op;
		var emptied = new List<string>();
		var separated = 0;

		foreach ( var target in targets )
		{
			foreach ( var tool in tools )
				target.Mesh = MeshBoolean.Apply( op, target.Mesh, tool.Mesh );

			// AN EMPTY RESULT IS A SUCCESSFUL BOOLEAN AND A USELESS PART. Subtracting a block from
			// a peg inside it, or intersecting two solids that never touch, both come back as a
			// mesh with nothing in it — the operation did exactly what was asked. Left alone it
			// shows up as a part that silently disappeared from the viewport, which reads as a
			// crash rather than as an answer.
			if ( target.Mesh is null || target.Mesh.FaceCount == 0 )
			{
				emptied.Add( target.Name );
				continue;
			}

			separated += SeparatePieces( ctx, target );
		}

		if ( emptied.Count > 0 )
		{
			Warn(
				emptied.Count == 1 ? $"{emptied[0]} has nothing left" : $"{emptied.Count} bodies have nothing left",
				op == BooleanOp.Subtract
					? $"{Describe( emptied )} was entirely inside the tool, so subtracting it removed everything."
					: $"{Describe( emptied )} does not overlap the tool, so there is no shared volume to keep.",
				op == BooleanOp.Subtract
					? "Make the tool smaller, or move it so it only covers part of the target"
					: "Move the solids so they overlap" );
		}

		if ( separated > 0 )
		{
			Warn(
				separated == 1 ? "The cut separated a piece" : $"The cut separated {separated} pieces",
				"The tool went right through the target, so what was one solid is now several. Each "
					+ "piece is its own body in the Parts list.",
				"Make the tool shallower if the part was meant to stay in one piece" );
		}

		if ( !KeepTools.Value )
		{
			foreach ( var tool in tools )
				ctx.Bodies.Remove( tool );
		}
	}

	static string OpName( BooleanOp op ) => op switch
	{
		BooleanOp.Subtract => "subtract",
		BooleanOp.Intersect => "intersect",
		_ => "union",
	};

	static string Describe( IEnumerable<Body> bodies ) => Describe( bodies.Select( b => b.Name ).ToList() );

	static string Describe( IReadOnlyList<string> names ) => names.Count switch
	{
		0 => "Nothing",
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join( ", ", names.Take( names.Count - 1 ) )} and {names[^1]}",
	};
}
