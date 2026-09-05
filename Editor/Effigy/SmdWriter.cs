using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// Studiomdl Data (SMD) export — the skinned path.
///
/// WHY SMD RATHER THAN OBJ. OBJ cannot carry bones or vertex weights, so the moment a model is
/// rigged it stops being an export target. SMD is plain ASCII, carries a bone hierarchy, a bind
/// pose and per-vertex weights, assigns a material per triangle, and is on ModelDoc's documented
/// import list (DMX, SMD, FBX, OBJ, VOX). It is the cheapest format that can express everything
/// this kernel produces.
///
/// It also collapses two export paths into one: a STATIC model is just a mesh weighted entirely to
/// a single root bone. There is no separate static writer to keep in step.
///
/// WHAT IT COSTS. SMD is triangles-only, so the quad cage is triangulated on the way out. That is
/// fine — the cage lives in the tool's own document and export is a bake — but it means an SMD is
/// not a round trip. It also carries exactly one UV channel and no vertex colours; if vertex paint
/// ever matters, that is the day to look at DMX.
///
/// PORTABILITY. This is an s&amp;box-side format and it deliberately knows nothing about the skeleton
/// beyond what Skeleton already exposes. Godot's equivalent writes bones and weights straight into
/// an ArrayMesh; it is a peer of this file, not a replacement for it, and neither should be able to
/// push assumptions back into Skeleton or SkinWeights.
/// </summary>
public static class SmdWriter
{
	/// <summary>Engines index a fixed number of bones per vertex, and four is effectively
	/// universal. Weights are pruned to this exactly once, here, at the boundary.</summary>
	public const int MaxInfluences = 4;

	public static void WriteFile(
		PolyMesh mesh,
		string path,
		Skeleton skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string> materialName = null )
	{
		File.WriteAllText( path, Write( mesh, skeleton, smoothingAngleDegrees, materialName ) );
	}

	/// <summary>
	/// Write the mesh as SMD. With no skeleton — or a mesh carrying no weights — everything is
	/// bound to a single root bone, which is what a static prop wants.
	/// </summary>
	public static string Write(
		PolyMesh mesh,
		Skeleton skeleton = null,
		float smoothingAngleDegrees = MeshNormals.DefaultSmoothingAngleDegrees,
		Func<int, string> materialName = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		skeleton ??= Skeleton.SingleRoot();

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "SMD needs at least one bone; use Skeleton.SingleRoot for a static model" );

		var skin = mesh.IsRigged ? mesh.Skin : SkinWeights.AllTo( mesh.VertexCount, 0 );
		materialName ??= slot => $"material_{slot}";

		var c = CultureInfo.InvariantCulture;
		var sb = new StringBuilder();

		sb.Append( "version 1\n" );

		// --- nodes: the bone hierarchy ---------------------------------------------------
		sb.Append( "nodes\n" );

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var bone = skeleton.Bones[i];
			sb.Append( string.Format( c, "{0} \"{1}\" {2}\n", i, Escape( bone.Name ), bone.Parent ) );
		}

		sb.Append( "end\n" );

		// --- skeleton: one keyframe, the bind pose ---------------------------------------
		//
		// Positions and rotations here are LOCAL to the parent, which is what Bone.Local already
		// holds — no conversion, and no chance of writing world transforms that look right for
		// roots and wrong for everything below them.
		sb.Append( "skeleton\n" );
		sb.Append( "time 0\n" );

		for ( var i = 0; i < skeleton.Count; i++ )
		{
			var local = skeleton.Bones[i].Local;
			var t = local.Origin;
			var r = local.ToEulerXyz();

			sb.Append( string.Format( c,
				"{0} {1:0.######} {2:0.######} {3:0.######} {4:0.######} {5:0.######} {6:0.######}\n",
				i, t.x, t.y, t.z, r.x, r.y, r.z ) );
		}

		sb.Append( "end\n" );

		// --- triangles -------------------------------------------------------------------
		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, smoothingAngleDegrees );

		sb.Append( "triangles\n" );

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			var material = materialName( face.Material );

			// Ear clipping, same as the render mesh and the raycaster - an extrude cap is whatever
			// region the user drew, and a fan over a concave one exports the notch filled in.
			var polygon = new List<Vec3>( face.Count );

			for ( var k = 0; k < face.Count; k++ )
				polygon.Add( mesh.Positions[face.Indices[k]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( polygon ) )
			{
				sb.Append( material );
				sb.Append( '\n' );

				AppendVertex( sb, c, mesh, face, cornerNormals[fi], normals, skin, skeleton, ia );
				AppendVertex( sb, c, mesh, face, cornerNormals[fi], normals, skin, skeleton, ib );
				AppendVertex( sb, c, mesh, face, cornerNormals[fi], normals, skin, skeleton, ic );
			}
		}

		sb.Append( "end\n" );

		return sb.ToString();
	}

	static void AppendVertex(
		StringBuilder sb,
		CultureInfo c,
		PolyMesh mesh,
		Face face,
		int[] faceCornerNormals,
		List<Vec3> normals,
		SkinWeights skin,
		Skeleton skeleton,
		int corner )
	{
		var vi = face.Indices[corner];
		var p = mesh.Positions[vi];
		var n = normals[faceCornerNormals[corner]];
		var uv = face.UVs[corner];

		var weights = vi < skin.Count ? SkinWeights.Prune( skin[vi], MaxInfluences ) : Array.Empty<BoneWeight>();

		// An unweighted vertex is pinned to the first bone rather than written with zero links.
		// Studiomdl reads a zero-link vertex as "use the parent bone column", and silently ending up
		// with a differently-rigged model is worse than being obviously blunt about it.
		if ( weights.Length == 0 )
			weights = new[] { new BoneWeight( 0, 1f ) };

		// The leading bone index is the vertex's parent bone. Convention is the strongest influence,
		// and Prune has already sorted them heaviest first.
		var parentBone = weights[0].Bone;

		sb.Append( string.Format( c,
			"{0} {1:0.######} {2:0.######} {3:0.######} {4:0.######} {5:0.######} {6:0.######} {7:0.######} {8:0.######} {9}",
			parentBone, p.x, p.y, p.z, n.x, n.y, n.z, uv.x, uv.y, weights.Length ) );

		foreach ( var w in weights )
		{
			if ( w.Bone < 0 || w.Bone >= skeleton.Count )
				throw new InvalidOperationException(
					$"Vertex {vi} is weighted to bone {w.Bone}, but the skeleton has {skeleton.Count} bones" );

			sb.Append( string.Format( c, " {0} {1:0.######}", w.Bone, w.Weight ) );
		}

		sb.Append( '\n' );
	}

	/// <summary>Bone names are written inside quotes, so a quote in a name would end the field
	/// early. Nothing in this kernel generates one, but a user typing a name can.</summary>
	static string Escape( string name ) => name.Replace( "\"", "'" );
}

/// <summary>
/// Minimal SMD reader, for round-tripping in tests. Understands only what SmdWriter emits — enough
/// to prove the bone hierarchy, the bind pose and the weights survive the trip, which is the part
/// that would otherwise only be discovered inside ModelDoc.
/// </summary>
public static class SmdReader
{
	public sealed class Result
	{
		public Skeleton Skeleton = new();
		public List<Vec3> BonePositions = new();
		public List<Vec3> BoneRotations = new();
		public int TriangleCount;
		public List<string> Materials = new();

		/// <summary>Per triangle corner, in file order: position, normal, uv and its influences.</summary>
		public List<(Vec3 Position, Vec3 Normal, Vec2 UV, BoneWeight[] Weights)> Corners = new();
	}

	public static Result Read( string text )
	{
		var r = new Result();
		var c = CultureInfo.InvariantCulture;
		var lines = text.Split( '\n' );
		var mode = "";
		var parents = new List<int>();
		var names = new List<string>();

		foreach ( var raw in lines )
		{
			var line = raw.Trim();

			if ( line.Length == 0 || line.StartsWith( "version" ) )
				continue;

			if ( line is "nodes" or "skeleton" or "triangles" )
			{
				mode = line;
				continue;
			}

			if ( line == "end" )
			{
				mode = "";
				continue;
			}

			if ( mode == "nodes" )
			{
				var open = line.IndexOf( '"' );
				var close = line.LastIndexOf( '"' );
				names.Add( line.Substring( open + 1, close - open - 1 ) );
				parents.Add( int.Parse( line[(close + 1)..].Trim(), c ) );
				continue;
			}

			if ( mode == "skeleton" )
			{
				if ( line.StartsWith( "time" ) )
					continue;

				var p = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
				r.BonePositions.Add( new Vec3( float.Parse( p[1], c ), float.Parse( p[2], c ), float.Parse( p[3], c ) ) );
				r.BoneRotations.Add( new Vec3( float.Parse( p[4], c ), float.Parse( p[5], c ), float.Parse( p[6], c ) ) );
				continue;
			}

			if ( mode != "triangles" )
				continue;

			var parts = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

			// A material line is the only line in this block that does not start with a number.
			if ( !int.TryParse( parts[0], NumberStyles.Integer, c, out _ ) )
			{
				r.Materials.Add( line );
				r.TriangleCount++;
				continue;
			}

			var position = new Vec3( float.Parse( parts[1], c ), float.Parse( parts[2], c ), float.Parse( parts[3], c ) );
			var normal = new Vec3( float.Parse( parts[4], c ), float.Parse( parts[5], c ), float.Parse( parts[6], c ) );
			var uv = new Vec2( float.Parse( parts[7], c ), float.Parse( parts[8], c ) );

			var links = parts.Length > 9 ? int.Parse( parts[9], c ) : 0;
			var weights = new BoneWeight[links];

			for ( var i = 0; i < links; i++ )
			{
				weights[i] = new BoneWeight(
					int.Parse( parts[10 + i * 2], c ),
					float.Parse( parts[11 + i * 2], c ) );
			}

			r.Corners.Add( (position, normal, uv, weights) );
		}

		for ( var i = 0; i < names.Count; i++ )
		{
			var local = Xform.FromEulerXyz( r.BoneRotations[i] );
			r.Skeleton.AddBone( names[i], parents[i],
				new Xform( local.X, local.Y, local.Z, r.BonePositions[i] ) );
		}

		return r;
	}
}
