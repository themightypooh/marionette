using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Verification for the kernel. Console runner rather than a test framework, so it stays a
/// dependency-free thing that can be pointed at any of this code from anywhere.
///
/// This exists because subdivision code is the classic case of "looks right, is wrong". A
/// Catmull-Clark implementation with the vertex rule subtly off still produces a smooth, plausible
/// blob — it just shrinks slightly incorrectly and drifts further from the limit surface at every
/// level. Eyeballing a render will not catch it. Euler characteristic and the exact V/E/F growth
/// laws will.
/// </summary>
public static class Program
{
	/// <summary>
	/// Effigy.Tests/out, wherever this file happens to live - NOT "out" relative to whatever
	/// directory the suite was launched from.
	///
	/// A plain relative path put the samples wherever you happened to be standing. Run through
	/// tools/test.sh they landed in Effigy.Tests/out, which is the tracked copy; run as
	/// `dotnet run --project Effigy.Tests` from the repo root - the obvious thing to type - they
	/// landed in a second `out/` beside the .sbproj. That one is gitignored, so it is invisible to
	/// every check that would have caught it, and s&amp;box publishes project files rather than
	/// tracked files: 46 sample OBJs went out in the library package before anyone noticed.
	///
	/// CallerFilePath is the compiler telling us where this source file was, which is the one
	/// answer that does not depend on the caller's working directory.
	/// </summary>
	static string DefaultOutDir( [CallerFilePath] string thisFile = "" ) =>
		Path.Combine( Path.GetDirectoryName( thisFile ) ?? ".", "out" );

	public static int Main( string[] rawArgs )
	{
		// `dotnet run --nologo` hands the flag straight through to us. The first positional
		// argument is the output directory, so an unrecognised flag taken as one wrote the whole
		// sample set into a folder literally named `--nologo`. Drop flags we do not own.
		var args = rawArgs.Where( a => !a.StartsWith( "--" ) || a == "--tree" || a == "--tentacle" || a == "--stadium" || a == "--paint" ).ToArray();

		if ( args.Length > 0 && args[0] == "--tree" )
			return TreeGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		if ( args.Length > 0 && args[0] == "--tentacle" )
			return TentacleGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		if ( args.Length > 0 && args[0] == "--stadium" )
			return StadiumGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		if ( args.Length > 0 && args[0] == "--paint" )
			return PaintGen.Run( args.Length > 1 ? args[1] : DefaultOutDir() );

		var outDir = args.Length > 0 ? args[0] : DefaultOutDir();

		Section( "primitives are valid and manifold" );
		TestPrimitiveValidity();

		Section( "primitives are closed, except the plane" );
		TestClosedness();

		Section( "Euler characteristic matches expected genus" );
		TestEuler();

		Section( "face winding puts normals outward" );
		TestWinding();

		Section( "Catmull-Clark output is all quads" );
		TestAllQuads();

		Section( "Catmull-Clark obeys the V/E/F growth laws" );
		TestGrowthLaws();

		Section( "Catmull-Clark preserves topology" );
		TestTopologyPreserved();

		Section( "Catmull-Clark keeps an open mesh's boundary" );
		TestBoundaryPreserved();

		Section( "subdivision converges rather than drifting" );
		TestConvergence();

		Section( "PredictCost agrees with reality" );
		TestPredictCost();

		Section( "UV seams survive subdivision" );
		TestUVSeams();

		Section( "OBJ round-trips" );
		TestObjRoundTrip();

		SculptTests.Run();

		FeatureTests.Run();
		SketchTests.Run();

		RigTests.Run();

		RigDiagnosticTests.Run();

		SoftBoneTests.Run();

		WeightPaintTests.Run();

		VmdlAnimationTests.Run();

		ShellTests.Run();

		EdgeBlendTests.Run();

		UVTests.Run();

		PaintCanvasTests.Run();
		PaintReplayTests.Run();

		BooleanFeatureTests.Run();

		ExpressionTests.Run();

		SnapTests.Run();

		RaycastTests.Run();
		BvhFaceTests.Run();

		AllFeaturesTests.Run();

		AcceptsTests.Run();

		FaceMoveTests.Run();

		FaceExtrudeTests.Run();

		DiagnosticTests.Run();

		FaceSurfaceTests.Run();

		FaceSketchTests.Run();

		ConstraintTests.Run();

		CurveTests.Run();

		SketchEditTests.Run();

		HandleTests.Run();

		SweepLoftTests.Run();

		UntestedKernelTests.Run();

		DmxGrammarTests.Run();

		DmxAnimTests.Run();

		MergeTests.Run();
		AtlasIdTests.Run();

		FaceMaterialTests.Run();
		FaceMenuTests.Run();
		MaterialDropTests.Run();
		MaterialDropCurvedTests.Run();
		MaterialScaleTests.Run();
		RenderTests.Run();
		ConstraintToolTests.Run();

		DocumentTests.Run();

		PaintDocumentTests.Run();

		HoleTests.Run();

		SplitTests.Run();

		CurvedHoleTests.Run();

		CoplanarMergeTests.Run();

		TaperTests.Run();

		BranchTests.Run();

		TerminationTests.Run();

		EditorFlowTests.Run();

		DraftTests.Run();

		HoleFeatureTests.Run();

		CollisionTests.Run();

		VmdlPhysicsTests.Run();

		VmdlMaterialsTests.Run();
		BoneSelectionTests.Run();

		UnwrapTests.Run();

		PngTests.Run();

		NoteTests.Run();

		KernelSyncTests.Run();

		MenuIconTests.Run();

		Section( "writing sample OBJs" );
		WriteSamples( outDir );

		Console.WriteLine();
		Console.WriteLine( new string( '-', 60 ) );
		Console.WriteLine( $"  {Report.Passed} passed, {Report.Failed} failed" );
		Console.WriteLine( new string( '-', 60 ) );

		return Report.Failed == 0 ? 0 : 1;
	}

	// ---------------------------------------------------------------------------------------

	internal static Dictionary<string, PolyMesh> Closed() => new()
	{
		["box"] = Primitives.Box( 2, 2, 2 ),
		["cylinder"] = Primitives.Cylinder( 0.5f, 1f, 16 ),
		["quadsphere"] = Primitives.QuadSphere( 0.5f, 4 ),
		["wedge"] = Primitives.Wedge( 1, 1, 1 ),
		["tube"] = Primitives.Tube( 0.5f, 0.3f, 1f, 16 ),
	};

	static void TestPrimitiveValidity()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var v = MeshValidator.Validate( mesh );
			Check( $"{name} validates", v.IsValid, v.ToString() );
		}

		var plane = Primitives.Plane( 2, 2, 3, 3 );
		Check( "plane validates", MeshValidator.Validate( plane ).IsValid );
	}

	static void TestClosedness()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var v = MeshValidator.Validate( mesh );
			Check( $"{name} is closed", v.IsClosed, $"{v.BoundaryEdges} boundary edges" );
		}

		var plane = Primitives.Plane( 2, 2, 3, 3 );
		var pv = MeshValidator.Validate( plane );

		// A 3x3 grid has 12 edges around its border.
		Check( "plane has a 12-edge boundary", pv.BoundaryEdges == 12, $"got {pv.BoundaryEdges}" );
	}

	static void TestEuler()
	{
		// Genus 0 closed surfaces have V - E + F = 2.
		foreach ( var name in new[] { "box", "cylinder", "quadsphere", "wedge" } )
		{
			var x = MeshValidator.EulerCharacteristic( Closed()[name] );
			Check( $"{name} has X = 2", x == 2, $"got {x}" );
		}

		// The tube is a torus - genus 1, so X = 0. If this reads 2 the inner wall is missing or
		// the rings welded together somewhere they should not have.
		var tubeX = MeshValidator.EulerCharacteristic( Closed()["tube"] );
		Check( "tube has X = 0 (genus 1)", tubeX == 0, $"got {tubeX}" );
	}

	static void TestWinding()
	{
		// Divergence theorem: for outward normals, sum over faces of (centroid . normal) * area
		// equals three times the enclosed volume, so it must come out positive. Inverted winding
		// anywhere flips the sign or cancels it toward zero.
		foreach ( var (name, mesh) in Closed() )
		{
			var volume = mesh.SignedVolume();
			Check( $"{name} winds outward (volume {volume:0.####} > 0)", volume > 0.001f );
		}

		// Spot-check the box exactly: a 2x2x2 box encloses 8.
		var box = Primitives.Box( 2, 2, 2 );
		var boxVol = box.SignedVolume();
		Check( "box volume is 8", MathF.Abs( boxVol - 8f ) < 1e-3f, $"got {boxVol:0.####}" );
	}

	static void TestAllQuads()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var sub = CatmullClark.Subdivide( mesh, 1 );
			var nonQuads = sub.Faces.Count( f => f.Count != 4 );
			Check( $"{name} subdivides to all quads", nonQuads == 0, $"{nonQuads} non-quads" );
		}

		// The wedge is the interesting one - it goes in with two triangles and must still come out
		// entirely quads.
		var wedge = CatmullClark.Subdivide( Primitives.Wedge(), 1 );
		Check( "wedge's triangles became quads", wedge.Faces.All( f => f.Count == 4 ) );
	}

	static void TestGrowthLaws()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var v = mesh.VertexCount;
			var e = mesh.BuildEdgeFaces().Count;
			var f = mesh.FaceCount;
			var corners = mesh.Faces.Sum( x => x.Count );

			var sub = CatmullClark.Subdivide( mesh, 1 );

			Check( $"{name}: V' = V+E+F", sub.VertexCount == v + e + f,
				$"expected {v + e + f}, got {sub.VertexCount}" );

			Check( $"{name}: F' = total corners", sub.FaceCount == corners,
				$"expected {corners}, got {sub.FaceCount}" );

			Check( $"{name}: E' = 2E + corners", sub.BuildEdgeFaces().Count == e * 2 + corners,
				$"expected {e * 2 + corners}, got {sub.BuildEdgeFaces().Count}" );
		}
	}

	static void TestTopologyPreserved()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var before = MeshValidator.EulerCharacteristic( mesh );
			var sub = CatmullClark.Subdivide( mesh, 3 );
			var after = MeshValidator.EulerCharacteristic( sub );
			var v = MeshValidator.Validate( sub );

			Check( $"{name} keeps X = {before} after 3 levels", after == before, $"got {after}" );
			Check( $"{name} still valid after 3 levels", v.IsValid, v.ToString() );
			Check( $"{name} still closed after 3 levels", v.IsClosed );
		}
	}

	static void TestBoundaryPreserved()
	{
		var plane = Primitives.Plane( 2, 2, 2, 2 );
		var sub = CatmullClark.Subdivide( plane, 2 );
		var v = MeshValidator.Validate( sub );

		Check( "subdivided plane is still valid", v.IsValid, v.ToString() );
		Check( "subdivided plane still has a boundary", v.BoundaryEdges > 0 );

		// The border must stay flat at z=0. If the boundary rules were wrong it would be pulled
		// toward the interior and this would drift.
		var maxZ = sub.Positions.Max( p => MathF.Abs( p.z ) );
		Check( "subdivided plane stays planar", maxZ < 1e-5f, $"max |z| = {maxZ}" );

		// A unit-ish plane's corners are pinned by the corner rule, so the extent should not
		// collapse inward the way an unclamped surface would.
		var extent = sub.Positions.Max( p => MathF.Abs( p.x ) );
		Check( "subdivided plane keeps its corners", MathF.Abs( extent - 1f ) < 1e-5f, $"extent {extent:0.#####}" );
	}

	static void TestConvergence()
	{
		// Subdividing a cube converges on a rounded solid. Two things must hold: the centroid must
		// not wander, and successive levels must move points less and less. A wrong vertex rule
		// typically shows up as drift that never settles.
		var mesh = Primitives.Box( 2, 2, 2 );
		var previousRadius = 0f;
		var deltas = new List<float>();

		for ( var level = 1; level <= 5; level++ )
		{
			var sub = CatmullClark.Subdivide( mesh, level );

			var centroid = Vec3.Zero;
			foreach ( var p in sub.Positions ) centroid += p;
			centroid /= sub.VertexCount;

			Check( $"level {level} stays centred", centroid.Length < 1e-4f, $"centroid {centroid}" );

			var radius = sub.Positions.Average( p => p.Length );

			if ( level > 1 )
				deltas.Add( MathF.Abs( radius - previousRadius ) );

			previousRadius = radius;
		}

		var settling = true;

		for ( var i = 1; i < deltas.Count; i++ )
		{
			if ( deltas[i] > deltas[i - 1] + 1e-6f )
				settling = false;
		}

		Check( "successive levels move less each time", settling,
			string.Join( ", ", deltas.Select( d => d.ToString( "0.#####" ) ) ) );
	}

	static void TestPredictCost()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			for ( var level = 1; level <= 3; level++ )
			{
				var predicted = CatmullClark.PredictCost( mesh, level );
				var actual = CatmullClark.Subdivide( mesh, level );

				Check( $"{name} level {level} cost predicted",
					predicted.Vertices == actual.VertexCount && predicted.Faces == actual.FaceCount,
					$"predicted {predicted.Vertices}v/{predicted.Faces}f, got {actual.VertexCount}v/{actual.FaceCount}f" );
			}
		}
	}

	static void TestUVSeams()
	{
		// A box's corner belongs to three faces that each want a different UV for the same
		// position. Per-corner UVs are what allow that, and subdivision must not quietly average
		// them together - that would smear the texture across every seam.
		var box = Primitives.Box( 2, 2, 2 );
		var sub = CatmullClark.Subdivide( box, 2 );

		var uvsByPosition = new Dictionary<int, HashSet<(long, long)>>();

		foreach ( var f in sub.Faces )
		{
			for ( var i = 0; i < f.Count; i++ )
			{
				if ( !uvsByPosition.TryGetValue( f.Indices[i], out var set ) )
					uvsByPosition[f.Indices[i]] = set = new HashSet<(long, long)>();

				set.Add( ((long)MathF.Round( f.UVs[i].x * 1000 ), (long)MathF.Round( f.UVs[i].y * 1000 )) );
			}
		}

		var seamVerts = uvsByPosition.Count( kv => kv.Value.Count > 1 );
		Check( "seam vertices still carry several UVs", seamVerts > 0, $"found {seamVerts}" );

		// Every UV must stay inside the unit square - the box's islands are all 0..1 and linear
		// subdivision cannot legitimately push one outside.
		var outOfRange = sub.Faces.SelectMany( f => f.UVs )
			.Count( uv => uv.x < -1e-4f || uv.x > 1 + 1e-4f || uv.y < -1e-4f || uv.y > 1 + 1e-4f );

		Check( "UVs stay in the unit square", outOfRange == 0, $"{outOfRange} outside" );
	}

	static void TestObjRoundTrip()
	{
		foreach ( var (name, mesh) in Closed() )
		{
			var text = ObjWriter.Write( mesh, name );
			var back = ObjReader.Read( text );

			Check( $"{name} OBJ keeps vertex count", back.VertexCount == mesh.VertexCount,
				$"{mesh.VertexCount} -> {back.VertexCount}" );

			Check( $"{name} OBJ keeps face count", back.FaceCount == mesh.FaceCount,
				$"{mesh.FaceCount} -> {back.FaceCount}" );

			Check( $"{name} OBJ keeps topology",
				MeshValidator.EulerCharacteristic( back ) == MeshValidator.EulerCharacteristic( mesh ) );

			// Normals must be present and finite; some importers reject a zero normal outright.
			var vnCount = text.Split( '\n' ).Count( l => l.StartsWith( "vn " ) );
			Check( $"{name} OBJ writes normals", vnCount > 0, $"{vnCount} normals" );
			Check( $"{name} OBJ has no NaN", !text.Contains( "NaN" ) && !text.Contains( "âˆž" ) );
		}

		// The smoothing threshold has to actually do something: a box should end up with exactly
		// six distinct normals, a cylinder with far more than six.
		var boxNormals = ObjWriter.Write( Primitives.Box(), "box" )
			.Split( '\n' ).Count( l => l.StartsWith( "vn " ) );

		Check( "box gets 6 hard normals", boxNormals == 6, $"got {boxNormals}" );

		var cylNormals = ObjWriter.Write( Primitives.Cylinder( 0.5f, 1f, 16 ), "cyl" )
			.Split( '\n' ).Count( l => l.StartsWith( "vn " ) );

		Check( "cylinder gets smoothed sides", cylNormals >= 16, $"got {cylNormals}" );
	}

	static void WriteSamples( string outDir )
	{
		Directory.CreateDirectory( outDir );

		foreach ( var (name, mesh) in Closed() )
		{
			ObjWriter.WriteFile( mesh, Path.Combine( outDir, $"{name}.obj" ), name );

			var sub = CatmullClark.Subdivide( mesh, 2 );
			ObjWriter.WriteFile( sub, Path.Combine( outDir, $"{name}_subdiv2.obj" ), $"{name}_subdiv2" );
		}

		WriteSketchSamples( outDir );
		WritePreviews( outDir );
		WriteBakeSample( outDir );
		WriteDmxSamples( outDir );
		VmdlPhysicsTests.WriteSample( outDir );

		var files = Directory.GetFiles( outDir, "*.obj" ).Length;
		var svgs = Directory.GetFiles( outDir, "*.svg" ).Length;
		Check( $"wrote {files} sample OBJs to {outDir}/", files > 0 );
		Check( $"wrote {svgs} SVG previews to {outDir}/", svgs > 0 );

		Console.WriteLine();
		Console.WriteLine( "  cost table (what a level slider would warn about):" );
		Console.WriteLine( $"  {"primitive",-12} {"L0",12} {"L2",14} {"L4",16} {"L6",18}" );

		foreach ( var (name, mesh) in Closed() )
		{
			string At( int level )
			{
				var (v, f) = CatmullClark.PredictCost( mesh, level );
				return $"{v}v/{f}f";
			}

			Console.WriteLine( $"  {name,-12} {At( 0 ),12} {At( 2 ),14} {At( 4 ),16} {At( 6 ),18}" );
		}
	}

	/// <summary>
	/// A normal map, baked from a sculpted plane onto its cage and written out as a PNG.
	///
	/// THE SUITE CANNOT JUDGE THIS ONE. It checks that the flanks of a bump lean the right way and
	/// that the numbers are what they should be, and every one of those can pass while the map is
	/// unusable in a shader — the green channel's convention in particular is a coin flip that looks
	/// entirely plausible either way in a thumbnail and lights every dent as a bump in the engine.
	/// So the file is written for the same reason the sample DMX is: the real verdict is somewhere
	/// else, and this is what gets carried there.
	///
	/// Expect a mostly flat lilac sheet — (128, 128, 255) is "no change from the cage" — with a disc
	/// in the middle: pink to the right of centre and blue to the left (red is +u), cyan below centre
	/// and purple above it (green is +v, and this file's first row is v = 0, so +v runs DOWN the
	/// image). That last clause is a convention, not a fact about the bake, and it is the second
	/// thing to check in the engine after the green channel's sign — an upside-down map lights
	/// exactly as wrongly as a flipped one.
	/// </summary>
	static void WriteBakeSample( string outDir )
	{
		var sculpt = new MultiresSculpt( Primitives.Plane( 2, 2, 4, 4 ) );
		sculpt.AddLevel();
		sculpt.AddLevel();
		sculpt.AddLevel();

		var mesh = sculpt.Evaluate( 3 );

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var p = mesh.Positions[i];
			var r = MathF.Sqrt( p.x * p.x + p.y * p.y );

			if ( r >= 0.6f )
				continue;

			var t = 1f - r / 0.6f;
			mesh.Positions[i] = new Vec3( p.x, p.y, p.z + 0.2f * t * t * (3f - 2f * t) );
		}

		sculpt.Record( 3, mesh );

		var coverage = NormalBake.Measure( sculpt.Cage, 256 );
		var map = NormalBake.Bake( sculpt.Cage, sculpt.Evaluate( 3 ), 256 );
		var pixels = new int[map.Width * map.Height];

		for ( var i = 0; i < pixels.Length; i++ )
			pixels[i] = (map.Rgb[i * 3] << 16) | (map.Rgb[i * 3 + 1] << 8) | map.Rgb[i * 3 + 2];

		PngPreview.WritePng( Path.Combine( outDir, "sample_normal_bake.png" ), pixels, map.Width, map.Height );

		Check( $"baked a {map.Width}x{map.Height} normal map to {outDir}/sample_normal_bake.png "
			+ $"({map.FilledCount} texels hit, UVs {(coverage.CanBake ? "clean" : "unusable")})",
			map.FilledCount > 0 && coverage.CanBake );
	}

	/// <summary>
	/// A static and a rigged DMX, written out so the engine's own reader can pass judgement on them.
	/// The suite cannot do that itself — the parser lives in the engine — but the file is the whole
	/// input, so validating it needs nothing else running:
	///
	///   bin/win64/dmxconvert.exe -i out/sample_rigged.dmx -o /tmp/check.dmx -oe keyvalues2_noids
	///
	/// That is the standalone loader, and it reports a line number. Finding it is what turned
	/// "Couldn't load DMX file" from the compiler — which names no line and no reason — into a
	/// missing comma between element_array members.
	/// </summary>
	static void WriteDmxSamples( string outDir )
	{
		var box = Primitives.Box( 2, 2, 2 );
		DmxWriter.WriteFile( box, Path.Combine( outDir, "sample_static.dmx" ), modelName: "sample_static" );

		var skeleton = new Skeleton();
		var root = skeleton.AddBone( "root", -1, Xform.Identity );
		skeleton.AddBone( "child", root, Xform.Translate( new Vec3( 0, 1, 0 ) ) );

		DmxWriter.WriteFile( box, Path.Combine( outDir, "sample_rigged.dmx" ), skeleton, modelName: "sample_rigged" );

		Check( "wrote a static and a rigged sample DMX",
			File.Exists( Path.Combine( outDir, "sample_static.dmx" ) )
			&& File.Exists( Path.Combine( outDir, "sample_rigged.dmx" ) ) );

		// The same two models again in FBX, so the engine's own importer can be pointed at them:
		//
		//   bin/win64/fbx2dmx.exe -i out/sample_rigged.fbx -o check.dmx
		//
		// That is the whole reason FBX is worth writing — the format's reader is Autodesk's, so a
		// file it accepts is correct by something other than our own reading of a spec.
		FbxWriter.WriteFile( box, Path.Combine( outDir, "sample_static.fbx" ), modelName: "sample_static" );
		FbxWriter.WriteFile( box, Path.Combine( outDir, "sample_rigged.fbx" ), skeleton, modelName: "sample_rigged" );

		Check( "wrote a static and a rigged sample FBX",
			File.Exists( Path.Combine( outDir, "sample_static.fbx" ) )
			&& File.Exists( Path.Combine( outDir, "sample_rigged.fbx" ) ) );

		// The .vmdl that wraps the rigged DMX, so the bind pose and the bone markup can be put in
		// front of the compiler. Same skeleton, so the bone count the file claims is the one the DMX
		// actually carries.
		VmdlAnimationTests.WriteSample( outDir, skeleton, box );

		// And the animation path on that same skeleton: a clip DMX plus a .vmdl that compiles the
		// two together. Same skeleton object as the mesh, deliberately — a clip written against a
		// different rig compiles and animates nothing.
		DmxAnimTests.WriteSample( outDir, skeleton );
	}

	/// <summary>
	/// Sketch-driven samples, so the whole chain — sketch, profile, solid, subdivision — can be
	/// looked at rather than only asserted about. Dropping one of these into ModelDoc is still the
	/// cheapest way to find out what s&amp;box makes of kernel output.
	/// </summary>
	static void WriteSketchSamples( string outDir )
	{
		// A rounded slot: two lines and two arcs stitched into one loop, then extruded.
		var slotStudio = new PartStudio();
		var slotSketch = slotStudio.Add( new SketchFeature() );
		var s = slotSketch.Sketch;
		var a0 = s.AddPoint( 0, 0 );
		var a1 = s.AddPoint( 4, 0 );
		var a2 = s.AddPoint( 4, 2 );
		var a3 = s.AddPoint( 0, 2 );
		var c0 = s.AddPoint( 4, 1 );
		var c1 = s.AddPoint( 0, 1 );
		s.Add( new SketchLine( a0, a1 ) );
		s.Add( new SketchArc( c0, a1, a2 ) );
		s.Add( new SketchLine( a2, a3 ) );
		s.Add( new SketchArc( c1, a3, a0 ) );
		slotStudio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		slotStudio.Rebuild();
		ObjWriter.WriteFile( slotStudio.ToMesh(), Path.Combine( outDir, "sketch_slot.obj" ), "slot" );

		// The same slot subdivided twice — the CAD cage and the dense surface from one tree.
		slotStudio.Add( new SubdivideFeature() ).Levels.Value = 2;
		slotStudio.Rebuild();
		ObjWriter.WriteFile( slotStudio.ToMesh(), Path.Combine( outDir, "sketch_slot_subdiv2.obj" ), "slot_subdiv2" );

		// A revolved torus.
		var torusStudio = new PartStudio();
		torusStudio.Add( new SketchFeature() ).Sketch.AddRectangle( new Vec2( 0, 1 ), new Vec2( 1, 2 ) );
		var revolve = torusStudio.Add( new RevolveFeature() );
		revolve.AxisDirection.Value = new Vec3( 1, 0, 0 );
		revolve.Segments.Value = 32;
		torusStudio.Rebuild();
		ObjWriter.WriteFile( torusStudio.ToMesh(), Path.Combine( outDir, "sketch_torus.obj" ), "torus" );

		// A revolved profile that touches the axis, which collapses to a proper closed tip.
		var coneStudio = new PartStudio();
		coneStudio.Add( new SketchFeature() ).Sketch
			.AddPolygon( new Vec2( 0, 0 ), new Vec2( 2, 0 ), new Vec2( 0, 3 ) );
		coneStudio.Add( new RevolveFeature() ).AxisDirection.Value = new Vec3( 0, 1, 0 );
		coneStudio.Rebuild();
		ObjWriter.WriteFile( coneStudio.ToMesh(), Path.Combine( outDir, "sketch_cone.obj" ), "cone" );

		// A plate with four bolt holes — the profile that was unbuildable until holes landed, and the
		// fastest way to see whether the caps really are open rather than filled in.
		var plateStudio = new PartStudio();
		var plateSketch = plateStudio.Add( new SketchFeature() );
		plateSketch.Sketch.AddRectangle( new Vec2( -5, -3 ), new Vec2( 5, 3 ) );

		foreach ( var centre in new[] { (-3.5f, -1.5f), (3.5f, -1.5f), (3.5f, 1.5f), (-3.5f, 1.5f) } )
			plateSketch.Sketch.AddCircle( new Vec2( centre.Item1, centre.Item2 ), 0.6f );

		plateStudio.Add( new ExtrudeFeature() ).Distance.Value = 0.8f;
		plateStudio.Rebuild();
		ObjWriter.WriteFile( plateStudio.ToMesh(), Path.Combine( outDir, "sketch_plate_holes.obj" ), "plate_holes" );

		// A drafted boss: the same square section, leaning 8 degrees. Draft is the kind of thing that
		// reads as "looks slightly better" until you put it beside the straight version.
		var draftStudio = new PartStudio();
		var draftSketch = draftStudio.Add( new SketchFeature() );
		draftSketch.Sketch.AddRectangle( new Vec2( -2, -2 ), new Vec2( 2, 2 ) );
		var draft = draftStudio.Add( new ExtrudeFeature() );
		draft.Distance.Value = 3f;
		draft.Taper.Value = 8f;
		draftStudio.Rebuild();
		ObjWriter.WriteFile( draftStudio.ToMesh(), Path.Combine( outDir, "sketch_taper.obj" ), "taper" );

		// A cube with every edge chamfered — the flat-bevel look, side by side with the sharp box.
		var bevelStudio = new PartStudio();
		var bevelBox = bevelStudio.Add( new PrimitiveFeature() );
		bevelBox.Shape.Index = 0;
		bevelBox.SizeX.Value = bevelBox.SizeY.Value = bevelBox.SizeZ.Value = 2f;
		var bevel = bevelStudio.Add( new ChamferFeature() );
		bevel.Width.Value = 0.2f;
		bevel.AngleThreshold.Value = 15f;
		bevelStudio.Rebuild();
		ObjWriter.WriteFile( bevelStudio.ToMesh(), Path.Combine( outDir, "bevel_box.obj" ), "bevel_box" );
	}

	/// <summary>
	/// Shaded previews of every sample, so the output can be seen rather than only measured.
	/// Backface culling means an inside-out solid renders as a hole, which makes these a visual
	/// double-check on the winding tests.
	/// </summary>
	static void WritePreviews( string outDir )
	{
		foreach ( var file in Directory.GetFiles( outDir, "*.obj" ) )
		{
			var name = Path.GetFileNameWithoutExtension( file );
			var mesh = ObjReader.Read( File.ReadAllText( file ) );

			SvgPreview.Write( mesh, Path.Combine( outDir, $"{name}.svg" ), name );
		}

		// A wireframe of one subdivided result, where the quad topology is the point.
		var slot = ObjReader.Read( File.ReadAllText( Path.Combine( outDir, "sketch_slot_subdiv2.obj" ) ) );
		SvgPreview.Write( slot, Path.Combine( outDir, "wire_slot_subdiv2.svg" ), "sketch_slot_subdiv2 (wireframe)", wireframe: true );

		var cage = ObjReader.Read( File.ReadAllText( Path.Combine( outDir, "sketch_slot.obj" ) ) );
		SvgPreview.Write( cage, Path.Combine( outDir, "wire_slot_cage.svg" ), "sketch_slot cage (wireframe)", wireframe: true );

		WriteContactSheets( outDir );
	}

	/// <summary>PNG contact sheets — one image showing everything, viewable anywhere.</summary>
	static void WriteContactSheets( string outDir )
	{
		PolyMesh Load( string name ) => ObjReader.Read( File.ReadAllText( Path.Combine( outDir, $"{name}.obj" ) ) );

		var primitives = new[]
		{
			new PngPreview.Tile( Load( "box" ), "box" ),
			new PngPreview.Tile( Load( "cylinder" ), "cylinder" ),
			new PngPreview.Tile( Load( "quadsphere" ), "quad sphere" ),
			new PngPreview.Tile( Load( "wedge" ), "wedge" ),
			new PngPreview.Tile( Load( "tube" ), "tube" ),
			new PngPreview.Tile( Load( "sketch_slot" ), "sketch extrude" ),
			new PngPreview.Tile( Load( "sketch_torus" ), "sketch revolve" ),
			new PngPreview.Tile( Load( "sketch_cone" ), "revolve on axis" ),
			new PngPreview.Tile( Load( "sketch_plate_holes" ), "profile with holes" ),
			new PngPreview.Tile( Load( "sketch_taper" ), "8 degree draft" ),
		};

		PngPreview.WriteSheet( primitives, Path.Combine( outDir, "preview_primitives.png" ) );

		// Cage beside subdivided, in wireframe, which is where the quad topology shows.
		var subdivision = new[]
		{
			new PngPreview.Tile( Load( "sketch_slot" ), "cage", wireframe: true ),
			new PngPreview.Tile( Load( "sketch_slot_subdiv2" ), "subdiv 2", wireframe: true ),
			new PngPreview.Tile( Load( "box" ), "box cage", wireframe: true ),
			new PngPreview.Tile( Load( "box_subdiv2" ), "box subdiv 2", wireframe: true ),
			new PngPreview.Tile( Load( "sketch_slot" ), "cage shaded" ),
			new PngPreview.Tile( Load( "sketch_slot_subdiv2" ), "subdiv 2 shaded" ),
			new PngPreview.Tile( Load( "cylinder" ), "cylinder cage" ),
			new PngPreview.Tile( Load( "cylinder_subdiv2" ), "cylinder subdiv 2" ),
		};

		PngPreview.WriteSheet( subdivision, Path.Combine( outDir, "preview_subdivision.png" ) );
	}

	// ---------------------------------------------------------------------------------------

	static void Section( string title ) => Report.Section( title );

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
