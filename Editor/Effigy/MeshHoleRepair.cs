using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Close a surface whose holes arrived as open boundary loops instead of as holes in a face.
///
/// WHY THIS EXISTS. s&amp;box's mesh boolean cuts correctly and then cannot fully describe what it
/// cut. A half-edge face is one closed loop of half-edges, so a face with a hole in it has no
/// representation, and reading such a face back through GetFaceVertices returns its OUTER contour
/// only — the inner loop is simply not in the answer. There is no hole API on PolygonMesh to ask
/// instead; the shipped XML documentation lists none.
///
/// What comes out is therefore a mesh that is correct everywhere except at the mouth of the cut:
/// the tunnel walls are all there, the far cap is there, and the face the cut entered through is a
/// plain polygon with no opening in it. The hole shows up as a RING OF BOUNDARY EDGES that no face
/// closes — the walls claim those edges once each and nothing claims them a second time. On screen
/// that is a hole you can see the tunnel through and cannot see into: the opening is covered.
///
/// This puts the opening back. Every boundary loop is matched to the face it lies inside and
/// spliced into it as a hole, using the same Triangulate.WithHoles that Effigy's own holed caps go
/// through — so a repaired cut and a sketched hole end up built the same way.
///
/// IT IS DELIBERATELY CONSERVATIVE. A loop is only spliced when it is unambiguously inside exactly
/// one coplanar face; anything else is left alone as an honest open boundary rather than guessed
/// at. A wrong repair welds a surface shut that was meant to be open, and that is far worse than
/// an opening someone can see is missing.
/// </summary>
public static class MeshHoleRepair
{
	/// <summary>How far off a face's plane a boundary loop may sit and still be counted as lying
	/// in it. The cut's mouth is exactly coplanar in exact arithmetic and a few ulps off in
	/// practice, so this is a float-drift tolerance and not a modelling one.</summary>
	const float PlaneTolerance = 1e-3f;

	/// <summary>Cosine limit for "the loop's plane and the face's plane are the same plane".
	/// Compared on the absolute dot, because the loop bounds a hole and is wound against the face
	/// that will contain it.</summary>
	const float NormalTolerance = 0.999f;

	/// <summary>
	/// Splice every unambiguous boundary loop into the face that contains it. Returns how many
	/// were closed, so a caller can report having done something rather than guessing.
	/// </summary>
	public static int CloseBoundaryLoopsIntoFaces( PolyMesh mesh )
	{
		if ( mesh is null || mesh.FaceCount == 0 )
			return 0;

		var loops = BoundaryLoops( mesh );

		if ( loops.Count == 0 )
			return 0;

		var closed = 0;

		foreach ( var loop in loops )
		{
			if ( loop.Count < 3 )
				continue;

			var host = FindContainingFace( mesh, loop );

			if ( host < 0 )
				continue;

			if ( SpliceIntoFace( mesh, host, loop ) )
				closed++;
		}

		// Whatever is left is a mouth no single face contains. Most of those are a cut that landed
		// where two coplanar faces meet, which MeshHoleRepairSpan closes by notching both rather than
		// by loosening the containment test above - see that file for why loosening it would be wrong.
		closed += MeshHoleRepairSpan.CloseLoopsSpanningFaces( mesh );

		// And what is left after THAT is a mouth across more than two faces, or across faces that
		// do not share a plane at all - a cut through a curved surface, or a second cut into a face
		// a first repair already triangulated. MeshHoleRepairCurved handles both by giving each face
		// the piece of the loop that lies in it, and checks its own work before keeping it.
		closed += MeshHoleRepairCurved.CloseCurvedLoops( mesh );

		// And last, the mouth that crosses nothing because the surface under it is already in
		// pieces: a second cut into a face this repair triangulated the first time. That one is
		// closed by taking the whole coplanar group as one region and putting the mouth in as one
		// more hole - a bigger hammer than the three above, which is why it goes last.
		closed += MeshHoleRepairFragment.CloseLoopsInFragments( mesh );

		return closed;
	}

	/// <summary>
	/// Chain the mesh's boundary edges into closed loops.
	///
	/// Boundary edges are the ones exactly one face uses. Walking them is only well defined when
	/// each boundary vertex has exactly two of them — a vertex with four is two openings meeting at
	/// a point, and there is no way to tell which pairs with which. Those are abandoned rather than
	/// walked, for the same reason ShellOperation refuses openings that meet at a vertex.
	/// </summary>
	static List<List<int>> BoundaryLoops( PolyMesh mesh )
	{
		var atVertex = new Dictionary<int, List<int>>();

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 1 )
				continue;

			Link( key.A, key.B );
			Link( key.B, key.A );
		}

		var loops = new List<List<int>>();
		var used = new HashSet<EdgeKey>();

		foreach ( var start in atVertex.Keys )
		{
			if ( atVertex[start].Count != 2 )
				continue;

			var loop = new List<int>();
			var current = start;
			var previous = -1;

			while ( true )
			{
				loop.Add( current );

				if ( !atVertex.TryGetValue( current, out var neighbours ) || neighbours.Count != 2 )
				{
					loop.Clear();
					break;
				}

				var next = neighbours[0] == previous ? neighbours[1] : neighbours[0];
				var edge = new EdgeKey( current, next );

				if ( used.Contains( edge ) )
					break;

				used.Add( edge );
				previous = current;
				current = next;

				if ( current == start )
					break;

				// A walk longer than the whole boundary is a bookkeeping fault, not a shape.
				if ( loop.Count > used.Count + mesh.VertexCount )
				{
					loop.Clear();
					break;
				}
			}

			if ( loop.Count >= 3 )
				loops.Add( loop );
		}

		return loops;

		void Link( int from, int to )
		{
			if ( !atVertex.TryGetValue( from, out var list ) )
			{
				list = new List<int>( 2 );
				atVertex[from] = list;
			}

			list.Add( to );
		}
	}

	/// <summary>
	/// The one face this loop is a hole in, or -1.
	///
	/// Coplanar, containing, and UNIQUE. Two candidate faces means the answer is a guess, and a
	/// guess here silently seals a surface the wrong way — so it declines instead.
	/// </summary>
	static int FindContainingFace( PolyMesh mesh, List<int> loop )
	{
		var loopNormal = LoopNormal( mesh, loop );

		if ( loopNormal.LengthSquared < 1e-20f )
			return -1;

		loopNormal = loopNormal.Normal;

		var found = -1;

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 )
				continue;

			// A face that already contains one of the loop's vertices is the wall the loop came
			// from, not the surface it is a hole in.
			if ( SharesAnyVertex( face, loop ) )
				continue;

			var normal = mesh.FaceNormal( face );

			if ( MathF.Abs( Vec3.Dot( normal, loopNormal ) ) < NormalTolerance )
				continue;

			var centroid = mesh.FaceCentroid( face );

			if ( MathF.Abs( Vec3.Dot( mesh.Positions[loop[0]] - centroid, normal ) ) > PlaneTolerance )
				continue;

			if ( !LoopIsInsideFace( mesh, face, normal, loop ) )
				continue;

			// Two candidates and there is no way to choose. Leave it open.
			if ( found >= 0 )
				return -1;

			found = fi;
		}

		return found;
	}

	static bool SharesAnyVertex( Face face, List<int> loop )
	{
		foreach ( var index in face.Indices )
		{
			if ( loop.Contains( index ) )
				return true;
		}

		return false;
	}

	/// <summary>Newell normal of a loop of mesh vertices — the plane it bounds, whichever way it
	/// happens to be wound.</summary>
	static Vec3 LoopNormal( PolyMesh mesh, List<int> loop )
	{
		var n = new Vec3( 0, 0, 0 );

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = mesh.Positions[loop[i]];
			var b = mesh.Positions[loop[(i + 1) % loop.Count]];

			n = new Vec3(
				n.x + (a.y - b.y) * (a.z + b.z),
				n.y + (a.z - b.z) * (a.x + b.x),
				n.z + (a.x - b.x) * (a.y + b.y) );
		}

		return n;
	}

	/// <summary>Every point of the loop inside the face's outline, tested in the face's own plane.
	/// All of them rather than one, so a loop straddling an edge is refused.</summary>
	static bool LoopIsInsideFace( PolyMesh mesh, Face face, Vec3 normal, List<int> loop )
	{
		Basis( normal, out var u, out var v );

		var outline = new List<Vec2>( face.Count );

		foreach ( var index in face.Indices )
			outline.Add( Flatten( mesh.Positions[index], u, v ) );

		foreach ( var index in loop )
		{
			if ( !PointInPolygon( outline, Flatten( mesh.Positions[index], u, v ) ) )
				return false;
		}

		return true;
	}

	/// <summary>Replace the face with a triangulation of itself carrying the loop as a hole.</summary>
	static bool SpliceIntoFace( PolyMesh mesh, int faceIndex, List<int> loop )
	{
		var face = mesh.Faces[faceIndex];
		var normal = mesh.FaceNormal( face );

		Basis( normal, out var u, out var v );

		var outer = new List<Vec2>( face.Count );

		foreach ( var index in face.Indices )
			outer.Add( Flatten( mesh.Positions[index], u, v ) );

		var hole = new List<Vec2>( loop.Count );

		foreach ( var index in loop )
			hole.Add( Flatten( mesh.Positions[index], u, v ) );

		var triangles = Triangulate.WithHoles( outer, new List<IReadOnlyList<Vec2>> { hole } );

		if ( triangles.Count == 0 )
			return false;

		// WithHoles indexes outer first, then each hole in order — the contract its own callers in
		// SketchFeatures rely on, so the mapping back is positional and needs no search.
		var combined = new int[face.Count + loop.Count];

		for ( var i = 0; i < face.Count; i++ )
			combined[i] = face.Indices[i];

		for ( var i = 0; i < loop.Count; i++ )
			combined[face.Count + i] = loop[i];

		var material = face.Material;

		// The old face goes; its triangles take its place. Removing before adding keeps the face
		// list free of a stale polygon that would claim the same edges.
		mesh.Faces.RemoveAt( faceIndex );

		foreach ( var (a, b, c) in triangles )
		{
			var ia = combined[a];
			var ib = combined[b];
			var ic = combined[c];

			if ( ia == ib || ib == ic || ia == ic )
				continue;

			mesh.AddFace( new[] { ia, ib, ic }, null, material );
		}

		return true;
	}

	static void Basis( Vec3 normal, out Vec3 u, out Vec3 v )
	{
		var n = normal.Normal;
		var seed = MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );

		u = Vec3.Cross( seed, n ).Normal;
		v = Vec3.Cross( n, u );
	}

	static Vec2 Flatten( Vec3 p, Vec3 u, Vec3 v ) => new( Vec3.Dot( p, u ), Vec3.Dot( p, v ) );

	static bool PointInPolygon( List<Vec2> polygon, Vec2 point )
	{
		var inside = false;

		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
		{
			var a = polygon[i];
			var b = polygon[j];

			if ( a.y > point.y != b.y > point.y
				&& point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x )
				inside = !inside;
		}

		return inside;
	}
}
