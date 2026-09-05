using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Where a snapped click landed, and what pulled it there.</summary>
public readonly struct SnapResult
{
	/// <summary>The snapped position, in sketch plane coordinates.</summary>
	public readonly Vec2 Point;

	/// <summary>Index of the existing sketch point the cursor snapped onto, or -1. The UI draws a
	/// ring on it, and it is what makes closing a profile possible rather than a matter of luck.</summary>
	public readonly int SnappedPointIndex;

	/// <summary>Which axes got locked by inference: bit 1 = x held (a vertical line), bit 2 = y
	/// held (a horizontal one). The UI draws a guide per bit and the sketcher turns them into
	/// Vertical/Horizontal constraints.</summary>
	public readonly int InferenceAxis;

	/// <summary>Index into <see cref="SketchSnapper.Reference"/>'s points that the cursor landed
	/// on, or -1. A corner of the face being sketched on, not of the sketch — nothing is added to
	/// the sketch by landing here, the click simply lands exactly on that corner.</summary>
	public readonly int ReferencePointIndex;

	/// <summary>Index into the reference's edges the cursor slid onto, or -1. Only ever set when
	/// no point of any kind won, since a corner is always the better answer than the edge running
	/// through it.</summary>
	public readonly int ReferenceEdgeIndex;

	/// <summary>Index into the sketch's curves the cursor landed on, or -1. Set for a midpoint
	/// or a slide along an existing edge; never set when an existing point won, because a corner
	/// is the better answer than the curve running through it.</summary>
	public readonly int SnappedCurveIndex;

	public SnapResult( Vec2 point, int snappedPointIndex, int inferenceAxis )
		: this( point, snappedPointIndex, inferenceAxis, -1, -1, -1 ) { }

	public SnapResult( Vec2 point, int snappedPointIndex, int inferenceAxis,
		int referencePointIndex, int referenceEdgeIndex )
		: this( point, snappedPointIndex, inferenceAxis, referencePointIndex, referenceEdgeIndex, -1 ) { }

	public SnapResult( Vec2 point, int snappedPointIndex, int inferenceAxis,
		int referencePointIndex, int referenceEdgeIndex, int snappedCurveIndex )
	{
		Point = point;
		SnappedPointIndex = snappedPointIndex;
		InferenceAxis = inferenceAxis;
		ReferencePointIndex = referencePointIndex;
		ReferenceEdgeIndex = referenceEdgeIndex;
		SnappedCurveIndex = snappedCurveIndex;
	}
}

/// <summary>
/// Turns a raw click on the sketch plane into the point the user meant.
///
/// THIS LIVES IN THE KERNEL ON PURPOSE. It is sketch-domain maths — point reuse, alignment
/// inference, grid rounding — with no engine surface at all, and while it sat inside the editor's
/// viewport file it could not be compiled or tested outside s&amp;box. That is where the bug lived
/// that stopped closed sketches registering as closed: the tolerances were fixed sketch-unit
/// constants, so on a part one unit across every existing point sat inside the snap radius of
/// every new click, corners collapsed onto each other, and the profile silently became a branching
/// mess that ProfileFinder refused. Out here it is covered by SnapTests at five orders of
/// magnitude of part size.
///
/// TOLERANCES ARE IN SKETCH UNITS, and the caller converts. The editor multiplies a pixel count by
/// its units-per-pixel at the sketch plane's depth, so the tolerance is a constant number of
/// pixels at any zoom and any part size. Passing raw world constants is exactly the mistake this
/// class exists to stop being invisible.
/// </summary>
public sealed class SketchSnapper
{
	/// <summary>How close the cursor must be to an existing point to land on it.</summary>
	public float PointRadius;

	/// <summary>How close counts as "lined up" with an existing point or the active line. Smaller
	/// than PointRadius on purpose: alignment should assist a click, not drag it across the sketch.</summary>
	public float AlignmentRadius;

	/// <summary>Grid rounding, or zero for none. See <see cref="AutoGridStep"/>.</summary>
	public float GridStep;

	/// <summary>
	/// Geometry from outside the sketch that the cursor may also land on — the boundary of the face
	/// the sketch is attached to. Null for a sketch on one of the global planes, which has nothing
	/// underneath it to reference.
	/// </summary>
	public SketchReference Reference;

	/// <summary>How close the cursor must be to a corner of the reference geometry to land on it.
	/// Zero disables the pass, the same way <see cref="PointRadius"/> does.</summary>
	public float ReferencePointRadius;

	/// <summary>How close the cursor must be to a reference EDGE to slide onto it. Kept separate
	/// from the corner radius because the two want different numbers: an edge is a whole line of
	/// targets and pulls far more often than a corner does, so being greedier about it than about
	/// the corners would make the corners hard to hit.</summary>
	public float ReferenceEdgeRadius;

	/// <summary>
	/// How close the cursor must be to an existing sketch curve to slide onto it. Zero disables
	/// the pass. Separate from <see cref="PointRadius"/> for the same reason reference edges are
	/// separate from reference corners: a curve is a whole line of targets.
	/// </summary>
	public float CurveRadius;

	/// <summary>
	/// A point index to leave out of snapping, or -1 for none.
	///
	/// This exists for DRAGGING a point. Every committed point is a snap target, including the one
	/// being dragged - so the moment you picked it up it snapped straight back onto itself and the
	/// drag went nowhere. It still has to be excluded from the alignment pass too, or the point
	/// lines itself up with where it used to be.
	/// </summary>
	public int IgnorePoint = -1;

	/// <summary>
	/// A grid step that stays about <paramref name="targetPixels"/> apart on screen, rounded to 1,
	/// 2 or 5 times a power of ten so it is always a number a person would have picked.
	///
	/// A fixed step cannot work when a part may be one unit or a thousand: 0.25 gave a one-unit
	/// part four steps across it.
	/// </summary>
	public static float AutoGridStep( float unitsPerPixel, float targetPixels = 14f )
	{
		var target = unitsPerPixel * targetPixels;

		if ( target <= 0f || float.IsNaN( target ) || float.IsInfinity( target ) )
			return 0f;

		var magnitude = MathF.Pow( 10f, MathF.Floor( MathF.Log10( target ) ) );
		var normalised = target / magnitude;
		var step = normalised < 1.5f ? 1f : normalised < 3.5f ? 2f : normalised < 7.5f ? 5f : 10f;

		return step * magnitude;
	}

	/// <summary>
	/// Reuse an existing point when the coordinate already exists, so shared corners really are
	/// shared and the chain closes.
	///
	/// Sketch.AddPoint deliberately does not do this — it appends unconditionally, because a caller
	/// typing coordinates wants the literal point. Reuse is an input concern, which is here.
	/// </summary>
	public static int PointIndex( Sketch sketch, Vec2 p )
	{
		for ( var i = 0; i < sketch.Points.Count; i++ )
		{
			if ( (sketch.Points[i] - p).LengthSquared < 1e-8f )
				return i;
		}

		return sketch.AddPoint( p );
	}

	/// <summary>
	/// Snap a raw plane hit.
	/// </summary>
	/// <param name="sketch">The sketch being drawn on; its committed points are snap targets.</param>
	/// <param name="raw">The cursor's position on the plane.</param>
	/// <param name="pending">Points clicked for the entity in progress. They are not in the sketch
	/// yet but must still be snap targets — that is what lets a line close back onto its own start
	/// and lets a rectangle share the corner the cursor is visibly over.</param>
	/// <param name="lineInProgress">True when a line has exactly one pending point, which makes
	/// that point the strongest alignment target on the plane.</param>
	public SnapResult Snap( Sketch sketch, Vec2 raw, IReadOnlyList<Vec2> pending, bool lineInProgress )
	{
		pending ??= Array.Empty<Vec2>();

		var inference = 0;

		// The active line is evaluated FIRST, so a near-horizontal or near-vertical second click
		// cannot be swallowed by a less useful grid result.
		if ( lineInProgress && pending.Count == 1 )
		{
			var start = pending[0];
			var dx = MathF.Abs( raw.x - start.x );
			var dy = MathF.Abs( raw.y - start.y );

			if ( dx <= AlignmentRadius && dx <= dy )
			{
				inference = 1;
				raw = new Vec2( start.x, raw.y );
			}
			else if ( dy <= AlignmentRadius )
			{
				inference = 2;
				raw = new Vec2( raw.x, start.y );
			}
		}

		// Where the cursor was before any point pass moved it. The reference passes below measure
		// against THIS rather than against the running `raw`, so "which target is nearer" is one
		// comparison of two distances from the same place - the point passes chain their own
		// candidates and their `best` is not measured from a fixed origin.
		var cursor = raw;

		var best = PointRadius * PointRadius;
		var snappedIndex = -1;
		var pendingWon = false;

		for ( var i = 0; i < pending.Count; i++ )
		{
			var dist = (pending[i] - raw).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			raw = pending[i];
			pendingWon = true;
		}

		for ( var i = 0; i < sketch.Points.Count; i++ )
		{
			if ( i == IgnorePoint )
				continue;

			var dist = (sketch.Points[i] - raw).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			snappedIndex = i;
		}

		// A corner of the geometry the sketch is drawn ON - the face's own vertices. Competes with
		// the sketch's own points on distance, and LOSES A TIE, because closing a chain onto a
		// point that is already in the sketch is the snap that a profile depends on and nothing
		// should be allowed to steal it.
		var referencePoint = NearestReferencePoint( cursor, out var referenceDistance );

		if ( referencePoint >= 0 && (snappedIndex >= 0 || pendingWon) )
		{
			var claimed = ((snappedIndex >= 0 ? sketch.Points[snappedIndex] : raw) - cursor).LengthSquared;

			if ( claimed <= referenceDistance )
				referencePoint = -1;
		}

		// NO INFERENCE REPORTED ALONGSIDE A REFERENCE SNAP, here or on the edge path below.
		//
		// InferenceAxis is not decoration: the line tool turns it into a real Vertical or Horizontal
		// CONSTRAINT on the line it commits. The alignment pass above moved the cursor onto that
		// axis, and then this pass moved it somewhere else - onto a corner of the face, which is
		// under no obligation to be square with anything. Reporting the axis anyway would attach a
		// rule the geometry does not satisfy, and the solver would then drag the point off the
		// corner to satisfy it. The corner is what the user asked for; it wins outright.
		if ( referencePoint >= 0 )
			return new SnapResult( Reference.Points[referencePoint], -1, 0, referencePoint, -1 );

		// Landing exactly on a committed point beats every other consideration - no grid rounding,
		// no inference, or the snap would be nudged back off the point it just found.
		//
		// AND THAT INCLUDES THE AXIS LOCK, which is why the inference goes through Verified here.
		// The pass at the top moved the cursor onto the line's axis; this pass then moved it
		// somewhere else entirely - onto a committed point, which is under no obligation to be
		// square with the line being drawn. See Verified for what a false bit costs.
		if ( snappedIndex >= 0 )
			return new SnapResult( sketch.Points[snappedIndex], snappedIndex,
				Verified( inference, sketch.Points[snappedIndex], pending ) );

		// A line's midpoint is a point that is not in the sketch yet. Corners have already
		// returned, so this cannot steal a close. Without it, dividing a rectangle means aiming
		// at empty space in the middle of an edge and hoping the grid is kind.
		if ( !pendingWon && PointRadius > 0f
			&& TryNearestMidpoint( sketch, cursor, pending, lineInProgress, out var mid, out var midCurve ) )
		{
			return new SnapResult( mid, -1, Verified( inference, mid, pending ), -1, -1, midCurve );
		}

		var snapped = GridStep > 0f
			? new Vec2(
				MathF.Round( raw.x / GridStep ) * GridStep,
				MathF.Round( raw.y / GridStep ) * GridStep )
			: raw;

		// AN AXIS THE LINE PASS LOCKED SURVIVES THE GRID.
		//
		// Without this the two features quietly cancel: the pass at the top puts the cursor exactly
		// on the start point's x, and then rounding puts it on the nearest grid line instead, which
		// is the same number only when the start point happens to sit on the grid. It usually does -
		// it was grid-snapped too - so the failure hides until the start point came from somewhere
		// else, an existing corner or a face's vertex, and then a line drawn deliberately vertical
		// is a fraction off vertical and Verified below strips the lock it was promised.
		//
		// Only the top pass can have set a bit this early, so this restores exactly the axis the
		// user aimed down and leaves the other one on the grid.
		if ( (inference & 1) != 0 )
			snapped = new Vec2( raw.x, snapped.y );

		if ( (inference & 2) != 0 )
			snapped = new Vec2( snapped.x, raw.y );

		// An EDGE of the geometry underneath, which is a whole line of targets rather than one.
		//
		// Decided from where the cursor actually is, but landed from the grid-rounded point: the
		// result is on the edge, at a round distance along it wherever the edge runs along an axis.
		// "10 units in from that corner, along that edge" is most of what anyone wants a face's
		// outline for, and it comes out of those two lines rather than out of a dimension typed
		// afterwards.
		//
		// Last of the snaps and first to give way - a corner or a sketch point has already returned
		// by the time this runs, and a pending point is excluded here, since every one of them is a
		// better answer than the line passing through it. Closing a rectangle onto its own first
		// corner must not be stolen by the edge of the block it is being drawn on.
		//
		// SKETCH CURVES BEAT THE FACE UNDERNEATH. Drawing a line to an edge you just drew is the
		// thing you are looking at; the reference outline is a fallback for when the sketch is
		// empty. Same landing rule as the reference edge: distance from the cursor, position from
		// the grid-rounded point, so a click near the middle of a 10-unit edge lands at 5.
		if ( !pendingWon && CurveRadius > 0f
			&& TryNearestCurve( sketch, cursor, snapped, pending, lineInProgress, out var onCurve, out var curveIndex ) )
		{
			return new SnapResult( onCurve, -1, Verified( inference, onCurve, pending ), -1, -1, curveIndex );
		}

		var edgeIndex = pendingWon ? -1 : NearestReferenceEdge( cursor );

		if ( edgeIndex >= 0 )
		{
			var (from, to) = Reference.Segment( edgeIndex );

			// Inference dropped, for the reason given at the corner path above: the point is on the
			// edge now, not on the inferred axis, and a Vertical constraint saying otherwise would
			// be pulled straight back off it by the solver.
			return new SnapResult( ClosestOnSegment( from, to, snapped ), -1, 0, -1, edgeIndex );
		}

		// Line up with any existing point on either axis, and with the sketch origin, which is what
		// the zero-initialised targets below mean.
		var xTarget = 0f;
		var yTarget = 0f;
		var xDistance = MathF.Abs( snapped.x );
		var yDistance = MathF.Abs( snapped.y );

		for ( var i = 0; i < sketch.Points.Count; i++ )
		{
			if ( i == IgnorePoint )
				continue;

			var point = sketch.Points[i];
			var dx = MathF.Abs( snapped.x - point.x );

			if ( dx < xDistance )
			{
				xDistance = dx;
				xTarget = point.x;
			}

			var dy = MathF.Abs( snapped.y - point.y );

			if ( dy < yDistance )
			{
				yDistance = dy;
				yTarget = point.y;
			}
		}

		// And with the corners of the face underneath, on the same footing as the sketch's own
		// points. Lining a new rectangle up with the corner of the block it sits on is the whole
		// reason that outline is drawn; a guide that stopped at the sketch's own geometry would
		// show the corner and then refuse to help you meet it.
		//
		// NOT WHEN A PENDING POINT ALREADY TOOK THE CURSOR. `raw` is then exactly a corner the user
		// placed a moment ago, and nudging it a couple of pixels to line up with the face beneath is
		// how a rectangle stops closing on itself - which is the failure this whole snapper exists
		// to prevent.
		if ( ReferencePointRadius > 0f && Reference is not null && !pendingWon )
		{
			foreach ( var point in Reference.Points )
			{
				var dx = MathF.Abs( snapped.x - point.x );

				if ( dx < xDistance )
				{
					xDistance = dx;
					xTarget = point.x;
				}

				var dy = MathF.Abs( snapped.y - point.y );

				if ( dy < yDistance )
				{
					yDistance = dy;
					yTarget = point.y;
				}
			}
		}

		// AN AXIS THE LINE PASS ALREADY LOCKED IS NOT UP FOR RE-ALIGNMENT, which is what the two bit
		// tests are doing here. Lining up with the nearest x on the plane is the weaker claim of the
		// two - the line pass locked that axis because the user is drawing DOWN it - and letting the
		// weaker one move the coordinate is how a deliberately vertical line ends up a fraction off
		// vertical, pulled sideways onto the origin or onto some unrelated corner that happened to
		// be within seven pixels.
		if ( (inference & 1) == 0 && xDistance <= AlignmentRadius )
		{
			snapped = new Vec2( xTarget, snapped.y );
			inference |= 1;
		}

		if ( (inference & 2) == 0 && yDistance <= AlignmentRadius )
		{
			snapped = new Vec2( snapped.x, yTarget );
			inference |= 2;
		}

		// Finally the active line's own target, which keeps the second click square with the first
		// even when there is no other geometry anywhere near it.
		if ( lineInProgress && pending.Count == 1 && inference == 0 )
		{
			var start = pending[0];
			var dx = MathF.Abs( snapped.x - start.x );
			var dy = MathF.Abs( snapped.y - start.y );

			if ( dx <= AlignmentRadius && dx <= dy )
			{
				snapped = new Vec2( start.x, snapped.y );
				inference |= 1;
			}
			else if ( dy <= AlignmentRadius )
			{
				snapped = new Vec2( snapped.x, start.y );
				inference |= 2;
			}
		}

		return new SnapResult( snapped, -1, Verified( inference, snapped, pending ) );
	}

	/// <summary>
	/// Drop any axis bit the returned point does not actually satisfy.
	///
	/// INFERENCE IS NOT A HINT. The line tool turns InferenceAxis into a real Vertical or Horizontal
	/// CONSTRAINT on the line it commits, and the sketcher draws its guide through pending[0] on the
	/// strength of the same bit. Both readings mean one thing: "this point shares a coordinate with
	/// the start of the line being drawn". A bit that is true of something else is not a weaker
	/// version of that claim, it is a false one - the solver enforces the rule and drags the point
	/// off wherever the user put it, and the guide is drawn through a place the point is not.
	///
	/// The alignment pass is where they diverge. It lines the cursor up with the nearest x of ANY
	/// point on the plane and sets bit 1 for it, which is a genuinely useful snap and simply not the
	/// statement the bit makes. Lining up with some other corner says nothing about whether the line
	/// from pending[0] to here is vertical. So the snap stays and the bit goes.
	///
	/// With no pending point there is no line, nothing for a bit to be true OF, and no consumer -
	/// the guide and the constraint both require one - so the whole set goes.
	/// </summary>
	static int Verified( int inference, Vec2 point, IReadOnlyList<Vec2> pending )
	{
		if ( inference == 0 || pending.Count == 0 )
			return 0;

		var start = pending[0];

		if ( (inference & 1) != 0 && !Same( point.x, start.x ) )
			inference &= ~1;

		if ( (inference & 2) != 0 && !Same( point.y, start.y ) )
			inference &= ~2;

		return inference;
	}

	/// <summary>
	/// Whether two coordinates are the same number, at any part size.
	///
	/// Every pass that legitimately sets a bit ASSIGNS the coordinate rather than computing it, so
	/// exact equality would do - but a tolerance that scales is the difference between this staying
	/// correct and it becoming another fixed constant for someone to find the hard way, which is the
	/// mistake this whole class was rewritten to stop making. Sub-pixel at every size the sketcher
	/// is tested at, so nothing visibly off-axis can ever pass.
	/// </summary>
	static bool Same( float a, float b ) =>
		MathF.Abs( a - b ) <= 1e-5f * MathF.Max( 1f, MathF.Max( MathF.Abs( a ), MathF.Abs( b ) ) );

	/// <summary>The reference corner nearest <paramref name="cursor"/> within
	/// <see cref="ReferencePointRadius"/>, or -1, reporting its squared distance so the caller can
	/// weigh it against a sketch point without measuring twice.</summary>
	int NearestReferencePoint( Vec2 cursor, out float distanceSquared )
	{
		distanceSquared = float.MaxValue;

		if ( Reference is null || ReferencePointRadius <= 0f )
			return -1;

		var best = ReferencePointRadius * ReferencePointRadius;
		var found = -1;

		for ( var i = 0; i < Reference.Points.Count; i++ )
		{
			var dist = (Reference.Points[i] - cursor).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			found = i;
		}

		if ( found >= 0 )
			distanceSquared = best;

		return found;
	}

	/// <summary>The reference edge nearest <paramref name="cursor"/> within
	/// <see cref="ReferenceEdgeRadius"/>, or -1.</summary>
	int NearestReferenceEdge( Vec2 cursor )
	{
		if ( Reference is null || ReferenceEdgeRadius <= 0f )
			return -1;

		var best = ReferenceEdgeRadius * ReferenceEdgeRadius;
		var found = -1;

		for ( var i = 0; i < Reference.Edges.Count; i++ )
		{
			var (a, b) = Reference.Segment( i );
			var dist = (ClosestOnSegment( a, b, cursor ) - cursor).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;
			found = i;
		}

		return found;
	}

	/// <summary>
	/// Midpoint of an existing line nearest <paramref name="cursor"/> within
	/// <see cref="PointRadius"/>, or false. Lines the in-progress click already sits on are
	/// skipped — snapping the far end of a divider back onto the edge you started from is how
	/// you get a zero-length line instead of a split.
	/// </summary>
	bool TryNearestMidpoint( Sketch sketch, Vec2 cursor, IReadOnlyList<Vec2> pending,
		bool lineInProgress, out Vec2 mid, out int curveIndex )
	{
		mid = cursor;
		curveIndex = -1;

		var best = PointRadius * PointRadius;
		var hasStart = lineInProgress && pending.Count == 1;
		var start = hasStart ? pending[0] : Vec2.Zero;

		for ( var i = 0; i < sketch.Curves.Count; i++ )
		{
			if ( sketch.Curves[i] is not SketchLine line || line.Construction )
				continue;

			if ( IgnorePoint >= 0 && (line.Start == IgnorePoint || line.End == IgnorePoint) )
				continue;

			var a = sketch.Points[line.Start];
			var b = sketch.Points[line.End];
			var candidate = new Vec2( (a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f );
			var dist = (candidate - cursor).LengthSquared;

			if ( dist >= best )
				continue;

			if ( hasStart && CurveHolds( sketch, sketch.Curves[i], start ) )
				continue;

			best = dist;
			mid = candidate;
			curveIndex = i;
		}

		return curveIndex >= 0;
	}

	/// <summary>
	/// Existing sketch curve nearest <paramref name="cursor"/> within <see cref="CurveRadius"/>.
	/// Lands at the projection of <paramref name="preferred"/> onto that curve, so a grid-rounded
	/// click near an axis-aligned edge stays on a round coordinate along it.
	/// </summary>
	bool TryNearestCurve( Sketch sketch, Vec2 cursor, Vec2 preferred, IReadOnlyList<Vec2> pending,
		bool lineInProgress, out Vec2 landed, out int curveIndex )
	{
		landed = preferred;
		curveIndex = -1;

		var best = CurveRadius * CurveRadius;
		var hasStart = lineInProgress && pending.Count == 1;
		var start = hasStart ? pending[0] : Vec2.Zero;

		for ( var i = 0; i < sketch.Curves.Count; i++ )
		{
			var curve = sketch.Curves[i];

			if ( curve.Construction || !ClosestOnCurve( sketch, curve, cursor, out var onCursor ) )
				continue;

			if ( IgnorePoint >= 0 && curve.PointRefs.Contains( IgnorePoint ) )
				continue;

			var dist = (onCursor - cursor).LengthSquared;

			if ( dist >= best )
				continue;

			if ( hasStart && CurveHolds( sketch, curve, start ) )
				continue;

			if ( !ClosestOnCurve( sketch, curve, preferred, out var onPreferred ) )
				continue;

			best = dist;
			landed = onPreferred;
			curveIndex = i;
		}

		return curveIndex >= 0;
	}

	/// <summary>Whether <paramref name="point"/> already sits on this curve, at either end or
	/// along it. Used to keep the far click of a line off the edge its start is already on.</summary>
	static bool CurveHolds( Sketch sketch, SketchCurve curve, Vec2 point )
	{
		if ( !ClosestOnCurve( sketch, curve, point, out var closest ) )
			return false;

		return (closest - point).LengthSquared < 1e-8f;
	}

	/// <summary>The point of <paramref name="curve"/> nearest <paramref name="p"/>.</summary>
	static bool ClosestOnCurve( Sketch sketch, SketchCurve curve, Vec2 p, out Vec2 closest )
	{
		closest = p;

		switch ( curve )
		{
			case SketchLine line:
				closest = ClosestOnSegment( sketch.Points[line.Start], sketch.Points[line.End], p );
				return true;

			case SketchCircle circle:
			{
				var c = sketch.Points[circle.Center];
				var r = circle.Radius;

				if ( r < 1e-12f )
					return false;

				var d = p - c;

				closest = d.LengthSquared < 1e-16f
					? new Vec2( c.x + r, c.y )
					: c + d * (r / d.Length);

				return true;
			}

			case SketchArc arc:
			{
				var c = sketch.Points[arc.Center];
				var r = arc.Radius( sketch );

				if ( r < 1e-12f )
					return false;

				var d = p - c;
				var onCircle = d.LengthSquared < 1e-16f
					? sketch.Points[arc.Start]
					: c + d * (r / d.Length);

				if ( SketchIntersect.OnCurve( sketch, arc, onCircle, out _ ) )
				{
					closest = onCircle;
					return true;
				}

				var s = sketch.Points[arc.Start];
				var e = sketch.Points[arc.End];
				closest = (s - p).LengthSquared <= (e - p).LengthSquared ? s : e;
				return true;
			}

			default:
			{
				var pts = curve.Tessellate( sketch, sketch.Tolerance );

				if ( pts.Count < 2 )
					return false;

				var best = float.MaxValue;

				for ( var i = 0; i < pts.Count - 1; i++ )
				{
					var candidate = ClosestOnSegment( pts[i], pts[i + 1], p );
					var dist = (candidate - p).LengthSquared;

					if ( dist >= best )
						continue;

					best = dist;
					closest = candidate;
				}

				return best < float.MaxValue;
			}
		}
	}

	/// <summary>The point of segment a-b nearest <paramref name="p"/>, clamped to the segment so a
	/// short edge never pulls the cursor out past its own end.</summary>
	public static Vec2 ClosestOnSegment( Vec2 a, Vec2 b, Vec2 p )
	{
		var along = b - a;
		var lengthSquared = along.LengthSquared;

		if ( lengthSquared < 1e-12f )
			return a;

		var t = Vec2.Dot( p - a, along ) / lengthSquared;

		return a + along * MathF.Max( 0f, MathF.Min( 1f, t ) );
	}
}
