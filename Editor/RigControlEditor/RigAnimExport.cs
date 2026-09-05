using Editor;
using Effigy;
using Marionette.EditorTools;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Skeleton = Effigy.Skeleton;

namespace Marionette.Tools;

/// <summary>
/// Turns the open .riganim into a compiled model the game can play.
///
/// WHY THIS LIVES IN MARIONETTE, NOT ONLY EFFIGY. Effigy compiles clips INTO a model it authored.
/// Most people here are posing first-person arms or citizen, which they did not author, and the
/// only way they had to "export" was to leave this window, open a different tool, and add the
/// clip to a list that then compiles a different model. There was no button. This is the button.
///
/// WHAT IT WRITES. An animation-only .vmdl whose Base Model is the clip's source model, plus the
/// baked .dmx the compiler reads. That is the documented way to add sequences to an existing
/// model (s&amp;box Model Editor: "Your new VMDL should only hold new animations"). The game then
/// sees a model that looks like the original and has this clip as a named sequence.
/// </summary>
internal static class RigAnimExport
{
	public sealed class Result
	{
		public bool Ok;
		public string Error;
		public string SequenceName;
		public string VmdlAssetPath;
		public string DmxAssetPath;
		public int Frames;
		public int MatchedBones;
		public int SkeletonBones;
		public bool Compiled;
	}

	public static Result Export( RigAnimDocument doc, Asset riganim, string clipName, bool looping )
	{
		if ( doc is null )
			return Fail( "Nothing open to export." );

		if ( riganim is null )
			return Fail( "Save the clip first — export writes next to the .riganim." );

		if ( doc.SourceModel is null )
			return Fail( "Set a Model in BonesObject first — export has to know which skeleton this clip is for." );

		if ( doc.BoneTracks is null || !doc.BoneTracks.Any( t => t.Keyframes.Count > 0 ) )
			return Fail( "Nothing to export — key some bones first." );

		var name = string.IsNullOrWhiteSpace( clipName )
			? Path.GetFileNameWithoutExtension( riganim.Name )
			: clipName.Trim();

		if ( string.IsNullOrWhiteSpace( name ) )
			name = "clip";

		var fileStem = Sanitise( name );
		var modelPath = PathOf( doc.SourceModel );

		if ( string.IsNullOrWhiteSpace( modelPath ) )
			return Fail( "The source model has no path, so a Base Model vmdl cannot be written." );

		Skeleton skeleton;

		try
		{
			skeleton = SkeletonFromModel( doc.SourceModel );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not read the model's skeleton: {e.Message}" );
		}

		if ( skeleton.Count == 0 )
			return Fail( "The source model has no bones — there is nothing to animate." );

		AnimClip clip;
		int matched;
		List<string> unmatched;

		try
		{
			clip = EffigyAnimExport.ToClip( doc, skeleton, name, looping, out matched, out unmatched );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not sample the clip: {e.Message}" );
		}

		if ( matched == 0 )
		{
			return Fail( "This clip names none of the model's bones, so it would compile and animate "
				+ "nothing. It was probably authored against a different model." );
		}

		var assetsRoot = EffigyAssetFolder.AssetsRoot();

		if ( string.IsNullOrWhiteSpace( assetsRoot ) )
			return Fail( "Could not find the project's Assets folder." );

		var folder = FolderOf( riganim, assetsRoot );

		if ( folder is null )
			return Fail( "The .riganim is not inside Assets, so there is nowhere legal to write the export." );

		Directory.CreateDirectory( folder );

		var dmxFile = $"{fileStem}.dmx";
		var vmdlFile = $"{fileStem}.vmdl";
		var dmxAbs = Path.Combine( folder, dmxFile );
		var vmdlAbs = Path.Combine( folder, vmdlFile );

		try
		{
			DmxAnimWriter.WriteFile( dmxAbs, skeleton, clip, Path.GetFileNameWithoutExtension( modelPath ) );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not write the animation file: {e.Message}" );
		}

		var relativeFolder = Path.GetRelativePath( assetsRoot, folder ).Replace( '\\', '/' );

		if ( relativeFolder == "." )
			relativeFolder = "";

		var dmxAsset = string.IsNullOrEmpty( relativeFolder ) ? dmxFile : $"{relativeFolder}/{dmxFile}";
		var vmdlAsset = string.IsNullOrEmpty( relativeFolder ) ? vmdlFile : $"{relativeFolder}/{vmdlFile}";

		var entry = new VmdlAnimation.ClipEntry( name, dmxAsset, looping );

		try
		{
			File.WriteAllText( vmdlAbs, AnimationOnlyVmdl( modelPath, entry ) );
		}
		catch ( Exception e )
		{
			return Fail( $"Could not write the model: {e.Message}" );
		}

		if ( unmatched.Count > 0 )
		{
			Log.Warning( $"[Marionette] clip '{name}' poses {unmatched.Count} bone(s) this model does "
				+ $"not have, which will not animate: {string.Join( ", ", unmatched.Take( 8 ) )}"
				+ (unmatched.Count > 8 ? " ..." : "") );
		}

		EffigyAssetFolder.Register( folder );

		var asset = AssetSystem.FindByPath( vmdlAsset ) ?? AssetSystem.RegisterFile( vmdlAbs );
		var compiled = false;

		if ( asset is null )
		{
			Log.Warning( $"[Marionette] wrote {vmdlAsset} but the asset system could not find it" );
		}
		else
		{
			asset.Compile( true );
			compiled = !asset.IsCompileFailed;

			if ( !compiled )
			{
				Log.Warning( $"[Marionette] {vmdlAsset} compile FAILED — the compiler's own output "
					+ "above says why. The .dmx is on disk either way." );
			}
		}

		Log.Info( $"[Marionette] exported {vmdlAsset} — sequence '{name}', {clip.FrameCount} frame(s), "
			+ $"{matched}/{skeleton.Count} bone(s) posed, compiled={compiled}" );

		return new Result
		{
			Ok = true,
			SequenceName = name,
			VmdlAssetPath = vmdlAsset,
			DmxAssetPath = dmxAsset,
			Frames = clip.FrameCount,
			MatchedBones = matched,
			SkeletonBones = skeleton.Count,
			Compiled = compiled,
		};
	}

	static Result Fail( string error ) => new() { Ok = false, Error = error };

	/// <summary>
	/// The model's bind-pose skeleton, in parent-before-child order, which is what
	/// <see cref="Skeleton.AddBone"/> requires and what the DMX writer walks.
	///
	/// Bind locals are converted the same way RigViewport.BindPoseFor does: despite the name,
	/// Bone.LocalTransform is MODEL space, and stacking it as parent-space explodes the mesh.
	/// </summary>
	public static Skeleton SkeletonFromModel( Model model )
	{
		var skeleton = new Skeleton();

		if ( model?.Bones is not { } bones )
			return skeleton;

		var remaining = bones.AllBones.ToList();
		var indexByName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

		while ( remaining.Count > 0 )
		{
			var next = remaining.Find( b =>
				b.Parent is null || indexByName.ContainsKey( b.Parent.Name ) );

			if ( next is null )
			{
				// A parent the collection does not list - treat the rest as roots rather than
				// hanging the export. Better a slightly wrong hierarchy than no file.
				next = remaining[0];
			}

			remaining.Remove( next );

			if ( string.IsNullOrWhiteSpace( next.Name ) || indexByName.ContainsKey( next.Name ) )
				continue;

			var parent = next.Parent is { } p && indexByName.TryGetValue( p.Name, out var parentIndex )
				? parentIndex
				: -1;

			var local = BindLocal( next );

			skeleton.AddBone( next.Name, parent, EffigyAnimExport.ToXform( local ) );
			indexByName[next.Name] = skeleton.Count - 1;
		}

		return skeleton;
	}

	static Transform BindLocal( BoneCollection.Bone bone ) =>
		bone.Parent is { } parent
			? parent.LocalTransform.ToLocal( bone.LocalTransform )
			: bone.LocalTransform;

	static string PathOf( Model model )
	{
		if ( model is null )
			return null;

		var path = model.Name;

		return string.IsNullOrWhiteSpace( path ) ? null : path.Replace( '\\', '/' );
	}

	static string FolderOf( Asset riganim, string assetsRoot )
	{
		var relative = (riganim.Path ?? "").Replace( '/', Path.DirectorySeparatorChar );
		var abs = Path.GetFullPath( Path.Combine( assetsRoot, relative ) );
		var folder = Path.GetDirectoryName( abs );

		if ( string.IsNullOrWhiteSpace( folder ) )
			return null;

		var full = Path.GetFullPath( folder );

		return full.StartsWith( assetsRoot, StringComparison.OrdinalIgnoreCase ) ? full : null;
	}

	/// <summary>
	/// Animation-only vmdl: Base Model is the original, children are just the AnimationList.
	/// Mesh, physics and materials stay on the base — putting them here is how you accidentally
	/// replace the model instead of extending it.
	/// </summary>
	static string AnimationOnlyVmdl( string baseModel, VmdlAnimation.ClipEntry clip ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n"
		+ "{\n"
		+ "\trootNode = \n"
		+ "\t{\n"
		+ "\t\t_class = \"RootNode\"\n"
		+ "\t\tchildren = \n"
		+ "\t\t[\n"
		+ VmdlAnimation.AnimationList( clip )
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ "\t\tanim_graph_name = \"\"\n"
		+ $"\t\tbase_model_name = \"{baseModel}\"\n"
		+ "\t}\n"
		+ "}\n";

	static string Sanitise( string name )
	{
		var chars = name.Select( c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' ? c : '_' )
			.ToArray();

		var cleaned = new string( chars ).Trim( '_' );

		return string.IsNullOrEmpty( cleaned ) ? "clip" : cleaned.ToLowerInvariant();
	}
}
