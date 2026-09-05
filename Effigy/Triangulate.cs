using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Ear clipping for simple polygons — the triangulation every part of the tool that turns an
/// n-gon face into triangles needs.
///
/// WHY THIS EXISTS: everything used to fan from corner 0, on the stated grounds that "Effigy's
/// faces are convex". That is true of primitives and of extrude side walls, and it is NOT true of
/// an extrude cap, which is whatever closed region the user drew. Fanning a concave profile fills
/// its notches in: draw a dart and the extrude comes back as a quadrilateral with the concave
/// corner swallowed. Ear clipping handles any simple polygon, convex or not.
///
/// Holes ARE supported, by WithHoles below — inner loops are spliced into the outer one along a
/// bridge and the whole thing is ear clipped as a single ring. This comment said the opposite for
/// some time after that landed, which is the failure mode this repo keeps hitting: a stated
/// limitation outliving the limitation itself.
/// </summary>
public static class Triangulate
{
	/// <summary>
	/// Triangulate a polygon given in plane coordinates. Returns index triples INTO THE INPUT
	/// LIST, wound the same way the input is, so a caller can map them straight back onto its own
	/// vertices without worrying about which way the face faces.
	/// </summary>
	/// <summary>
	/// Triangulate a loop that has already had its holes SPLICED IN along bridges — one boundary
	/// that runs out to an inner ring, round it, and back along the same seam.
	///
	/// WHY THIS IS NOT Polygon(). Polygon assumes a SIMPLE polygon: no repeated vertex, no edge
	/// travelled twice. A bridged loop breaks both, and Polygon does not fail on one - it returns
	/// overlapping triangles that cover more area than the outline encloses, which renders as a
	/// hole that has been filled in. WithHoles never hit this because it builds its own bridges and
	/// finishes through ClipRing, the clipper that tolerates them; Polygon is simply the wrong door
	/// and nothing had ever knocked on it with a bridged loop before.
	///
	/// What knocks now is s&amp;box's mesh boolean. A half-edge face is one closed loop, so a cut that
	/// leaves a hole in a face can only hand it back bridged, and that is the shape a cut arrives
	/// in — see EffigyMeshBoolean.
	///
	/// Winding follows the input, the same contract Polygon gives.
	/// </summary>
	public static List<(int A, int B, int C)> BridgedLoop( IReadOnlyList<Vec2> points )
	{
		if ( points is null || points.Count < 3 )
			return new List<(int, int, int)>();

		// WELDING IS THE WHOLE TRICK, and getting it wrong looks like success. ClipRing tolerates a
		// bridge only when the seam's two visits are THE SAME INDEX - that identity is what lets its
		// ear test recognise the doubled edge instead of measuring a zero-area corner and rejecting
		// every candidate. Handed a ring whose visits are two different indices at identical
		// positions, it finds no ear at all and falls back to a fan, which returns a plausible pile
		// of triangles covering the hole and everything else besides.
		//
		// WithHoles never meets this because it splices indices into a shared point list and the
		// repeat is literal. A loop arriving from outside - the shape s&box's boolean returns - has
		// to be put into that same form first.
		WeldRing( points, out var welded, out var ring, out var representative );

		var triangles = ClipRing( welded, ring, reversed: RingSignedArea( welded, ring ) < 0f );

		for ( var i = 0; i < triangles.Count; i++ )
		{
			var (a, b, c) = triangles[i];
			triangles[i] = (representative[a], representative[b], representative[c]);
		}

		return triangles;
	}

	/// <summary>Two points this close together in plane units are the same point. Loose enough for
	/// a seam the engine reports twice, far below anything anyone draws.</summary>
	const float WeldTolerance = 1e-5f;

	/// <summary>Signed area of a ring of indices, rather than of a bare point list.</summary>
	static float RingSignedArea( IReadOnlyList<Vec2> points, IReadOnlyList<int> ring )
	{
		var sum = 0f;

		for ( var i = 0; i < ring.Count; i++ )
		{
			var a = points[ring[i]];
			var b = points[ring[(i + 1) % ring.Count]];
			sum += a.x * b.y - b.x * a.y;
		}

		return sum * 0.5f;
	}

	/// <summary>The 3D form of <see cref="BridgedLoop"/>, flattened onto the loop's own Newell
	/// normal exactly as <see cref="Face"/> does.</summary>
	public static List<(int A, int B, int C)> BridgedFace( IReadOnlyList<Vec3> positions )
	{
		if ( positions is null || positions.Count < 3 )
			return new List<(int, int, int)>();

		return BridgedLoop( Flatten( positions ) );
	}

	/// <summary>
	/// Collapse a loop's coincident corners so a seam's two visits become THE SAME INDEX, which is
	/// the form both the ear clipper and the splitter need. Shared by BridgedLoop and
	/// SplitBridgedLoop rather than written twice: they must agree exactly on which corners are the
	/// same point, or one of them will find a bridge the other cannot.
	/// </summary>
	static void WeldRing( IReadOnlyList<Vec2> points, out List<Vec2> welded, out List<int> ring,
		out List<int> representative )
	{
		welded = new List<Vec2>( points.Count );
		ring = new List<int>( points.Count );
		representative = new List<int>( points.Count );

		for ( var i = 0; i < points.Count; i++ )
		{
			var index = -1;

			for ( var j = 0; j < welded.Count; j++ )
			{
				if ( MathF.Abs( welded[j].x - points[i].x ) > WeldTolerance
					|| MathF.Abs( welded[j].y - points[i].y ) > WeldTolerance )
					continue;

				index = j;
				break;
			}

			if ( index < 0 )
			{
				index = welded.Count;
				welded.Add( points[i] );

				// Which position in the CALLER'S list this welded point stands for, so the results
				// come back indexed the way the caller handed its loop in.
				representative.Add( i );
			}

			ring.Add( index );
		}
	}

	/// <summary>
	/// Split a bridged loop into TWO simple polygons instead of triangulating it. Returns index
	/// loops into the caller's list, in the caller's own winding, or null when it will not do it.
	///
	/// WHY, given BridgedLoop already works. Because a Face is the unit of SELECTION and of
	/// material assignment, and triangulating spends that unit freely. A 24-gon cap with a pocket
	/// cut into it comes back as 29 triangles; clicking it to paint it paints one of them. The
	/// topology genuinely forbids ONE face here - a face is one loop of corners and a face with a
	/// hole has two boundaries - but TWO is available, and two is what someone means when they say
	/// the cut should not have broken the face up.
	///
	/// THE SECOND BRIDGE IS THE WHOLE IDEA. The loop already carries one bridge joining the outer
	/// boundary to the hole. Cut the ring a second time somewhere else and the annulus falls into
	/// two ordinary n-gons. No new vertices, no triangles, and the quads everywhere else on the
	/// model were never involved.
	///
	/// N HOLES GIVE N+1 FACES. Each hole is cut against whichever face it landed in, twice, so one
	/// face becomes two and the count climbs by one per hole. The one-hole case is the same code
	/// run once and is still by far the common one.
	///
	/// WHAT IT REFUSES, and why refusing matters more than succeeding: a loop whose repeated visits
	/// do not sit where a bridge puts them, a hole that lands in no face, a hole with no valid pair
	/// of bridges. Each returns null and the caller falls back to BridgedLoop, which is never wrong
	/// - only coarse. A WRONG split is a self-intersecting face that is closed, manifold and
	/// Euler-correct, which is precisely the class of defect the cut work already lost a day to.
	/// </summary>
	public static List<List<int>> SplitBridgedLoop( IReadOnlyList<Vec2> points )
	{
		// Three corners of outer boundary, three of hole, and the bridge's two repeated visits.
		if ( points is null || points.Count < 8 )
			return null;

		WeldRing( points, out var welded, out var ring, out var representative );

		if ( !RecoverBridges( ring, out var outer, out var holes ) || holes.Count == 0 )
			return null;

		var loops = SplitIntoFaces( welded, outer, holes );

		if ( loops is null )
			return null;

		foreach ( var loop in loops )
		{
			for ( var i = 0; i < loop.Count; i++ )
				loop[i] = representative[loop[i]];
		}

		return loops;
	}

	/// <summary>The 3D form of <see cref="SplitBridgedLoop"/>, flattened exactly as
	/// <see cref="BridgedFace"/> does.</summary>
	public static List<List<int>> SplitBridgedFace( IReadOnlyList<Vec3> positions )
	{
		if ( positions is null || positions.Count < 8 )
			return null;

		return SplitBridgedLoop( Flatten( positions ) );
	}

	/// <summary>
	/// Take a welded ring apart into its outer boundary and every hole bridged into it.
	///
	/// ONE BRIDGE AT A TIME, INNERMOST FIRST. A bridge is walked out and back, so it leaves a
	/// contiguous run - out to the hole, round it, back along the same seam - and peeling that run
	/// off leaves a ring with one bridge fewer. Taking the shortest run first is what makes the
	/// peel unambiguous when there are several, since a shorter run cannot contain a longer one.
	///
	/// Everything about the shape is checked rather than assumed at every peel: these loops come
	/// from the engine, not from Bridge() above, and the cost of guessing wrong is a face that
	/// passes validation while overlapping itself. Whatever is left when no more runs can be peeled
	/// has to be a plain simple ring, or the whole recovery is refused.
	/// </summary>
	static bool RecoverBridges( List<int> ring, out List<int> outer, out List<List<int>> holes )
	{
		outer = new List<int>( ring );
		holes = new List<List<int>>();

		while ( PeelBridge( outer, out var reduced, out var hole ) )
		{
			outer = reduced;
			holes.Add( hole );
		}

		return outer.Count >= 3 && outer.Distinct().Count() == outer.Count;
	}

	/// <summary>
	/// Peel the innermost bridged hole off a ring. False when there is none left to take, which is
	/// both the success case for a plain ring and the refusal for a ring whose repeats are not
	/// bridges - <see cref="RecoverBridges"/> tells them apart by what is left over.
	/// </summary>
	static bool PeelBridge( List<int> ring, out List<int> reduced, out List<int> hole )
	{
		reduced = null;
		hole = null;

		var visits = new Dictionary<int, List<int>>();

		for ( var i = 0; i < ring.Count; i++ )
		{
			if ( !visits.TryGetValue( ring[i], out var at ) )
			{
				at = new List<int>();
				visits[ring[i]] = at;
			}

			at.Add( i );
		}

		// Shortest span first: an innermost run cannot have another nested inside it, so the
		// pattern check below is being applied to a bridge and its own hole and nothing else.
		var candidates = visits.Values
			.Where( at => at.Count == 2 )
			.OrderBy( at => at[1] - at[0] )
			.ToList();

		foreach ( var span in candidates )
		{
			var p = span[0];
			var q = span[1];

			// The hole's seam sits one step inside the outer's on both sides. Anything else is not
			// a bridge, whatever else it may be.
			if ( q - p < 4 || ring[p + 1] != ring[q - 1] )
				continue;

			var candidate = new List<int>();

			for ( var i = p + 1; i < q - 1; i++ )
				candidate.Add( ring[i] );

			if ( candidate.Count < 3 || candidate.Distinct().Count() != candidate.Count )
				continue;

			// A hole's vertices belong to the hole and nowhere else. One that also turns up out on
			// the outer boundary means the run being peeled is not the self-contained thing a
			// bridge leaves behind.
			var inside = candidate.ToHashSet();
			var elsewhere = false;

			// The run being skipped is p+1..q-1: the hole itself, and the seam repeat that closes
			// it back onto its first vertex. Position q is the outer vertex the bridge left from
			// and belongs to the boundary, so it is scanned like any other.
			for ( var i = 0; i < ring.Count && !elsewhere; i++ )
			{
				if ( i > p && i < q )
					continue;

				elsewhere = inside.Contains( ring[i] );
			}

			if ( elsewhere )
				continue;

			reduced = new List<int>();

			for ( var i = 0; i <= p; i++ )
				reduced.Add( ring[i] );

			for ( var i = q + 1; i < ring.Count; i++ )
				reduced.Add( ring[i] );

			if ( reduced.Count < 3 )
				continue;

			hole = candidate;

			return true;
		}

		return false;
	}

	/// <summary>
	/// Cut a boundary and the holes inside it into simple faces, one more face per hole.
	///
	/// EACH HOLE IS CUT AGAINST THE FACE IT LANDED IN, which is what makes n holes work as the
	/// one-hole case run n times rather than as a different algorithm. The first hole splits the
	/// outer boundary in two; the second splits whichever of those two it sits inside; and so on.
	/// Holes still waiting their turn are obstacles while the cuts for this one are chosen - a
	/// bridge laid across a hole not yet placed would leave that hole straddling two faces, with
	/// nowhere to put it.
	///
	/// Rightmost hole first, the same order WithHoles bridges them in, so the result does not
	/// depend on the order the profile finder happened to discover loops in.
	/// </summary>
	static List<List<int>> SplitIntoFaces( List<Vec2> points, List<int> outer, List<List<int>> holes )
	{
		var pending = holes
			.OrderByDescending( h => h.Max( i => points[i].x ) )
			.ToList();

		var faces = new List<List<int>> { new List<int>( outer ) };

		for ( var k = 0; k < pending.Count; k++ )
		{
			var hole = pending[k];
			var others = pending.Skip( k + 1 ).ToList();

			var index = faces.FindIndex( face => LoopInside( points, face, hole ) );

			if ( index < 0 )
				return null;

			if ( !SplitFaceOnHole( points, faces[index], hole, others, out var first, out var second ) )
				return null;

			faces[index] = first;
			faces.Add( second );
		}

		return faces;
	}

	/// <summary>
	/// One face and one hole inside it become two faces, cut on two bridges.
	///
	/// The first bridge is the shortest valid one, the same choice <see cref="Bridge"/> makes; the
	/// second starts opposite it and swings outward, because a bridge next door to the first splits
	/// off a sliver beside a nearly whole face, which is two faces in the sense that a paper cut is
	/// surgery.
	/// </summary>
	static bool SplitFaceOnHole( List<Vec2> points, List<int> face, List<int> hole,
		List<List<int>> others, out List<int> first, out List<int> second )
	{
		first = null;
		second = null;

		if ( !FirstBridge( points, face, hole, others, out var a1, out var b1 ) )
			return false;

		if ( !SecondBridge( points, face, hole, others, a1, b1, out var a2, out var b2 ) )
			return false;

		first = WalkPair( face, hole, a1, a2, b2, b1 );
		second = WalkPair( face, hole, a2, a1, b1, b2 );

		return true;
	}

	/// <summary>The shortest bridge from face to hole that crosses nothing and runs through the
	/// material rather than out in space or down the hole.</summary>
	static bool FirstBridge( List<Vec2> points, List<int> face, List<int> hole,
		List<List<int>> others, out int a, out int b )
	{
		a = -1;
		b = -1;

		var best = float.MaxValue;

		for ( var i = 0; i < face.Count; i++ )
		{
			for ( var j = 0; j < hole.Count; j++ )
			{
				var length = (points[hole[j]] - points[face[i]]).LengthSquared;

				if ( length >= best || length < 1e-12f )
					continue;

				if ( !SplitIsClear( points, face, hole, others, face[i], hole[j] ) )
					continue;

				best = length;
				a = i;
				b = j;
			}
		}

		return a >= 0;
	}

	/// <summary>Whether a hole sits wholly within a face. One vertex inside and no edge crossing
	/// the face's boundary is enough for two loops that are each simple: a loop with a point inside
	/// and no way through the wall has no way out.</summary>
	static bool LoopInside( List<Vec2> points, List<int> face, List<int> hole )
	{
		if ( !Contains( points, face, points[hole[0]] ) )
			return false;

		for ( var i = 0; i < hole.Count; i++ )
		{
			var a = points[hole[i]];
			var b = points[hole[(i + 1) % hole.Count]];

			if ( Crosses( points, face, a, b, hole[i], hole[(i + 1) % hole.Count] ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Find a second bridge to cut the ring on, preferring the far side of it.
	///
	/// A bridge next door to the first one is perfectly valid and splits off a sliver beside a
	/// nearly whole face, which is two faces in the sense that a paper cut is surgery. Starting
	/// opposite and walking outward takes the balanced cut when there is one and still finds the
	/// awkward one when there is not.
	/// </summary>
	static bool SecondBridge( List<Vec2> welded, List<int> outer, List<int> hole,
		List<List<int>> others, int a1, int b1, out int a2, out int b2 )
	{
		a2 = -1;
		b2 = -1;

		var firstA = welded[outer[a1]];
		var firstB = welded[hole[b1]];

		for ( var step = 0; step < outer.Count; step++ )
		{
			var swing = step % 2 == 0 ? step / 2 : -(step / 2 + 1);
			var candidate = ((a1 + outer.Count / 2 + swing) % outer.Count + outer.Count) % outer.Count;

			if ( candidate == a1 )
				continue;

			var anchor = welded[outer[candidate]];

			foreach ( var j in Enumerable.Range( 0, hole.Count )
				.OrderBy( j => (welded[hole[j]] - anchor).LengthSquared ) )
			{
				// The first bridge's own landing: reusing it leaves one of the two faces with no
				// hole side at all.
				if ( j == b1 )
					continue;

				if ( !SplitIsClear( welded, outer, hole, others, outer[candidate], hole[j] ) )
					continue;

				// Two crossing bridges cut the ring into a figure of eight, and both halves come
				// back self-intersecting.
				if ( SegmentsCross( anchor, welded[hole[j]], firstA, firstB ) )
					continue;

				a2 = candidate;
				b2 = j;

				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The same three conditions <see cref="BridgeIsClear"/> demands - crossing no edge of either
	/// ring, and a midpoint in the material rather than out in space or down the hole - plus one
	/// the n-hole case adds: it must clear every hole still waiting to be placed. A cut laid across
	/// one of those leaves it straddling two faces with nowhere to go, and a cut that merely ends
	/// up inside one puts material where a hole is.
	/// </summary>
	static bool SplitIsClear( List<Vec2> points, List<int> outer, List<int> hole,
		List<List<int>> others, int from, int to )
	{
		var a = points[from];
		var b = points[to];

		if ( Crosses( points, outer, a, b, from, to ) || Crosses( points, hole, a, b, from, to ) )
			return false;

		var mid = (a + b) * 0.5f;

		if ( !Contains( points, outer, mid ) || Contains( points, hole, mid ) )
			return false;

		if ( others is null )
			return true;

		foreach ( var other in others )
		{
			if ( Crosses( points, other, a, b, from, to ) || Contains( points, other, mid ) )
				return false;
		}

		return true;
	}

	/// <summary>One of the two halves: the outer boundary walked forward between the bridges, then
	/// the hole walked forward back to where it started. Both rings are walked in the order the
	/// original loop gave them, which is what carries the caller's winding through untouched.</summary>
	static List<int> WalkPair( List<int> outer, List<int> hole, int aFrom, int aTo, int bFrom, int bTo )
	{
		var loop = new List<int>( outer.Count + hole.Count );

		for ( var i = aFrom; ; i = (i + 1) % outer.Count )
		{
			loop.Add( outer[i] );

			if ( i == aTo )
				break;
		}

		for ( var j = bFrom; ; j = (j + 1) % hole.Count )
		{
			loop.Add( hole[j] );

			if ( j == bTo )
				break;
		}

		return loop;
	}

	public static List<(int A, int B, int C)> Polygon( IReadOnlyList<Vec2> points )
	{
		var triangles = new List<(int, int, int)>( Math.Max( points.Count - 2, 0 ) );

		if ( points.Count < 3 )
			return triangles;

		// Ear clipping needs a known winding to tell "convex corner" from "reflex corner". Work
		// counter-clockwise internally and let the caller keep whatever winding it had.
		//
		// THAT SECOND HALF IS NOT FREE, and it read as free for as long as nothing exercised it.
		// Walking a clockwise polygon backwards makes every emitted triple counter-clockwise, so
		// the output silently disagreed with the input about which way the surface faces — and a
		// caller mapping those indices back onto its own vertices got a face pointing the wrong
		// way, which under backface culling is an invisible one. It never showed because every
		// caller today feeds counter-clockwise: Face() flattens onto the face's own Newell normal,
		// which is counter-clockwise by construction, and ProfileFinder orients outer loops the
		// same way. Hole loops are clockwise, and they are the next thing to arrive here.
		//
		// So the ring is walked backwards to find the ears, and each triple is emitted back in the
		// caller's own winding.
		var indices = new List<int>( points.Count );
		var reversed = SignedArea( points ) < 0f;

		if ( reversed )
		{
			for ( var i = points.Count - 1; i >= 0; i-- )
				indices.Add( i );
		}
		else
		{
			for ( var i = 0; i < points.Count; i++ )
				indices.Add( i );
		}

		return ClipRing( points, indices, reversed );
	}

	/// <summary>
	/// Ear-clip a ring of indices that is already walked the right way round.
	///
	/// Shared by Polygon and WithHoles, which differ only in the ring they arrive with: a plain
	/// polygon's is its own points, a holed one's has each hole spliced in along a bridge. Once the
	/// ring exists there is one algorithm, and having one copy of it is what keeps a holed cap and
	/// a plain one from ever disagreeing about winding.
	/// </summary>
	static List<(int A, int B, int C)> ClipRing( IReadOnlyList<Vec2> points, List<int> indices, bool reversed )
	{
		var triangles = new List<(int, int, int)>( Math.Max( indices.Count - 2, 0 ) );

		void Emit( int a, int b, int c ) => triangles.Add( reversed ? (c, b, a) : (a, b, c) );

		// Each pass round the remaining ring clips at most one ear, so this cannot run longer than
		// n passes over an n-gon. The guard is for degenerate input - repeated or collinear points
		// can leave a ring with no ear at all, and a silent infinite loop inside a viewport frame
		// is not something anyone gets to debug comfortably.
		var guard = indices.Count * indices.Count + 16;

		while ( indices.Count > 3 && guard-- > 0 )
		{
			var clipped = false;

			for ( var i = 0; i < indices.Count; i++ )
			{
				var prev = indices[(i - 1 + indices.Count) % indices.Count];
				var cur = indices[i];
				var next = indices[(i + 1) % indices.Count];

				if ( !IsEar( points, indices, prev, cur, next ) )
					continue;

				Emit( prev, cur, next );
				indices.RemoveAt( i );
				clipped = true;
				break;
			}

			if ( clipped )
				continue;

			// No ear anywhere: the polygon is self-intersecting or degenerate. Fall back to a fan
			// so the caller still gets a surface rather than nothing at all.
			break;
		}

		if ( indices.Count == 3 )
		{
			Emit( indices[0], indices[1], indices[2] );
			return triangles;
		}

		for ( var i = 2; i < indices.Count; i++ )
			Emit( indices[0], indices[i - 1], indices[i] );

		return triangles;
	}

	/// <summary>
	/// Triangulate a polygon that has holes in it — a plate with bolt holes, a washer, a flange.
	///
	/// THE TRICK IS THAT THERE IS NO TRICK. Ear clipping only works on a SIMPLE polygon, so each
	/// hole is spliced into the outer loop along a "bridge": a segment from an outer vertex to a
	/// hole vertex, walked out and back, which turns a ring-with-a-hole into one boundary that
	/// happens to visit the bridge twice. After that it is an ordinary ear clip, and the doubled
	/// bridge edge takes care of itself because IsEar already refuses a zero-area corner.
	///
	/// This is why a holed profile never needed a boolean. Capping around a hole is a 2D
	/// triangulation problem and it always was; the old refusal called it "really the same problem
	/// as a boolean subtract", which is what kept a rectangle with a circle in it unbuildable long
	/// after ear clipping arrived.
	///
	/// BRIDGE CHOICE. The textbook answer is Eberly's: take the hole's rightmost vertex, cast a ray
	/// and find a visible outer vertex. This takes the shortest bridge that is actually valid
	/// instead — no crossing of any edge, and a midpoint genuinely inside the material — which is
	/// more work per hole and far less to get subtly wrong. Sketch profiles have tens of points, not
	/// thousands, so the cost does not matter and the certainty does.
	///
	/// Returns triples indexing a CONCATENATED list: the outer loop's points first, then each hole's
	/// in the order given. The caller adds its vertices in that same order and maps straight across.
	/// Empty when a hole cannot be bridged, which the caller should report rather than ignore.
	/// </summary>
	public static List<(int A, int B, int C)> WithHoles( IReadOnlyList<Vec2> outer,
		IReadOnlyList<IReadOnlyList<Vec2>> holes )
	{
		if ( outer is null || outer.Count < 3 )
			return new List<(int, int, int)>();

		if ( holes is null || holes.Count == 0 )
			return Polygon( outer );

		if ( !BridgedRing( outer, holes, out var points, out var merged ) )
			return new List<(int, int, int)>();

		return ClipRing( points, merged, reversed: SignedArea( outer ) < 0f );
	}

	/// <summary>
	/// The same profile as <see cref="WithHoles"/>, capped as TWO N-GONS instead of a pile of
	/// triangles. Null when it will not do it, and then the caller falls back to WithHoles.
	///
	/// This is the sketch-side twin of <see cref="SplitBridgedLoop"/>, which already does it for a
	/// face a CUT left a hole in. Both start from the same shape - one bridged ring - and the
	/// reason is the same one: a Face is the unit of SELECTION and of material assignment, so a
	/// washer capped as 29 triangles is a cap you paint one twenty-ninth of per click. The
	/// difference was only ever where the ring came from. A cut's arrives from the boolean; this
	/// one WithHoles builds for itself and used to hand straight to the ear clipper.
	///
	/// N HOLES, N+1 FACES. A profile does not have to be bridged first - it arrives as loops, which
	/// is what the splitter wants anyway - so this hands them over directly rather than splicing a
	/// ring for the sake of taking it apart again. Two bolt holes in a plate come back as three
	/// faces.
	///
	/// Indexes the CONCATENATED list WithHoles promises - outer first, then each hole in order - and
	/// comes back wound the way WithHoles winds its triples, so a caller swaps one for the other
	/// without touching how it maps or which way it faces.
	/// </summary>
	public static List<List<int>> SplitWithHoles( IReadOnlyList<Vec2> outer,
		IReadOnlyList<IReadOnlyList<Vec2>> holes )
	{
		if ( outer is null || outer.Count < 3 || holes is null || holes.Count == 0 )
			return null;

		// The concatenated layout the caller was promised, built here rather than borrowed from
		// BridgedRing: the loops go in as loops and only their indices need to line up.
		var points = new List<Vec2>( outer );
		var rings = new List<List<int>>();
		var offset = outer.Count;

		foreach ( var hole in holes )
		{
			if ( hole is null || hole.Count < 3 )
				return null;

			var ring = new List<int>( hole.Count );

			for ( var i = 0; i < hole.Count; i++ )
				ring.Add( offset + i );

			points.AddRange( hole );
			offset = points.Count;

			// A hole is walked the OPPOSITE way to the outer loop, the same as in BridgedRing: it
			// is what keeps the material on one side the whole way round each face that comes out.
			if ( SignedArea( hole ) > 0f == SignedArea( outer ) > 0f )
				ring.Reverse();

			rings.Add( ring );
		}

		var face = new List<int>( outer.Count );

		for ( var i = 0; i < outer.Count; i++ )
			face.Add( i );

		var loops = SplitIntoFaces( points, face, rings );

		if ( loops is null )
			return null;

		// ClipRing normalises a clockwise profile's output by emitting its triples backwards, so
		// these have to turn the same way for the same reason - a caller that swapped one for the
		// other would otherwise get a cap facing the other way.
		if ( SignedArea( outer ) < 0f )
		{
			foreach ( var loop in loops )
				loop.Reverse();
		}

		return loops;
	}

	/// <summary>
	/// The one boundary a holed profile becomes: the outer loop with each hole spliced into it
	/// along a bridge, indexing the concatenated point list WithHoles promises its callers.
	///
	/// Shared by WithHoles and SplitWithHoles rather than written twice, for the same reason
	/// WeldRing is shared by the two loop readers: they must agree exactly on which ring they are
	/// looking at, or the splitter will refuse a ring the clipper would have accepted and nobody
	/// will be able to say why.
	/// </summary>
	static bool BridgedRing( IReadOnlyList<Vec2> outer, IReadOnlyList<IReadOnlyList<Vec2>> holes,
		out List<Vec2> points, out List<int> merged )
	{
		// One flat list, in the order the caller was promised.
		points = new List<Vec2>( outer );
		merged = new List<int>( outer.Count );

		var holeRings = new List<List<int>>();
		var offset = outer.Count;

		foreach ( var hole in holes )
		{
			if ( hole is null || hole.Count < 3 )
			{
				// A degenerate hole is skipped rather than fatal, but its points still go into the
				// list: the caller's vertex layout is fixed and must not shift underneath it.
				points.AddRange( hole ?? Array.Empty<Vec2>() );
				offset = points.Count;
				continue;
			}

			var ring = new List<int>( hole.Count );

			for ( var i = 0; i < hole.Count; i++ )
				ring.Add( offset + i );

			points.AddRange( hole );
			offset = points.Count;

			// A hole is walked the OPPOSITE way to the outer loop. That is what makes the spliced
			// boundary keep the material on one side the whole way round.
			if ( SignedArea( hole ) > 0f == SignedArea( outer ) > 0f )
				ring.Reverse();

			holeRings.Add( ring );
		}

		for ( var i = 0; i < outer.Count; i++ )
			merged.Add( i );

		// Rightmost hole first, which is the conventional order and keeps the result deterministic
		// rather than depending on the order the profile finder happened to discover loops in.
		var built = points;

		holeRings.Sort( ( a, b ) => b.Max( i => built[i].x ).CompareTo( a.Max( i => built[i].x ) ) );

		foreach ( var hole in holeRings )
		{
			if ( !Bridge( points, merged, hole ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Splice one hole into the working boundary along the shortest valid bridge.
	///
	/// Valid means three things, and all three are needed: the segment crosses no edge of the
	/// boundary as it currently stands (including holes merged before this one), it crosses no edge
	/// of the hole itself, and its midpoint is inside the material. The last is what rules out a
	/// bridge that runs cleanly around the outside of everything and crosses nothing at all.
	/// </summary>
	static bool Bridge( List<Vec2> points, List<int> boundary, List<int> hole )
	{
		var bestOuter = -1;
		var bestHole = -1;
		var bestLength = float.MaxValue;

		for ( var i = 0; i < boundary.Count; i++ )
		{
			for ( var j = 0; j < hole.Count; j++ )
			{
				var a = points[boundary[i]];
				var b = points[hole[j]];
				var length = (b - a).LengthSquared;

				if ( length >= bestLength || length < 1e-12f )
					continue;

				if ( !BridgeIsClear( points, boundary, hole, boundary[i], hole[j] ) )
					continue;

				bestLength = length;
				bestOuter = i;
				bestHole = j;
			}
		}

		if ( bestOuter < 0 )
			return false;

		// ...outer[i], hole[j], hole[j+1] ... all the way round ... hole[j], outer[i], outer[i+1]...
		// The two repeated indices are the bridge, walked out and back.
		var spliced = new List<int>( boundary.Count + hole.Count + 2 );

		for ( var i = 0; i <= bestOuter; i++ )
			spliced.Add( boundary[i] );

		for ( var j = 0; j < hole.Count; j++ )
			spliced.Add( hole[(bestHole + j) % hole.Count] );

		spliced.Add( hole[bestHole] );
		spliced.Add( boundary[bestOuter] );

		for ( var i = bestOuter + 1; i < boundary.Count; i++ )
			spliced.Add( boundary[i] );

		boundary.Clear();
		boundary.AddRange( spliced );

		return true;
	}

	static bool BridgeIsClear( List<Vec2> points, List<int> boundary, List<int> hole, int from, int to )
	{
		var a = points[from];
		var b = points[to];

		if ( Crosses( points, boundary, a, b, from, to ) || Crosses( points, hole, a, b, from, to ) )
			return false;

		// Inside the material: within the boundary, and not down the hole it is bridging to.
		var mid = (a + b) * 0.5f;

		return Contains( points, boundary, mid ) && !Contains( points, hole, mid );
	}

	/// <summary>Whether a segment properly crosses any edge of a ring. Edges touching either end of
	/// the segment are skipped by INDEX — a bridge necessarily meets the two edges at each of its
	/// own endpoints, and that is not a crossing.</summary>
	static bool Crosses( List<Vec2> points, List<int> ring, Vec2 a, Vec2 b, int from, int to )
	{
		for ( var i = 0; i < ring.Count; i++ )
		{
			var p = ring[i];
			var q = ring[(i + 1) % ring.Count];

			if ( p == from || q == from || p == to || q == to )
				continue;

			if ( SegmentsCross( a, b, points[p], points[q] ) )
				return true;
		}

		return false;
	}

	/// <summary>Strict crossing: the two segments meet at a point interior to both. Touching at an
	/// endpoint does not count, which is what makes this usable on a ring where consecutive edges
	/// share a vertex.</summary>
	static bool SegmentsCross( Vec2 a, Vec2 b, Vec2 c, Vec2 d )
	{
		var d1 = Vec2.Cross( b - a, c - a );
		var d2 = Vec2.Cross( b - a, d - a );
		var d3 = Vec2.Cross( d - c, a - c );
		var d4 = Vec2.Cross( d - c, b - c );

		return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
			&& ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
	}

	/// <summary>Even-odd point-in-ring test.</summary>
	static bool Contains( List<Vec2> points, List<int> ring, Vec2 p )
	{
		var inside = false;

		for ( int i = 0, j = ring.Count - 1; i < ring.Count; j = i++ )
		{
			var a = points[ring[i]];
			var b = points[ring[j]];

			if ( a.y > p.y != b.y > p.y
				&& p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x )
			{
				inside = !inside;
			}
		}

		return inside;
	}

	/// <summary>
	/// Triangulate a face given as 3D positions, by flattening it onto its own best-fit plane
	/// first. Newell's method rather than a cross product of the first three points: three
	/// consecutive corners of a real face are often nearly collinear, and their cross product is
	/// then numerical noise pointing anywhere.
	/// </summary>
	public static List<(int A, int B, int C)> Face( IReadOnlyList<Vec3> positions )
	{
		if ( positions.Count < 3 )
			return new List<(int, int, int)>();

		return Polygon( Flatten( positions ) );
	}

	/// <summary>
	/// Drop a planar 3D loop onto its own plane, keeping the order and the winding.
	///
	/// Shared by Face and BridgedFace rather than written twice: the two differ only in which
	/// clipper they hand the result to, and a second copy of this projection would be a second
	/// place for the seed-axis choice below to drift.
	/// </summary>
	static List<Vec2> Flatten( IReadOnlyList<Vec3> positions )
	{
		var normal = NewellNormal( positions );

		if ( normal.LengthSquared < 1e-20f )
			normal = new Vec3( 0, 0, 1 );

		normal = normal.Normal;

		// Any two axes perpendicular to the normal will do; pick the one furthest from it to seed
		// them so the cross product is well conditioned.
		var seed = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var u = Vec3.Cross( seed, normal ).Normal;
		var v = Vec3.Cross( normal, u );

		var flat = new List<Vec2>( positions.Count );

		foreach ( var p in positions )
			flat.Add( new Vec2( Vec3.Dot( p, u ), Vec3.Dot( p, v ) ) );

		return flat;
	}

	static Vec3 NewellNormal( IReadOnlyList<Vec3> points )
	{
		var n = new Vec3( 0, 0, 0 );

		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % points.Count];

			n = new Vec3(
				n.x + (a.y - b.y) * (a.z + b.z),
				n.y + (a.z - b.z) * (a.x + b.x),
				n.z + (a.x - b.x) * (a.y + b.y) );
		}

		return n;
	}

	static float SignedArea( IReadOnlyList<Vec2> points )
	{
		var sum = 0f;

		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % points.Count];
			sum += a.x * b.y - b.x * a.y;
		}

		return sum * 0.5f;
	}

	/// <summary>A corner is an ear when it turns the same way the polygon does and no other
	/// remaining corner is inside the triangle it would cut off. The second half is the part that
	/// makes this work on concave polygons - without it, ear clipping is just a fan.</summary>
	static bool IsEar( IReadOnlyList<Vec2> points, List<int> ring, int prev, int cur, int next )
	{
		var a = points[prev];
		var b = points[cur];
		var c = points[next];

		var cross = Vec2.Cross( b - a, c - b );

		// Reflex, or a zero-area sliver: not an ear.
		if ( cross <= 1e-12f )
			return false;

		foreach ( var index in ring )
		{
			if ( index == prev || index == cur || index == next )
				continue;

			if ( InsideTriangle( points[index], a, b, c ) )
				return false;
		}

		return true;
	}

	static bool InsideTriangle( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		// All three edge tests agreeing in sign means inside. Points exactly ON an edge count as
		// inside: clipping an ear through one would leave a T-junction in the surface.
		var ab = Vec2.Cross( b - a, p - a );
		var bc = Vec2.Cross( c - b, p - b );
		var ca = Vec2.Cross( a - c, p - c );

		return ab >= 0f && bc >= 0f && ca >= 0f;
	}
}
