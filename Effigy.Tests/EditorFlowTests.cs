using System;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// The editor's workflows, replayed headlessly against the real kernel.
///
/// WHY THIS EXISTS. The editor cannot be compiled outside s&amp;box, so its logic used to be verified
/// by reading it — and reading it missed a bug that made every parameter edit a no-op, which in
/// turn looked like "sketches only work on the Top plane" and "the extrude controls do nothing".
/// Everything the editor does to the studio is ordinary kernel calls in a particular ORDER, and
/// that order is exactly what was wrong. This replays those sequences so the next one is caught
/// here rather than by hand at the far end of a compile.
///
/// These are not UI tests. They assert nothing about widgets. They assert that the sequence of
/// kernel calls an editor action performs produces the geometry the user was promised.
/// </summary>
public static class EditorFlowTests
{
	public static void Run()
	{
		Report.Section( "editor flow: sketch on a plane, then extrude it" );
		TestSketchThenExtrudeOnEveryPlane();

		Report.Section( "editor flow: changing a plane after the fact moves the solid" );
		TestChangingPlaneMovesTheSolid();

		Report.Section( "editor flow: undo restores parameter VALUES, not just the feature list" );
		TestUndoRestoresValues();

		Report.Section( "editor flow: the rollback bar" );
		TestRollback();

		Report.Section( "editor flow: the feature tree as a parametric tree" );
		TestTreeOperations();

		Report.Section( "editor flow: a messy sketch degrades instead of blocking" );
		TestMessySketchStillBuilds();

		Report.Section( "editor flow: sculpting a feature that is not the last one" );
		TestSculptModeSurvivesTheRollbackDance();

		Report.Section( "editor flow: a revolve that works on the first press" );
		TestTheTypedAxisStillRefusesAndSaysSo();
		TestAnEdgeAxisBuildsTheProfileAsDrawn();
		TestOldDocumentsKeepTheAxisTheyWereSavedWith();

		Report.Section( "editor flow: a new extrude waits to be pointed at a sketch" );
		TestANewExtrudeBuildsNothingUntilItIsPicked();
		TestPickingTheSketchBuildsTheSolid();
		TestAnUnsetReferenceStillMeansTheMostRecentSketch();

		Report.Section( "editor flow: a picked face or part seeds the next tool" );
		TestAPickedFaceSeedsASketchAndADraft();
		TestAPickedPartSeedsAFillet();
		TestAPickedFaceOpensAShell();
		TestAPickedFaceSeedsAFilletWithItsEdges();
		TestAPickedEdgeSeedsAFillet();
		TestAnEdgeRefSurvivesATallerBox();
		TestAPickedSketchSeedsAnExtrude();
		TestAPickedSketchRegionSeedsAnExtrude();
	}

	/// <summary>A sketch with a square in it and an extrude below it, in whatever state of
	/// pickedness the caller wants the extrude to be in.</summary>
	static (PartStudio Studio, SketchFeature Sketch, ExtrudeFeature Extrude) SquareAndExtrude( string sketchId )
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0f, 0f ), new Vec2( 2f, 2f ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.SketchFeatureId = sketchId;

		studio.Rebuild();

		return (studio, sketch, extrude);
	}

	/// <summary>
	/// THE POINT OF THE CHANGE. Pressing Extrude used to put a solid on screen before anyone had
	/// said which profile they meant - the kernel reads an unset reference as "the most recent
	/// sketch", so the default distance was applied to whatever was nearest. A feature the toolbar
	/// has just made now arrives awaiting its pick and builds nothing until it gets one.
	/// </summary>
	static void TestANewExtrudeBuildsNothingUntilItIsPicked()
	{
		var (studio, _, extrude) = SquareAndExtrude( SketchConsumingFeature.AwaitingPick );

		Report.Check( "a brand new extrude builds nothing", studio.Bodies.Count == 0,
			$"{studio.Bodies.Count} bodies" );
		Report.Check( "and does not do it silently", extrude.Error is not null, "no diagnostic" );
		Report.Check( "the way out is to point at a sketch",
			extrude.Diagnostic?.Remedies.Exists( r => r.Contains( "Click a sketch" ) ) == true,
			extrude.Diagnostic is null ? "no diagnostic" : string.Join( "; ", extrude.Diagnostic.Remedies ) );

		// Nothing has consumed the sketch, which is what keeps it on screen and pickable - the only
		// way to answer the question the extrude is asking.
		Report.Check( "and it has consumed nothing", studio.ConsumedSketchIds().Count == 0 );

		var reloaded = StudioDocument.Read( StudioDocument.Write( studio ) )
			.Features.OfType<ExtrudeFeature>().Single();

		Report.Check( "a document saved mid-pick comes back still waiting", reloaded.IsAwaitingPick,
			reloaded.SketchFeatureId );
	}

	/// <summary>What the viewport pick does: write the sketch's id into the reference and rebuild.</summary>
	static void TestPickingTheSketchBuildsTheSolid()
	{
		var (studio, sketch, extrude) = SquareAndExtrude( SketchConsumingFeature.AwaitingPick );

		extrude.SketchFeatureId = sketch.Id;
		studio.MarkDirty( extrude );
		studio.Rebuild();

		Report.Check( "picking the sketch builds it", extrude.Error is null, extrude.Error ?? "" );
		Report.Check( "into one solid", studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount > 0,
			$"{studio.Bodies.Count} bodies" );
		Report.Check( "and the sketch now counts as consumed",
			studio.ConsumedSketchIds().Contains( sketch.Id ) );
	}

	/// <summary>
	/// The empty reference still means "the most recent sketch", and it has to: it is what every
	/// extrude built in code does, and what every document saved before the awaiting-pick state
	/// existed comes back as. Waiting is a state the EDITOR puts a new feature into, not a new
	/// meaning for unset.
	/// </summary>
	static void TestAnUnsetReferenceStillMeansTheMostRecentSketch()
	{
		var (studio, sketch, extrude) = SquareAndExtrude( "" );

		Report.Check( "an unset reference still builds from the last sketch", extrude.Error is null,
			extrude.Error ?? "" );
		Report.Check( "and produces a solid", studio.Bodies.Count == 1, $"{studio.Bodies.Count} bodies" );
		Report.Check( "which the studio reports as consuming that sketch",
			studio.ResolveSketchFeatureId( extrude ) == sketch.Id );
	}

	/// <summary>A sketch with a rectangle straddling the origin - what somebody actually draws.</summary>
	static (PartStudio Studio, RevolveFeature Revolve) Lathe( int axisMode )
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		// Straddling BOTH axes on purpose. The typed default runs along X through the origin, so a
		// profile only has to cross y = 0 to defeat it - which a rectangle drawn around the origin
		// does, and which is what people draw.
		sketch.Sketch.AddRectangle( new Vec2( -1f, -1f ), new Vec2( 2f, 3f ) );

		var revolve = studio.Add( new RevolveFeature() );
		revolve.AxisMode.Index = axisMode;

		studio.Rebuild();

		return (studio, revolve);
	}

	/// <summary>
	/// The typed axis is still the kernel's default, and it still refuses a profile drawn across it.
	///
	/// That refusal is CORRECT - each half sweeps the same solid - and it is not the thing that was
	/// wrong. What was wrong is that it was the only thing a fresh Revolve could do.
	/// </summary>
	static void TestTheTypedAxisStillRefusesAndSaysSo()
	{
		var (_, revolve) = Lathe( RevolveFeature.AxisCustom );

		Report.Check( "a profile drawn across the typed axis is still refused", revolve.Error is not null,
			"it built" );
		Report.Check( "and the refusal offers the dropdown as the way out",
			revolve.Diagnostic is not null
			&& revolve.Diagnostic.Remedies.Exists( r => r.Contains( "Axis" ) || r.Contains( "axis" ) ),
			revolve.Diagnostic is null ? "no diagnostic" : string.Join( "; ", revolve.Diagnostic.Remedies ) );
	}

	/// <summary>
	/// THE POINT OF THE CHANGE. The same sketch, on the mode the editor creates a revolve with,
	/// builds - because an axis tangent to the profile is one the profile cannot straddle.
	/// </summary>
	static void TestAnEdgeAxisBuildsTheProfileAsDrawn()
	{
		var (studio, revolve) = Lathe( RevolveFeature.AxisProfileLeftEdge );

		Report.Check( "the same sketch spun about its own left edge builds", revolve.Error is null,
			revolve.Error ?? "" );
		Report.Check( "and produces a solid", studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount > 0,
			$"{studio.Bodies.Count} bodies" );

		// A ring, by Pappus: the rectangle spans x -1..2 and y -1..3, so it is 3 by 4 with its
		// centroid at x = 0.5, which is 1.5 from the left edge it is being spun about.
		var volume = MathF.Abs( studio.Bodies[0].Mesh.SignedVolume() );
		var pappus = 12f * 2f * MathF.PI * 1.5f;

		Report.Check( "with the volume Pappus predicts, to within the faceting",
			MathF.Abs( volume - pappus ) < pappus * 0.02f,
			$"{volume:0.###}, expected about {pappus:0.###}" );

		// Every edge mode has to be legal on the same profile - that is what "tangent" buys.
		for ( var mode = RevolveFeature.AxisProfileLeftEdge; mode <= 4; mode++ )
		{
			var (_, each) = Lathe( mode );

			Report.Check( $"axis mode {mode} builds too", each.Error is null, each.Error ?? "" );
		}
	}

	/// <summary>
	/// A ChoiceParam serialises its INDEX, and a document saved before this dropdown existed has no
	/// line for it — so it loads on index 0. If index 0 were an edge mode, every revolve in every
	/// saved file would quietly move to a different axis on the next open, and the model would come
	/// back a different shape with nothing to say it had changed.
	/// </summary>
	static void TestOldDocumentsKeepTheAxisTheyWereSavedWith()
	{
		Report.Check( "index 0 is the typed axis", RevolveFeature.AxisCustom == 0 );

		// A document from before the parameter existed: no AxisMode line at all.
		var document = "effigy 1\n"
			+ "feature SketchFeature\n\tid sk\nend\n"
			+ "feature RevolveFeature\n\tid rv\n\tparam AxisPoint 2 0 0\n"
			+ "\tparam AxisDirection 0 1 0\n\tparam Angle 360\nend\n";

		var loaded = StudioDocument.Read( document );
		var revolve = loaded.Features.OfType<RevolveFeature>().Single();

		Report.Check( "an old document loads on the typed axis",
			revolve.AxisMode.Index == RevolveFeature.AxisCustom, $"index {revolve.AxisMode.Index}" );
		Report.Check( "and keeps the axis it was saved with",
			MathF.Abs( revolve.AxisPoint.Value.x - 2f ) < 1e-4f
			&& MathF.Abs( revolve.AxisDirection.Value.y - 1f ) < 1e-4f,
			$"{revolve.AxisPoint.Value.x}, {revolve.AxisDirection.Value.y}" );
	}

	/// <summary>
	/// What EffigyWindow actually does around a sculpt, replayed without any widgets.
	///
	/// ENTERING SCULPT MODE MOVES THE ROLLBACK BAR. EditFeature rolls to just after the feature so
	/// the cage is what you see rather than whatever is stacked on top of it, and finishing puts the
	/// bar back. That is three pieces of state changing around a fourth - the deltas - and the order
	/// is exactly the kind of thing that reads as correct and is not: roll back, sculpt, roll
	/// forward, and the feature above has to come back carrying the sculpt underneath it.
	///
	/// This is the sequence, not the widgets. If it passes, what is left in the editor is a strip of
	/// buttons calling these methods in this order.
	/// </summary>
	static void TestSculptModeSurvivesTheRollbackDance()
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 2f;
		box.SizeY.Value = 2f;
		box.SizeZ.Value = 2f;

		var sculpt = studio.Add( new SculptFeature() );

		// Something ABOVE the sculpt, so the rollback actually hides one feature and putting the bar
		// back has something to restore.
		var mirror = studio.Add( new MirrorFeature() );

		studio.Rebuild();

		var bodiesWithMirror = studio.Bodies.Count;

		Report.Check( "the fixture builds with the mirror on top", bodiesWithMirror > 1,
			$"{bodiesWithMirror} bodies" );

		// --- EnterSculpt: roll to just after the sculpt, and rebuild ------------------------------
		var restoreTo = studio.RollbackIndex;
		studio.RollbackIndex = studio.Features.IndexOf( sculpt ) + 1;
		studio.Rebuild();

		Report.Check( "rolling to the sculpt hides the feature above it", studio.Bodies.Count < bodiesWithMirror,
			$"{studio.Bodies.Count} bodies" );
		Report.Check( "and the sculpt has a cage to work on", sculpt.Sculpt is not null );

		// --- the session, exactly as the viewport drives it ---------------------------------------
		var session = new SculptSession( sculpt.Sculpt );
		session.Radius = session.SuggestedRadius;
		session.Sculpt.AddLevel();
		session.Level = session.Sculpt.TopLevel;

		var started = session.BeginStroke( new Vec3( 0, 0, 5 ), new Vec3( 0, 0, -1 ) );
		session.MoveTo( new Vec3( 0.3f, 0, 5 ), new Vec3( 0, 0, -1 ) );
		var edit = session.EndStroke();

		Report.Check( "a stroke lands on the cage from above", started && edit is not null,
			started ? "nothing committed" : "the press missed" );

		// --- FinishSculpt: mark dirty, put the bar back, rebuild ----------------------------------
		studio.MarkDirty( sculpt );
		studio.RollbackIndex = restoreTo;

		var report = studio.Rebuild();

		Report.Check( "the model builds again with the bar back", !report.HasErrors, report.ToString() );
		Report.Check( "the feature above the sculpt is back", studio.Bodies.Count == bodiesWithMirror,
			$"{studio.Bodies.Count} bodies, expected {bodiesWithMirror}" );
		Report.Check( "and the sculpt survived the round trip", sculpt.Sculpt.HasDetail( sculpt.Sculpt.TopLevel ) );

		// The mirror copies the sculpted body, so the sculpt has to be in what it copied - the check
		// that the bar going back did not quietly rebuild the cage from scratch underneath it.
		var vertices = 0;

		foreach ( var body in studio.Bodies )
			vertices += body.Mesh.VertexCount;

		Report.Check( "the sculpted level is what everything above it was built from",
			vertices > bodiesWithMirror * sculpt.Sculpt.Cage.VertexCount,
			$"{vertices} vertices across {studio.Bodies.Count} bodies" );
	}

	/// <summary>
	/// A stray line left in a sketch used to fail EVERY feature that read it, with no way forward
	/// but to hunt the stray down - and no indication of where it was beyond a point index.
	///
	/// Now the good regions build and the feature carries a warning saying what it skipped. The
	/// opposite mistake matters too: it must NOT silently ignore the stray, because extruding one
	/// arbitrary sub-loop and looking like it worked is worse than refusing.
	/// </summary>
	static void TestMessySketchStillBuilds()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );

		// A clean rectangle...
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		// ...plus a stray line spliced onto one of its corners. Branching is handled now, so this is
		// no longer about the corner joining three curves — it is that the line's far end is loose,
		// so the line encloses nothing and gets pruned. What must not happen is it being pruned
		// SILENTLY.
		var corner = sketch.Sketch.Points
			.Select( ( p, i ) => (p, i) )
			.First( t => MathF.Abs( t.p.x ) < 1e-6f && MathF.Abs( t.p.y ) < 1e-6f ).i;

		var stray = sketch.Sketch.AddPoint( new Vec2( -3, -3 ) );
		sketch.Sketch.Add( new SketchLine( corner, stray ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;
		var report = EditAndRebuild( studio, extrude );

		Check( "ProfileFinder reports the dangling line rather than dropping it",
			ProfileFinder.Find( sketch.Sketch ).Warnings.Count > 0 );

		Check( "the extrude still builds a body rather than failing outright",
			!report.HasErrors && studio.Bodies.Count >= 1,
			$"{studio.Bodies.Count} bodies, {report}" );

		Check( "and it says what it ignored",
			report.HasWarnings && extrude.Warning is not null
			&& extrude.Warning.Contains( "ignored" ), extrude.Warning ?? "no warning" );

		// A sketch with NOTHING closed is still a hard error - there is no geometry to show.
		var empty = new PartStudio();
		var emptySketch = empty.Add( new SketchFeature() );
		var a = emptySketch.Sketch.AddPoint( new Vec2( 0, 0 ) );
		var b = emptySketch.Sketch.AddPoint( new Vec2( 1, 0 ) );
		emptySketch.Sketch.Add( new SketchLine( a, b ) );

		empty.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var emptyReport = empty.Rebuild();

		Check( "a sketch with no closed region is still an error, not a warning",
			emptyReport.HasErrors && empty.Bodies.Count == 0, emptyReport.ToString() );
	}

	// --- what the editor does, named the way the editor names it ---------------------------

	/// <summary>
	/// The editor's OnFeatureEdited: mark the edited feature dirty, then rebuild.
	///
	/// The mark is the whole thing. PartStudio re-runs only from the first dirty feature and
	/// Rebuild() clears the mark on its way out, so a rebuild with nothing marked reuses the entire
	/// cache and re-executes nothing at all.
	/// </summary>
	static RebuildReport EditAndRebuild( PartStudio studio, Feature edited )
	{
		studio.MarkDirty( edited );
		return studio.Rebuild();
	}

	static (Vec3 Min, Vec3 Max) Bounds( PolyMesh mesh )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return (min, max);
	}

	// --- the flows --------------------------------------------------------------------------

	/// <summary>
	/// Press Sketch, pick a plane, draw a rectangle, finish, press Extrude, type a distance.
	/// Run for all three planes, because "it only works on Top" was the reported symptom.
	/// </summary>
	static void TestSketchThenExtrudeOnEveryPlane()
	{
		var planes = new[] { ("Top (XY)", 0), ("Front (XZ)", 1), ("Right (YZ)", 2) };

		foreach ( var (name, index) in planes )
		{
			var studio = new PartStudio();

			// Press Sketch. The feature is added before a plane is chosen, exactly as the toolbar
			// button does it.
			var sketch = studio.Add( new SketchFeature() );
			studio.Rebuild();

			// Pick a plane in the viewport. This is the edit that was being discarded.
			sketch.Plane.Index = index;
			EditAndRebuild( studio, sketch );

			var expected = index switch { 0 => SketchPlane.XY, 1 => SketchPlane.XZ, _ => SketchPlane.YZ };

			Check( $"{name}: the sketch actually moved to the chosen plane",
				sketch.Sketch.Plane.Normal.AlmostEquals( expected.Normal ),
				$"normal {sketch.Sketch.Plane.Normal}, expected {expected.Normal}" );

			// Draw a 2x3 rectangle, then press Extrude and type 4.
			sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 3 ) );
			EditAndRebuild( studio, sketch );

			var extrude = studio.Add( new ExtrudeFeature() );
			extrude.Distance.Value = 4f;
			var report = EditAndRebuild( studio, extrude );

			Check( $"{name}: it builds one body with no errors",
				!report.HasErrors && studio.Bodies.Count == 1,
				$"{studio.Bodies.Count} bodies, {report}" );

			if ( studio.Bodies.Count != 1 )
				continue;

			var (min, max) = Bounds( studio.Bodies[0].Mesh );
			var span = max - min;

			Check( $"{name}: it grew 4 along that plane's normal",
				MathF.Abs( MathF.Abs( Vec3.Dot( span, expected.Normal ) ) - 4f ) < 1e-3f,
				$"got {MathF.Abs( Vec3.Dot( span, expected.Normal ) )}" );
		}
	}

	/// <summary>
	/// The exact thing that looked broken: build on Top, then switch the sketch to Front and check
	/// the SOLID follows. Everything downstream has to re-run, not just the sketch.
	/// </summary>
	static void TestChangingPlaneMovesTheSolid()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 3 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 4f;
		studio.Rebuild();

		var onTop = Bounds( studio.Bodies[0].Mesh );

		Check( "starts out growing along +Z",
			MathF.Abs( onTop.Max.z - onTop.Min.z - 4f ) < 1e-3f, $"{onTop.Max.z - onTop.Min.z}" );

		// Switch the sketch to Front. The extrude is DOWNSTREAM and must rebuild too.
		sketch.Plane.Index = 1;
		EditAndRebuild( studio, sketch );

		var onFront = Bounds( studio.Bodies[0].Mesh );

		Check( "after switching to Front, the solid grows along Y instead",
			MathF.Abs( onFront.Max.y - onFront.Min.y - 4f ) < 1e-3f, $"{onFront.Max.y - onFront.Min.y}" );

		Check( "and it is no longer 4 deep in Z",
			MathF.Abs( onFront.Max.z - onFront.Min.z - 3f ) < 1e-3f,
			$"{onFront.Max.z - onFront.Min.z}, expected the profile's own 3" );
	}

	/// <summary>
	/// Undo has to put NUMBERS back. Snapshotting the feature list alone keeps the same Feature
	/// objects, so it restores membership and order while every edited value survives untouched -
	/// which is undo appearing to do nothing at all.
	/// </summary>
	static void TestUndoRestoresValues()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		var extrude = studio.Add( new ExtrudeFeature() );
		extrude.Distance.Value = 1f;
		studio.Rebuild();

		// The editor's RecordUndo: capture every parameter's value, keyed by the parameter object.
		var snapshot = studio.Features
			.SelectMany( f => f.Parameters )
			.OfType<FloatParam>()
			.ToDictionary( p => p, p => p.Value );

		var listOnly = studio.Features.ToList();   // the old, broken snapshot

		extrude.Distance.Value = 9f;
		EditAndRebuild( studio, extrude );

		Check( "the edit took effect", MathF.Abs( extrude.Distance.Value - 9f ) < 1e-6f );

		// Restoring the LIST alone changes nothing, because the objects in it are the same ones.
		studio.Features = listOnly.ToList();
		studio.MarkAllDirty();
		studio.Rebuild();

		Check( "restoring only the feature list leaves the edited value behind",
			MathF.Abs( extrude.Distance.Value - 9f ) < 1e-6f, $"{extrude.Distance.Value}" );

		// Restoring values is what actually undoes it.
		foreach ( var (param, value) in snapshot )
			param.Value = value;

		studio.MarkAllDirty();
		studio.Rebuild();

		Check( "restoring parameter values undoes the edit",
			MathF.Abs( extrude.Distance.Value - 1f ) < 1e-6f, $"{extrude.Distance.Value}" );
	}

	/// <summary>Roll to here / roll to end, which is one int on the studio.</summary>
	static void TestRollback()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		studio.Add( new SubdivideFeature() ).Levels.Value = 1;
		studio.Rebuild();

		var full = studio.Bodies[0].Mesh.FaceCount;

		// Roll to the Subdivide: everything from it down stops being evaluated.
		studio.RollbackIndex = 2;
		studio.Rebuild();

		var rolled = studio.Bodies[0].Mesh.FaceCount;

		Check( "rolling back past Subdivide leaves the un-subdivided body",
			rolled < full, $"{rolled} faces rolled back vs {full} at the end" );

		// And rolling forward puts it back exactly.
		studio.RollbackIndex = int.MaxValue;
		studio.Rebuild();

		Check( "rolling to the end restores the full result",
			studio.Bodies[0].Mesh.FaceCount == full, $"{studio.Bodies[0].Mesh.FaceCount} vs {full}" );
	}

	/// <summary>
	/// Reorder, suppress, delete, rename — the operations that make a feature list a parametric
	/// history rather than a list of labels. The reported complaint was that tree entries did not
	/// seem to be holding their data; these check the kernel side of every one of them.
	/// </summary>
	static void TestTreeOperations()
	{
		// --- suppress -------------------------------------------------------------------------
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( 2, 2 ) );

		studio.Add( new ExtrudeFeature() ).Distance.Value = 1f;
		var subdivide = studio.Add( new SubdivideFeature() );
		subdivide.Levels.Value = 1;
		studio.Rebuild();

		var subdivided = studio.Bodies[0].Mesh.FaceCount;

		subdivide.Suppressed = true;
		EditAndRebuild( studio, subdivide );

		var suppressed = studio.Bodies[0].Mesh.FaceCount;

		Check( "suppressing a feature takes its effect out", suppressed < subdivided,
			$"{suppressed} vs {subdivided}" );

		subdivide.Suppressed = false;
		EditAndRebuild( studio, subdivide );

		Check( "un-suppressing puts it back exactly",
			studio.Bodies[0].Mesh.FaceCount == subdivided,
			$"{studio.Bodies[0].Mesh.FaceCount} vs {subdivided}" );

		// --- rename ---------------------------------------------------------------------------
		// Bodies take the name of the feature that made them, so a rename has to survive a rebuild.
		var extrude = studio.Features.OfType<ExtrudeFeature>().First();
		extrude.Name = "Boss";
		EditAndRebuild( studio, extrude );

		Check( "a renamed feature names the body it produces",
			studio.Bodies[0].Name == "Boss", studio.Bodies[0].Name );

		// --- reorder --------------------------------------------------------------------------
		// Subdivide BEFORE Extrude is a different model: it would subdivide nothing, then extrude.
		// Whatever it produces, the point is that reordering re-runs and changes the result.
		var before = studio.Bodies[0].Mesh.FaceCount;
		var subIndex = studio.Features.IndexOf( subdivide );
		var extIndex = studio.Features.IndexOf( extrude );

		studio.Move( subIndex, extIndex );
		var report = studio.Rebuild();

		Check( "reordering re-runs the tree rather than reusing the old cache",
			studio.Bodies.Count == 0 || studio.Bodies[0].Mesh.FaceCount != before || report.HasErrors,
			$"{studio.Bodies.Count} bodies, {studio.Bodies.FirstOrDefault()?.Mesh.FaceCount} faces, {report}" );

		// Put it back and confirm the original result returns - reorder has to be reversible or
		// dragging in the tree is a one-way trip.
		studio.Move( studio.Features.IndexOf( subdivide ), studio.Features.Count - 1 );
		studio.Rebuild();

		Check( "moving it back restores the original result",
			studio.Bodies.Count == 1 && studio.Bodies[0].Mesh.FaceCount == before,
			$"{studio.Bodies.Count} bodies, {studio.Bodies.FirstOrDefault()?.Mesh.FaceCount} vs {before}" );

		// --- delete ---------------------------------------------------------------------------
		// Deleting the sketch has to break the extrude that consumes it, loudly.
		studio.Remove( sketch );
		var afterDelete = studio.Rebuild();

		Check( "deleting an upstream sketch makes its consumer report an error",
			afterDelete.HasErrors, afterDelete.ToString() );
	}

	/// <summary>What the toolbar does after a face is already selected: the new feature arrives
	/// already pointed at that face, so Draft/Hole/Sketch do not open asking you to pick it again.
	/// </summary>
	static void TestAPickedFaceSeedsASketchAndADraft()
	{
		var (studio, face, _) = BoxAndTopFace();

		var sketch = new SketchFeature();
		sketch.ApplyGeometrySelection( new[] { face }, Array.Empty<string>(), studio.Bodies );

		Check( "a sketch takes the picked face as its plane",
			sketch.Face is { } attached && attached.BodyId == face.BodyId,
			sketch.Face?.BodyId ?? "none" );

		var draft = new DraftFeature();
		draft.ApplyGeometrySelection( new[] { face }, Array.Empty<string>(), studio.Bodies );

		Check( "a draft takes the picked face", draft.Faces.Count == 1, $"{draft.Faces.Count} faces" );

		var hole = new HoleFeature();
		hole.ApplyGeometrySelection( new[] { face }, Array.Empty<string>(), studio.Bodies );

		Check( "a hole takes the picked face", hole.Faces.Count == 1, $"{hole.Faces.Count} faces" );
	}

	/// <summary>Selecting a part in the Parts list, then Fillet, should fillet that part rather
	/// than every body in the studio.</summary>
	static void TestAPickedPartSeedsAFillet()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		var second = studio.Add( new PrimitiveFeature() );
		second.Position.Value = new Vec3( 3f, 0f, 0f );
		studio.Rebuild();

		Check( "two parts to choose between", studio.Bodies.Count == 2, $"{studio.Bodies.Count}" );

		var fillet = new FilletFeature();
		fillet.ApplyGeometrySelection( Array.Empty<FaceRef>(), new[] { studio.Bodies[1].Id },
			studio.Bodies );

		Check( "the fillet is pointed at the picked part",
			fillet.Bodies.BodyIds.Count == 1 && fillet.Bodies.BodyIds[0] == studio.Bodies[1].Id,
			string.Join( ",", fillet.Bodies.BodyIds ) );
	}

	/// <summary>A face pick on a shell is an opening, not just a body filter.</summary>
	static void TestAPickedFaceOpensAShell()
	{
		var (studio, face, index) = BoxAndTopFace();

		var shell = new ShellFeature();
		shell.ApplyGeometrySelection( new[] { face }, Array.Empty<string>(), studio.Bodies );

		Check( "the shell acts on the face's body",
			shell.Bodies.BodyIds.Count == 1 && shell.Bodies.BodyIds[0] == studio.Bodies[0].Id,
			string.Join( ",", shell.Bodies.BodyIds ) );
		Check( "and opens the picked face",
			shell.OpenFaces.Count == 1 && shell.OpenFaces[0] == index,
			string.Join( ",", shell.OpenFaces ) );
	}

	/// <summary>Selecting a face and then Fillet means the edges of that face, not every sharp
	/// edge on the part.</summary>
	static void TestAPickedFaceSeedsAFilletWithItsEdges()
	{
		var (studio, face, _) = BoxAndTopFace();
		var before = studio.Bodies[0].Mesh.FaceCount;

		var fillet = studio.Add( new FilletFeature() );
		fillet.ApplyGeometrySelection( new[] { face }, Array.Empty<string>(), studio.Bodies );
		studio.MarkDirty( fillet );
		studio.Rebuild();

		Check( "the fillet stored the face's four edges", fillet.Edges.Count == 4, $"{fillet.Edges.Count}" );
		Check( "and it built", fillet.Error is null, fillet.Error ?? "" );
		Check( "the solid gained faces, but not a full-cube worth",
			studio.Bodies[0].Mesh.FaceCount > before && studio.Bodies[0].Mesh.FaceCount < before + 20,
			$"{before} → {studio.Bodies[0].Mesh.FaceCount}" );
	}

	static void TestAPickedEdgeSeedsAFillet()
	{
		var (studio, _, index) = BoxAndTopFace();
		var body = studio.Bodies[0];
		var edges = FacePlane.CaptureBoundary( body, index );

		Check( "the top face has edges to pick", edges.Count > 0 );

		var fillet = new FilletFeature();
		fillet.ApplyGeometrySelection( Array.Empty<FaceRef>(), Array.Empty<string>(), studio.Bodies,
			new[] { edges[0] } );

		Check( "the fillet took that one edge", fillet.Edges.Count == 1, $"{fillet.Edges.Count}" );
		Check( "and the body that owns it",
			fillet.Bodies.BodyIds.Count == 1 && fillet.Bodies.BodyIds[0] == body.Id,
			string.Join( ",", fillet.Bodies.BodyIds ) );
	}

	/// <summary>An EdgeRef is geometry, so making the box taller must not attach the fillet to a
	/// bottom edge that happens to keep the old indices.</summary>
	static void TestAnEdgeRefSurvivesATallerBox()
	{
		var (studio, _, index) = BoxAndTopFace();
		var body = studio.Bodies[0];
		var edge = FacePlane.CaptureBoundary( body, index )[0];
		var zBefore = edge.Point.z;

		var box = studio.Features.OfType<PrimitiveFeature>().Single();
		box.SizeZ.Value = 4f;
		EditAndRebuild( studio, box );

		Check( "the edge still resolves after the box grew",
			FacePlane.TryResolveEdge( studio.Bodies, edge, out _, out var key ) );

		var a = studio.Bodies[0].Mesh.Positions[key.A];
		var b = studio.Bodies[0].Mesh.Positions[key.B];
		var midZ = (a.z + b.z) * 0.5f;

		Check( "and it is still on the top, not the bottom",
			midZ > 0f && midZ > zBefore - 0.1f, $"was {zBefore}, now {midZ}" );
	}

	/// <summary>Click the shaded sketch, then Extrude — the toolbar's AwaitingPick is overwritten
	/// by the sketch that was already selected, so a solid appears without a second pick.
	/// </summary>
	static void TestAPickedSketchSeedsAnExtrude()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0f, 0f ), new Vec2( 2f, 2f ) );
		studio.Rebuild();

		var extrude = new ExtrudeFeature();
		extrude.SketchFeatureId = SketchConsumingFeature.AwaitingPick;
		extrude.ApplyGeometrySelection( Array.Empty<FaceRef>(), Array.Empty<string>(), studio.Bodies,
			Array.Empty<EdgeRef>(), sketch.Id );
		studio.Add( extrude );
		studio.Rebuild();

		Check( "awaiting-pick is replaced by the selected sketch",
			extrude.SketchFeatureId == sketch.Id, extrude.SketchFeatureId );
		Check( "and the extrude builds", extrude.Error is null && studio.Bodies.Count == 1,
			$"{extrude.Error ?? "ok"}, {studio.Bodies.Count} bodies" );
	}

	/// <summary>Clicking one face of a two-face sketch, then Extrude, builds only that face.
	/// </summary>
	static void TestAPickedSketchRegionSeedsAnExtrude()
	{
		var studio = new PartStudio();
		var sketch = studio.Add( new SketchFeature() );
		sketch.Sketch.AddRectangle( new Vec2( 0f, 0f ), new Vec2( 2f, 2f ) );
		sketch.Sketch.AddRectangle( new Vec2( 4f, 0f ), new Vec2( 6f, 2f ) );
		studio.Rebuild();

		var both = new ExtrudeFeature();
		both.SketchFeatureId = sketch.Id;
		studio.Add( both );
		studio.Rebuild();
		var bothVolume = studio.Bodies.Sum( b => b.Mesh.SignedVolume() );

		studio.Remove( both );
		studio.Rebuild();

		var extrude = new ExtrudeFeature();
		extrude.SketchFeatureId = SketchConsumingFeature.AwaitingPick;
		extrude.ApplyGeometrySelection( Array.Empty<FaceRef>(), Array.Empty<string>(), studio.Bodies,
			Array.Empty<EdgeRef>(), sketch.Id, new[] { new Vec2( 1f, 1f ) } );
		studio.Add( extrude );
		studio.Rebuild();

		var oneVolume = studio.Bodies.Sum( b => b.Mesh.SignedVolume() );

		Check( "the picked face is stored", extrude.RegionSeeds.Count == 1, $"{extrude.RegionSeeds.Count}" );
		Check( "and only that face is built",
			extrude.Error is null && oneVolume < bothVolume * 0.75f,
			$"one {oneVolume} vs both {bothVolume}, {extrude.Error ?? "ok"}" );
	}

	static (PartStudio Studio, FaceRef Face, int Index) BoxAndTopFace()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );
		studio.Rebuild();

		var body = studio.Bodies[0];
		var mesh = body.Mesh;
		var top = mesh.Faces
			.Select( ( f, i ) => (Index: i, Normal: mesh.FaceNormal( f ), Centroid: mesh.FaceCentroid( f )) )
			.Where( t => t.Normal.z > 0.99f )
			.OrderByDescending( t => t.Centroid.z )
			.First();

		return (studio, FacePlane.Capture( body, top.Index, top.Centroid ), top.Index);
	}

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
