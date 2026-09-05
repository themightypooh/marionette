using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Replays paint strokes onto a canvas — the derived-artifact half of the storage decision in
/// docs/dev/PAINTING.md §4.
///
/// Paint is stored as strokes in object space, not as texels, so a rebuilt or re-unwrapped mesh gets
/// its paint re-applied from scratch rather than pasted through an atlas that moved under it. This is
/// the re-applier: for each stroke it drops the brush sphere onto the surface and rasterises whatever
/// faces the sphere touches, weighting every texel by its 3D distance from the dab centre. Neither
/// the brush nor the rasteriser ever learns what a seam is — a stroke crossing a chart boundary
/// paints both charts because both have faces inside the sphere.
///
/// THE DAB IS SHARED, NOT DUPLICATED. <see cref="PaintSession"/> composites a live dab through
/// <see cref="Dab"/>; a rebuild replays the whole list through <see cref="Replay"/>. Both paths end
/// at the same rasterise-and-blend, so a stroke painted live and the same stroke replayed later
/// produce identical texels.
/// </summary>
public static class PaintReplay
{
	/// <summary>How many texels a replayed canvas bleeds outward at its islands' edges, so a shader
	/// filtering across a seam finds colour there instead of the gutter. Same figure the bake uses.</summary>
	const int DilatePasses = 4;

	/// PARKED. This is the texture half - see the note on PaintCanvas. The live path is
	/// <see cref="ReplayColors"/> just below, which is what PaintFeature and PaintSession use.
	///
	/// <summary>
	/// Replay every stroke, in order, onto a fresh canvas. The order is the whole point — colour
	/// blending does not commute — and this preserves it exactly.
	/// </summary>
	public static PaintCanvas Replay( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var canvas = new PaintCanvas( resolution, resolution );

		if ( strokes is { Count: > 0 } )
		{
			var bvh = MeshBVH.Build( mesh );
			var faces = new List<int>();

			foreach ( var stroke in strokes )
				PaintStroke( stroke, mesh, bvh, canvas, faces );

			canvas.Dilate( DilatePasses );
		}

		return canvas;
	}

	/// <summary>Paint one stroke's dabs onto an existing canvas, without the final dilation. The live
	/// session uses this to seed its canvas from already-committed strokes.</summary>
	internal static void PaintStroke( PaintStroke stroke, PolyMesh mesh, MeshBVH bvh, PaintCanvas canvas, List<int> faces )
	{
		if ( stroke is null || stroke.Path.Count == 0 )
			return;

		var r = ToByte( stroke.R );
		var g = ToByte( stroke.G );
		var b = ToByte( stroke.B );

		// Coverage folds the stroke's own opacity into its strength, so the alpha Blend sees is
		// "how much paint arrives here" and the colour has no alpha of its own to double-count.
		var coverage = stroke.Strength * stroke.A;

		foreach ( var point in stroke.Path )
			Dab( mesh, bvh, canvas, point.Position, point.Normal, stroke.Radius, coverage, stroke.Falloff, r, g, b, faces );
	}

	/// <summary>
	/// One dab: the brush sphere at <paramref name="point"/>, rasterised into the faces it reaches.
	///
	/// THE 3D FOOTPRINT IS THE WHOLE DESIGN. A 2D disc in UV space cannot know a seam exists; a sphere
	/// in object space touches faces on both sides of one and paints them both for free. The normal
	/// gate is the other half of the same idea — a dab on one side of a thin wall must not bleed
	/// through to the far face, which is rejected by comparing its normal to the recorded surface
	/// normal.
	/// </summary>
	internal static void Dab( PolyMesh mesh, MeshBVH bvh, PaintCanvas canvas, Vec3 point, Vec3 normal,
		float radius, float strength, BrushFalloff falloff, byte r, byte g, byte b, List<int> faces )
	{
		if ( radius <= 0f || strength <= 0f )
			return;

		bvh.FacesInRadius( mesh, point, radius, faces );

		var n = normal.LengthSquared >= 0.5f ? normal.Normal : new Vec3( 0, 0, 1 );

		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			// The far side of a thin wall points away from the brush; a face exactly perpendicular to
			// it belongs to the neighbouring face's own dab, not this one.
			if ( Vec3.Dot( mesh.FaceNormal( face ), n ) <= 0f )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var p0 = corners[ia];
				var p1 = corners[ib];
				var p2 = corners[ic];

				var u0 = face.UVs[ia];
				var u1 = face.UVs[ib];
				var u2 = face.UVs[ic];

				NormalBake.Rasterise( u0, u1, u2, canvas.Width, canvas.Height, ( x, y, wa, wb, wc ) =>
				{
					// The barycentrics Rasterise hands back let a texel's 3D position be rebuilt from
					// its three corners, so the falloff can be measured in object space rather than in
					// UV space — the same distance that made the brush one consistent size everywhere.
					var p = p0 * wa + p1 * wb + p2 * wc;
					var t = (p - point).Length / radius;
					var weight = Brush.Falloff( t, falloff ) * strength;

					if ( weight <= 0f )
						return;

					canvas.Blend( x, y, r, g, b, weight );
				} );
			}
		}
	}

	/// <summary>A float colour channel to a byte, the same rounding the canvas blend uses.</summary>
	internal static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );

	// --- vertex-colour output ------------------------------------------------------------------

	/// <summary>
	/// Replay every stroke, in order, onto a per-vertex colour array — the vertex-colour half of the
	/// same storage. Where the texture path rasterises dabs into an atlas, this one colours vertices,
	/// so it needs no UVs at all and composes over a material the engine multiplies by vertex colour.
	/// Resolution is the mesh's, which is why it is meant to run after a Subdivide or Sculpt.
	/// </summary>
	public static Vec4[] ReplayColors( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var colors = new Vec4[mesh.VertexCount];

		if ( strokes is { Count: > 0 } )
		{
			var bvh = MeshBVH.Build( mesh );
			var normals = mesh.ComputeVertexNormals();
			var found = new List<int>();

			foreach ( var stroke in strokes )
				PaintStrokeColors( stroke, mesh, bvh, normals, colors, found );
		}

		return colors;
	}

	internal static void PaintStrokeColors( PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		Vec3[] normals, Vec4[] colors, List<int> found )
	{
		if ( stroke is null || stroke.Path.Count == 0 )
			return;

		var coverage = stroke.Strength * stroke.A;

		foreach ( var point in stroke.Path )
			DabColors( mesh, bvh, normals, colors, point.Position, point.Normal,
				stroke.Radius, coverage, stroke.Falloff, stroke.R, stroke.G, stroke.B, found );
	}

	/// <summary>
	/// One dab into vertex colours: the vertices in the brush sphere, the far side of a thin wall
	/// rejected by its normal, each weighted by its 3D distance through the falloff and blended
	/// source-over — the same colour math the texture dab uses, per vertex instead of per texel.
	/// </summary>
	internal static void DabColors( PolyMesh mesh, MeshBVH bvh, Vec3[] normals, Vec4[] colors,
		Vec3 point, Vec3 normal, float radius, float coverage, BrushFalloff falloff,
		float r, float g, float b, List<int> found )
	{
		if ( radius <= 0f || coverage <= 0f )
			return;

		bvh.VerticesInRadius( mesh, point, radius, found );

		var n = normal.LengthSquared >= 0.5f ? normal.Normal : new Vec3( 0, 0, 1 );

		foreach ( var vi in found )
		{
			// A vertex whose normal points away from the brush is on the far side of a thin wall and
			// must not be painted from here.
			if ( Vec3.Dot( normals[vi], n ) <= 0f )
				continue;

			var dist = (mesh.Positions[vi] - point).Length;
			var t = dist / radius;
			var weight = Brush.Falloff( t, falloff ) * coverage;

			if ( weight <= 0f )
				continue;

			colors[vi] = SourceOver( colors[vi], r, g, b, weight );
		}
	}

	/// <summary>Source-over over straight-alpha RGBA, in float space. Mirrors PaintCanvas.Blend so a
	/// vertex dab and a texel dab read the same colour, but keeps full float precision.</summary>
	internal static Vec4 SourceOver( Vec4 dst, float r, float g, float b, float a )
	{
		a = Math.Clamp( a, 0f, 1f );

		var da = dst.w;
		var oa = a + da * (1f - a);

		if ( oa <= 0f )
			return dst;

		var or = (r * a + dst.x * da * (1f - a)) / oa;
		var og = (g * a + dst.y * da * (1f - a)) / oa;
		var ob = (b * a + dst.z * da * (1f - a)) / oa;

		return new Vec4( or, og, ob, oa );
	}
}
