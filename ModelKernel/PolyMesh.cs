using System;
using System.Collections.Generic;
using System.Linq;

namespace ModelKernel;

/// <summary>
/// An undirected edge, identified by its two endpoints regardless of which way round they were
/// given. Two faces sharing an edge traverse it in opposite directions, so the key has to ignore
/// direction or every shared edge looks like two different edges.
/// </summary>
public readonly struct EdgeKey : IEquatable<EdgeKey>
{
	public readonly int A, B;

	public EdgeKey( int a, int b )
	{
		// Sorted, so (3,7) and (7,3) are the same key.
		A = Math.Min( a, b );
		B = Math.Max( a, b );
	}

	public bool Equals( EdgeKey o ) => A == o.A && B == o.B;
	public override bool Equals( object obj ) => obj is EdgeKey e && Equals( e );
	public override int GetHashCode() => HashCode.Combine( A, B );
	public override string ToString() => $"[{A}-{B}]";
}

/// <summary>
/// One n-gon. Indices reference PolyMesh.Positions; UVs are stored PER CORNER rather than per
/// vertex.
///
/// Per-corner UVs are what make seams possible: a cube corner belongs to three faces which each
/// want a different UV for the same position. Storing UVs on the vertex would force one value and
/// smear the texture across the seam. It also makes UV subdivision purely local — see
/// CatmullClark.
/// </summary>
public sealed class Face
{
	public int[] Indices;
	public Vec2[] UVs;

	/// <summary>Material slot. Export groups faces by this, so a model can carry several materials
	/// without the kernel knowing what a material is.</summary>
	public int Material;

	public Face( int[] indices, Vec2[] uvs = null, int material = 0 )
	{
		Indices = indices;
		UVs = uvs ?? new Vec2[indices.Length];
		Material = material;

		if ( UVs.Length != Indices.Length )
			throw new ArgumentException( $"Face has {Indices.Length} corners but {UVs.Length} UVs" );
	}

	public int Count => Indices.Length;
}

/// <summary>
/// A polygon mesh of n-gons. Not a half-edge structure — adjacency is derived on demand instead.
///
/// WHY NOT HALF-EDGE: Catmull-Clark needs edge→faces and vertex→(faces, edges), and nothing else
/// in phase one needs more than that. Deriving those three maps from an indexed face list is a
/// few dozen lines and is hard to get subtly wrong; a half-edge structure is a few hundred and is
/// easy to corrupt during editing. If interactive per-element editing arrives later and profiling
/// says the rebuild hurts, that is the moment to switch, not before.
///
/// QUADS ARE THE POINT. Catmull-Clark degrades badly on triangle soup, so every primitive here is
/// built quad-dominant on purpose. See Primitives.
/// </summary>
public sealed class PolyMesh
{
	public List<Vec3> Positions = new();
	public List<Face> Faces = new();

	/// <summary>
	/// Per-edge crease sharpness. Absent means 0, which is fully smooth.
	///
	/// WITHOUT THESE, SUBDIVISION MELTS EVERY HARD EDGE. Catmull-Clark's limit surface for a cube
	/// is a rounded blob — mathematically correct and useless for CAD, because the crispness the
	/// parametric stage produced has to survive into the sculpt stage. A sharpness of 1 keeps an
	/// edge for one level, 2 for two, and infinity keeps it forever. Fractional values blend, which
	/// is what gives the tight-but-not-razor edge real hard-surface models want.
	///
	/// Boundary edges are treated as infinitely sharp automatically and are not stored here.
	/// </summary>
	public Dictionary<EdgeKey, float> Creases = new();

	public PolyMesh() { }

	public PolyMesh( IEnumerable<Vec3> positions, IEnumerable<Face> faces )
	{
		Positions = positions.ToList();
		Faces = faces.ToList();
	}

	/// <summary>Crease an edge. Sharpness &lt;= 0 removes any existing crease rather than storing
	/// a no-op zero, so the dictionary only ever holds edges that actually do something.</summary>
	public void MarkSharp( EdgeKey key, float sharpness = float.PositiveInfinity )
	{
		if ( sharpness <= 0f )
			Creases.Remove( key );
		else
			Creases[key] = sharpness;
	}

	public void MarkAllSharp( float sharpness = float.PositiveInfinity )
	{
		foreach ( var key in BuildEdgeFaces().Keys )
			MarkSharp( key, sharpness );
	}

	public int VertexCount => Positions.Count;
	public int FaceCount => Faces.Count;

	public int AddVertex( Vec3 p )
	{
		Positions.Add( p );
		return Positions.Count - 1;
	}

	public Face AddFace( int[] indices, Vec2[] uvs = null, int material = 0 )
	{
		var f = new Face( indices, uvs, material );
		Faces.Add( f );
		return f;
	}

	/// <summary>Every distinct undirected edge, with the faces using it. A closed manifold has
	/// exactly two faces per edge; a boundary edge has one; anything else is non-manifold.</summary>
	public Dictionary<EdgeKey, List<int>> BuildEdgeFaces()
	{
		var map = new Dictionary<EdgeKey, List<int>>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			var f = Faces[fi];

			for ( var i = 0; i < f.Count; i++ )
			{
				var key = new EdgeKey( f.Indices[i], f.Indices[(i + 1) % f.Count] );

				if ( !map.TryGetValue( key, out var list ) )
					map[key] = list = new List<int>();

				list.Add( fi );
			}
		}

		return map;
	}

	/// <summary>Faces touching each vertex, indexed by vertex.</summary>
	public List<int>[] BuildVertexFaces()
	{
		var map = new List<int>[Positions.Count];

		for ( var i = 0; i < map.Length; i++ )
			map[i] = new List<int>();

		for ( var fi = 0; fi < Faces.Count; fi++ )
		{
			// Distinct guards against a malformed face listing the same vertex twice, which would
			// otherwise inflate the valence and skew the Catmull-Clark vertex rule.
			foreach ( var vi in Faces[fi].Indices.Distinct() )
				map[vi].Add( fi );
		}

		return map;
	}

	/// <summary>Edges touching each vertex, indexed by vertex.</summary>
	public List<EdgeKey>[] BuildVertexEdges()
	{
		var map = new List<EdgeKey>[Positions.Count];

		for ( var i = 0; i < map.Length; i++ )
			map[i] = new List<EdgeKey>();

		var seen = new HashSet<(int, EdgeKey)>();

		foreach ( var f in Faces )
		{
			for ( var i = 0; i < f.Count; i++ )
			{
				var a = f.Indices[i];
				var b = f.Indices[(i + 1) % f.Count];
				var key = new EdgeKey( a, b );

				if ( seen.Add( (a, key) ) ) map[a].Add( key );
				if ( seen.Add( (b, key) ) ) map[b].Add( key );
			}
		}

		return map;
	}

	/// <summary>Centroid of a face's corners.</summary>
	public Vec3 FaceCentroid( Face f )
	{
		var sum = Vec3.Zero;

		foreach ( var i in f.Indices )
			sum += Positions[i];

		return sum / f.Count;
	}

	/// <summary>Newell's method, which is correct for non-planar n-gons where a simple
	/// cross-product of the first three corners is not.</summary>
	public Vec3 FaceNormal( Face f )
	{
		var n = Vec3.Zero;

		for ( var i = 0; i < f.Count; i++ )
		{
			var a = Positions[f.Indices[i]];
			var b = Positions[f.Indices[(i + 1) % f.Count]];

			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}

		return n.Normal;
	}

	/// <summary>Area-weighted vertex normals. Area weighting rather than a plain average, so a
	/// vertex surrounded by one big face and several slivers leans toward the big one.</summary>
	public Vec3[] ComputeVertexNormals()
	{
		var normals = new Vec3[Positions.Count];

		foreach ( var f in Faces )
		{
			var n = FaceNormal( f );
			var weight = FaceArea( f );

			foreach ( var i in f.Indices )
				normals[i] += n * weight;
		}

		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].Normal;

		return normals;
	}

	/// <summary>Fan triangulation about the centroid, which is valid for the convex and mildly
	/// non-planar faces this kernel produces.</summary>
	public float FaceArea( Face f )
	{
		var c = FaceCentroid( f );
		var area = 0f;

		for ( var i = 0; i < f.Count; i++ )
		{
			var a = Positions[f.Indices[i]] - c;
			var b = Positions[f.Indices[(i + 1) % f.Count]] - c;
			area += Vec3.Cross( a, b ).Length * 0.5f;
		}

		return area;
	}

	public PolyMesh Clone()
	{
		var m = new PolyMesh
		{
			Positions = new List<Vec3>( Positions ),
			Creases = new Dictionary<EdgeKey, float>( Creases )
		};

		foreach ( var f in Faces )
			m.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );

		return m;
	}
}

/// <summary>What Validate found. Kept as data rather than thrown, because a mesh mid-edit is
/// allowed to be briefly invalid and the caller decides whether it matters.</summary>
public sealed class MeshValidation
{
	public List<string> Errors = new();
	public int BoundaryEdges;
	public int NonManifoldEdges;
	public bool IsClosed => BoundaryEdges == 0 && NonManifoldEdges == 0;
	public bool IsValid => Errors.Count == 0;

	public override string ToString() =>
		IsValid
			? $"valid, {(IsClosed ? "closed" : $"{BoundaryEdges} boundary edges")}"
			: string.Join( "; ", Errors );
}

public static class MeshValidator
{
	public static MeshValidation Validate( PolyMesh mesh )
	{
		var r = new MeshValidation();

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];

			if ( f.Count < 3 )
				r.Errors.Add( $"face {fi} has {f.Count} corners" );

			if ( f.UVs.Length != f.Count )
				r.Errors.Add( $"face {fi} has {f.Count} corners but {f.UVs.Length} UVs" );

			foreach ( var i in f.Indices )
			{
				if ( i < 0 || i >= mesh.Positions.Count )
					r.Errors.Add( $"face {fi} references vertex {i}, out of range" );
			}

			if ( f.Indices.Distinct().Count() != f.Count )
				r.Errors.Add( $"face {fi} repeats a vertex" );
		}

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count == 1 ) r.BoundaryEdges++;
			else if ( faces.Count > 2 )
			{
				r.NonManifoldEdges++;
				r.Errors.Add( $"edge {key} is shared by {faces.Count} faces" );
			}
		}

		return r;
	}

	/// <summary>V - E + F. Equals 2 for a closed genus-0 surface, and is the cheapest single check
	/// that a subdivision pass did not quietly corrupt the topology.</summary>
	public static int EulerCharacteristic( PolyMesh mesh ) =>
		mesh.VertexCount - mesh.BuildEdgeFaces().Count + mesh.FaceCount;
}
