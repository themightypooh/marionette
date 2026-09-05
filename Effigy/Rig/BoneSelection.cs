using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// What a multi-bone selection means when you drag it.
///
/// WHY THIS IS NOT IN THE VIEWPORT, where it started. It is a question about a tree and a set of
/// names — no engine types, no widgets, nothing to draw — and getting it wrong is invisible:
/// a bone transformed twice looks like a bone that moved further than the others, which reads as
/// a gizmo problem rather than a selection one. Out here a test can state the shape and check it.
/// </summary>
public static class BoneSelection
{
	/// <summary>
	/// The selected bones with no selected ANCESTOR — the ones a group drag is applied to. Anything
	/// under one of them is carried by it through the hierarchy and must not be transformed again.
	///
	/// EVERY ANCESTOR, NOT THE IMMEDIATE PARENT. Checking only the parent is right whenever the
	/// selection is contiguous and wrong the moment it skips a generation: select a bone and its
	/// GRANDCHILD without the bone between them, and the grandchild's parent is not in the
	/// selection, so it passes the test — it is then transformed directly AND carried by its
	/// grandparent, moving twice as far as everything else. That reads as a broken gizmo rather
	/// than a broken selection, which is why it is worth spelling out here.
	///
	/// <paramref name="parentOf"/> returns the parent's name, or null at a root. A cycle would
	/// hang the walk, so the depth is capped at the number of bones in the selection plus the
	/// chain it climbs; a malformed skeleton stops being this function's problem at that point.
	/// </summary>
	public static List<string> TopMost( IEnumerable<string> selected, Func<string, string> parentOf )
	{
		var names = new HashSet<string>( selected ?? Array.Empty<string>() );
		var tops = new List<string>();

		if ( parentOf is null )
			return new List<string>( names );

		foreach ( var name in names )
		{
			if ( !HasSelectedAncestor( name, names, parentOf ) )
				tops.Add( name );
		}

		return tops;
	}

	/// <summary>Whether anything above <paramref name="name"/> is also selected.</summary>
	public static bool HasSelectedAncestor( string name, ISet<string> selected, Func<string, string> parentOf )
	{
		if ( name is null || selected is null || parentOf is null )
			return false;

		// Guard against a skeleton that points at itself. The walk is bounded by the selection
		// size plus a sane chain length rather than by trust in the data.
		var guard = 0;
		var limit = selected.Count + 256;

		for ( var current = parentOf( name ); current is not null; current = parentOf( current ) )
		{
			if ( selected.Contains( current ) )
				return true;

			if ( ++guard > limit )
				return false;
		}

		return false;
	}
}
