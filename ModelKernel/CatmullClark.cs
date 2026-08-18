using System;
using System.Collections.Generic;

namespace ModelKernel;

/// <summary>
/// Catmull-Clark subdivision.
///
/// This is the bridge between the two halves of the tool: the parametric stage produces a coarse
/// quad cage, this turns it into something dense enough to sculpt, and the cage survives
/// underneath as the low-poly the sculpt eventually bakes down onto. That is the whole reason the
/// pipeline starts parametric — the cage IS the retopology, produced as a side effect rather than
/// as a step somebody has to do.
///
/// EVERY OUTPUT FACE IS A QUAD, whatever went in. An n-gon becomes n quads, a triangle becomes 3.
/// So triangles in the input are survivable but not free: they produce valence-3 extraordinary
/// vertices that stay extraordinary at every level and pucker visibly under a sculpt brush. Keep
/// the primitives quad-dominant.
///
/// Boundaries use the cubic B-spline curve rules, so an open mesh keeps its border instead of
/// shrinking away from it.
///
/// EDGE CREASES USE THE SAME MACHINERY AS BOUNDARIES, DELIBERATELY. A boundary edge is really just
/// a permanently, infinitely sharp edge — one face instead of two — and the crease rules below are
/// written as a strict generalisation of the boundary rules that already existed: with no creases
/// marked, every formula reduces to exactly the old boundary/smooth code path. This is why a mesh
/// with an empty Creases dictionary behaves byte-for-byte as before, which the test suite checks
/// directly rather than assuming.
/// </summary>
public static class CatmullClark
{
	/// <summary>Subdivide `levels` times. Level 0 returns a clone, so callers can always treat the
	/// result as theirs to mutate.</summary>
	public static PolyMesh Subdivide( PolyMesh mesh, int levels = 1 )
	{
		if ( levels < 0 )
			throw new ArgumentOutOfRangeException( nameof( levels ) );

		var current = mesh.Clone();

		for ( var i = 0; i < levels; i++ )
			current = SubdivideOnce( current );

		return current;
	}

	static PolyMesh SubdivideOnce( PolyMesh mesh )
	{
		var edgeFaces = mesh.BuildEdgeFaces();
		var vertexFaces = mesh.BuildVertexFaces();
		var vertexEdges = mesh.BuildVertexEdges();

		// Stable index per edge, so edge points can live in a contiguous block of the new vertex
		// list rather than in a dictionary that later lookups have to chase.
		var edgeIndex = new Dictionary<EdgeKey, int>( edgeFaces.Count );
		var edgeList = new List<EdgeKey>( edgeFaces.Count );

		foreach ( var key in edgeFaces.Keys )
		{
			edgeIndex[key] = edgeList.Count;
			edgeList.Add( key );
		}

		var vertCount = mesh.VertexCount;
		var edgeCount = edgeList.Count;
		var faceCount = mesh.FaceCount;

		// Layout of the new vertex list:
		//   [0 .. V)                 updated original vertices
		//   [V .. V+E)               edge points
		//   [V+E .. V+E+F)           face points
		var newPositions = new Vec3[vertCount + edgeCount + faceCount];

		// --- face points -------------------------------------------------------------------
		var facePoints = new Vec3[faceCount];

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			facePoints[fi] = mesh.FaceCentroid( mesh.Faces[fi] );
			newPositions[vertCount + edgeCount + fi] = facePoints[fi];
		}

		// --- edge points -------------------------------------------------------------------
		for ( var ei = 0; ei < edgeCount; ei++ )
		{
			var key = edgeList[ei];
			var a = mesh.Positions[key.A];
			var b = mesh.Positions[key.B];
			var faces = edgeFaces[key];

			// The sharp rule is always the plain midpoint — pulling toward an adjacent face point
			// is exactly the smoothing this edge is meant to resist. A boundary edge (one face) is
			// infinitely sharp by definition: there's no second face to pull it toward anyway, and
			// treating it as anything else is the "my open mesh shrank" bug.
			var sharpEdgePoint = (a + b) * 0.5f;

			Vec3 smoothEdgePoint;

			if ( faces.Count == 1 )
			{
				smoothEdgePoint = sharpEdgePoint;
			}
			else
			{
				var faceSum = Vec3.Zero;

				foreach ( var fi in faces )
					faceSum += facePoints[fi];

				// (v0 + v1 + average of adjacent face points) weighted as the standard 4-point
				// rule. Written to average over faces.Count rather than assuming 2, so a
				// non-manifold edge degrades to something sane instead of reading past the end.
				smoothEdgePoint = (a + b + faceSum / faces.Count * 2f) * 0.25f;
			}

			var sharpness = EffectiveSharpness( mesh, key, faces );
			var t = float.IsPositiveInfinity( sharpness ) ? 1f : Math.Clamp( sharpness, 0f, 1f );

			newPositions[vertCount + ei] = t >= 1f
				? sharpEdgePoint
				: Vec3.Lerp( smoothEdgePoint, sharpEdgePoint, t );
		}

		// --- updated original vertices -----------------------------------------------------
		for ( var vi = 0; vi < vertCount; vi++ )
		{
			var v = mesh.Positions[vi];
			var edges = vertexEdges[vi];

			// The smooth rule is needed as a blend target even at a crease, so compute it first
			// and unconditionally.
			Vec3 smoothRule;
			var faces = vertexFaces[vi];
			var n = faces.Count;

			if ( n < 3 )
			{
				// Valence 1 or 2 interior vertices are degenerate; the (n-3)/n term misbehaves.
				// Pinning is the least surprising thing to do with them.
				smoothRule = v;
			}
			else
			{
				// F: average of adjacent face points.
				var f = Vec3.Zero;

				foreach ( var fi in faces )
					f += facePoints[fi];

				f /= n;

				// R: average of adjacent EDGE MIDPOINTS — not of the edge points computed above.
				// This is the single most commonly mis-implemented line in Catmull-Clark, and
				// using edge points here produces a surface that looks nearly right and shrinks
				// slightly wrong.
				var r = Vec3.Zero;

				foreach ( var key in edges )
					r += (mesh.Positions[key.A] + mesh.Positions[key.B]) * 0.5f;

				r /= edges.Count;

				smoothRule = (f + r * 2f + v * (n - 3)) / n;
			}

			// Sharp neighbours generalise "boundary neighbours": a boundary edge counts as
			// infinitely sharp, so a plain boundary vertex (exactly two boundary edges) falls out
			// of this as a special case rather than needing its own branch.
			var sharpNeighbours = new List<(int Other, float Sharpness)>( 2 );

			foreach ( var key in edges )
			{
				var edgeFacesHere = edgeFaces[key];
				var sharpness = EffectiveSharpness( mesh, key, edgeFacesHere );

				if ( sharpness > 0f )
					sharpNeighbours.Add( (key.A == vi ? key.B : key.A, sharpness) );
			}

			if ( sharpNeighbours.Count <= 1 )
			{
				// Zero or one sharp edge touching this vertex isn't enough to define a crease
				// curve through it, so the surface rule alone decides its position.
				newPositions[vi] = smoothRule;
				continue;
			}

			var infiniteCount = 0;
			var finiteSum = 0f;

			foreach ( var (_, s) in sharpNeighbours )
			{
				if ( float.IsPositiveInfinity( s ) ) infiniteCount++;
				else finiteSum += s;
			}

			// A vertex is only as sharp as the softest edge feeding it — averaging the finite
			// sharpnesses is what lets a partially-creased vertex settle between the two rules
			// instead of snapping. Any infinitely sharp neighbour (including a boundary edge)
			// forces full sharpness regardless of the others, the same way one hard edge at a
			// corner pins it even if the rest are soft.
			var blend = infiniteCount > 0
				? 1f
				: Math.Clamp( finiteSum / sharpNeighbours.Count, 0f, 1f );

			Vec3 sharpRule;

			if ( sharpNeighbours.Count == 2 )
			{
				// Crease rule: cubic B-spline running along the two sharp edges through this
				// vertex — exactly the old boundary-curve formula, generalised to any pair of
				// sharp edges rather than only a pair of boundary edges.
				sharpRule = (mesh.Positions[sharpNeighbours[0].Other]
					+ v * 6f
					+ mesh.Positions[sharpNeighbours[1].Other]) / 8f;
			}
			else
			{
				// Three or more sharp edges converge here — a corner. There's no single curve to
				// interpolate along, so the fully-sharp target is simply staying put, the same as
				// the old "several borders meet" pin.
				sharpRule = v;
			}

			newPositions[vi] = blend >= 1f ? sharpRule : Vec3.Lerp( smoothRule, sharpRule, blend );
		}

		// --- new faces ---------------------------------------------------------------------
		var result = new PolyMesh { Positions = new List<Vec3>( newPositions ) };

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			var n = face.Count;
			var facePointIdx = vertCount + edgeCount + fi;

			// UVs are subdivided using only this face's own corner UVs. Because nothing outside
			// the face is consulted, a seam — where two faces disagree about the UV at a shared
			// position — survives subdivision automatically. That is why UVs live per corner.
			var faceUV = Vec2.Zero;

			foreach ( var uv in face.UVs )
				faceUV += uv;

			faceUV /= n;

			for ( var i = 0; i < n; i++ )
			{
				var prev = (i - 1 + n) % n;
				var next = (i + 1) % n;

				var vCur = face.Indices[i];
				var eNext = vertCount + edgeIndex[new EdgeKey( vCur, face.Indices[next] )];
				var ePrev = vertCount + edgeIndex[new EdgeKey( face.Indices[prev], vCur )];

				// Winding runs corner → forward edge → centre → backward edge, which preserves the
				// original face's orientation.
				result.AddFace(
					new[] { vCur, eNext, facePointIdx, ePrev },
					new[]
					{
						face.UVs[i],
						(face.UVs[i] + face.UVs[next]) * 0.5f,
						faceUV,
						(face.UVs[prev] + face.UVs[i]) * 0.5f
					},
					face.Material );
			}
		}

		// --- propagate creases to the two child edges of every creased edge -----------------
		//
		// A creased edge splits into two child edges at this level — original-vertex to
		// edge-point, on each side — and each inherits the crease, decayed by one. Sharpness 1
		// decays to 0 and is dropped, so it reads as sharp for exactly one more level and smooth
		// after that; infinite sharpness stays infinite forever. The other new edges this level
		// produces — the four "spoke" edges from each face point out to its corners — are never
		// creased, because nothing marked them.
		//
		// Boundary edges are deliberately absent from this loop: their sharpness is implicit
		// (faces.Count == 1), and their child edges are boundary edges too, so they stay sharp
		// automatically without an explicit dictionary entry.
		foreach ( var (key, sharpness) in mesh.Creases )
		{
			if ( sharpness <= 0f || !edgeIndex.TryGetValue( key, out var ei ) )
				continue;

			var decayed = float.IsPositiveInfinity( sharpness ) ? sharpness : MathF.Max( sharpness - 1f, 0f );

			if ( decayed <= 0f )
				continue;

			var edgePointIdx = vertCount + ei;
			result.Creases[new EdgeKey( key.A, edgePointIdx )] = decayed;
			result.Creases[new EdgeKey( key.B, edgePointIdx )] = decayed;
		}

		return result;
	}

	/// <summary>Sharpness this edge should use this level: infinite for a boundary (one adjacent
	/// face), whatever was marked otherwise, zero by default.</summary>
	static float EffectiveSharpness( PolyMesh mesh, EdgeKey key, List<int> faces ) =>
		faces.Count == 1
			? float.PositiveInfinity
			: mesh.Creases.TryGetValue( key, out var s ) ? s : 0f;

	/// <summary>
	/// What Subdivide will cost, without running it. Cheap enough to call on every parameter change
	/// so a level slider can warn before it makes eight million vertices rather than after.
	///
	/// For a closed mesh each level gives V' = V+E+F, F' = 2E, E' = 4E.
	/// </summary>
	public static (int Vertices, int Faces) PredictCost( PolyMesh mesh, int levels )
	{
		var v = mesh.VertexCount;
		var e = mesh.BuildEdgeFaces().Count;
		var f = mesh.FaceCount;

		for ( var i = 0; i < levels; i++ )
		{
			var corners = 0;

			// Only exact for the first level; afterwards everything is quads, so 4 per face.
			if ( i == 0 )
			{
				foreach ( var face in mesh.Faces )
					corners += face.Count;
			}
			else
			{
				corners = f * 4;
			}

			var nv = v + e + f;
			var nf = corners;
			var ne = e * 2 + corners;

			v = nv;
			f = nf;
			e = ne;
		}

		return (v, f);
	}
}
