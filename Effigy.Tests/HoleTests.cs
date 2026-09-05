using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Extruding profiles with holes in them — a plate with bolt holes, a washer, a bracket.
///
/// This was refused for a long time on the reasoning that capping around a hole was "really the
/// same problem as a boolean subtract, and better solved once, there". That reasoning was wrong, and
/// wrong in a way worth remembering: capping is a 2D triangulation problem and never needed CSG at
/// all. Ear clipping had been sitting in the kernel for a while by the time anyone noticed.
///
/// The tests lean on two things a filled-in cap cannot fake. VOLUME, because a cap over the hole
/// adds exactly the hole's area times the height. And EULER CHARACTERISTIC, because a plate with n
/// holes is genus n and reads X = 2 - 2n, which nothing but a genuinely open hole produces — a small
/// enough hole could hide inside a volume tolerance, and it cannot hide from a vertex count.
/// </summary>
public static class HoleTests
{
	public static void Run()
	{
		Report.Section( "holes: a plate with four bolt holes" );
		TestBoltHoles();

		Report.Section( "holes: the walls face into the hole" );
		TestHoleWalls();

		Report.Section( "holes: awkward outers and awkward holes" );
		TestAwkward();

		Report.Section( "holes: a loop inside a hole is an island, not a hole" );
		TestIsland();

		Report.Section( "holes: profiles without them are untouched" );
		TestNoRegression();

		Report.Section( "holes: a BRIDGED loop, which is how the engine's boolean returns one" );
		TestBridgedLoopKeepsItsHole();

		Report.Section( "holes: an opening the boolean left as a bare boundary loop" );
		TestBoundaryLoopRepair();

		Report.Section( "holes: a bridged loop splits into TWO faces rather than a pile of triangles" );
		TestBridgedLoopSplitsIntoTwo();

		Report.Section( "holes: a loop the engine bridged twice, for a face with two pockets" );
		TestTwoBridgesRecovered();

		Report.Section( "holes: the splitter refuses what it cannot be sure of" );
		TestSplitRefusals();

		Report.Section( "holes: a SKETCH profile's cap splits the same way a cut face does" );
		TestSketchCapSplits();

		Report.Section( "holes: a mouth landing across two faces is closed, not abandoned" );
		TestMouthSpanningTwoFaces();
		TestSpanRepairRefusesWhatItCannotBeSureOf();
	}

	/// <summary>
	/// A lid made of TWO coplanar quads, with the tunnel's mouth straddling the edge between them.
	///
	/// This is the case the single-face repair declines: FindContainingFace
	/// wants one face that contains the whole loop, and here neither quad does. It was right to
	/// decline - a guess seals a surface the wrong way - so the answer is to split the loop at the
	/// crossing rather than to loosen the test.
	///
	/// MEASURED BY BOUNDARY EDGES AND ENCLOSED VOLUME, never by eye. Every bug fixed in this boolean
	/// produced a mesh that was closed, manifold, Euler-correct and wrong.
	/// </summary>
	static void TestMouthSpanningTwoFaces()
	{
		var mesh = SpanFixture( out var expectedVolume );

		var before = MeshValidator.Validate( mesh );

		Report.Check( "the fixture starts with the mouth open", before.BoundaryEdges == 12,
			$"{before.BoundaryEdges} boundary edges" );

		var closed = MeshHoleRepairSpan.CloseLoopsSpanningFaces( mesh );

		Report.Check( "the span repair closes it", closed == 1, $"closed {closed}" );

		// And it is reachable the ordinary way. CloseBoundaryLoopsIntoFaces runs the single-face pass
		// first and hands whatever it declined to the span pass, so a caller never has to know which
		// shape of mouth it has - which is the point of chaining them rather than exposing both.
		var throughTheFrontDoor = SpanFixture( out _ );

		Report.Check( "and the ordinary repair reaches it without being asked specially",
			MeshHoleRepair.CloseBoundaryLoopsIntoFaces( throughTheFrontDoor ) == 1
			&& MeshValidator.Validate( throughTheFrontDoor ).BoundaryEdges == 0,
			$"{MeshValidator.Validate( throughTheFrontDoor ).BoundaryEdges} boundary edges left" );

		var after = MeshValidator.Validate( mesh );

		Report.Check( "no boundary edges are left", after.BoundaryEdges == 0,
			$"{after.BoundaryEdges} left" );
		Report.Check( "and nothing was made non-manifold doing it", after.NonManifoldEdges == 0,
			$"{after.NonManifoldEdges} non-manifold" );
		Report.Check( "the mesh is valid", after.IsValid, after.ToString() );

		// THE CHECK THAT CANNOT BE FAKED. A repair that sealed the mouth over instead of around it
		// would also report zero boundary edges - and would enclose the tunnel's volume as solid.
		var volume = MathF.Abs( mesh.SignedVolume() );

		Report.Check( "and it encloses the volume of a block with a hole through it",
			MathF.Abs( volume - expectedVolume ) < 0.05f,
			$"{volume:0.####}, expected about {expectedVolume:0.####}" );

		// The lid is still two faces, each notched - not two faces plus a patch, and not a fan.
		// Counted at z = 0 specifically: the pocket's floor also faces up, and counting every upward
		// face called a correct repair wrong.
		var lidFaces = mesh.Faces.Count( f =>
			MathF.Abs( mesh.FaceNormal( f ).Normal.z - 1f ) < 1e-3f
			&& MathF.Abs( mesh.FaceCentroid( f ).z ) < 1e-3f );

		Report.Check( "the lid is still two faces, each with the notch spliced in", lidFaces == 2,
			$"{lidFaces} faces at the lid" );

		// THE CHECK A BOW-TIE CANNOT PASS, and the reason it is here: splicing the arc into the face
		// the wrong way round produces a polygon that crosses itself. It keeps its vertex count, it
		// keeps a boundary edge count of zero, and Newell still calls its normal +Z - so every other
		// check in this test waves it through. Its AREA does not survive: a notched half-lid is its
		// 4x2 quad less half the bore, and a bow-tie is neither.
		var lidArea = 0f;

		foreach ( var face in mesh.Faces )
		{
			if ( MathF.Abs( mesh.FaceNormal( face ).Normal.z - 1f ) < 1e-3f
				&& MathF.Abs( mesh.FaceCentroid( face ).z ) < 1e-3f )
			{
				lidArea += mesh.FaceArea( face );
			}
		}

		// 16 for the lid, less the 12-gon bore's 3.0.
		Report.Check( "and together they cover the lid less the bore, so neither is folded over itself",
			MathF.Abs( lidArea - 13f ) < 0.02f, $"{lidArea:0.####}, expected 13" );
	}

	/// <summary>
	/// The refusals, which matter as much as the repair. A span repair that seals whatever it is
	/// handed is worse than one that declines, because the failure is invisible.
	/// </summary>
	static void TestSpanRepairRefusesWhatItCannotBeSureOf()
	{
		// A mouth entirely inside ONE face is the single-face case and must be left to it - closing
		// it here would notch a face that should have got a hole.
		var single = OneFaceFixture();

		Report.Check( "a mouth inside one face is left to the single-face repair",
			MeshHoleRepairSpan.CloseLoopsSpanningFaces( single ) == 0 );
		Report.Check( "which then closes it", MeshHoleRepair.CloseBoundaryLoopsIntoFaces( single ) == 1 );

		// A loop whose crossing points are NOT vertices cannot be spliced without inventing one, and
		// inventing one means splitting a face this was not asked to touch.
		var offset = SpanFixture( out _, rotate: 15f );

		Report.Check( "a mouth crossing between vertices is declined rather than guessed at",
			MeshHoleRepairSpan.CloseLoopsSpanningFaces( offset ) == 0,
			"it spliced a crossing it could not name" );
		Report.Check( "and the opening is still there to be seen",
			MeshValidator.Validate( offset ).BoundaryEdges > 0 );
	}

	/// <summary>
	/// A 4x4 lid split into two coplanar quads at x = 0, with a 12-sided tunnel through the middle
	/// whose mouth crosses that split at exactly (0, -1) and (0, 1).
	///
	/// Those two crossings ARE ring vertices, which is what makes the repair possible: the point
	/// where the mouth meets the edge already exists, so nothing has to be invented. `rotate` turns
	/// the ring so they no longer do, which is the case that must be declined.
	/// </summary>
	static PolyMesh SpanFixture( out float expectedVolume, float rotate = 0f )
	{
		const int segments = 12;
		const float radius = 1f;
		const float pocket = 1f;
		const float block = 2f;

		var mesh = new PolyMesh();

		// The lid at z = 0, as two coplanar quads meeting along x = 0. The split is the whole point:
		// the mouth below crosses it, so neither quad contains the loop and the single-face repair
		// declines.
		var a0 = mesh.AddVertex( new Vec3( -2, -2, 0 ) );
		var a1 = mesh.AddVertex( new Vec3( 0, -2, 0 ) );
		var b1 = mesh.AddVertex( new Vec3( 2, -2, 0 ) );
		var b2 = mesh.AddVertex( new Vec3( 2, 2, 0 ) );
		var a2 = mesh.AddVertex( new Vec3( 0, 2, 0 ) );
		var a3 = mesh.AddVertex( new Vec3( -2, 2, 0 ) );

		mesh.AddFace( new[] { a0, a1, a2, a3 } );
		mesh.AddFace( new[] { a1, b1, b2, a2 } );

		// The pocket. Its wall faces INWARD - the material is outside the bore, so the surface that
		// bounds it points into the void. Getting this backwards makes a solid plug rather than a
		// hole, and the volume check below is what says which one was built.
		var top = new int[segments];
		var bottom = new int[segments];

		for ( var i = 0; i < segments; i++ )
		{
			var angle = MathF.Tau * i / segments + rotate * MathF.PI / 180f;
			var x = MathF.Cos( angle ) * radius;
			var y = MathF.Sin( angle ) * radius;

			top[i] = mesh.AddVertex( new Vec3( x, y, 0 ) );
			bottom[i] = mesh.AddVertex( new Vec3( x, y, -pocket ) );
		}

		for ( var i = 0; i < segments; i++ )
		{
			var next = (i + 1) % segments;

			mesh.AddFace( new[] { top[i], top[next], bottom[next], bottom[i] } );
		}

		// The pocket's floor, facing up into the void for the same reason.
		mesh.AddFace( (int[])bottom.Clone() );

		// The block's own base, two units down so it is nowhere near the pocket floor - two coplanar
		// faces facing opposite ways is a fixture that tests the fixture.
		var e0 = mesh.AddVertex( new Vec3( -2, -2, -block ) );
		var e1 = mesh.AddVertex( new Vec3( 2, -2, -block ) );
		var e2 = mesh.AddVertex( new Vec3( 2, 2, -block ) );
		var e3 = mesh.AddVertex( new Vec3( -2, 2, -block ) );

		mesh.AddFace( new[] { e0, e3, e2, e1 } );

		// The four walls. The two that meet the split lid are FIVE-sided, because their top edge is
		// broken at x = 0 by the lid's own split - a quad there would leave the split's edges
		// unmatched, which is a boundary the repair never touched and would be blamed for.
		mesh.AddFace( new[] { a0, e0, e1, b1, a1 } );
		mesh.AddFace( new[] { a3, a2, b2, e2, e3 } );
		mesh.AddFace( new[] { a0, a3, e3, e0 } );
		mesh.AddFace( new[] { b1, e1, e2, b2 } );

		// A TWELVE-SIDED BORE, not a circle. Its area is (n/2) r^2 sin(2pi/n) = 3.0 exactly at n = 12,
		// against pi r^2 = 3.1416 - and using the circle would have called a correct mesh wrong by
		// 0.14, which is the sort of gap that gets "fixed" by loosening the tolerance.
		var boreArea = 0.5f * segments * radius * radius * MathF.Sin( MathF.Tau / segments );

		expectedVolume = 4f * 4f * block - boreArea * pocket;

		return mesh;
	}

	/// <summary>The same idea with ONE lid face, which the single-face repair already handles.</summary>
	static PolyMesh OneFaceFixture()
	{
		const int segments = 12;

		var mesh = new PolyMesh();

		mesh.AddFace( new[]
		{
			mesh.AddVertex( new Vec3( -2, -2, 0 ) ),
			mesh.AddVertex( new Vec3( 2, -2, 0 ) ),
			mesh.AddVertex( new Vec3( 2, 2, 0 ) ),
			mesh.AddVertex( new Vec3( -2, 2, 0 ) ),
		} );

		var top = new int[segments];
		var bottom = new int[segments];

		for ( var i = 0; i < segments; i++ )
		{
			var angle = MathF.Tau * i / segments;
			var x = MathF.Cos( angle );
			var y = MathF.Sin( angle );

			top[i] = mesh.AddVertex( new Vec3( x, y, 0 ) );
			bottom[i] = mesh.AddVertex( new Vec3( x, y, -1 ) );
		}

		for ( var i = 0; i < segments; i++ )
		{
			var next = (i + 1) % segments;

			mesh.AddFace( new[] { top[i], bottom[i], bottom[next], top[next] } );
		}

		var floor = new int[segments];

		for ( var i = 0; i < segments; i++ )
			floor[i] = bottom[segments - 1 - i];

		mesh.AddFace( floor );

		return mesh;
	}

	/// <summary>
	/// The other shape a cut arrives in, and the one that produced "the tunnel is there and the
	/// mouth is covered".
	///
	/// s&amp;box's boolean cuts correctly and cannot describe a face with a hole in it, so reading the
	/// entered face back gives its outer contour only. The opening survives as a ring of boundary
	/// edges no face closes, and the flat face sits over it looking solid. This builds exactly that
	/// defect - a square lid, a ring of walls hanging off an inner loop it does not share - and
	/// asserts the repair sews the two together.
	///
	/// BOUNDARY EDGE COUNT is the measure, because it is the thing that is actually wrong: the
	/// walls claim the mouth's edges once each and nothing claims them again.
	/// </summary>
	static void TestBoundaryLoopRepair()
	{
		const int segments = 12;
		const float radius = 1f;

		var mesh = new PolyMesh();

		// A 4x4 lid at z = 0, facing up, with no hole in it - the face the cut went through.
		var lid = new[]
		{
			mesh.AddVertex( new Vec3( -2, -2, 0 ) ),
			mesh.AddVertex( new Vec3( 2, -2, 0 ) ),
			mesh.AddVertex( new Vec3( 2, 2, 0 ) ),
			mesh.AddVertex( new Vec3( -2, 2, 0 ) ),
		};

		mesh.AddFace( lid );

		// The tunnel: a ring at z = 0 and the same ring at z = -1, walled between them and capped
		// at the bottom. Its top ring shares no vertex with the lid, which is the defect.
		var top = new int[segments];
		var bottom = new int[segments];

		for ( var i = 0; i < segments; i++ )
		{
			var a = MathF.Tau * i / segments;
			var x = MathF.Cos( a ) * radius;
			var y = MathF.Sin( a ) * radius;

			top[i] = mesh.AddVertex( new Vec3( x, y, 0 ) );
			bottom[i] = mesh.AddVertex( new Vec3( x, y, -1 ) );
		}

        for ( var i = 0; i < segments; i++ )
		{
			var j = (i + 1) % segments;
			mesh.AddFace( new[] { top[i], bottom[i], bottom[j], top[j] } );
		}

		mesh.AddFace( bottom );

		var before = MeshValidator.Validate( mesh );

		Report.Check( "the fixture really is open at the mouth", before.BoundaryEdges == segments + 4,
			$"{before.BoundaryEdges} boundary edges, expected {segments + 4}" );

		var closed = MeshHoleRepair.CloseBoundaryLoopsIntoFaces( mesh );

		Report.Check( "the repair closes exactly one loop", closed == 1, $"closed {closed}" );

		var after = MeshValidator.Validate( mesh );

		Report.Check( "the mesh stays valid", after.IsValid, after.ToString() );

		// The lid's own outer square is still open - this fixture is a lid and a tunnel, not a
		// solid - so 4 boundary edges are correct and the mouth's 12 are gone.
		Report.Check( "and only the outer square is still open", after.BoundaryEdges == 4,
			$"{after.BoundaryEdges} boundary edges, expected 4" );

		// The lid must still cover its own area minus the hole, or the repair sealed the hole shut
		// while removing the boundary - which would pass every count above.
		var area = 0f;

		foreach ( var face in mesh.Faces )
		{
			if ( MathF.Abs( mesh.FaceNormal( face ).z ) > 0.99f && MathF.Abs( mesh.FaceCentroid( face ).z ) < 1e-4f )
				area += mesh.FaceArea( face );
		}

		var expected = 16f - HoleArea( radius, segments );

		Report.Check( "the lid covers its area minus the opening", MathF.Abs( area - expected ) < 1e-3f,
			$"{area:0.####}, expected {expected:0.####}" );
	}

	/// <summary>Area of the tessellated opening, not of the true circle - the mesh only ever had
	/// the polygon.</summary>
	static float HoleArea( float radius, int segments ) =>
		0.5f * segments * radius * radius * MathF.Sin( MathF.Tau / segments );

	/// <summary>
	/// Ear clipping a loop that runs out to an inner boundary and back along the same seam.
	///
	/// This is the assumption EffigyMeshBoolean.AddFaceSplittingBridges rests on, and it is the
	/// mechanism a cut hole survives by. A half-edge mesh cannot hold a face with a hole in it, so
	/// s&amp;box's boolean hands a holed face back as ONE loop that visits the two seam vertices twice.
	/// That loop has to triangulate into a ring with a genuine hole in the middle - not a filled
	/// disc - or a cut produces a tunnel whose opening is painted over, which is exactly what it
	/// did before the adapter learned to split them.
	///
	/// Checked by AREA rather than by triangle count, because a filled square and a square with a
	/// hole in it triangulate to a similar number of triangles and differ only in what they cover.
	/// The one thing that cannot be faked is how much surface is there.
	/// </summary>
	static void TestBridgedLoopKeepsItsHole()
	{
		// A 4x4 square with a 2x2 hole, spliced along a bridge at the left edge: out along y=-1 to
		// the hole, anticlockwise round it, back to where the bridge started, then round the outside.
		var loop = new List<Vec2>
		{
			// the bridge out, and the hole, wound OPPOSITE to the outer so it reads as a hole
			new( -2, -1 ), new( -1, -1 ), new( -1, 1 ), new( 1, 1 ), new( 1, -1 ), new( -1, -1 ),
			// back along the bridge and round the outside
			new( -2, -1 ), new( -2, -2 ), new( 2, -2 ), new( 2, 2 ), new( -2, 2 ),
		};

		var triangles = Triangulate.BridgedLoop( loop );

		Report.Check( "a bridged loop triangulates at all", triangles.Count > 0, "no triangles" );

		var area = 0f;

		foreach ( var (a, b, c) in triangles )
		{
			var p = loop[a];
			var q = loop[b];
			var r = loop[c];

			area += MathF.Abs( (q.x - p.x) * (r.y - p.y) - (r.x - p.x) * (q.y - p.y) ) * 0.5f;
		}

		// 4x4 outer minus the 2x2 hole. A filled square would measure 16 and is the failure this
		// whole test exists to catch.
		Report.Check( "and covers the ring only, not the hole", MathF.Abs( area - 12f ) < 1e-3f,
			$"covered {area:0.###}, expected 12 (16 means the hole was filled in)" );

		// The defect the adapter removes: no triangle may reuse one vertex twice, or the face it
		// becomes repeats a vertex and MeshValidator rejects the mesh exactly as before.
		var degenerate = 0;

		foreach ( var (a, b, c) in triangles )
		{
			// By POSITION, not by loop index: the seam's two visits are different indices into the
			// loop and the same point in space, which is precisely the case that matters.
			if ( Same( loop[a], loop[b] ) || Same( loop[b], loop[c] ) || Same( loop[a], loop[c] ) )
				degenerate++;
		}

		Report.Check( "and no triangle collapses onto the seam", degenerate == 0,
			$"{degenerate} triangles reuse a seam vertex" );

		static bool Same( Vec2 a, Vec2 b ) => MathF.Abs( a.x - b.x ) < 1e-6f && MathF.Abs( a.y - b.y ) < 1e-6f;
	}

	static void TestBoltHoles()
	{
		// A 10x10 plate, 1 deep, with four r=0.5 holes near the corners.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		sketch.Sketch.AddRectangle( new Vec2( -5, -5 ), new Vec2( 5, 5 ) );

		var holes = new List<SketchCircle>();

		foreach ( var centre in new[] { (-3f, -3f), (3f, -3f), (3f, 3f), (-3f, 3f) } )
			holes.Add( sketch.Sketch.AddCircle( new Vec2( centre.Item1, centre.Item2 ), 0.5f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var plate = studio.Bodies.Single().Mesh;

		var holeArea = holes.Sum( h => TessellatedArea( sketch.Sketch, h ) );
		var expected = (100f - holeArea) * 1f;

		Report.Check( "volume is the plate minus all four holes",
			MathF.Abs( Volume( plate ) - expected ) < 0.05f,
			$"{Volume( plate ):0.####}, expected {expected:0.####}" );

		// Genus 4: X = 2 - 2g = -6. This is the check a filled cap cannot survive.
		var x = MeshValidator.EulerCharacteristic( plate );

		Report.Check( "four holes make it genus 4, so X = -6", x == -6, $"X = {x}" );

		var validation = MeshValidator.Validate( plate );

		Report.Check( "the mesh is valid", validation.IsValid, validation.ToString() );
		Report.Check( "and closed", validation.IsClosed, $"{validation.BoundaryEdges} boundary edges" );

		// Positive volume is the winding check: an inside-out solid looks entirely normal in
		// wireframe and measures negative.
		Report.Check( "it winds outward", Volume( plate ) > 0f, $"{Volume( plate ):0.####}" );

		// Subdivision is where a bad cap shows up as a lumpy surface rather than a wrong number, so
		// the topology has to survive it even though a triangulated cap is not the ideal input.
		var subdivided = CatmullClark.Subdivide( plate, 1 );

		Report.Check( "it still subdivides to a valid mesh",
			MeshValidator.Validate( subdivided ).IsValid );

		Report.Check( "keeping its genus", MeshValidator.EulerCharacteristic( subdivided ) == -6,
			$"X = {MeshValidator.EulerCharacteristic( subdivided )}" );
	}

	static void TestHoleWalls()
	{
		// The wall of a hole faces INWARD — the material is outside it, so the outward-facing
		// surface normal points at the hole's axis. This falls out of ProfileFinder handing holes
		// back wound the opposite way to the outer loop, with no sign handling in the extrude at
		// all, which is exactly the kind of thing that is true by accident until someone checks.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -4, -4 ), new Vec2( 4, 4 ) );
		sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 1.5f );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;
		var wrong = 0;
		var checkedFaces = 0;

		foreach ( var face in mesh.Faces )
		{
			var normal = mesh.FaceNormal( face );

			// Side walls only: caps point along Z.
			if ( MathF.Abs( normal.z ) > 0.1f )
				continue;

			var centroid = mesh.FaceCentroid( face );
			var outward = new Vec3( centroid.x, centroid.y, 0f );

			// Inside the hole's radius means it is one of the hole's walls.
			if ( outward.Length > 2f )
				continue;

			checkedFaces++;

			// Pointing at the axis: the normal opposes the direction from the axis to the face.
			if ( Vec3.Dot( normal, outward.Normal ) > -0.5f )
				wrong++;
		}

		Report.Check( "the hole has walls", checkedFaces > 8, $"{checkedFaces} wall faces found" );

		Report.Check( "and every one of them faces into the hole", wrong == 0,
			$"{wrong} of {checkedFaces} faced outward" );

		// The outer wall must still face away, which is the other half of the same question.
		var outerWrong = mesh.Faces
			.Where( f => MathF.Abs( mesh.FaceNormal( f ).z ) < 0.1f )
			.Where( f => new Vec3( mesh.FaceCentroid( f ).x, mesh.FaceCentroid( f ).y, 0f ).Length > 2f )
			.Count( f => Vec3.Dot( mesh.FaceNormal( f ),
				new Vec3( mesh.FaceCentroid( f ).x, mesh.FaceCentroid( f ).y, 0f ).Normal ) < 0.5f );

		Report.Check( "while the outer walls still face outward", outerWrong == 0,
			$"{outerWrong} faced inward" );
	}

	static void TestAwkward()
	{
		// AN L-SHAPED PLATE WITH A HOLE IN THE SHORT ARM. The bridge from the outer loop to the hole
		// has to avoid the notch, which a naive "nearest vertex" bridge would cut straight across.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		sketch.Sketch.AddPolygon(
			new Vec2( 0, 0 ), new Vec2( 6, 0 ), new Vec2( 6, 2 ),
			new Vec2( 2, 2 ), new Vec2( 2, 6 ), new Vec2( 0, 6 ) );

		var hole = sketch.Sketch.AddCircle( new Vec2( 4.5f, 1f ), 0.5f );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var report = studio.Rebuild();

		Report.Check( "a hole in a concave plate builds", !report.HasErrors, report.ToString() );

		if ( !report.HasErrors )
		{
			var mesh = studio.Bodies.Single().Mesh;

			// The L is 20 units of area: 6x2 plus 2x4.
			var expected = 20f - TessellatedArea( sketch.Sketch, hole );

			Report.Check( "with the L's area minus the hole",
				MathF.Abs( Volume( mesh ) - expected ) < 0.05f,
				$"{Volume( mesh ):0.####}, expected {expected:0.####}" );

			Report.Check( "and it is genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
				$"X = {MeshValidator.EulerCharacteristic( mesh )}" );

			Report.Check( "closed and valid", MeshValidator.Validate( mesh ) is { IsValid: true, IsClosed: true } );
		}

		// A SQUARE HOLE, so the hole is not always the smooth case. Its corners are the vertices a
		// bridge is most likely to pick and most likely to graze.
		var square = new PartStudio();
		var ss = square.Add( new SketchFeature() );
		ss.Sketch.AddRectangle( new Vec2( -3, -3 ), new Vec2( 3, 3 ) );
		ss.Sketch.AddRectangle( new Vec2( -1, -1 ), new Vec2( 1, 1 ) );

		square.Add( new ExtrudeFeature() ).Distance.Value = 2f;
		var squareReport = square.Rebuild();

		Report.Check( "a square hole in a square plate builds", !squareReport.HasErrors, squareReport.ToString() );

		if ( !squareReport.HasErrors )
		{
			var mesh = square.Bodies.Single().Mesh;

			// 36 minus 4, times 2. A clean number, and the case where a filled cap would read 72.
			Report.Check( "with exactly the right volume",
				MathF.Abs( Volume( mesh ) - 64f ) < 1e-2f, $"{Volume( mesh ):0.####}, expected 64" );

			Report.Check( "and it is genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
				$"X = {MeshValidator.EulerCharacteristic( mesh )}" );
		}

		// A hole close enough to the edge that the bridge is very short, which is where a
		// tolerance-based validity test would be tempted to accept a degenerate bridge.
		var tight = new PartStudio();
		var ts = tight.Add( new SketchFeature() );
		ts.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 4, 4 ) );
		ts.Sketch.AddCircle( new Vec2( 0.55f, 2f ), 0.5f );

		tight.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var tightReport = tight.Rebuild();

		Report.Check( "a hole almost touching the edge still builds", !tightReport.HasErrors,
			tightReport.ToString() );

		if ( !tightReport.HasErrors )
		{
			Report.Check( "and is still genus 1",
				MeshValidator.EulerCharacteristic( tight.Bodies.Single().Mesh ) == 0 );
		}
	}

	static void TestIsland()
	{
		// A loop inside a hole is not a hole — it is solid again, and ProfileFinder already knows
		// that ("a loop inside an odd number of other loops is a hole"). So a ring with a disc in
		// the middle of it is TWO profiles and therefore two bodies, and the disc must not be
		// treated as a hole in the ring.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		sketch.Sketch.AddRectangle( new Vec2( -6, -6 ), new Vec2( 6, 6 ) );
		var middle = sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 3f );
		var island = sketch.Sketch.AddCircle( new Vec2( 0, 0 ), 1f );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;

		// Two separate solids from one sketch, so they stay separate rather than merging.
		extrude.Result.Index = 1;

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		Report.Check( "a ring and an island make two bodies", studio.Bodies.Count == 2,
			$"{studio.Bodies.Count} bodies" );

		var total = studio.Bodies.Sum( b => Volume( b.Mesh ) );
		var ringArea = 144f - TessellatedArea( sketch.Sketch, middle );
		var islandArea = TessellatedArea( sketch.Sketch, island );

		Report.Check( "whose volumes are the ring and the disc",
			MathF.Abs( total - (ringArea + islandArea) ) < 0.05f,
			$"{total:0.####}, expected {ringArea + islandArea:0.####}" );

		// The ring is genus 1; the island is a plain disc at genus 0. Getting this backwards would
		// mean the island had been treated as a hole in the ring.
		var genus = studio.Bodies.Select( b => MeshValidator.EulerCharacteristic( b.Mesh ) ).OrderBy( v => v ).ToList();

		Report.Check( "the ring is genus 1 and the island genus 0",
			genus.SequenceEqual( new[] { 0, 2 } ), string.Join( ", ", genus ) );
	}

	static void TestNoRegression()
	{
		// The n-gon cap is a deliberate choice this kernel argues for at length — Catmull-Clark
		// turns one into n clean quads. Holed profiles cannot have one, and everything else still
		// must, so this pins the shape of an ordinary extrude against the change.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 3, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "a plain extrude is still 6 faces: four walls and two n-gon caps",
			mesh.FaceCount == 6, $"{mesh.FaceCount} faces" );

		Report.Check( "with the caps still whole quads, not triangulated",
			mesh.Faces.Count( f => f.Count == 4 ) == 6, string.Join( ", ", mesh.Faces.Select( f => f.Count ) ) );

		Report.Check( "and the volume unchanged at 6",
			MathF.Abs( Volume( mesh ) - 6f ) < 1e-3f, $"{Volume( mesh ):0.####}" );

		// A hexagon keeps its 6-gon caps too — the n-gon path is about any simple loop, not just
		// four-sided ones.
		var hex = new PartStudio();
		var hs = hex.Add( new SketchFeature() );
		var corners = new Vec2[6];

		for ( var i = 0; i < 6; i++ )
		{
			var angle = i * MathF.PI / 3f;
			corners[i] = new Vec2( MathF.Cos( angle ), MathF.Sin( angle ) );
		}

		hs.Sketch.AddPolygon( corners );
		hex.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		hex.Rebuild();

		var hexMesh = hex.Bodies.Single().Mesh;

		Report.Check( "a hexagonal profile still caps with two 6-gons",
			hexMesh.Faces.Count( f => f.Count == 6 ) == 2,
			string.Join( ", ", hexMesh.Faces.Select( f => f.Count ) ) );
	}

	// --- helpers ------------------------------------------------------------------------------

	static float TessellatedArea( Sketch sketch, SketchCurve curve )
	{
		var points = curve.Tessellate( sketch, sketch.Tolerance );
		var n = points.Count - 1;
		var sum = 0f;

		for ( var i = 0; i < n; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % n];
			sum += a.x * b.y - b.x * a.y;
		}

		return MathF.Abs( sum * 0.5f );
	}

	static float Volume( PolyMesh mesh ) => mesh.SignedVolume();

	/// <summary>
	/// The regression this exists for: a cut left the face it went through as 29 TRIANGLES.
	///
	/// The mesh was correct every time - closed, manifold, right volume - so every existing test
	/// here passed while the face a user clicks on had been shattered. A Face is the unit of
	/// selection and of material assignment, so painting that cap meant 29 clicks. FACE COUNT is
	/// therefore the measure, and it is the one thing none of the tests above look at.
	///
	/// Two is the floor, not one: a face is a single loop of corners, so a face with a hole in it
	/// cannot be fewer.
	/// </summary>
	static void TestBridgedLoopSplitsIntoTwo()
	{
		// The same 4x4-square-with-a-2x2-hole fixture TestBridgedLoopKeepsItsHole uses.
		var loop = new List<Vec2>
		{
			new( -2, -1 ), new( -1, -1 ), new( -1, 1 ), new( 1, 1 ), new( 1, -1 ), new( -1, -1 ),
			new( -2, -1 ), new( -2, -2 ), new( 2, -2 ), new( 2, 2 ), new( -2, 2 ),
		};

		var loops = Triangulate.SplitBridgedLoop( loop );

		Report.Check( "a bridged loop splits at all", loops is not null, "refused" );

		if ( loops is null )
			return;

		Report.Check( "into exactly two faces", loops.Count == 2, $"{loops.Count} faces" );

		foreach ( var face in loops )
		{
			Report.Check( "each face has at least three corners", face.Count >= 3, $"{face.Count} corners" );

			// The defect the whole bridge machinery exists to avoid. A split that reintroduces it
			// has done nothing but rename the problem.
			Report.Check( "no face repeats a corner",
				face.Select( i => loop[i] ).Distinct().Count() == face.Count, "repeated corner" );

			Report.Check( "no face crosses itself", IsSimple( loop, face ), "self-intersecting" );
		}

		// AREA IS WHAT CATCHES A PLAUSIBLE-LOOKING WRONG SPLIT. Two faces that between them cover
		// 16 have filled the hole back in; two that cover 12 are the annulus and nothing else. This
		// is the measure that found the overlapping-fan bug in the ear clipper.
		var area = loops.Sum( face => MathF.Abs( LoopArea( loop, face ) ) );

		Report.Check( "the two faces cover the ring and not the hole", MathF.Abs( area - 12f ) < 1e-3f,
			$"covered {area}, expected 12" );

		// Same winding as the input, or the face points the wrong way and vanishes under culling.
		Report.Check( "both faces keep the input's winding", loops.All( f => LoopArea( loop, f ) > 0f ),
			"a face came back reversed" );
	}

	/// <summary>
	/// A loop the engine bridged TWICE, which is what a face with two pockets in it comes back as.
	///
	/// Peeled one bridge at a time, innermost first, and the fixture is spliced here the way the
	/// engine splices it rather than written out as a literal: a hand-typed sixteen-corner ring
	/// with two seams in it is a fixture nobody can check by reading, and getting it subtly wrong
	/// would prove the splitter works on a shape that never occurs.
	/// </summary>
	static void TestTwoBridgesRecovered()
	{
		var outer = new List<Vec2> { new( -3, -3 ), new( 3, -3 ), new( 3, 3 ), new( -3, 3 ) };
		var right = new List<Vec2> { new( 1, -1 ), new( 1, 1 ), new( 2, 1 ), new( 2, -1 ) };
		var left = new List<Vec2> { new( -1, -1 ), new( -2, -1 ), new( -2, 1 ), new( -1, 1 ) };

		// Each hole bridged from the outer corner nearest it, so neither bridge crosses the other.
		var ring = Splice( Splice( outer, right, 1, 3 ), left, 0, 1 );

		var loops = Triangulate.SplitBridgedLoop( ring );

		Report.Check( "a twice-bridged loop splits at all", loops is not null, "refused" );

		if ( loops is null )
			return;

		Report.Check( "into three faces", loops.Count == 3, $"{loops.Count} faces" );

		foreach ( var face in loops )
		{
			Report.Check( "no face repeats a corner",
				face.Select( i => ring[i] ).Distinct().Count() == face.Count, "repeated corner" );

			Report.Check( "no face crosses itself", IsSimple( ring, face ), "self-intersecting" );
		}

		// 36 less two 2x1 pockets. Three faces covering 36 have filled both back in.
		var area = loops.Sum( face => MathF.Abs( LoopArea( ring, face ) ) );

		Report.Check( "covering the plate and neither pocket", MathF.Abs( area - 32f ) < 1e-3f,
			$"covered {area}, expected 32" );

		Report.Check( "all three keeping the input's winding",
			loops.All( f => LoopArea( ring, f ) > 0f ), "a face came back reversed" );
	}

	/// <summary>
	/// Splice a hole into a ring along a bridge, exactly as a half-edge mesh hands one back: out to
	/// the hole at <paramref name="from"/>, all the way round it, back along the same seam.
	/// </summary>
	static List<Vec2> Splice( List<Vec2> ring, List<Vec2> hole, int at, int from )
	{
		var result = new List<Vec2>();

		for ( var i = 0; i <= at; i++ )
			result.Add( ring[i] );

		for ( var j = 0; j < hole.Count; j++ )
			result.Add( hole[(from + j) % hole.Count] );

		result.Add( hole[from] );
		result.Add( ring[at] );

		for ( var i = at + 1; i < ring.Count; i++ )
			result.Add( ring[i] );

		return result;
	}

	/// <summary>
	/// Refusing is a feature. A wrong split is a self-intersecting face that is closed, manifold and
	/// Euler-correct - the exact class of defect that cost this repo a day - so anything the
	/// splitter is not certain of returns null and the caller triangulates instead.
	/// </summary>
	static void TestSplitRefusals()
	{
		var square = new List<Vec2> { new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 ) };

		Report.Check( "a plain polygon has no bridge to split on",
			Triangulate.SplitBridgedLoop( square ) is null, "split a simple polygon" );

		// A vertex visited three times is not a bridge, whatever else it is.
		var tangled = new List<Vec2>
		{
			new( -2, -1 ), new( -1, -1 ), new( -1, 1 ), new( -1, -1 ), new( 1, 1 ),
			new( 1, -1 ), new( -1, -1 ), new( -2, -1 ), new( -2, -2 ), new( 2, -2 ), new( 2, 2 ),
		};

		Report.Check( "a thrice-visited corner is refused",
			Triangulate.SplitBridgedLoop( tangled ) is null, "split a tangled loop" );
	}

	/// <summary>
	/// The same measure as TestBridgedLoopSplitsIntoTwo, one step earlier in the pipeline.
	///
	/// A cut that leaves a hole in a face has come back as two n-gons since the boolean work; a
	/// SKETCH with a hole in it still capped with triangles at both ends, because WithHoles built
	/// its own bridged ring and handed it straight to the ear clipper. Same defect, same cost -
	/// painting a washer's end face is one click per triangle - and the same fix, now that the
	/// splitter exists to point that ring at.
	///
	/// FACE COUNT AND COVERED AREA, never a look at it. Every hole test here passed throughout the
	/// 29-triangle regression: a shattered cap is closed, manifold and exactly the right volume.
	/// </summary>
	static void TestSketchCapSplits()
	{
		// A 6x6 plate with a 2x2 hole, the fixture the extrude volume check above uses.
		var outer = new List<Vec2> { new( -3, -3 ), new( 3, -3 ), new( 3, 3 ), new( -3, 3 ) };
		var hole = new List<Vec2> { new( -1, -1 ), new( 1, -1 ), new( 1, 1 ), new( -1, 1 ) };

		var flat = new List<Vec2>( outer );
		flat.AddRange( hole );

		var loops = Triangulate.SplitWithHoles( outer, new List<IReadOnlyList<Vec2>> { hole } );

		Report.Check( "a holed profile's cap splits at all", loops is not null, "refused" );

		if ( loops is not null )
		{
			Report.Check( "into exactly two faces", loops.Count == 2, $"{loops.Count} faces" );

			foreach ( var face in loops )
			{
				Report.Check( "no face repeats a corner",
					face.Select( i => flat[i] ).Distinct().Count() == face.Count, "repeated corner" );

				Report.Check( "no face crosses itself", IsSimple( flat, face ), "self-intersecting" );
			}

			// 36 minus 4. Two faces covering 36 have filled the hole back in.
			var area = loops.Sum( face => MathF.Abs( LoopArea( flat, face ) ) );

			Report.Check( "the two faces cover the plate and not the hole",
				MathF.Abs( area - 32f ) < 1e-3f, $"covered {area}, expected 32" );

			// WithHoles normalises its triples to the outer loop's winding, and a caller swapping
			// one for the other must not have to think about which way the cap ends up facing.
			Report.Check( "wound the way WithHoles winds its triangles",
				loops.All( f => LoopArea( flat, f ) > 0f ), "a face came back reversed" );
		}

		// AND THE SAME PROFILE THROUGH THE FEATURE, because the split is only worth anything if the
		// cap a person clicks on is the one that changed.
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( -3, -3 ), new Vec2( 3, 3 ) );
		sketch.Sketch.AddRectangle( new Vec2( -1, -1 ), new Vec2( 1, 1 ) );
		studio.Add( new ExtrudeFeature() ).Distance.Value = 2f;

		var report = studio.Rebuild();

		Report.Check( "the extrude builds", !report.HasErrors, report.ToString() );

		if ( report.HasErrors )
			return;

		var mesh = studio.Bodies.Single().Mesh;

		// Four outer walls, four hole walls, two caps of two faces each. Twelve, where the ear
		// clipper left twenty-four: the same eight walls, and eight triangles per cap.
		Report.Check( "the extrusion has twelve faces, not twenty-four", mesh.FaceCount == 12,
			$"{mesh.FaceCount} faces" );

		Report.Check( "and still measures the plate minus the hole",
			MathF.Abs( Volume( mesh ) - 64f ) < 1e-2f, $"{Volume( mesh ):0.####}, expected 64" );

		Report.Check( "closed and valid", MeshValidator.Validate( mesh ) is { IsValid: true, IsClosed: true } );

		Report.Check( "and still genus 1", MeshValidator.EulerCharacteristic( mesh ) == 0,
			$"X = {MeshValidator.EulerCharacteristic( mesh )}" );

		// N HOLES, N+1 FACES. Each hole is cut against whichever face it landed in, so the second
		// one splits a face the first one made rather than needing a different algorithm.
		var twoHoles = new List<IReadOnlyList<Vec2>>
		{
			new List<Vec2> { new( -2, -2 ), new( -1, -2 ), new( -1, -1 ), new( -2, -1 ) },
			new List<Vec2> { new( 1, 1 ), new( 2, 1 ), new( 2, 2 ), new( 1, 2 ) },
		};

		var twoFlat = new List<Vec2>( outer );

		foreach ( var h in twoHoles )
			twoFlat.AddRange( h );

		var three = Triangulate.SplitWithHoles( outer, twoHoles );

		Report.Check( "two holes in one profile split as well", three is not null, "refused" );

		if ( three is not null )
		{
			Report.Check( "into three faces", three.Count == 3, $"{three.Count} faces" );

			foreach ( var face in three )
			{
				Report.Check( "no face repeats a corner",
					face.Select( i => twoFlat[i] ).Distinct().Count() == face.Count, "repeated corner" );

				Report.Check( "no face crosses itself", IsSimple( twoFlat, face ), "self-intersecting" );
			}

			// 36 less two 1x1 holes. Three faces covering 36 have filled both back in, and three
			// covering 35 have missed one.
			var covered = three.Sum( face => MathF.Abs( LoopArea( twoFlat, face ) ) );

			Report.Check( "covering the plate and neither hole", MathF.Abs( covered - 34f ) < 1e-3f,
				$"covered {covered}, expected 34" );

			Report.Check( "and all three keep the winding",
				three.All( f => LoopArea( twoFlat, f ) > 0f ), "a face came back reversed" );
		}

		// The same plate through the feature: eight walls for the two holes, four for the outer,
		// and two caps of three faces each.
		var plate = new PartStudio();
		var ps = plate.Add( new SketchFeature() );
		ps.Sketch.AddRectangle( new Vec2( -3, -3 ), new Vec2( 3, 3 ) );
		ps.Sketch.AddRectangle( new Vec2( -2, -2 ), new Vec2( -1, -1 ) );
		ps.Sketch.AddRectangle( new Vec2( 1, 1 ), new Vec2( 2, 2 ) );
		plate.Add( new ExtrudeFeature() ).Distance.Value = 2f;

		var plateReport = plate.Rebuild();

		Report.Check( "a two-hole plate builds", !plateReport.HasErrors, plateReport.ToString() );

		if ( plateReport.HasErrors )
			return;

		var twoHoleMesh = plate.Bodies.Single().Mesh;

		Report.Check( "and extrudes to eighteen faces", twoHoleMesh.FaceCount == 18,
			$"{twoHoleMesh.FaceCount} faces" );

		Report.Check( "measuring the plate less both holes",
			MathF.Abs( Volume( twoHoleMesh ) - 68f ) < 1e-2f, $"{Volume( twoHoleMesh ):0.####}, expected 68" );

		Report.Check( "closed and valid with two holes",
			MeshValidator.Validate( twoHoleMesh ) is { IsValid: true, IsClosed: true } );

		// Two tunnels through a slab: X = 2 - 2 x genus, so genus 2 reads -2.
		Report.Check( "and it is genus 2", MeshValidator.EulerCharacteristic( twoHoleMesh ) == -2,
			$"X = {MeshValidator.EulerCharacteristic( twoHoleMesh )}" );
	}

	static float LoopArea( List<Vec2> points, List<int> loop )
	{
		var sum = 0f;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = points[loop[i]];
			var b = points[loop[(i + 1) % loop.Count]];
			sum += a.x * b.y - b.x * a.y;
		}

		return sum * 0.5f;
	}

	/// <summary>No two non-adjacent edges of the loop properly cross.</summary>
	static bool IsSimple( List<Vec2> points, List<int> loop )
	{
		for ( var i = 0; i < loop.Count; i++ )
		{
			for ( var j = i + 1; j < loop.Count; j++ )
			{
				// Edges sharing an endpoint meet there by construction and that is not a crossing.
				if ( j == i || (j + 1) % loop.Count == i || (i + 1) % loop.Count == j )
					continue;

				var a = points[loop[i]];
				var b = points[loop[(i + 1) % loop.Count]];
				var c = points[loop[j]];
				var d = points[loop[(j + 1) % loop.Count]];

				var d1 = Vec2.Cross( b - a, c - a );
				var d2 = Vec2.Cross( b - a, d - a );
				var d3 = Vec2.Cross( d - c, a - c );
				var d4 = Vec2.Cross( d - c, b - c );

				if ( ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
					&& ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)) )
					return false;
			}
		}

		return true;
	}
}
