using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Dragging a material out of a browser and letting go over a face.
///
/// The browser, the drag and the raycast are the editor's problem. What is testable is the decision
/// underneath, which is the whole reason this is not just FaceMaterialEdit with a name attached:
/// the drop names no slot, so something has to CHOOSE one, and choosing badly is silent. Take a
/// slot the face menu already painted with and faces elsewhere on the model change material for no
/// visible reason; take a fresh slot every time and thirty faces of one material become thirty
/// slots that export as thirty materials; take slot 0 and the drop paints the entire part.
/// </summary>
public static class MaterialDropTests
{
	public static void Run()
	{
		Report.Section( "material drop: a material nobody has used takes a fresh slot" );
		TestFirstDrop();

		Report.Section( "material drop: the same material again reuses its slot" );
		TestReuse();

		Report.Section( "material drop: a second material takes the next slot" );
		TestSecondMaterial();

		Report.Section( "material drop: slots already spoken for are left alone" );
		TestSkipsUsedSlots();

		Report.Section( "material drop: slot 0 is never allocated" );
		TestNeverAllocatesZero();

		Report.Section( "material drop: the same path spelled differently is the same material" );
		TestSpelling();

		Report.Section( "material drop: a compiled path is the same asset as its source" );
		TestCompiledPath();

		Report.Section( "material drop: a brush dab covers many faces at once" );
		TestBrushDab();
		TestBrushReleasesEmptiedSlots();

		Report.Section( "material drop: nothing to do, nothing changed" );
		TestNoChange();

		Report.Section( "material drop: no slots left" );
		TestExhausted();

		Report.Section( "material drop: which slot already carries a material" );
		TestSlotCarrying();

		Report.Section( "material drop: changing your mind frees the slot you left" );
		TestVacatedSlotIsFreed();

		Report.Section( "material drop: a slot somebody else is holding is not freed" );
		TestOccupiedSlotSurvives();
	}

	/// <summary>
	/// THE ONE THIS WAS ADDED FOR. Dropping four materials onto one face used to leave four bound
	/// slots — the face wears the last, and the other three are named, wear nothing, and get written
	/// by every exporter. Nothing on screen counts them except the Materials panel's footer, which
	/// is where it was noticed: "9 bound" on a part with three painted faces.
	/// </summary>
	static void TestVacatedSlotIsFreed()
	{
		var studio = Boxed( out var body );
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Drop( studio, body, top, "materials/a.vmat", out var first );

		studio.Rebuild();
		body = studio.Bodies.Single();
		top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var changed = Drop( studio, body, top, "materials/b.vmat", out var second, out var released );

		Report.Check( "the second material still gets its own slot", first == 1 && second == 2,
			$"{first} then {second}" );

		Report.Check( "the drop reports the slot it retired", changed && released == first,
			$"released {released}" );

		Report.Check( "and only the material actually worn is left bound",
			studio.MaterialNames.Count == 1
			&& studio.MaterialNames[second] == "materials/b.vmat",
			string.Join( ", ", studio.MaterialNames.Select( kv => $"{kv.Key}={kv.Value}" ) ) );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "with the face on the second slot and nothing on the first",
			mesh.Faces.Count( f => f.Material == second ) == 1
			&& mesh.Faces.Count( f => f.Material == first ) == 0 );

		// Third and fourth changes of mind, to show the count does not creep. Slot 1 is free again
		// after the release above, so this walks 2 -> 1 -> 2 rather than climbing.
		studio.Rebuild();
		body = studio.Bodies.Single();
		top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		Drop( studio, body, top, "materials/c.vmat", out _ );

		studio.Rebuild();
		body = studio.Bodies.Single();
		top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		Drop( studio, body, top, "materials/d.vmat", out _ );

		Report.Check( "four materials onto one face still leaves one bound slot",
			studio.MaterialNames.Count == 1,
			string.Join( ", ", studio.MaterialNames.Select( kv => $"{kv.Key}={kv.Value}" ) ) );
	}

	/// <summary>
	/// The other half, and the reason the release is not just "drop any slot with no faces on it".
	/// A slot two faces share must survive one of them leaving, and a slot somebody named in the
	/// Materials panel before painting anything must survive full stop — it has no faces by
	/// definition, and reserving one is a thing you are allowed to do.
	/// </summary>
	static void TestOccupiedSlotSurvives()
	{
		var studio = Boxed( out var body );

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), "materials/a.vmat", out var shared );

		studio.Rebuild();
		body = studio.Bodies.Single();

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, -1 ) ), "materials/a.vmat", out _ );

		// A slot named and never painted, exactly as the Materials panel leaves one.
		studio.MaterialNames[9] = "materials/reserved.vmat";

		studio.Rebuild();
		body = studio.Bodies.Single();

		// One of the two faces moves away. The other is still on the slot.
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		Drop( studio, body, top, "materials/b.vmat", out var moved, out var released );

		Report.Check( "nothing was retired", released == -1, $"released {released}" );

		Report.Check( "the shared slot keeps its material",
			studio.MaterialNames.TryGetValue( shared, out var name ) && name == "materials/a.vmat" );

		Report.Check( "the reserved slot is untouched",
			studio.MaterialNames.TryGetValue( 9, out var reserved ) && reserved == "materials/reserved.vmat" );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "one face left behind on the shared slot, one on the new one",
			mesh.Faces.Count( f => f.Material == shared ) == 1
			&& mesh.Faces.Count( f => f.Material == moved ) == 1 );
	}

	/// <summary>
	/// The question the browser's slot badge asks, and the reason it is not SlotFor.
	///
	/// SlotFor answers "where would this go", which for a material the part does not use is a FREE
	/// slot — a perfectly good answer to a different question, and a badge drawn from it would put
	/// the same number on every unused material in the project.
	/// </summary>
	static void TestSlotCarrying()
	{
		var studio = Boxed( out var body );

		Report.Check( "nothing carries a material in an empty document",
			MaterialDrop.SlotCarrying( studio, "materials/a.vmat" ) == -1 );

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), "materials/a.vmat", out var slot );

		Report.Check( "the slot it landed on is the slot carrying it",
			MaterialDrop.SlotCarrying( studio, "materials/a.vmat" ) == slot, $"slot {slot}" );

		Report.Check( "a material the part does not use carries nowhere, rather than reporting the free slot",
			MaterialDrop.SlotCarrying( studio, "materials/b.vmat" ) == -1
			&& MaterialDrop.SlotFor( studio, "materials/b.vmat" ) > 0 );

		Report.Check( "spelling does not hide it",
			MaterialDrop.SlotCarrying( studio, "MATERIALS\\A.VMAT" ) == slot );

		studio.MaterialNames[0] = "materials/base.vmat";

		Report.Check( "slot 0 is reported like any other", MaterialDrop.SlotCarrying( studio, "materials/base.vmat" ) == 0 );
	}

	static void TestFirstDrop()
	{
		var studio = Boxed( out var body );
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var changed = Drop( studio, body, top, "materials/dev/reflectivity_30.vmat", out var slot );

		Report.Check( "the drop reports that it did something", changed );
		Report.Check( "it went to slot 1", slot == 1, $"slot {slot}" );

		Report.Check( "the slot carries the material",
			studio.MaterialNames.TryGetValue( 1, out var name )
			&& name == "materials/dev/reflectivity_30.vmat",
			studio.MaterialNames.TryGetValue( 1, out var shown ) ? shown : "nothing" );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "exactly the face that was dropped on is on the slot",
			mesh.Faces.Count( f => f.Material == 1 ) == 1
			&& mesh.FaceNormal( mesh.Faces.First( f => f.Material == 1 ) ).z > 0.99f );
	}

	static void TestReuse()
	{
		var studio = Boxed( out var body );

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), "materials/a.vmat", out var first );

		studio.Rebuild();
		body = studio.Bodies.Single();

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, -1 ) ), "materials/a.vmat", out var second );

		Report.Check( "the second drop lands on the same slot", first == second, $"{first} then {second}" );

		Report.Check( "and named exactly one slot", studio.MaterialNames.Count == 1 );

		Report.Check( "on one assignment feature, not two",
			studio.Features.OfType<FaceMaterialFeature>().Count() == 1 );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "both faces carry it", mesh.Faces.Count( f => f.Material == first ) == 2 );
	}

	static void TestSecondMaterial()
	{
		var studio = Boxed( out var body );

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), "materials/a.vmat", out var first );

		studio.Rebuild();
		body = studio.Bodies.Single();

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) ), "materials/b.vmat", out var second );

		Report.Check( "a different material gets its own slot", first == 1 && second == 2, $"{first} then {second}" );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "one face each", mesh.Faces.Count( f => f.Material == 1 ) == 1
			&& mesh.Faces.Count( f => f.Material == 2 ) == 1 );
	}

	/// <summary>
	/// The one that matters. A slot the face menu has painted with carries no name, so a drop that
	/// only avoided NAMED slots would take it — and every face somebody had put on slot 1 by hand
	/// would change material without being touched.
	/// </summary>
	static void TestSkipsUsedSlots()
	{
		var studio = Boxed( out var body );

		var side = FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) );
		Assign( studio, body, side, 1 );

		studio.Rebuild();
		body = studio.Bodies.Single();

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), "materials/a.vmat", out var slot );

		Report.Check( "the painted slot is not taken", slot == 2, $"slot {slot}" );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "the hand-painted face is still on its own slot",
			mesh.Faces.Count( f => f.Material == 1 ) == 1
			&& MathF.Abs( mesh.FaceNormal( mesh.Faces.First( f => f.Material == 1 ) ).x - 1f ) < 0.01f );
	}

	static void TestNeverAllocatesZero()
	{
		var studio = Boxed( out _ );

		// Every slot from 1 up named, so slot 0 is the only free NUMBER in the range — and still
		// must not be chosen, because it is what the untouched rest of the part is on.
		for ( var i = 1; i <= MaterialDrop.HighestSlot; i++ )
			studio.MaterialNames[i] = $"materials/filler_{i}.vmat";

		Report.Check( "an unused material finds nowhere to go rather than falling back to 0",
			MaterialDrop.SlotFor( studio, "materials/new.vmat" ) == -1 );

		// Named deliberately from the Materials panel, which is allowed: rule 1 finds it, and the
		// drop then means "this face goes back to the part's base material", which is a real thing
		// to want.
		studio.MaterialNames[0] = "materials/base.vmat";

		Report.Check( "but a material somebody put on slot 0 is still found there",
			MaterialDrop.SlotFor( studio, "materials/base.vmat" ) == 0 );
	}

	static void TestSpelling()
	{
		var studio = Boxed( out var body );

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) ), "materials/Dev/Grid.vmat", out var first );

		studio.Rebuild();
		body = studio.Bodies.Single();

		Drop( studio, body, FaceIndexFacing( body.Mesh, new Vec3( 0, 0, -1 ) ), "materials\\dev\\grid.vmat", out var second );

		Report.Check( "backslashes and case do not open a second slot", first == second, $"{first} then {second}" );

		Report.Check( "and the name written first is the one kept",
			studio.MaterialNames[first] == "materials/Dev/Grid.vmat", studio.MaterialNames[first] );
	}

	/// <summary>
	/// A dab is many faces and ONE slot — the case the brush exists for, and the one where looping
	/// the single-face drop by hand would be easiest to get wrong.
	/// </summary>
	/// <summary>
	/// `.vmat_c` is what the asset browser calls anything that ships compiled, which is most of the
	/// engine's own content. Binding that string is what put the missing-material shader on a face
	/// somebody had just painted: nothing resolves it, so the model asks for a material that is not
	/// there. Two separate jobs, and both were wrong.
	/// </summary>
	static void TestCompiledPath()
	{
		Report.Check( "the source path is what a document stores",
			MaterialDrop.AsSourcePath( "materials/concrete/wall/concrete_rough_a.vmat_c" )
				== "materials/concrete/wall/concrete_rough_a.vmat" );

		Report.Check( "a source path is left alone",
			MaterialDrop.AsSourcePath( "materials/wood/oak.vmat" ) == "materials/wood/oak.vmat" );

		Report.Check( "and case is not touched, because this is written to disk and shown to people",
			MaterialDrop.AsSourcePath( "Materials/Wood/Oak.vmat_c" ) == "Materials/Wood/Oak.vmat" );

		Report.Check( "the two spellings compare equal, so the browser badge finds a bound material",
			MaterialDrop.Normalise( "materials/wood/oak.vmat_c" )
				== MaterialDrop.Normalise( "materials/wood/oak.vmat" ) );

		// The consequence that actually bit: a second slot for a material the part already wore.
		var studio = Boxed( out var body );
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Drop( studio, body, top, "materials/wood/oak.vmat", out var first );

		Report.Check( "the compiled spelling finds the slot the source one took",
			MaterialDrop.SlotCarrying( studio, "materials/wood/oak.vmat_c" ) == first,
			$"{MaterialDrop.SlotCarrying( studio, "materials/wood/oak.vmat_c" )} vs {first}" );

		Report.Check( "so it does not allocate a second slot for the same material",
			MaterialDrop.SlotFor( studio, "materials/wood/oak.vmat_c" ) == first );
	}

	static void TestBrushDab()
	{
		var studio = Boxed( out var body );
		var faces = Enumerable.Range( 0, body.Mesh.Faces.Count ).ToList();

		var changed = MaterialDrop.Brush( studio, body, faces,
			"materials/dev/reflectivity_30.vmat", out var slot, out var released );

		Report.Check( "every face of the box was moved", changed == faces.Count,
			$"{changed} of {faces.Count}" );

		Report.Check( "onto one slot, not one each", slot == 1, $"slot {slot}" );

		Report.Check( "and only that one slot is named", studio.MaterialNames.Count == 1
			&& studio.MaterialNames[1] == "materials/dev/reflectivity_30.vmat" );

		Report.Check( "nothing was emptied, because nothing was painted before",
			released.Count == 0, string.Join( ", ", released ) );

		var report = studio.Rebuild();

		Report.Check( "and it builds", !report.HasErrors, report.ToString() );

		var mesh = studio.Bodies.Single().Mesh;

		Report.Check( "every face wears it after the rebuild",
			mesh.Faces.All( f => f.Material == 1 ) );

		// The second dab over the same faces is the ordinary thing a held brush does, and it must
		// not report an edit — that would put a do-nothing step on the undo stack per mouse-move.
		var again = MaterialDrop.Brush( studio, studio.Bodies.Single(),
			Enumerable.Range( 0, mesh.Faces.Count ), "materials/dev/reflectivity_30.vmat",
			out _, out _ );

		Report.Check( "dragging back over the same faces changes nothing", again == 0, $"{again}" );
	}

	/// <summary>
	/// Sweeping one material off the last face holding another retires the slot it left, the same
	/// rule the single drop follows — a stroke can empty several at once.
	/// </summary>
	static void TestBrushReleasesEmptiedSlots()
	{
		var studio = Boxed( out var body );
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Drop( studio, body, top, "materials/dev/reflectivity_30.vmat", out var first );
		studio.Rebuild();

		Report.Check( "the first material has a slot", first == 1, $"slot {first}" );

		var live = studio.Bodies.Single();

		var changed = MaterialDrop.Brush( studio, live,
			Enumerable.Range( 0, live.Mesh.Faces.Count ), "materials/wood/oak.vmat",
			out var slot, out var released );

		Report.Check( "the second material sweeps every face", changed > 0, $"{changed}" );

		Report.Check( "and the slot the first one had is retired, not left named on nothing",
			released.Contains( first ), string.Join( ", ", released ) );

		Report.Check( "so only the new material is named",
			!studio.MaterialNames.ContainsKey( first ) || studio.MaterialNames[first] == "materials/wood/oak.vmat",
			string.Join( ", ", studio.MaterialNames.Select( kv => $"{kv.Key}={kv.Value}" ) ) );

		Report.Check( "which is on the slot the brush reported", slot >= 0
			&& studio.MaterialNames.TryGetValue( slot, out var n ) && n == "materials/wood/oak.vmat" );
	}

	static void TestNoChange()
	{
		var studio = Boxed( out var body );
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		Drop( studio, body, top, "materials/a.vmat", out _ );

		studio.Rebuild();
		body = studio.Bodies.Single();

		top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var again = Drop( studio, body, top, "materials/a.vmat", out _ );

		Report.Check( "dropping the same material on the same face reports no change", !again );

		var reference = FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) );

		Report.Check( "an empty material is refused",
			!MaterialDrop.Drop( studio, body.Id, top, reference, "  ", out var slot ) && slot == -1 );
	}

	static void TestExhausted()
	{
		var studio = Boxed( out var body );

		for ( var i = 1; i <= MaterialDrop.HighestSlot; i++ )
			studio.MaterialNames[i] = $"materials/filler_{i}.vmat";

		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var changed = Drop( studio, body, top, "materials/one_too_many.vmat", out var slot );

		Report.Check( "the drop is refused rather than overwriting somebody's slot", !changed && slot == -1 );

		Report.Check( "and nothing was named",
			!studio.MaterialNames.Values.Contains( "materials/one_too_many.vmat" ) );
	}

	// --- helpers, the same ones FaceMenuTests uses ----------------------------------------------

	static bool Drop( PartStudio studio, Body body, int faceIndex, string material, out int slot ) =>
		Drop( studio, body, faceIndex, material, out slot, out _ );

	static bool Drop( PartStudio studio, Body body, int faceIndex, string material, out int slot,
		out int released )
	{
		var reference = FacePlane.Capture( body, faceIndex, body.Mesh.FaceCentroid( body.Mesh.Faces[faceIndex] ) );

		return MaterialDrop.Drop( studio, body.Id, faceIndex, reference, material, out slot, out released );
	}

	static bool Assign( PartStudio studio, Body body, int faceIndex, int slot )
	{
		var reference = FacePlane.Capture( body, faceIndex, body.Mesh.FaceCentroid( body.Mesh.Faces[faceIndex] ) );

		return FaceMaterialEdit.Assign( studio, body.Id, faceIndex, reference, slot );
	}

	static PartStudio Boxed( out Body body )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;

		studio.Rebuild();
		body = studio.Bodies.Single();

		return studio;
	}

	static int FaceIndexFacing( PolyMesh mesh, Vec3 direction )
	{
		for ( var i = 0; i < mesh.Faces.Count; i++ )
		{
			if ( Vec3.Dot( mesh.FaceNormal( mesh.Faces[i] ), direction.Normal ) > 0.99f )
				return i;
		}

		return -1;
	}
}
