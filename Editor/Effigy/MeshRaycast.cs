using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Where a ray hit a mesh: the point, the face it hit, and that face's normal.</summary>
public readonly struct MeshHit
{
	public readonly Vec3 Point;
	public readonly int FaceIndex;
	public readonly Vec3 Normal;
	public readonly float Distance;

	public MeshHit( Vec3 point, int faceIndex, Vec3 normal, float distance )
	{
		Point = point;
		FaceIndex = faceIndex;
		Normal = normal;
		Distance = distance;
	}
}

/// <summary>
/// Ray-mesh intersection, for clicking a face of a solid in the viewport.
///
/// PURE GEOMETRY, NO ENGINE SURFACE — which is why it lives here rather than in the editor. The
/// only thing the viewport contributes is the ray itself (Gizmo.CurrentRay, converted to Vec3);
/// everything about deciding which triangle it hit is ordinary math that can be built and proven
/// without s&amp;box anywhere near it.
///
/// Faces are triangulated the same way EffigyPreview builds the render mesh — by Triangulate.Face
/// — so a click hits exactly the triangle that would actually be drawn there. A different
/// triangulation would occasionally pick a face whose diagonal put the real geometry on the other
/// side of the click, and a fan over a concave cap would let clicks land in the notch it wrongly
/// filled in.
/// </summary>
public static class MeshRaycast
{
	/// <summary>
	/// The nearest face of <paramref name="mesh"/> that <paramref name="origin"/> + t *
	/// <paramref name="direction"/> hits, for t > 0. Null if nothing is hit.
	/// </summary>
	public static MeshHit? Raycast( PolyMesh mesh, Vec3 origin, Vec3 direction )
	{
		if ( mesh is null )
			return null;

		var dir = direction.Normal;

		MeshHit? best = null;

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			if ( !HitFace( mesh, fi, origin, dir, out var t, out var point ) )
				continue;

			if ( best is { } current && t >= current.Distance )
				continue;

			best = new MeshHit( point, fi, mesh.FaceNormal( mesh.Faces[fi] ), t );
		}

		return best;
	}

	/// <summary>
	/// Nearest triangle of one face. Shared by the linear scan and the BVH so a click cannot
	/// disagree with a stroke sample about which triangle was there.
	/// </summary>
	public static bool HitFace( PolyMesh mesh, int faceIndex, Vec3 origin, Vec3 dir, out float t, out Vec3 point )
	{
		t = 0f;
		point = default;

		if ( mesh is null || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return false;

		var face = mesh.Faces[faceIndex];

		if ( face.Count < 3 )
			return false;

		var corners = new List<Vec3>( face.Count );

		for ( var c = 0; c < face.Count; c++ )
			corners.Add( mesh.Positions[face.Indices[c]] );

		var hit = false;
		var bestT = float.MaxValue;
		var bestP = default( Vec3 );

		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			if ( !TriangleHit( origin, dir, corners[ia], corners[ib], corners[ic], out var cand, out var p ) )
				continue;

			if ( cand >= bestT )
				continue;

			bestT = cand;
			bestP = p;
			hit = true;
		}

		if ( !hit )
			return false;

		t = bestT;
		point = bestP;
		return true;
	}

	/// <summary>
	/// The edge of this face nearest <paramref name="point"/>, and how far the point sits from
	/// that segment.
	///
	/// A click on a solid is a face hit first. Whether it was meant as an EDGE is a question of
	/// how close the hit landed to a boundary — the viewport compares this distance to a
	/// screen-pixel threshold, so a click in the middle of a face stays a face and a click near
	/// a corner becomes the edge.
	/// </summary>
	public static bool ClosestEdge( PolyMesh mesh, int faceIndex, Vec3 point, out EdgeKey key,
		out Vec3 closest, out float distance )
	{
		key = default;
		closest = default;
		distance = float.MaxValue;

		if ( mesh is null || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return false;

		var face = mesh.Faces[faceIndex];

		if ( face.Count < 2 )
			return false;

		var found = false;

		for ( var i = 0; i < face.Count; i++ )
		{
			var a = mesh.Positions[face.Indices[i]];
			var b = mesh.Positions[face.Indices[(i + 1) % face.Count]];
			var ab = b - a;
			var lengthSq = ab.LengthSquared;

			if ( lengthSq < 1e-20f )
				continue;

			var t = Vec3.Dot( point - a, ab ) / lengthSq;

			if ( t < 0f )
				t = 0f;
			else if ( t > 1f )
				t = 1f;

			var on = a + ab * t;
			var d = (on - point).Length;

			if ( d >= distance )
				continue;

			distance = d;
			closest = on;
			key = new EdgeKey( face.Indices[i], face.Indices[(i + 1) % face.Count] );
			found = true;
		}

		return found;
	}

	/// <summary>
	/// Nearest hit across several bodies at once, with the winning body reported alongside it —
	/// what a click in a multi-body studio actually needs.
	/// </summary>
	/// <summary>
	/// The nearest face of any of <paramref name="bodies"/> that YOU CAN ACTUALLY SEE.
	///
	/// Effigy does not union bodies - two overlapping extrudes are two separate closed solids, and
	/// the faces of one that fall inside the other are still there, still hit by a ray, and quite
	/// invisible. Picking one is how you end up sketching on a plane buried inside your part,
	/// which is exactly as confusing as it sounds: the highlight paints a rectangle straight
	/// through the model and the sketch lands somewhere you never pointed at.
	///
	/// So a hit is discarded when the surface it landed on is inside another solid. Sorting the
	/// candidates first means the common case - nothing overlapping - costs one containment test.
	/// </summary>
	public static (Body Body, MeshHit Hit)? Raycast( IEnumerable<Body> bodies, Vec3 origin, Vec3 direction )
	{
		if ( bodies is null )
			return null;

		var list = new List<Body>();

		foreach ( var body in bodies )
		{
			if ( body?.Mesh is not null )
				list.Add( body );
		}

		var candidates = new List<(Body Body, MeshHit Hit)>( list.Count );

		foreach ( var body in list )
		{
			if ( Raycast( body.Mesh, origin, direction ) is { } hit )
				candidates.Add( (body, hit) );
		}

		candidates.Sort( ( a, b ) => a.Hit.Distance.CompareTo( b.Hit.Distance ) );

		var dir = direction.Normal;

		foreach ( var candidate in candidates )
		{
			// Step back off the surface along the ray, so the test point is in the space the ray
			// travelled through rather than exactly on the boundary, where inside/outside is a
			// coin flip. Scaled by the distance travelled, since a sketch can be a unit across or
			// a thousand.
			var epsilon = 1e-4f * (1f + candidate.Hit.Distance);
			var probe = candidate.Hit.Point - dir * epsilon;
			var buried = false;

			foreach ( var other in list )
			{
				if ( ReferenceEquals( other, candidate.Body ) )
					continue;

				if ( PointInsideSolid( other.Mesh, probe ) )
				{
					buried = true;
					break;
				}
			}

			if ( !buried )
				return candidate;
		}

		return null;
	}

	/// <summary>
	/// Is a point inside a closed mesh? Crossing count along an arbitrary ray: odd is inside.
	///
	/// The direction is a fixed lopsided one rather than an axis, because an axis-aligned ray from
	/// a point on a box lands exactly along edges and coplanar faces, and every such ray is a
	/// coin-flip on whether a crossing gets counted once, twice or not at all.
	/// </summary>
	public static bool PointInsideSolid( PolyMesh mesh, Vec3 point )
	{
		if ( mesh is null || mesh.Faces.Count == 0 )
			return false;

		var direction = new Vec3( 0.5773f, 0.5771f, 0.5775f ).Normal;
		var crossings = 0;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				if ( TriangleHit( point, direction, corners[ia], corners[ib], corners[ic], out _, out _ ) )
					crossings++;
			}
		}

		return (crossings & 1) == 1;
	}

	/// <summary>
	/// Möller–Trumbore. Returns the ray parameter and world point on a hit with t > 0; a
	/// back-facing triangle counts too, since a click through a thin wall should still register
	/// something rather than nothing.
	/// </summary>
	public static bool TriangleHit( Vec3 origin, Vec3 dir, Vec3 a, Vec3 b, Vec3 c, out float t, out Vec3 point )
	{
		t = 0f;
		point = default;

		const float eps = 1e-7f;

		var edge1 = b - a;
		var edge2 = c - a;
		var h = Vec3.Cross( dir, edge2 );
		var det = Vec3.Dot( edge1, h );

		if ( MathF.Abs( det ) < eps )
			return false;

		var invDet = 1f / det;
		var s = origin - a;
		var u = invDet * Vec3.Dot( s, h );

		if ( u < -eps || u > 1f + eps )
			return false;

		var q = Vec3.Cross( s, edge1 );
		var v = invDet * Vec3.Dot( dir, q );

		if ( v < -eps || u + v > 1f + eps )
			return false;

		var candidate = invDet * Vec3.Dot( edge2, q );

		if ( candidate <= eps )
			return false;

		t = candidate;
		point = origin + dir * t;
		return true;
	}
}
