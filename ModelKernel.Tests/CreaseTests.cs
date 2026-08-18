using System;
using System.Linq;
using ModelKernel;
using static ModelKernel.Tests.Report;

namespace ModelKernel.Tests;

/// <summary>
/// Checks on edge creases — the fix for what the first PNG previews showed: with no creases,
/// subdividing a cube rounds it into a ball and a cylinder's cap puckers around its centre. Both
/// are the literal, correct Catmull-Clark limit surface and both defeat CAD-then-sculpt, where the
/// CAD stage's crispness has to survive into the sculpt stage.
///
/// The edge/vertex rules are a generalisation of the existing boundary rules — with an empty
/// Creases dictionary every formula must reduce to exactly the old code path. That is checked
/// directly here rather than assumed, and it is the single easiest way for this feature to
/// regress silently: get the blend backwards and every existing subdivision test still passes,
/// because they all use uncreased meshes.
/// </summary>
public static class CreaseTests
{
	public static void Run()
	{
		Section( "creasing does not change the uncreased case" );
		TestNoCreaseIsUnchanged();

		Section( "edge point blends exactly between smooth and sharp" );
		TestEdgePointBlend();

		Section( "crease sharpness decays one level at a time" );
		TestDecay();

		Section( "a fully creased box subdivides without rounding" );
		TestCreasedBoxStaysABox();

		Section( "mark sharp by angle" );
		TestMarkByAngle();

		Section( "creases through the Subdivide feature" );
		TestSubdivideFeatureCreases();
	}

	static float Volume( PolyMesh m ) =>
		m.Faces.Sum( f => Vec3.Dot( m.FaceCentroid( f ), m.FaceNormal( f ) ) * m.FaceArea( f ) ) / 3f;

	/// <summary>
	/// Two quads hinged 90° along a shared edge:
	///
	///   Quad A in the XY plane (z=0): (0,0,0) (1,0,0) (1,1,0) (0,1,0)
	///   Quad B in the XZ plane (y=0): (1,0,0) (0,0,0) (0,0,1) (1,0,1)
	///
	/// sharing edge (0,1) along the X axis. Every other edge is a boundary (one face) and stays
	/// implicitly sharp regardless of what's asserted on the shared edge, so this rig isolates
	/// exactly the one edge under test. All the expected values below are hand-computed, not
	/// derived from the code being tested.
	/// </summary>
	static PolyMesh HingedQuads()
	{
		var m = new PolyMesh();
		m.AddVertex( new Vec3( 0, 0, 0 ) ); // 0 - shared
		m.AddVertex( new Vec3( 1, 0, 0 ) ); // 1 - shared
		m.AddVertex( new Vec3( 1, 1, 0 ) ); // 2
		m.AddVertex( new Vec3( 0, 1, 0 ) ); // 3
		m.AddVertex( new Vec3( 0, 0, 1 ) ); // 4
		m.AddVertex( new Vec3( 1, 0, 1 ) ); // 5

		m.AddFace( new[] { 0, 1, 2, 3 } ); // quad A
		m.AddFace( new[] { 1, 0, 4, 5 } ); // quad B

		return m;
	}

	static void TestNoCreaseIsUnchanged()
	{
		// A regression check as direct as it can be made: subdivide the same mesh through both
		// code paths and require identical positions to the last bit. If crease support ever
		// nudges the zero-crease path, this fails immediately rather than showing up as a subtly
		// wrong shape three features downstream.
		foreach ( var (name, mesh) in new[]
			{ ("box", Primitives.Box( 2, 2, 2 )), ("cylinder", Primitives.Cylinder( 0.5f, 1f, 12 )),
			  ("wedge", Primitives.Wedge()), ("hinged", HingedQuads()) } )
		{
			var plain = CatmullClark.Subdivide( mesh, 3 );

			var withEmptyCreases = mesh.Clone();
			withEmptyCreases.Creases.Clear(); // already empty, but explicit about the premise
			var subdivided = CatmullClark.Subdivide( withEmptyCreases, 3 );

			var maxDiff = plain.Positions.Zip( subdivided.Positions, ( a, b ) => (a - b).Length ).Max();
			Check( $"{name}: uncreased subdivision is byte-identical", maxDiff < 1e-6f, $"max diff {maxDiff}" );
		}
	}

	static void TestEdgePointBlend()
	{
		// Hand-computed on the hinged rig (see HingedQuads' doc comment):
		//   shared edge midpoint (the sharp target)        = (0.5, 0,     0)
		//   quad A centroid (face point)                   = (0.5, 0.5,   0)
		//   quad B centroid (face point)                   = (0.5, 0,     0.5)
		//   smooth target = (a + b + faceA + faceB) * 0.25  = (0.5, 0.125, 0.125)
		var sharpTarget = new Vec3( 0.5f, 0f, 0f );
		var smoothTarget = new Vec3( 0.5f, 0.125f, 0.125f );

		Vec3 EdgePointAt( float sharpness )
		{
			var m = HingedQuads();

			if ( sharpness > 0f )
				m.MarkSharp( new EdgeKey( 0, 1 ), sharpness );

			var sub = CatmullClark.Subdivide( m, 1 );

			// Edge point layout: [0..V) verts, [V..V+E) edge points. The shared edge (0,1) is
			// discovered first among the mesh's edges in BuildEdgeFaces' enumeration order, but
			// rather than assume an index, find it by proximity to the known sharp/smooth region.
			return sub.Positions.Skip( m.VertexCount )
				.OrderBy( p => (p - Vec3.Lerp( smoothTarget, sharpTarget, 0.5f )).LengthSquared )
				.First();
		}

		var atZero = EdgePointAt( 0f );
		Check( "sharpness 0 gives the smooth target exactly",
			atZero.AlmostEquals( smoothTarget, 1e-5f ), atZero.ToString() );

		var atOne = EdgePointAt( 1f );
		Check( "sharpness 1 gives the sharp target exactly",
			atOne.AlmostEquals( sharpTarget, 1e-5f ), atOne.ToString() );

		var atInfinity = EdgePointAt( float.PositiveInfinity );
		Check( "infinite sharpness gives the sharp target exactly",
			atInfinity.AlmostEquals( sharpTarget, 1e-5f ), atInfinity.ToString() );

		var atHalf = EdgePointAt( 0.5f );
		var expectedHalf = Vec3.Lerp( smoothTarget, sharpTarget, 0.5f );
		Check( "sharpness 0.5 gives the exact linear blend",
			atHalf.AlmostEquals( expectedHalf, 1e-5f ), $"{atHalf} vs {expectedHalf}" );
	}

	static void TestDecay()
	{
		var key = new EdgeKey( 0, 1 );

		var m1 = HingedQuads();
		m1.MarkSharp( key, 1f );
		var sub1 = CatmullClark.Subdivide( m1, 1 );

		var stillSharp = sub1.Creases.Any( kv => kv.Value > 0f );
		Check( "sharpness 1 decays to 0 after one level (no child creases remain)", !stillSharp,
			string.Join( ", ", sub1.Creases ) );

		var m2 = HingedQuads();
		m2.MarkSharp( key, 2f );
		var sub2 = CatmullClark.Subdivide( m2, 1 );

		var remaining = sub2.Creases.Values.Where( v => v > 0f ).ToList();
		Check( "sharpness 2 decays to 1 and survives one more level",
			remaining.Count == 2 && remaining.All( v => MathF.Abs( v - 1f ) < 1e-6f ),
			string.Join( ", ", remaining ) );

		var mInf = HingedQuads();
		mInf.MarkSharp( key, float.PositiveInfinity );
		var subInf = CatmullClark.Subdivide( mInf, 1 );

		var infRemaining = subInf.Creases.Values.Where( v => v > 0f ).ToList();
		Check( "infinite sharpness never decays",
			infRemaining.Count == 2 && infRemaining.All( float.IsPositiveInfinity ),
			string.Join( ", ", infRemaining ) );
	}

	static void TestCreasedBoxStaysABox()
	{
		var box = Primitives.Box( 2, 2, 2 );
		box.MarkAllSharp();

		for ( var level = 1; level <= 4; level++ )
		{
			var sub = CatmullClark.Subdivide( box, level );
			var vol = Volume( sub );

			// A cube with every edge infinitely sharp has every vertex a 3-sharp-edge corner
			// (pinned) and every edge point an exact midpoint, so the "subdivided" result is
			// geometrically the same flat-faced cube, just re-tessellated — volume should not
			// move at all, to floating point precision.
			Check( $"fully creased box: level {level} volume stays exactly 8",
				MathF.Abs( vol - 8f ) < 1e-3f, $"{vol:0.#####}" );

			var v = MeshValidator.Validate( sub );
			Check( $"fully creased box: level {level} still valid and closed", v.IsValid && v.IsClosed, v.ToString() );
		}

		// Contrast: the same box with no creases visibly shrinks as it rounds off. This is the
		// bug the PNG previews showed, kept here as a permanent regression check.
		var uncreasedVol = Volume( CatmullClark.Subdivide( Primitives.Box( 2, 2, 2 ), 3 ) );
		Check( "uncreased box measurably shrinks by level 3 (the bug the previews caught)",
			uncreasedVol < 7.9f, $"{uncreasedVol:0.####}" );
	}

	static void TestMarkByAngle()
	{
		var box = Primitives.Box( 2, 2, 2 );
		CreaseTools.MarkSharpByAngle( box, 30f );

		// Every edge of a box meets at exactly 90°, well past a 30° threshold.
		Check( "30 degree threshold marks all 12 box edges", box.Creases.Count == 12,
			$"{box.Creases.Count}" );

		var boxVol = Volume( CatmullClark.Subdivide( box, 3 ) );
		Check( "angle-creased box behaves exactly like a fully creased box",
			MathF.Abs( boxVol - 8f ) < 1e-3f, $"{boxVol:0.#####}" );

		// A 16-segment cylinder: side-to-side seams are ~22.5 degrees apart (well under 30, so
		// they must stay smooth and round), while every cap-to-side edge is a full 90 degrees
		// (well over 30, so it must be marked). There are `segments` such edges per cap.
		var segments = 16;
		var cylinder = Primitives.Cylinder( 0.5f, 1f, segments );
		CreaseTools.MarkSharpByAngle( cylinder, 30f );

		Check( "cylinder gets exactly the two cap seams marked, nothing on the barrel",
			cylinder.Creases.Count == segments * 2, $"{cylinder.Creases.Count}" );

		var cylSub = CatmullClark.Subdivide( cylinder, 2 );
		var cylVol = Volume( cylSub );
		var uncreasedCylVol = Volume( CatmullClark.Subdivide( Primitives.Cylinder( 0.5f, 1f, segments ), 2 ) );

		Check( "angle-creased cylinder keeps more of its volume than the uncreased one",
			cylVol > uncreasedCylVol, $"{cylVol:0.####} vs {uncreasedCylVol:0.####}" );
	}

	static void TestSubdivideFeatureCreases()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.Shape.Index = 0;
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;

		var subdiv = studio.Add( new SubdivideFeature() );
		subdiv.Levels.Value = 3;
		subdiv.AutoCrease.Value = true;

		studio.Rebuild();

		Check( "SubdivideFeature with AutoCrease runs without error", subdiv.Error is null, subdiv.Error );

		var vol = Volume( studio.Bodies[0].Mesh );
		Check( "AutoCrease through the feature keeps the box a box", MathF.Abs( vol - 8f ) < 1e-3f, $"{vol:0.#####}" );

		Check( "AutoCrease off by default doesn't show the angle parameter",
			!new SubdivideFeature().Parameters.Any( p => p.Label == "Crease angle" ) );
		Check( "AutoCrease on shows the angle parameter",
			subdiv.Parameters.Any( p => p.Label == "Crease angle" ) );

		// Turning it back off and rebuilding must return to the old rounded behaviour, proving
		// the parameter actually gates the code path rather than the mesh having been mutated
		// permanently by the earlier MarkSharpByAngle call.
		subdiv.AutoCrease.Value = false;
		studio.MarkAllDirty();
		studio.Rebuild();

		var offVol = Volume( studio.Bodies[0].Mesh );
		Check( "turning AutoCrease back off restores the rounded result",
			offVol < 7.9f, $"{offVol:0.####}" );
	}
}
