using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Effigy.Tests;

/// <summary>
/// Guards the one piece of this repo that duplication is unavoidable in.
///
/// s&amp;box compiles a library's `Code/` into a game assembly and its `Editor/` into an editor
/// assembly, and nothing else - a top-level `Effigy/` is invisible to it. The kernel cannot live in `Code/`
/// either, because ObjWriter and SmdWriter call File.WriteAllText and the game assembly's sandbox
/// whitelist does not allow it. So the editor assembly needs its own copy of the kernel, and
/// `Editor/Effigy/` is that copy.
///
/// `Effigy/` is canonical: it is what this test project compiles, and keeping it out of any engine
/// folder is what keeps the Godot option open.
///
/// The failure mode this exists to catch is silent: edit the canonical kernel, forget to mirror,
/// and the tests keep passing against source the editor is not running. That already happened once
/// - the mirror was committed with a stray blank line in SolidFeatures.cs, which is a diff of no
/// consequence and proof that nothing was checking. Run tools/sync-kernel.sh to fix a failure here.
/// </summary>
public static class KernelSyncTests
{
	public static void Run()
	{
		Report.Section( "Geppetto's Editor/Effigy mirrors the canonical kernel" );

		if ( FindRepoRoot() is not { } root )
		{
			// Not a failure: the runner is allowed to be pointed at the kernel from somewhere with
			// no repo around it. Saying so beats a green tick that checked nothing.
			Report.Check( "repo root located", true, "skipped - no repo layout around the runner" );
			return;
		}

		var src = Path.Combine( root, "Effigy" );

		// s&box installs the package under its ident (`pooh.geppetto`); a plain clone has it as
		// `Geppetto`. Same mirror either way, so check whichever this checkout has - otherwise the
		// test reports a missing mirror on a working tree where the editor is perfectly happy.
		var lib = LibraryDir( root );

		if ( lib is null )
		{
			Report.Check( "Geppetto project found", false,
				"no Geppetto project beside this repo - set GEPPETTO_DIR if it lives elsewhere" );
			return;
		}

		var dst = Path.Combine( lib, "Editor", "Effigy" );

		if ( !Directory.Exists( dst ) )
		{
			Report.Check( "the library's Editor/Effigy exists", false, $"{dst} is missing - run tools/sync-kernel.sh" );
			return;
		}

		var srcFiles = RelativeCsFiles( src );
		var dstFiles = RelativeCsFiles( dst );

		// THE RUNTIME SUBSET IS DELIBERATELY NOT IN THE EDITOR MIRROR. These four go to Code/ for
		// the game assembly, the editor assembly references that assembly, and a type declared in
		// both is CS0436 on every use - 1857 warnings, and a Vec2 from one side that will not go
		// where a Vec2 from the other is expected. The editor gets them from the reference.
		//
		// tools/sync-kernel.sh keeps this list; it is repeated here rather than parsed out of the
		// shell so that changing one without the other fails the suite instead of quietly
		// reintroducing the duplicate.
		var runtimeSubset = new[] { "Vec.cs", "Xform.cs", "Rig/Skeleton.cs", "Rig/SoftBone.cs" };

		var mirrored = srcFiles.Where( f => !runtimeSubset.Contains( f ) ).ToList();

		var missing = mirrored.Except( dstFiles ).ToList();
		var extra = dstFiles.Except( srcFiles ).ToList();

		var duplicated = dstFiles.Where( f => runtimeSubset.Contains( f ) ).ToList();

		Report.Check( "no kernel file missing from the mirror", missing.Count == 0, string.Join( ", ", missing ) );

		Report.Check( "the runtime subset is not duplicated into the editor mirror",
			duplicated.Count == 0,
			duplicated.Count == 0
				? ""
				: string.Join( ", ", duplicated ) + " - these come from the game assembly; "
					+ "mirroring them too is what CS0436 complains about" );
		Report.Check( "no stale file left in the mirror", extra.Count == 0, string.Join( ", ", extra ) );

		// Byte-for-byte rather than token-for-token. A whitespace-only difference is harmless in
		// itself and is exactly the signal that the two are being maintained by hand.
		foreach ( var file in srcFiles.Intersect( dstFiles ).OrderBy( f => f, StringComparer.Ordinal ) )
		{
			var same = File.ReadAllBytes( Path.Combine( src, file ) )
				.SequenceEqual( File.ReadAllBytes( Path.Combine( dst, file ) ) );

			Report.Check( $"{file} matches", same, same ? null : "run tools/sync-kernel.sh" );
		}

		CheckRuntimeSubset( src, lib );
	}

	/// <summary>
	/// The much smaller mirror that goes to the GAME assembly.
	///
	/// The whole kernel cannot go there - the writers call File.WriteAllText and the sandbox
	/// refuses - but it does not follow that a game can have none of it. SoftSolver is arithmetic
	/// with no filesystem near it, and Code/Effigy is the subset that carries it.
	///
	/// THE SUBSET MUST NOT REACH OUTSIDE ITSELF, and that is what the second check here is for. A
	/// game assembly compiles these four files and nothing else from the kernel, so a new reference
	/// to a type living elsewhere in Effigy does not fail here, it fails in somebody's game with a
	/// missing-type error against a file they did not write. Cheap to check, miserable to diagnose.
	/// </summary>
	static void CheckRuntimeSubset( string src, string lib )
	{
		string[] subset = { "Vec.cs", "Xform.cs", "Rig/Skeleton.cs", "Rig/SoftBone.cs" };

		var dst = Path.Combine( lib, "Code", "Effigy" );

		if ( !Directory.Exists( dst ) )
		{
			Report.Check( "the library's Code/Effigy exists", false, $"{dst} is missing - run tools/sync-kernel.sh" );
			return;
		}

		var present = RelativeCsFiles( dst );
		Report.Check( "the runtime subset holds exactly the files it should",
			present.SequenceEqual( subset.OrderBy( f => f, StringComparer.Ordinal ) ),
			string.Join( ", ", present ) );

		foreach ( var file in subset )
		{
			var a = Path.Combine( src, file );
			var b = Path.Combine( dst, file );

			if ( !File.Exists( b ) )
			{
				Report.Check( $"runtime {file} present", false, "run tools/sync-kernel.sh" );
				continue;
			}

			var same = File.ReadAllBytes( a ).SequenceEqual( File.ReadAllBytes( b ) );
			Report.Check( $"runtime {file} matches", same, same ? null : "run tools/sync-kernel.sh" );
		}

		// Nothing in the subset may name a type that only exists outside it.
		var outside = DeclaredTypes( src, subset );
		var leaked = new List<string>();

		foreach ( var file in subset )
		{
			var path = Path.Combine( src, file );
			if ( !File.Exists( path ) ) continue;

			var code = StripComments( File.ReadAllText( path ) );

			foreach ( var type in outside )
			{
				if ( Regex.IsMatch( code, $@"{Regex.Escape( type )}" ) )
					leaked.Add( $"{file} -> {type}" );
			}
		}

		Report.Check( "the runtime subset references nothing outside itself",
			leaked.Count == 0, string.Join( ", ", leaked.Distinct() ) );

		// And the reason the subset exists at all: no filesystem in a game assembly.
		var io = subset
			.Where( f => File.Exists( Path.Combine( src, f ) ) )
			.Where( f => Regex.IsMatch( StripComments( File.ReadAllText( Path.Combine( src, f ) ) ),
				@"System\.IO|File\.|Directory\." ) )
			.ToList();

		Report.Check( "the runtime subset touches no filesystem", io.Count == 0, string.Join( ", ", io ) );
	}

	/// <summary>Every top-level type the kernel declares OUTSIDE the given files.</summary>
	static List<string> DeclaredTypes( string src, string[] exclude )
	{
		var skip = new HashSet<string>( exclude, StringComparer.Ordinal );
		var types = new List<string>();

		foreach ( var file in RelativeCsFiles( src ) )
		{
			if ( skip.Contains( file ) ) continue;

			var code = StripComments( File.ReadAllText( Path.Combine( src, file ) ) );

			foreach ( Match m in Regex.Matches( code,
				@"(?:class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)" ) )
			{
				types.Add( m.Groups[1].Value );
			}
		}

		return types.Distinct().ToList();
	}

	/// <summary>
	/// Comments out, so a type NAMED in prose does not read as a reference to it. This file's own
	/// kernel is heavily commented and half the type names in the repo appear in one docstring or
	/// another.
	/// </summary>
	static string StripComments( string code )
	{
		code = Regex.Replace( code, @"/\*.*?\*/", " ", RegexOptions.Singleline );
		code = Regex.Replace( code, @"//[^
]*", " " );

		return code;
	}

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

	static string[] RelativeCsFiles( string dir ) =>
		Directory.EnumerateFiles( dir, "*.cs", SearchOption.AllDirectories )
			.Select( f => Path.GetRelativePath( dir, f ).Replace( '\\', '/' ) )
			.OrderBy( f => f, StringComparer.Ordinal )
			.ToArray();

	/// <summary>Walk up from the running binary looking for the layout, so the check works whether
	/// the runner was started from the repo root or from Effigy.Tests.</summary>
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
