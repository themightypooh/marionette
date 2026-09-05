using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>What a bake produced. RGB8, row 0 at v = 0.</summary>
public sealed class BakedMap
{
	public readonly int Width;
	public readonly int Height;

	/// <summary>
	/// Three bytes per texel, row-major, with row 0 at v = 0.
	///
	/// WHICH END OF THE IMAGE v = 0 LANDS AT IS THE CALLER'S PROBLEM, and it is a real one: writing
	/// row 0 first puts +v down the image, which half the tools in the world disagree with. An
	/// upside-down normal map lights exactly as wrongly as one with the green channel inverted, and
	/// neither is visible in a thumbnail. See BakeOptions.FlipGreen for the other half of the same
	/// coin flip.
	/// </summary>
	public readonly byte[] Rgb;

	/// <summary>Which texels a ray actually landed on. Everything else is padding or flat.</summary>
	public readonly bool[] Filled;

	public BakedMap( int width, int height )
	{
		Width = width;
		Height = height;
		Rgb = new byte[width * height * 3];
		Filled = new bool[width * height];
	}

	public int FilledCount
	{
		get
		{
			var n = 0;

			foreach ( var f in Filled )
			{
				if ( f )
					n++;
			}

			return n;
		}
	}

	public (byte R, byte G, byte B) At( int x, int y )
	{
		var i = (y * Width + x) * 3;
		return (Rgb[i], Rgb[i + 1], Rgb[i + 2]);
	}

	/// <summary>The texel decoded back to a unit vector, which is what a test wants to reason about.</summary>
	public Vec3 NormalAt( int x, int y )
	{
		var (r, g, b) = At( x, y );
		return new Vec3( r / 127.5f - 1f, g / 127.5f - 1f, b / 127.5f - 1f ).Normal;
	}
}

public sealed class BakeOptions
{
	/// <summary>
	/// How far either side of the cage surface to look for the sculpt. Zero means "work it out from
	/// the cage's size".
	///
	/// THIS IS THE KNOB THAT PRODUCES BAD BAKES. Too small and a tall detail is missed, leaving flat
	/// patches. Too large and a ray fired from outside meets something else entirely first — the far
	/// side of a sphere, the neighbouring finger — and paints that surface's normal onto this one.
	/// Real bakers solve it with an explicit cage envelope; this takes a distance, which is the same
	/// decision with fewer controls.
	/// </summary>
	public float MaxDistance;

	/// <summary>
	/// Flip the green channel. Two conventions exist and differ only in the sign of Y: OpenGL-style
	/// (+Y up, the default here) and DirectX-style. The wrong one lights every dent as a bump and is
	/// invisible in a thumbnail, so this is a switch rather than a guess. Which one s&amp;box wants has
	/// to be confirmed by looking at a bake in the engine.
	/// </summary>
	public bool FlipGreen;

	/// <summary>
	/// How many texels to bleed the edges outward. A bake stops at the island's edge, and a shader
	/// filtering across that edge picks up whatever is outside it, so seams glow at a distance once
	/// mipmaps get involved. Four is the usual figure.
	/// </summary>
	public int Padding = 4;
}

/// <summary>
/// Cage + sculpted mesh + the cage's UVs, in; a tangent-space normal map, out.
///
/// THIS IS WHERE THE PIPELINE PAYS OFF. Everything before it exists to get a coarse quad cage that is
/// still parametric and a dense sculpted surface that rides it. This is the step that puts the second
/// one onto the first as texture, so the model that ships is the cage — a few hundred faces, already
/// unwrapped, already rigged — wearing the detail of something a thousand times heavier.
///
/// For each texel: find the cage point that texel belongs to, fire a ray along the cage normal, hit
/// the sculpt, and write the difference between the two surfaces as a direction in the cage's own
/// tangent frame. Tangent-space rather than object-space because the cage moves: it deforms with the
/// rig, and an object-space map would be wrong the moment a bone turned.
///
/// UVS MUST NOT OVERLAP. Two faces sharing texels bake over each other and the last one wins. Box
/// projection, the tool's default, overlaps by construction — it maps +X and -X onto the same square
/// on purpose, which is exactly right for tiling a wall and exactly wrong for a bake. Ask
/// <see cref="Measure"/> before trusting a map.
/// </summary>
public static class NormalBake
{
	public static BakedMap Bake( PolyMesh cage, PolyMesh sculpted, int size, BakeOptions options = null ) =>
		Bake( cage, sculpted, size, size, options );

	public static BakedMap Bake( PolyMesh cage, PolyMesh sculpted, int width, int height, BakeOptions options = null )
	{
		if ( cage is null )
			throw new ArgumentNullException( nameof( cage ) );

		if ( sculpted is null )
			throw new ArgumentNullException( nameof( sculpted ) );

		if ( width < 1 || height < 1 )
			throw new ArgumentOutOfRangeException( nameof( width ), "A map needs at least one texel." );

		options ??= new BakeOptions();

		var map = new BakedMap( width, height );

		// Flat everywhere first. An unhit texel reading as "no change from the cage" is the harmless
		// answer; leaving it black would light as a crease.
		for ( var i = 0; i < width * height; i++ )
			Encode( map, i, new Vec3( 0, 0, 1 ), options.FlipGreen );

		var cageNormals = cage.ComputeVertexNormals();
		var sculptNormals = sculpted.ComputeVertexNormals();
		var bvh = MeshBVH.Build( sculpted );
		var reach = options.MaxDistance > 0f ? options.MaxDistance : MeasuredReach( cage, sculpted, bvh );

		foreach ( var face in cage.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			// Fan from corner 0, the same triangulation everything else here uses.
			for ( var i = 1; i < face.Count - 1; i++ )
			{
				var t = new Triangle(
					cage.Positions[face.Indices[0]], cage.Positions[face.Indices[i]], cage.Positions[face.Indices[i + 1]],
					cageNormals[face.Indices[0]], cageNormals[face.Indices[i]], cageNormals[face.Indices[i + 1]],
					face.UVs[0], face.UVs[i], face.UVs[i + 1] );

				BakeTriangle( map, t, sculpted, sculptNormals, bvh, reach, options );
			}
		}

		Dilate( map, options.Padding );
		return map;
	}

	readonly struct Triangle
	{
		public readonly Vec3 P0, P1, P2;
		public readonly Vec3 N0, N1, N2;
		public readonly Vec2 U0, U1, U2;

		public Triangle( Vec3 p0, Vec3 p1, Vec3 p2, Vec3 n0, Vec3 n1, Vec3 n2, Vec2 u0, Vec2 u1, Vec2 u2 )
		{
			P0 = p0; P1 = p1; P2 = p2;
			N0 = n0; N1 = n1; N2 = n2;
			U0 = u0; U1 = u1; U2 = u2;
		}
	}

	static void BakeTriangle( BakedMap map, Triangle t, PolyMesh sculpted, Vec3[] sculptNormals,
		MeshBVH bvh, float reach, BakeOptions options )
	{
		if ( !TangentFrame( t, out var tangent, out var bitangent ) )
			return;

		Rasterise( t.U0, t.U1, t.U2, map.Width, map.Height, ( x, y, a, b, c ) =>
		{
			var point = t.P0 * a + t.P1 * b + t.P2 * c;
			var normal = (t.N0 * a + t.N1 * b + t.N2 * c).Normal;

			if ( normal.LengthSquared < 0.5f )
				return;

			// Fired from outside the surface inward, so the nearest hit is the topmost sculpted
			// surface rather than whatever is behind it.
			var hit = bvh.Raycast( sculpted, point + normal * reach, -normal );

			if ( hit is null || hit.Value.Distance > reach * 2f )
				return;

			var sculptedNormal = SmoothNormal( sculpted, sculptNormals, hit.Value );

			// Orthonormalise the frame against THIS texel's interpolated normal, not the flat
			// triangle's, or every texel in the triangle shares one frame and the map facets.
			var tt = (tangent - normal * Vec3.Dot( tangent, normal )).Normal;

			if ( tt.LengthSquared < 0.5f )
				return;

			var bb = Vec3.Cross( normal, tt ).Normal;

			if ( Vec3.Dot( bb, bitangent ) < 0f )
				bb = -bb;

			var local = new Vec3(
				Vec3.Dot( sculptedNormal, tt ),
				Vec3.Dot( sculptedNormal, bb ),
				Vec3.Dot( sculptedNormal, normal ) ).Normal;

			if ( local.LengthSquared < 0.5f )
				return;

			var index = y * map.Width + x;
			Encode( map, index, local, options.FlipGreen );
			map.Filled[index] = true;
		} );
	}

	/// <summary>
	/// Tangent and bitangent from the UV gradient — the standard derivation, and the one a shader
	/// assumes on the other end. A triangle whose UVs are degenerate has no frame to give and is
	/// skipped rather than guessed at.
	/// </summary>
	static bool TangentFrame( Triangle t, out Vec3 tangent, out Vec3 bitangent )
	{
		tangent = Vec3.Zero;
		bitangent = Vec3.Zero;

		var e1 = t.P1 - t.P0;
		var e2 = t.P2 - t.P0;
		var d1 = new Vec2( t.U1.x - t.U0.x, t.U1.y - t.U0.y );
		var d2 = new Vec2( t.U2.x - t.U0.x, t.U2.y - t.U0.y );

		var det = d1.x * d2.y - d2.x * d1.y;

		if ( MathF.Abs( det ) < 1e-12f )
			return false;

		var r = 1f / det;
		tangent = (e1 * d2.y - e2 * d1.y) * r;
		bitangent = (e2 * d1.x - e1 * d2.x) * r;

		return tangent.LengthSquared > 1e-16f && bitangent.LengthSquared > 1e-16f;
	}

	/// <summary>
	/// The sculpted surface's normal where the ray landed, interpolated across the face rather than
	/// taken flat from it. A face normal would bake the sculpt's own faceting into the map, which is
	/// the one thing a normal map exists to avoid.
	/// </summary>
	static Vec3 SmoothNormal( PolyMesh mesh, Vec3[] normals, MeshHit hit )
	{
		var face = mesh.Faces[hit.FaceIndex];
		var corners = new List<Vec3>( face.Count );

		for ( var c = 0; c < face.Count; c++ )
			corners.Add( mesh.Positions[face.Indices[c]] );

		// THE SAME TRIANGULATION THE RAYCAST USED, not a fan from corner 0.
		//
		// Triangulate.Face splits a quad along whichever diagonal suits its shape, and a sculpted quad
		// is rarely planar. Fanning from corner 0 instead can put the hit point outside every triangle
		// this looks at, in which case the search finds nothing and the fallback writes the FACE
		// normal into that texel — a faceted speck in a map whose whole purpose is to not be faceted.
		//
		// Honest note: this was changed on the reasoning above, not on a measurement. Swapping it made
		// no visible difference to the smoothness numbers on the plane fixture, whose quads are near
		// enough planar that both triangulations agree. It is the correct thing to do and the fallback
		// is real; how often it fires on a heavily sculpted model has not been measured.
		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			if ( !Barycentric( hit.Point, corners[ia], corners[ib], corners[ic], out var u, out var v, out var w ) )
				continue;

			var n = normals[face.Indices[ia]] * u + normals[face.Indices[ib]] * v + normals[face.Indices[ic]] * w;

			if ( n.LengthSquared > 1e-12f )
				return n.Normal;
		}

		return hit.Normal;
	}

	/// <summary>Barycentric coordinates of a point already known to lie in the triangle's plane.</summary>
	static bool Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w )
	{
		u = v = w = 0f;

		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;

		var d00 = Vec3.Dot( v0, v0 );
		var d01 = Vec3.Dot( v0, v1 );
		var d11 = Vec3.Dot( v1, v1 );
		var d20 = Vec3.Dot( v2, v0 );
		var d21 = Vec3.Dot( v2, v1 );

		var denom = d00 * d11 - d01 * d01;

		if ( MathF.Abs( denom ) < 1e-20f )
			return false;

		v = (d11 * d20 - d01 * d21) / denom;
		w = (d00 * d21 - d01 * d20) / denom;
		u = 1f - v - w;

		const float slack = 1e-4f;
		return u >= -slack && v >= -slack && w >= -slack;
	}

	/// <summary>
	/// Walk the texels a UV triangle covers, sampling at texel centres. Shared by the bake and by
	/// <see cref="Measure"/>, so the two can never disagree about which texels a face owns.
	///
	/// Now also the paint dab, which is why it is internal rather than private: a dab rasterises a
	/// face's UV triangle with the same walk the bake uses, and two walks that disagreed would put
	/// paint and normals on different texels.
	/// </summary>
	internal static void Rasterise( Vec2 a, Vec2 b, Vec2 c, int width, int height, Action<int, int, float, float, float> texel )
	{
		var minX = (int)MathF.Floor( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) * width - 1f );
		var maxX = (int)MathF.Ceiling( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) * width + 1f );
		var minY = (int)MathF.Floor( MathF.Min( a.y, MathF.Min( b.y, c.y ) ) * height - 1f );
		var maxY = (int)MathF.Ceiling( MathF.Max( a.y, MathF.Max( b.y, c.y ) ) * height + 1f );

		minX = Math.Max( minX, 0 );
		minY = Math.Max( minY, 0 );
		maxX = Math.Min( maxX, width - 1 );
		maxY = Math.Min( maxY, height - 1 );

		var area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);

		if ( MathF.Abs( area ) < 1e-16f )
			return;

		// Wound consistently so the fill rule below has a fixed sense of "inside".
		var flipped = area < 0f;
		var p1 = flipped ? c : b;
		var p2 = flipped ? b : c;
		var doubled = MathF.Abs( area );

		for ( var y = minY; y <= maxY; y++ )
		{
			for ( var x = minX; x <= maxX; x++ )
			{
				var px = (x + 0.5f) / width;
				var py = (y + 0.5f) / height;

				var e0 = Edge( p1, p2, px, py );
				var e1 = Edge( p2, a, px, py );
				var e2 = Edge( a, p1, px, py );

				if ( !Covers( e0, p1, p2 ) || !Covers( e1, p2, a ) || !Covers( e2, a, p1 ) )
					continue;

				var w0 = e0 / doubled;
				var wp1 = e1 / doubled;
				var wp2 = e2 / doubled;

				texel( x, y, w0, flipped ? wp2 : wp1, flipped ? wp1 : wp2 );
			}
		}
	}

	static float Edge( Vec2 u, Vec2 v, float px, float py ) =>
		(v.x - u.x) * (py - u.y) - (v.y - u.y) * (px - u.x);

	/// <summary>
	/// The top-left fill rule, which is what makes a shared edge belong to exactly ONE face.
	///
	/// THIS REPLACED A TOLERANCE, AND THE TOLERANCE WAS WRONG. Accepting any texel within a slack of
	/// the triangle meant a texel centre landing on the edge between two coplanar faces satisfied
	/// both of them — so `Measure` reported overlapping UVs on a mesh whose UVs were perfect, and the
	/// bake wrote the same texel twice. It showed up as exactly one texel on a quadsphere, which is
	/// the kind of number that invites tuning the threshold instead of fixing the rule.
	///
	/// A point exactly on an edge is awarded to the triangle for which that edge is a left or a top
	/// edge; the neighbour, walking the same edge the other way, declines it. Standard, and the only
	/// answer that is exact rather than nearly exact.
	///
	/// Now also the paint dab's fill rule, which is why it is internal rather than private: the dab
	/// and the bake must agree about which face owns a texel on a shared edge, or the dab paints a
	/// seam the bake never touched.
	/// </summary>
	internal static bool Covers( float e, Vec2 u, Vec2 v )
	{
		if ( e > 0f )
			return true;

		if ( e < 0f )
			return false;

		// y is up here, and the winding above is counter-clockwise, so the interior lies to the left
		// of each directed edge: a left edge climbs, a top edge runs right to left.
		var dy = v.y - u.y;

		return dy > 0f || (dy == 0f && v.x < u.x);
	}

	static void Encode( BakedMap map, int index, Vec3 n, bool flipGreen )
	{
		var y = flipGreen ? -n.y : n.y;
		var i = index * 3;

		map.Rgb[i] = Byte( n.x );
		map.Rgb[i + 1] = Byte( y );
		map.Rgb[i + 2] = Byte( n.z );
	}

	static byte Byte( float v ) => (byte)Math.Clamp( MathF.Round( (v + 1f) * 127.5f ), 0f, 255f );

	/// <summary>
	/// Bleed filled texels outward, so a shader filtering across an island's edge finds something
	/// sensible there. Each pass takes the average of the filled neighbours; without it seams glow
	/// once mipmaps start mixing in whatever sat outside the island.
	///
	/// Now also the paint canvas, which is why it is internal rather than private: a painted dab
	/// bleeds outward at its edge for exactly the same reason a bake does.
	/// </summary>
	internal static void Dilate( BakedMap map, int passes )
	{
		if ( passes <= 0 )
			return;

		var filled = (bool[])map.Filled.Clone();

		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int Index, int R, int G, int B)>();

			for ( var y = 0; y < map.Height; y++ )
			{
				for ( var x = 0; x < map.Width; x++ )
				{
					var index = y * map.Width + x;

					if ( filled[index] )
						continue;

					int r = 0, g = 0, b = 0, n = 0;

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							var nx = x + dx;
							var ny = y + dy;

							if ( nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height )
								continue;

							var other = ny * map.Width + nx;

							if ( !filled[other] )
								continue;

							r += map.Rgb[other * 3];
							g += map.Rgb[other * 3 + 1];
							b += map.Rgb[other * 3 + 2];
							n++;
						}
					}

					if ( n > 0 )
						added.Add( (index, r / n, g / n, b / n) );
				}
			}

			if ( added.Count == 0 )
				return;

			foreach ( var (index, r, g, b) in added )
			{
				map.Rgb[index * 3] = (byte)r;
				map.Rgb[index * 3 + 1] = (byte)g;
				map.Rgb[index * 3 + 2] = (byte)b;
				filled[index] = true;
			}
		}
	}

	/// <summary>
	/// How far to search, MEASURED off the two surfaces rather than guessed from the model's size.
	///
	/// A FRACTION OF THE DIAGONAL IS NOT GOOD ENOUGH, and the case that proves it is the ordinary one:
	/// a SculptFeature's cage IS the coarse body and the sculpt IS its Catmull-Clark subdivision, and
	/// subdivision pulls a cube's corners a very long way in. On a 2x2x2 box the two surfaces are 2.6
	/// units apart at the corners while a tenth of the diagonal is 0.35 — so the old default missed
	/// three quarters of the map and the bake came out mostly flat. Nothing about that is exotic; it
	/// is what pressing Bake on a box does.
	///
	/// So: probe from every cage vertex along its own normal, far enough to cross both models, and
	/// take the largest separation actually found. Times 1.5, because texels sit between vertices and
	/// the surface can bow further out between two of them than at either.
	///
	/// Still bounded above by the cage's own diagonal. The failure at the other end — a reach so long
	/// that a ray meets the far side of the model and paints its normal onto this one — is worse than
	/// a flat patch, because it looks like detail.
	/// </summary>
	static float MeasuredReach( PolyMesh cage, PolyMesh sculpted, MeshBVH bvh )
	{
		var diagonal = cage.BoundsDiagonal;

		if ( diagonal <= 1e-6f )
			return 1f;

		var probe = diagonal + sculpted.BoundsDiagonal;
		var normals = cage.ComputeVertexNormals();
		var worst = 0f;

		for ( var i = 0; i < cage.VertexCount; i++ )
		{
			var normal = normals[i];

			if ( normal.LengthSquared < 0.5f )
				continue;

			var hit = bvh.Raycast( sculpted, cage.Positions[i] + normal * probe, -normal );

			if ( hit is null )
				continue;

			worst = MathF.Max( worst, MathF.Abs( hit.Value.Distance - probe ) );
		}

		// The floor covers a cage sitting exactly on its sculpt, where every probe returns zero and a
		// reach of zero would find nothing at all.
		return Math.Clamp( worst * 1.5f, diagonal * 0.1f, diagonal );
	}

	// --- the check the bake depends on ----------------------------------------------------------

	/// <summary>
	/// What a mesh's UVs look like as an atlas: how much of the square they cover, how much they
	/// cover twice, and whether they stay inside it at all.
	/// </summary>
	public static UVCoverage Measure( PolyMesh mesh, int resolution = 256 )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var counts = new int[resolution * resolution];
		var outside = 0;

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var escapes = false;

			foreach ( var uv in face.UVs )
			{
				if ( uv.x < -1e-4f || uv.x > 1f + 1e-4f || uv.y < -1e-4f || uv.y > 1f + 1e-4f )
					escapes = true;
			}

			if ( escapes )
				outside++;

			// One face must not count a texel twice, however many fan triangles cover it.
			var mine = new HashSet<int>();

			for ( var i = 1; i < face.Count - 1; i++ )
			{
				Rasterise( face.UVs[0], face.UVs[i], face.UVs[i + 1], resolution, resolution,
					( x, y, _, _, _ ) => mine.Add( y * resolution + x ) );
			}

			foreach ( var index in mine )
				counts[index]++;
		}

		var covered = 0;
		var overlapping = 0;

		foreach ( var n in counts )
		{
			if ( n > 0 )
				covered++;

			if ( n > 1 )
				overlapping++;
		}

		return new UVCoverage( resolution, covered, overlapping, outside, mesh.FaceCount );
	}
}

/// <summary>
/// The verdict on a mesh's UVs as a bake target.
///
/// The plan has said since it was written that a bake needs non-overlapping UVs and that nothing
/// checked it. This is that check. It is worth running before a bake rather than after, because an
/// overlapping bake does not fail — it produces a map that looks plausible and is wrong wherever two
/// faces shared a texel.
/// </summary>
public sealed class UVCoverage
{
	public readonly int Resolution;
	public readonly int CoveredTexels;
	public readonly int OverlappingTexels;
	public readonly int FacesOutsideTheSquare;
	public readonly int FaceCount;

	public UVCoverage( int resolution, int covered, int overlapping, int outside, int faces )
	{
		Resolution = resolution;
		CoveredTexels = covered;
		OverlappingTexels = overlapping;
		FacesOutsideTheSquare = outside;
		FaceCount = faces;
	}

	public float CoveredFraction => (float)CoveredTexels / (Resolution * Resolution);

	public float OverlapFraction => CoveredTexels == 0 ? 0f : (float)OverlappingTexels / CoveredTexels;

	/// <summary>Whether these UVs can carry a bake at all.</summary>
	public bool CanBake => OverlappingTexels == 0 && FacesOutsideTheSquare == 0 && CoveredTexels > 0;

	/// <summary>The refusal, in the shape a diagnostic wants: what is wrong, with this model's numbers.</summary>
	public string Problem
	{
		get
		{
			if ( CoveredTexels == 0 )
				return "These UVs cover none of the texture square, so a bake would have nowhere to write.";

			if ( FacesOutsideTheSquare > 0 )
				return $"{FacesOutsideTheSquare} of {FaceCount} faces have UVs outside the 0-1 square. "
					+ "Box projection tiles by design and is meant for repeating a texture, not for a bake.";

			if ( OverlappingTexels > 0 )
				return $"{OverlappingTexels} texels ({OverlapFraction:P0} of those covered) are claimed by more "
					+ "than one face. A bake writes both and keeps whichever ran last.";

			return null;
		}
	}
}
