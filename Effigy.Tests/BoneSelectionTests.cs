using System.Collections.Generic;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Which bones a group drag is applied to.
///
/// WHY THIS IS WORTH A TEST FILE. The rule decides whether a bone gets transformed once or twice,
/// and being transformed twice does not look like a selection bug — it looks like one bone flying
/// further than the rest, which sends you to the gizmo. It lived as a one-line LINQ filter inside a
/// 2,100-line viewport where nothing could reach it, and it was wrong.
/// </summary>
public static class BoneSelectionTests
{
	/// <summary>
	/// pelvis → spine → chest → arm → hand, one straight chain, which is enough for every case
	/// below: contiguous selections, selections that skip a generation, and roots.
	/// </summary>
	static readonly Dictionary<string, string> Chain = new()
	{
		["pelvis"] = null,
		["spine"] = "pelvis",
		["chest"] = "spine",
		["arm"] = "chest",
		["hand"] = "arm",
	};

	static string ParentOf( string name ) => Chain.TryGetValue( name, out var p ) ? p : null;

	static List<string> Top( params string[] selected ) =>
		BoneSelection.TopMost( selected, ParentOf ).OrderBy( n => n ).ToList();

	public static void Run()
	{
		Report.Section( "bone selection: one bone is its own top" );
		TestSingle();

		Report.Section( "bone selection: a parent carries its children" );
		TestContiguous();

		Report.Section( "bone selection: a selection that skips a generation" );
		TestSkippedGeneration();

		Report.Section( "bone selection: unrelated bones are all tops" );
		TestSiblings();

		Report.Section( "bone selection: it does not hang on a malformed skeleton" );
		TestCycle();
	}

	static void TestSingle()
	{
		Report.Check( "a lone bone is transformed directly",
			Top( "chest" ).SequenceEqual( new[] { "chest" } ) );

		Report.Check( "a root with nothing above it too",
			Top( "pelvis" ).SequenceEqual( new[] { "pelvis" } ) );

		Report.Check( "and an empty selection has no tops", Top().Count == 0 );
	}

	static void TestContiguous()
	{
		// The ordinary case, and the one the old immediate-parent check got right.
		var tops = Top( "spine", "chest", "arm" );

		Report.Check( "only the highest of a contiguous run is transformed",
			tops.SequenceEqual( new[] { "spine" } ), string.Join( ", ", tops ) );

		Report.Check( "the whole chain from the root collapses to the root",
			Top( "pelvis", "spine", "chest", "arm", "hand" ).SequenceEqual( new[] { "pelvis" } ) );
	}

	/// <summary>
	/// THE ONE THAT WAS BROKEN. spine and arm are selected; chest, between them, is not. The old
	/// rule asked only whether arm's PARENT was selected — chest is not, so arm passed, and it was
	/// transformed directly as well as being carried by spine. Twice the movement, on one bone.
	/// </summary>
	static void TestSkippedGeneration()
	{
		var tops = Top( "spine", "arm" );

		Report.Check( "a bone with an unselected parent but a selected grandparent is NOT a top",
			tops.SequenceEqual( new[] { "spine" } ), string.Join( ", ", tops ) );

		Report.Check( "the same across two skipped generations",
			Top( "pelvis", "hand" ).SequenceEqual( new[] { "pelvis" } ),
			string.Join( ", ", Top( "pelvis", "hand" ) ) );

		Report.Check( "and the ancestor test says so directly",
			BoneSelection.HasSelectedAncestor( "arm", new HashSet<string> { "spine", "arm" }, ParentOf ) );

		Report.Check( "while a bone above the selection has none",
			!BoneSelection.HasSelectedAncestor( "spine", new HashSet<string> { "spine", "arm" }, ParentOf ) );
	}

	static void TestSiblings()
	{
		// Two chains that never meet: nothing carries anything, so both are transformed.
		var forest = new Dictionary<string, string>
		{
			["left"] = null,
			["right"] = null,
			["left_hand"] = "left",
		};

		var tops = BoneSelection.TopMost( new[] { "left", "right" },
			n => forest.TryGetValue( n, out var p ) ? p : null ).OrderBy( n => n ).ToList();

		Report.Check( "two roots are both tops", tops.SequenceEqual( new[] { "left", "right" } ),
			string.Join( ", ", tops ) );

		var mixed = BoneSelection.TopMost( new[] { "left", "left_hand", "right" },
			n => forest.TryGetValue( n, out var p ) ? p : null ).OrderBy( n => n ).ToList();

		Report.Check( "and a child of one of them drops out, the other does not",
			mixed.SequenceEqual( new[] { "left", "right" } ), string.Join( ", ", mixed ) );
	}

	/// <summary>A skeleton that points at itself is not something this should hang on — the walk is
	/// bounded rather than trusting the data.</summary>
	static void TestCycle()
	{
		var loop = new Dictionary<string, string> { ["a"] = "b", ["b"] = "a" };

		var tops = BoneSelection.TopMost( new[] { "a" },
			n => loop.TryGetValue( n, out var p ) ? p : null );

		Report.Check( "a cycle returns rather than spinning", tops.Count <= 1 );
	}
}
