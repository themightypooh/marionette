using Editor;
using Sandbox;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Toolshed.Publishing;

/// <summary>
/// Publish the Geppetto library package from the console, with the change notes filled in from the
/// last commit.
///
/// WHY THIS EXISTS. `tools/ship.sh` puts a change on both git repos in one command, but the s&box
/// package - the copy that reaches people who INSTALLED Geppetto rather than cloned it - was a
/// four-screen wizard ending in a "Change Title" box pre-filled with "Changes on 2026-09-04",
/// which says nothing to anyone. The commit message already says what changed. Retyping a worse
/// version of it into a dialog is the kind of step that gets skipped, and a skipped publish means
/// installed users keep running the bug you just fixed.
///
/// HOW IT WORKS. `Editor.ProjectPublisher` is the type the wizard itself drives, and its whole
/// flow is public: FromProject, PrePublish, UploadFiles, Publish, SetChangeDetails. This calls
/// them in that order. Nothing here reimplements the upload; it presses the same buttons.
///
/// DRY RUN BY DEFAULT, and that is deliberate rather than cautious boilerplate. A publish is
/// visible to everyone who installed the package and cannot be taken back - the version is out.
/// So `geppetto_publish` alone reports what WOULD go, and only `geppetto_publish commit` sends it.
/// The wizard's four screens were, among other things, four chances to notice you were about to
/// publish the wrong thing; this keeps one of them.
///
///     geppetto_publish              what would be published, uploads nothing
///     geppetto_publish commit       publish it, notes taken from the last commit
///
/// INTERNAL API, SO IT CAN BREAK. These are editor types, not a documented contract, and an engine
/// update may move them. The failure is loud - a missing method throws here rather than publishing
/// something wrong - and the wizard is always still there.
/// </summary>
public static class GeppettoPublish
{
	[ConCmd( "geppetto_publish" )]
	public static void Run( string mode = "" )
	{
		var commit = string.Equals( mode, "commit", StringComparison.OrdinalIgnoreCase );

		_ = PublishAsync( commit );
	}

	static async Task PublishAsync( bool commit )
	{
		try
		{
			var root = Project.Current?.RootDirectory?.FullName;

			if ( string.IsNullOrWhiteSpace( root ) )
			{
				Log.Error( "[publish] no current project - open Geppetto in the editor first" );
				return;
			}

			// THE OPEN PROJECT IS THE ONE TO PUBLISH, and that is only true since Geppetto
			// stopped being a folder inside another project. It used to have to find its own
			// .sbproj under the host's Libraries/ and be careful not to publish the host by
			// mistake - which is exactly the mistake that got made by hand, creating a stray
			// pooh.toolshed package. There is one project open here and it is this one.
			var project = Project.Current;

			Log.Info( $"[publish] project   {project.Config?.Ident} ({project.Config?.Type}), "
				+ $"published={project.IsPublished}" );

			var publisher = await ProjectPublisher.FromProject( project );

			if ( publisher is null )
			{
				Log.Error( "[publish] ProjectPublisher.FromProject returned nothing" );
				return;
			}

			var (title, detail) = ChangeNotes( root );

			publisher.SetChangeDetails( title, detail );

			// THE PUBLISH DIES WITHOUT THIS, and not anywhere near here. OnProgressChanged is the
			// wizard's progress bar, and nothing else sets it - so from the console it stays null.
			// UploadFile reports progress through MainThread.Queue, the queued lambda invokes a
			// null Action, and the NullReferenceException surfaces with Queue as its top frame and
			// no mention of a delegate at all. It reads exactly like a threading fault, which cost
			// an afternoon of chasing one.
			//
			// It hid until there was something to send: a publish with zero files to upload never
			// reports progress, so the very first run of this - which had nothing to upload - was
			// green.
			var uploaded = 0;

			publisher.OnProgressChanged = () =>
			{
				// A line per file rather than per callback: progress fires several times per file
				// and a console is not a progress bar.
				var done = publisher.TotalFileCount - publisher.MissingFileCount;

				if ( done <= uploaded )
					return;

				uploaded = done;

				Log.Info( $"[publish] uploaded  {done}/{publisher.TotalFileCount}" );
			};

			// PrePublish is what the wizard runs between its screens - it builds the manifest, so
			// the file counts below are meaningless before it.
			await publisher.PrePublish( CancellationToken.None );

			Log.Info( $"[publish] package   {publisher.TargetPackageIdent}" );
			Log.Info( $"[publish] from      {root}" );
			Log.Info( $"[publish] files     {publisher.TotalFileCount} total, "
				+ $"{publisher.MissingFileCount} to upload ({publisher.MissingFileSize / 1024}kb)" );
			Log.Info( $"[publish] title     {title}" );

			if ( !string.IsNullOrWhiteSpace( detail ) )
				Log.Info( $"[publish] detail    {detail.Replace( "\n", " / " )}" );

			var before = await VersionOf( publisher.TargetPackageIdent );

			Log.Info( $"[publish] live now  {Describe( before )}" );

			if ( !commit )
			{
				Log.Info( "[publish] DRY RUN - nothing uploaded. `geppetto_publish commit` to send it." );
				return;
			}

			Log.Info( "[publish] uploading..." );

			var toUpload = publisher.MissingFileCount;

			await publisher.UploadFiles();

			// UPLOADFILES DOES NOT THROW ON A REJECTED FILE. Every failure is logged by the engine
			// and the task completes exactly as it does on success, so "it returned" says nothing
			// about whether anything landed. On 2026-09-05 an expired editor login sent all 450
			// files back 401 Unauthorized, and because nothing threw, this ran on to report the
			// unmoved version as "byte-identical content" - an explanation for a publish that had
			// never happened, which cost hours pointed at the wrong thing.
			//
			// The manifest is the one that knows: it counts what has NOT arrived, and the upload
			// loop walks that number down as files land. If it has not moved, neither did they.
			var stillMissing = publisher.MissingFileCount;

			if ( toUpload > 0 && stillMissing >= toUpload )
			{
				Log.Error( $"[publish] NOTHING UPLOADED - all {toUpload} files were rejected, and "
					+ "nothing was published." );
				Log.Error( "[publish] the usual cause is an expired editor login: the uploads come "
					+ "back 401 Unauthorized, one logged error per file above this line. Sign out "
					+ "and back in, or restart the editor, then run this again." );
				return;
			}

			if ( stillMissing > 0 )
			{
				Log.Error( $"[publish] only {toUpload - stillMissing} of {toUpload} files uploaded "
					+ $"- {stillMissing} were rejected, see the errors above. Stopping rather than "
					+ "publishing a package with holes in it." );
				return;
			}

			await publisher.Publish( null, CancellationToken.None );

			// PUBLISH RETURNS A BARE TASK, so "it did not throw" is all it tells us on its own -
			// and a publish that quietly changed nothing looks exactly like one that worked. The
			// backend knows the answer, so ask it: a VersionId that moved is the difference
			// between a new revision and a no-op, and it is the number to quote when somebody
			// says which version broke.
			Log.Info( $"[publish] published {publisher.TargetPackageIdent}" );

			var after = await SettledVersion( publisher.TargetPackageIdent, before );

			if ( before is not null && after is not null && after.VersionId == before.VersionId )
			{
				// EVERY FILE UPLOADED TO GET HERE, so byte-identical content is now the likely
				// reading rather than a guess - but it is still a reading, and this line used to
				// state it as fact about a run where nothing had uploaded at all. The manifest can
				// be refused on its own (`PublishManifest: Unauthorized`), which no count here can
				// see. So say what is known, then point at the log rather than closing the case.
				Log.Warning( $"[publish] version did not move (still {after.VersionId}). All "
					+ $"{toUpload} files uploaded, so this is most likely byte-identical content - "
					+ "the backend accepts that without making a new revision." );
				Log.Warning( "[publish] if that is not what you expected, read the editor log for "
					+ "errors around the publish before running it again." );
			}
			else
			{
				Log.Info( $"[publish] version   {Describe( before )} -> {Describe( after )}" );
			}
		}
		catch ( Exception e )
		{
			// The whole point of the dry run is that a shape change shows up here rather than as a
			// bad version on the backend. Say which step, and leave the wizard as the way through.
			// ToString, not Message: "Object reference not set to an instance of an object" names
			// neither the step nor the cause, and the stack is what turned it into a diagnosis.
			Log.Error( $"[publish] failed: {e}" );
			Log.Error( "[publish] the editor's own Publish dialog still works - use that." );
		}
	}

	/// <summary>
	/// The new revision, once the backend is actually serving it.
	///
	/// Publish returns as soon as the manifest is accepted, and for a moment after that the
	/// backend still answers with the OLD revision. Asking once and reporting the answer said
	/// "version did not move" about a publish that had moved it - the worst kind of wrong, since
	/// the natural next move is to publish again.
	///
	/// So give it a few seconds to settle. Coming back with the old revision after that is a real
	/// answer worth printing; coming back with it immediately never was.
	/// </summary>
	static async Task<Package.IRevision> SettledVersion( string ident, Package.IRevision before )
	{
		Package.IRevision after = null;

		// Thirty seconds, not ten. Ten was measured against a publish that settled quickly and then
		// cried "did not move" about one that took twenty - which is the one report here that must
		// never be wrong, since the obvious response to it is to publish again.
		for ( var attempt = 0; attempt < 30; attempt++ )
		{
			after = await VersionOf( ident );

			if ( before is null || after is null || after.VersionId != before.VersionId )
				return after;

			await Task.Delay( 1000 );
		}

		return after;
	}

	/// <summary>
	/// The revision the backend is currently serving for this package.
	///
	/// `useCache: false` on purpose. The cached copy is whatever this editor last saw, which after
	/// a publish is the version we just replaced - so a cached read would report the publish did
	/// nothing every single time.
	/// </summary>
	static async Task<Package.IRevision> VersionOf( string ident )
	{
		try
		{
			var package = await Package.FetchAsync( ident, false, false );
			return package?.Revision;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[publish] could not read the live version of {ident}: {e.Message}" );
			return null;
		}
	}

	static string Describe( Package.IRevision revision ) =>
		revision is null ? "unknown" : $"v{revision.VersionId} ({revision.FileCount} files, {revision.Created:yyyy-MM-dd HH:mm})";

	/// <summary>
	/// The last commit's subject and body, which is what the change notes should say.
	///
	/// Shelling out to git rather than parsing .git ourselves: the repo is right there and git is
	/// the thing that knows how to read it. If git is missing or this is not a checkout, the date
	/// fallback is what the wizard would have offered anyway, so nothing is worse than before.
	/// </summary>
	static (string Title, string Detail) ChangeNotes( string root )
	{
		var subject = Git( root, "log -1 --pretty=%s" );
		var body = Git( root, "log -1 --pretty=%b" );

		if ( string.IsNullOrWhiteSpace( subject ) )
			return ($"Changes on {DateTime.Now:yyyy-MM-dd}", "");

		return (subject.Trim(), WithoutTrailers( body ));
	}

	/// <summary>
	/// The commit body without its trailer block - Co-Authored-By, Signed-off-by and friends.
	///
	/// Those are addressed to the repository, not to somebody reading a package's release notes,
	/// and they sit at the end where they are easy to drop. Only a run of trailers at the very END
	/// goes: a line shaped like "Note: something" in the middle of a paragraph is prose.
	/// </summary>
	static string WithoutTrailers( string body )
	{
		var lines = (body ?? "").TrimEnd().Split( '\n' );
		var end = lines.Length;

		while ( end > 0 )
		{
			var line = lines[end - 1].Trim();

			if ( line.Length == 0 )
			{
				end--;
				continue;
			}

			var colon = line.IndexOf( ':' );

			if ( colon <= 0 || line.Contains( ' ' ) && line.IndexOf( ' ' ) < colon )
				break;

			end--;
		}

		return string.Join( "\n", lines[..end] ).Trim();
	}

	static string Git( string root, string arguments )
	{
		try
		{
			using var p = Process.Start( new ProcessStartInfo( "git", arguments )
			{
				WorkingDirectory = root,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			} );

			if ( p is null )
				return null;

			var output = p.StandardOutput.ReadToEnd();
			p.WaitForExit( 5000 );

			return p.ExitCode == 0 ? output : null;
		}
		catch
		{
			return null;
		}
	}
}
