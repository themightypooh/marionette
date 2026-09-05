using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Building a part up out of several extrudes, and getting one part out of it.
///
/// The behaviour this replaces: every extrude made its own body, so three bosses on a block listed
/// as four separate parts. That is not what "I built this out of four extrudes" means to anyone,
/// and it made the parts list useless on exactly the models it was there for.
///
/// The rule is the sketch's attachment, not proximity: a sketch drawn ON A FACE of a body adds to
/// that body, a sketch on a global plane starts a new one. It needs no parameter set either way,
/// and it cannot pick the wrong body, because the answer was decided when the sketch was placed.
///
/// What merging does NOT do is cut the interface — see SketchConsumingFeature.Emit. The tests below
/// therefore assert on total enclosed volume and body count, and deliberately not on manifoldness,
/// because the merged mesh is not manifold along the join and pretending otherwise here would
/// enshrine an expectation the code does not meet.
/// </summary>
public static class MergeTests
{
	public static void Run()
	{
		Report.Section( "merge: extruding off a face builds up one part" );
		TestBossesMergeIn();

		Report.Section( "merge: what starts a new part instead" );
		TestNewBodyCases();

		Report.Section( "merge: the explicit settings" );
		TestExplicitResult();

		Report.Section( "merge: identity survives it" );
		TestIdentity();

		Report.Section( "remove: the cut goes through the boolean seam" );
		TestRemove();

		Report.Section( "remove: the cut tool has to be INSIDE the thing it cuts" );
		TestCutToolOverlapsTarget();
	}

	/// <summary>
	/// The assertion every other Remove test here was missing, and the reason a cut that is wired
	/// correctly end to end still does nothing in the editor.
	///
	/// Everything above stubs the boolean and checks the PLUMBING: that a cut is routed to a
	/// provider, that the result is adopted, that the body keeps its id. The stub returns whatever
	/// it is told to, so no test ever looked at the tool solid it was handed — and the tool solid
	/// is the whole problem.
	///
	/// A sketch on a face takes that FACE'S OUTWARD NORMAL as its plane normal (FacePlane.Capture
	/// reads mesh.FaceNormal directly). An extrude with Flip off travels along that normal. So a
	/// sketch drawn on the top of a block extrudes UP AND AWAY from it — correct for a boss, and
	/// exactly backwards for a cut, which needs to travel down INTO the material.
	///
	/// The two solids then meet on a plane and enclose no common volume, and subtracting something
	/// that isn't there takes nothing away. The engine reports that as a refusal whose text is "they
	/// may not overlap", which reads as an adapter fault and is nothing of the kind.
	/// </summary>
	static void TestCutToolOverlapsTarget()
	{
		var previous = MeshBoolean.Provider;

		try
		{
			var stub = new StubBoolean();
			MeshBoolean.Provider = stub;

			// Exactly the editor's case: block, sketch on its top face, Remove, default direction.
			var studio = CutSetup( out var cut );
			studio.Rebuild();

			Report.Check( "the cut reached the boolean at all", stub.Calls > 0,
				"the provider was never called" );

			var target = Bounds( stub.LastTarget );
			var tool = Bounds( stub.LastTool );

			// The block is 2 tall about the origin, so its top face is at z = +1. A tool that cuts
			// it has to reach BELOW that; one that starts there and climbs is outside the material.
			// Before DirectionSign existed this came out as target -1..1 against tool 1..2.
			var overlap = MathF.Min( target.Max, tool.Max ) - MathF.Max( target.Min, tool.Min );

			Report.Check( "a cut off a face travels into the material, not away from it", overlap > 1e-4f,
				$"target z {target.Min:0.###}..{target.Max:0.###}, tool z {tool.Min:0.###}..{tool.Max:0.###} "
				+ $"- overlap {overlap:0.###}" );

			Report.Check( "and it stops where the distance says", MathF.Abs( tool.Min - 0f ) < 1e-4f,
				$"tool reaches down to {tool.Min:0.###}, expected 0 for a distance of 1 off a face at z=1" );

			// Flip still means what it always meant: the other way from the sensible default. For a
			// cut that is back OUT of the material, which is now the broken direction rather than
			// the default one - and the pre-flight check is what has to catch it.
			cut.Flip.Value = true;
			studio.MarkDirty( 0 );
			var flippedReport = studio.Rebuild();

			Report.Check( "flipping a face cut back out of the material is refused", flippedReport.HasErrors,
				"it built something" );

			Report.Check( "and the refusal says which way it missed and by how much",
				cut.Error is not null && cut.Error.Contains( "does not reach into the part" )
					&& cut.Error.Contains( "Flip" ),
				cut.Error ?? "no error" );
		}
		finally
		{
			MeshBoolean.Provider = previous;
		}
	}

	/// <summary>Z extent of a mesh. The cut in CutSetup travels along Z, so one axis answers it.</summary>
	static (float Min, float Max) Bounds( PolyMesh mesh )
	{
		if ( mesh is null || mesh.VertexCount == 0 )
			return (0f, 0f);

		var min = float.MaxValue;
		var max = float.MinValue;

		foreach ( var p in mesh.Positions )
		{
			min = MathF.Min( min, p.z );
			max = MathF.Max( max, p.z );
		}

		return (min, max);
	}

	/// <summary>
	/// Removing material.
	///
	/// There is no boolean in the kernel and there is not going to be one — the plan of record is
	/// the engine's, installed at startup inside the s&amp;box editor (see MeshBoolean). So what can be
	/// verified here is everything EXCEPT the cut arithmetic: that Remove asks for a subtract, that
	/// it hands over the right two solids the right way round, that the answer replaces the target
	/// without disturbing its identity, and that every way this can go wrong produces something a
	/// user can act on.
	///
	/// A stub provider is what makes that testable. It is not a boolean and does not pretend to be
	/// one — it records what it was asked and returns a mesh it was told to return.
	/// </summary>
	static void TestRemove()
	{
		var previous = MeshBoolean.Provider;

		try
		{
			TestRemoveWithoutProvider();
			TestRemoveThroughProvider();
			TestRemoveFailures();
		}
		finally
		{
			// Restored whatever happens: a provider left installed would leak into every test that
			// runs after this one, and the ones that assert Remove is unavailable would start
			// passing for the wrong reason.
			MeshBoolean.Provider = previous;
		}
	}

	static void TestRemoveWithoutProvider()
	{
		MeshBoolean.Provider = null;

		Report.Check( "with nothing installed, the kernel reports no boolean", !MeshBoolean.Available );

		var studio = CutSetup( out var cut );
		var report = studio.Rebuild();

		Report.Check( "asking for a cut without a provider is an error, not a silent no-op",
			report.HasErrors, "it built something" );

		Report.Check( "and the error names what is missing",
			cut.Error is not null && cut.Error.Contains( "mesh boolean" ), cut.Error ?? "no error" );

		// A host that knows more gets to say more. The editor knows the engine has a boolean and
		// what is needed to reach it; the kernel only knows there is none here.
		MeshBoolean.UnavailableReason = "Run effigy_probe_boolean to dump the engine's API.";

		var hosted = CutSetup( out var hostedCut );
		hosted.Rebuild();

		Report.Check( "and a host can replace that with something actionable",
			hostedCut.Error is not null && hostedCut.Error.Contains( "effigy_probe_boolean" ),
			hostedCut.Error ?? "no error" );

		MeshBoolean.UnavailableReason = null;

		// The part it could not cut is still there. A failed feature must not take the model with
		// it — the geometry above the failure is exactly what you need to see to fix it.
		Report.Check( "the part being cut survives the failure", studio.Bodies.Count == 1,
			$"{studio.Bodies.Count} bodies" );
	}

	static void TestRemoveThroughProvider()
	{
		var stub = new StubBoolean();
		MeshBoolean.Provider = stub;

		var studio = CutSetup( out _ );
		var idBefore = studio.Bodies.Single().Id;

		// What the stub returns stands in for the cut result. A box of a known size means the
		// assertion below is about the result being ADOPTED, not about what a boolean would compute.
		stub.Result = Primitives.Box( 1, 1, 1 );

		var report = studio.Rebuild();

		Report.Check( "the cut builds", !report.HasErrors, report.ToString() );

		Report.Check( "it asked for a subtract", stub.LastOp == BooleanOp.Subtract, $"{stub.LastOp}" );

		Report.Check( "exactly once", stub.Calls == 1, $"{stub.Calls} calls" );

		// THE OPERANDS, THE RIGHT WAY ROUND. Subtract is not commutative, and swapping them is the
		// kind of mistake that produces a plausible-looking solid rather than an error: the target
		// is the part being cut, the tool is the shape of the hole.
		Report.Check( "the part is the target and the extrusion is the tool",
			MathF.Abs( EnclosedVolume( stub.LastTarget ) - 48f ) < 1e-2f
			&& MathF.Abs( EnclosedVolume( stub.LastTool ) - 2f ) < 1e-2f,
			$"target {EnclosedVolume( stub.LastTarget ):0.##} (block is 48), tool {EnclosedVolume( stub.LastTool ):0.##} (cut is 2)" );

		Report.Check( "the result replaces the part rather than adding another",
			studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );

		Report.Check( "and the part keeps its id, so anything built on it still resolves",
			studio.Bodies.Single().Id == idBefore, $"{idBefore} became {studio.Bodies.Single().Id}" );

		Report.Check( "the body now holds what the boolean returned",
			MathF.Abs( EnclosedVolume( studio.Bodies.Single().Mesh ) - 1f ) < 1e-2f,
			$"volume {EnclosedVolume( studio.Bodies.Single().Mesh ):0.####}" );
	}

	static void TestRemoveFailures()
	{
		var stub = new StubBoolean();
		MeshBoolean.Provider = stub;

		// A provider that cannot do it says so, and the reason has to reach the user rather than
		// being swallowed into a generic failure.
		stub.Fail = "the solids do not overlap";

		var studio = CutSetup( out var cut );
		studio.Rebuild();

		Report.Check( "a refused boolean surfaces the provider's own reason",
			cut.Error is not null && cut.Error.Contains( "do not overlap" ), cut.Error ?? "no error" );

		// A provider that throws is a failed boolean, not a failed rebuild.
		stub.Fail = null;
		stub.Throw = true;

		var throwing = CutSetup( out var thrown );
		var throwingReport = throwing.Rebuild();

		Report.Check( "a provider that throws is caught and reported",
			throwingReport.HasErrors && thrown.Error is not null && thrown.Error.Contains( "failed" ),
			thrown.Error ?? "no error" );

		Report.Check( "and the rebuild carries on rather than dying", throwing.Bodies.Count == 1 );

		// An empty result is a real answer to "cut this with something that swallows it", and it is
		// never a useful one — a body with no faces is indistinguishable from a broken feature
		// everywhere downstream, so it is refused where it happens.
		stub.Throw = false;
		stub.Result = new PolyMesh();

		var emptied = CutSetup( out var vanished );
		emptied.Rebuild();

		Report.Check( "a cut that leaves nothing behind explains itself",
			vanished.Error is not null && vanished.Error.Contains( "nothing" ), vanished.Error ?? "no error" );

		// Nothing to cut into at all.
		stub.Result = Primitives.Box( 1, 1, 1 );

		var lonely = new PartStudio();
		lonely.Add( new SketchFeature() ).Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );
		var nothing = lonely.Add( new ExtrudeFeature() );
		nothing.Distance.Value = 1f;
		nothing.Result.Index = 3;

		lonely.Rebuild();

		Report.Check( "removing with no body to remove from says what to do instead",
			nothing.Error is not null && nothing.Error.Contains( "remove from" ), nothing.Error ?? "no error" );

		// Auto must never cut. Adding and removing are indistinguishable from the geometry, so a
		// rule that guessed would eventually guess a hole into someone's part.
		stub.Calls = 0;

		var auto = CutSetup( out var automatic );
		automatic.Result.Index = 0;
		auto.Rebuild();

		Report.Check( "Auto never reaches for the boolean", stub.Calls == 0, $"{stub.Calls} calls" );
	}

	/// <summary>A block with a sketch on its top face set to cut into it: the shape of every Remove
	/// test, so each one differs only in what the provider does.</summary>
	static PartStudio CutSetup( out ExtrudeFeature cut )
	{
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = 6f;
		block.SizeY.Value = 4f;
		block.SizeZ.Value = 2f;
		studio.Rebuild();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio, studio.Bodies[0].Id );
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 1f, 0f ) );

		cut = studio.Add( new ExtrudeFeature() );
		cut.Distance.Value = 1f;
		cut.Result.Index = 3; // Remove

		return studio;
	}

	/// <summary>
	/// Stands in for the engine's boolean. Records what it was asked and returns what it was told
	/// to return — it performs no geometry at all, on purpose, because a half-real boolean in a test
	/// would be a second implementation to be wrong in its own way.
	/// </summary>
	sealed class StubBoolean : IMeshBoolean
	{
		public int Calls;
		public BooleanOp LastOp;
		public PolyMesh LastTarget, LastTool;

		/// <summary>What to hand back. Null means "refuse", via Fail.</summary>
		public PolyMesh Result = Primitives.Box( 1, 1, 1 );

		/// <summary>Set to refuse with this reason.</summary>
		public string Fail;

		/// <summary>Set to throw instead, which is what a misbehaving engine call looks like.</summary>
		public bool Throw;

		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			Calls++;
			LastOp = op;
			LastTarget = target;
			LastTool = tool;

			if ( Throw )
				throw new InvalidOperationException( "the engine said no" );

			if ( Fail is not null )
			{
				result = null;
				error = Fail;
				return false;
			}

			result = Result;
			error = null;
			return true;
		}
	}

	static void TestBossesMergeIn()
	{
		// A 6x4x2 block, then three separate 1x1x1 bosses off its top face. One part, and its
		// volume is the block plus all three: 48 + 3.
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = 6f;
		block.SizeY.Value = 4f;
		block.SizeZ.Value = 2f;
		studio.Rebuild();

		var blockId = studio.Bodies.Single().Id;

		// Captured ONCE, before any boss exists, and reused — which is also what actually happens
		// when someone draws three sketches on the same face. Re-capturing inside the loop finds the
		// highest +Z face instead, which after the first boss is that boss's own top, and the three
		// quietly stack into a tower. The reference resolving to the right face while a boss stands
		// on it is half of what is being tested.
		var blockTop = TopFaceOf( studio, blockId );

		for ( var i = 0; i < 3; i++ )
		{
			var sketch = studio.Add( new SketchFeature() );
			sketch.Face = blockTop;

			var x = -2f + i * 2f;
			sketch.Sketch.AddRectangle( new Vec2( x, -0.5f ), new Vec2( x + 1f, 0.5f ) );

			studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
			studio.Rebuild();
		}

		var report = studio.Rebuild();

		Report.Check( "it builds", !report.HasErrors, report.ToString() );

		Report.Check( "three bosses off the same block leave ONE part, not four",
			studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );

		var volume = EnclosedVolume( studio.Bodies.Single().Mesh );

		Report.Check( "and that part measures the block plus all three bosses",
			MathF.Abs( volume - 51f ) < 1e-2f, $"enclosed volume {volume:0.####}, expected 48 + 3" );

		// Volume alone would pass with a boss merged in at the wrong height, so check the part now
		// reaches exactly one unit above the block's top face.
		var top = studio.Bodies.Single().Mesh.Positions.Max( p => p.z );

		Report.Check( "the part stands a unit proud of the block's top face",
			MathF.Abs( top - 2f ) < 1e-3f, $"top at {top}, block top is 1" );

		// A boss on a boss: the second sketch attaches to a face of the merged body, which by then
		// is the same body id it always was. This is the case that breaks if merging invalidates
		// the face references built on it.
		var stacked = studio.Add( new SketchFeature() );
		stacked.Face = TopFaceOf( studio, blockId );
		stacked.Sketch.AddRectangle( new Vec2( -0.25f, -0.25f ), new Vec2( 0.25f, 0.25f ) );
		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;

		var stackedReport = studio.Rebuild();

		Report.Check( "building on top of what was already merged still works",
			!stackedReport.HasErrors, stackedReport.ToString() );

		Report.Check( "and is still one part", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );
	}

	static void TestNewBodyCases()
	{
		// A sketch on a global plane is not attached to anything, so it starts its own part even
		// with a body already in the studio. "Until a new sketch is extruded off the mass" — this is
		// the other half of that.
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = block.SizeY.Value = block.SizeZ.Value = 2f;

		var loose = studio.Add( new SketchFeature() );
		loose.PlaneOffset.Value = 10f;
		loose.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;

		var report = studio.Rebuild();

		Report.Check( "a sketch on a global plane starts its own part",
			!report.HasErrors && studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );

		// And moving a sketch back off a face must stop it merging. The attachment is republished
		// on every rebuild, so a stale one would keep merging into a body it no longer touches.
		var studio2 = new PartStudio();
		var host = studio2.Add( new PrimitiveFeature() );
		host.SizeX.Value = host.SizeY.Value = host.SizeZ.Value = 2f;
		studio2.Rebuild();

		var attached = studio2.Add( new SketchFeature() );
		attached.Face = TopFaceOf( studio2, studio2.Bodies[0].Id );
		attached.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );
		studio2.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio2.Rebuild();

		Report.Check( "attached, it merges", studio2.Bodies.Count == 1, $"{studio2.Bodies.Count} bodies" );

		attached.Face = null;
		attached.PlaneOffset.Value = 5f;
		studio2.MarkDirty( attached );
		studio2.Rebuild();

		Report.Check( "moved back onto a plane, it stops merging",
			studio2.Bodies.Count == 2, $"{studio2.Bodies.Count} bodies" );
	}

	static void TestExplicitResult()
	{
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = block.SizeY.Value = block.SizeZ.Value = 2f;
		studio.Rebuild();

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio, studio.Bodies[0].Id );
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );

		var boss = studio.Add( new ExtrudeFeature() );
		boss.Distance.Value = 1f;
		boss.Result.Index = 1; // New body

		studio.Rebuild();

		Report.Check( "New body overrides the attachment and keeps them apart",
			studio.Bodies.Count == 2, $"{studio.Bodies.Count} bodies" );

		boss.Result.Index = 0; // back to Auto
		studio.MarkDirty( boss );
		studio.Rebuild();

		Report.Check( "and Auto merges it again", studio.Bodies.Count == 1,
			$"{studio.Bodies.Count} bodies" );

		// Explicit Add with a sketch on a global plane: one body in the studio is unambiguous, so
		// it is used. This is the "sketch over the top of the only part" case.
		var single = new PartStudio();
		var only = single.Add( new PrimitiveFeature() );
		only.SizeX.Value = only.SizeY.Value = only.SizeZ.Value = 2f;

		var overSketch = single.Add( new SketchFeature() );
		overSketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		var adding = single.Add( new ExtrudeFeature() );
		adding.Distance.Value = 3f;
		adding.Result.Index = 2; // Add

		var singleReport = single.Rebuild();

		Report.Check( "Add with one body in the studio uses that body",
			!singleReport.HasErrors && single.Bodies.Count == 1, singleReport.ToString() );

		// Two bodies and no attachment: there is no way to tell which was meant, and guessing is
		// how a boss silently lands on the wrong part. It has to say so.
		var ambiguous = new PartStudio();
		var a = ambiguous.Add( new PrimitiveFeature() );
		a.SizeX.Value = a.SizeY.Value = a.SizeZ.Value = 2f;
		var b = ambiguous.Add( new PrimitiveFeature() );
		b.SizeX.Value = b.SizeY.Value = b.SizeZ.Value = 1f;
		b.Position.Value = new Vec3( 8f, 0f, 0f );

		ambiguous.Add( new SketchFeature() ).Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 1, 1 ) );

		var guessing = ambiguous.Add( new ExtrudeFeature() );
		guessing.Distance.Value = 1f;
		guessing.Result.Index = 2; // Add

		var ambiguousReport = ambiguous.Rebuild();

		Report.Check( "Add with two bodies and no attachment refuses rather than guessing",
			ambiguousReport.HasErrors, "it picked one" );

		Report.Check( "and the error says what to do about it",
			guessing.Error is not null && guessing.Error.Contains( "which" ),
			guessing.Error ?? "no error" );
	}

	static void TestIdentity()
	{
		// Merging must not change the host body's id. Everything built on that body — every face
		// reference, every body selection — is holding the id, and a merge that renamed it would
		// break all of them at once, which is the exact failure feature-derived ids were introduced
		// to stop.
		var studio = new PartStudio();

		var block = studio.Add( new PrimitiveFeature() );
		block.SizeX.Value = 4f;
		block.SizeY.Value = 4f;
		block.SizeZ.Value = 2f;
		studio.Rebuild();

		var idBefore = studio.Bodies.Single().Id;
		var featureBefore = studio.Bodies.Single().FeatureId;

		var sketch = studio.Add( new SketchFeature() );
		sketch.Face = TopFaceOf( studio, idBefore );
		sketch.Sketch.AddRectangle( new Vec2( -0.5f, -0.5f ), new Vec2( 0.5f, 0.5f ) );
		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Rebuild();

		var merged = studio.Bodies.Single();

		Report.Check( "the merged part keeps the host's id", merged.Id == idBefore,
			$"{idBefore} became {merged.Id}" );

		Report.Check( "and still names the feature that first made it",
			merged.FeatureId == featureBefore, $"{featureBefore} became {merged.FeatureId}" );

		// A body selection made before the merge still matches afterwards, which is the practical
		// consequence of the id holding.
		var selection = new BodySelectionParam( "Bodies" );
		selection.BodyIds.Add( idBefore );

		Report.Check( "so a selection made before the merge still matches the part",
			selection.Matches( merged ) );

		// Rebuilding twice must not merge twice. Bodies are rebuilt from scratch each time, but a
		// merge that appended into a cached mesh rather than a fresh one would double the volume on
		// every rebuild — and would look completely normal in the viewport.
		var first = EnclosedVolume( studio.Bodies.Single().Mesh );

		studio.MarkDirty( 0 );
		studio.Rebuild();
		studio.MarkDirty( 0 );
		studio.Rebuild();

		var third = EnclosedVolume( studio.Bodies.Single().Mesh );

		Report.Check( "rebuilding repeatedly does not merge the same boss again and again",
			MathF.Abs( third - first ) < 1e-3f, $"{first:0.####} became {third:0.####}" );

		// Same check on the incremental path: edit the LAST feature so everything above it is
		// restored from the snapshot cache rather than re-run.
		var boss = (ExtrudeFeature)studio.Features.Last();
		boss.Distance.Value = 2f;
		studio.MarkDirty( boss );
		studio.Rebuild();

		var taller = EnclosedVolume( studio.Bodies.Single().Mesh );

		Report.Check( "and an incremental rebuild resumes with the attachment intact",
			studio.Bodies.Count == 1 && MathF.Abs( taller - 34f ) < 1e-2f,
			$"{studio.Bodies.Count} bodies, volume {taller:0.####}, expected 32 + 2" );
	}

	// --- helpers ------------------------------------------------------------------------------

	static FaceRef TopFaceOf( PartStudio studio, string bodyId )
	{
		var body = studio.Bodies.First( b => b.Id == bodyId );
		var mesh = body.Mesh;

		var top = mesh.Faces
			.Select( f => (Face: f, Normal: mesh.FaceNormal( f ), Centroid: mesh.FaceCentroid( f )) )
			.Where( t => t.Normal.z > 0.99f )
			.OrderByDescending( t => t.Centroid.z )
			.First();

		return FacePlane.Capture( body, mesh.Faces.IndexOf( top.Face ), top.Centroid );
	}

	static float EnclosedVolume( PolyMesh mesh )
	{
		var acc = 0f;

		foreach ( var f in mesh.Faces )
			acc += Vec3.Dot( mesh.FaceCentroid( f ), mesh.FaceNormal( f ) ) * mesh.FaceArea( f );

		return acc / 3f;
	}
}
