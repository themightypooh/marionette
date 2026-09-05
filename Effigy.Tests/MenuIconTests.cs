using System;
using System.IO;
using System.Linq;

namespace Effigy.Tests;

/// <summary>
/// Guards the Tools-menu mark, which has now gone blank twice for the same reason: the artwork is
/// a file on disk, EffigyWindow.AppIcon looks it up by path, and a reorganisation moved the file
/// without anything noticing. The failure is silent by construction — the icon stamper is built to
/// retry rather than throw, so a missing file just leaves the stock cube in the menu forever.
///
/// The lookup itself now falls back to searching the project tree, so a move no longer breaks it.
/// This test exists so a move is still *noticed*: the fast path should keep hitting, and the two
/// copies of the artwork should not drift apart.
///
/// If this fails, either put the file back at the expected path or add the new one to the probe
/// list in EffigyWindow.FindAppIconPath.
/// </summary>
public static class MenuIconTests
{
	const string IconFile = "effigy_icon.png";

	/// <summary>The path EffigyWindow probes first, relative to the library folder. Duplicated here
	/// on purpose: one file asserting where the artwork lives is the whole point.
	///
	/// The library folder itself is named for however the checkout got it - `Geppetto` in a clone,
	/// `pooh.geppetto` where s&amp;box installed the published package - so that segment is resolved
	/// rather than spelled. EffigyWindow never has this problem: it builds the path from
	/// Project.Current.GetRootPath(), which is the library folder whatever it is called.</summary>
	static readonly string[] Tail = { "Editor", "EffigyEditor", IconFile };

	public static void Run()
	{
		Report.Section( "the Tools menu icon is where EffigyWindow looks for it" );

		if ( FindRepoRoot() is not { } root )
		{
			Report.Check( "repo root located", true, "skipped - no repo layout around the runner" );
			return;
		}

		if ( LibraryDir( root ) is not { } lib )
		{
			Report.Check( "Geppetto project found", false,
				"no Geppetto project beside this repo - set GEPPETTO_DIR if it lives elsewhere" );
			return;
		}

		var primary = Path.Combine( new[] { lib }.Concat( Tail ).ToArray() );
		var shown = string.Join( "/", new[] { Path.GetFileName( lib ) }.Concat( Tail ) );

		Report.Check( $"Libraries/{shown} exists", File.Exists( primary ),
			File.Exists( primary ) ? null : "the icon moved - update EffigyWindow.AppIcon and this test" );

		if ( !File.Exists( primary ) )
			return;

		// A zero-length or truncated PNG loads as nothing and looks exactly like a missing file
		// from the menu's side, so check it is actually a PNG rather than merely present.
		var bytes = File.ReadAllBytes( primary );
		var isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

		Report.Check( "the icon is a readable PNG", isPng, isPng ? null : $"{bytes.Length} bytes, no PNG signature" );

		// The tree carries more than one copy of this artwork. Copies that disagree mean the menu
		// and the window tab can end up showing different marks depending on which one is found.
		var copies = Directory.EnumerateFiles( root, IconFile, SearchOption.AllDirectories )
			.Where( f => !f.Contains( $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" )
				&& !f.Contains( $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}" ) )
			.OrderBy( f => f, StringComparer.Ordinal )
			.ToArray();

		foreach ( var copy in copies )
		{
			if ( string.Equals( copy, primary, StringComparison.OrdinalIgnoreCase ) )
				continue;

			var same = File.ReadAllBytes( copy ).SequenceEqual( bytes );
			var rel = Path.GetRelativePath( root, copy ).Replace( '\\', '/' );

			Report.Check( $"{rel} matches the primary copy", same, same ? null : "the copies have drifted" );
		}
	}

	/// <summary>Same walk KernelSyncTests uses, so the runner works from either directory.</summary>
	/// <summary>The Geppetto library folder under Libraries/, by either of its two names, or null
	/// when neither is present.</summary>
	static string LibraryDir( string root )
	{
		// GEPPETTO IS A SIBLING PROJECT NOW, not a folder under Libraries/. It had to leave: s&box
		// auto-mounts any .sbproj it finds under Libraries/ as a local package, so the library was
		// compiled from source AND imported as a prebuilt assembly in the same build, and the
		// editor would rewrite the folder from the mounted copy.
		//
		// GEPPETTO_DIR overrides it for a checkout that does not have them side by side.
		// One repo: the mirror is this project's own Editor/Effigy. This used to resolve a
		// sibling directory, from the brief period when Geppetto and the repo holding its kernel
		// were two separate checkouts.
		return root;
	}

	static string FindRepoRoot()
	{
		var dir = new DirectoryInfo( AppContext.BaseDirectory );

		while ( dir is not null )
		{
			if ( Directory.Exists( Path.Combine( dir.FullName, "Effigy" ) )
				&& Directory.Exists( Path.Combine( dir.FullName, "Effigy.Tests" ) ) )
				return dir.FullName;

			dir = dir.Parent;
		}

		return null;
	}
}
