using System;
using System.IO;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The AnimBindPose node, and a rigged .vmdl to carry it somewhere it can be compiled.
///
/// WHY THIS IS THIN. The node is a constant — every field copied off
/// `first_person_arms_preview.vmdl`, which ships as source. There is no arithmetic to check, so what
/// a headless test can honestly do is small: the node is one AnimBindPose inside one AnimationList,
/// its punctuation balances, and it goes where a RootNode's children go. Everything that actually
/// matters about it — whether the compiler accepts it, and whether the bones survive — is a compile,
/// which is why the sample file below exists.
///
/// WHAT THE NODE IS FOR. ModelDoc's own documentation says a model that is not fully static needs at
/// least an AnimBindPose or morph targets and IK data silently break. This project's skinned export
/// never had one. A wrong KV3 node fails as a model that will not load, which is worse than a model
/// missing a node.
/// </summary>
public static class VmdlAnimationTests
{
	public static void Run()
	{
		Section( "vmdl animation: the bind pose node" );

		var node = VmdlAnimation.BindPoseList();

		Check( "it is one AnimBindPose inside one AnimationList",
			Count( node, "_class = \"AnimBindPose\"" ) == 1
			&& Count( node, "_class = \"AnimationList\"" ) == 1,
			$"{Count( node, "_class = \"AnimBindPose\"" )} pose(s), {Count( node, "_class = \"AnimationList\"" )} list(s)" );

		Check( "braces balance", Count( node, "{" ) == Count( node, "}" ),
			$"{Count( node, "{" )} open, {Count( node, "}" )} close" );
		Check( "and brackets balance", Count( node, "[" ) == Count( node, "]" ),
			$"{Count( node, "[" )} open, {Count( node, "]" )} close" );

		// Spliced between a RootNode's other children, so it has to end the way they do.
		Check( "the node is a complete child entry, comma and all", node.TrimEnd( '\n' ).EndsWith( "}," ) );

		// EVERY FIELD, including the ones that look like defaults. The compiler's defaults are not
		// documented anywhere this project can read, and the file known to work carries all of them -
		// so a field quietly dropped here is a difference from the only evidence there is.
		foreach ( var field in new[]
		{
			"name", "activity_name", "activity_weight", "weight_list_name", "fade_in_time",
			"fade_out_time", "looping", "delta", "worldSpace", "hidden", "anim_markup_ordered",
			"disable_compression", "disable_interpolation", "enable_scale", "frame_count", "frame_rate",
		} )
		{
			Check( $"carries {field}", node.Contains( $"{field} = " ), "missing" );
		}

		Check( "and the list names its default root bone",
			node.Contains( "default_root_bone_name = \"\"" ) );
	}

	/// <summary>
	/// A skinned .vmdl around the rigged sample DMX, so the bind pose can be put in front of the
	/// compiler.
	///
	///     copy out/sample_rigged.{dmx,vmdl} into Assets/models/effigy_probe/
	///     register_external_assets, asset_compile, then kit_validate that folder
	///
	/// A model that compiles and loads is the answer. A compile error naming the node is the other
	/// one, and would mean the fields differ between a preview model and a plain one.
	///
	/// NO -90 YAW HERE, unlike the OBJ samples, and that is not an oversight: it is ModelDoc's OBJ
	/// importer that turns the mesh, and this one is a DMX.
	/// </summary>
	internal static void WriteSample( string outDir, Skeleton skeleton, PolyMesh mesh )
	{
		var vmdl =
			"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n"
			+ "{\n\trootNode = \n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren = \n\t\t[\n"
			+ "\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren = \n\t\t\t\t[\n"
			+ "\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n\t\t\t\t\t\tname = \"Body_LOD0\"\n"
			+ "\t\t\t\t\t\tchildren = \n\t\t\t\t\t\t[\n\t\t\t\t\t\t]\n"
			+ "\t\t\t\t\t\tfilename = \"models/effigy_probe/sample_rigged.dmx\"\n"
			+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
			+ "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n"
			+ "\t\t\t\t\t\timport_scale = 1.0\n"
			+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
			+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
			+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
			+ "\t\t\t\t\t\tparent_bone = \"\"\n\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n"
			// BOTH NODES, because the sample has to be what the editor writes. Without the markup
			// list this file compiled to a model with ONE bone out of two - measured, not feared -
			// so a sample missing it would answer a question about the sample.
			+ VmdlAnimation.BoneMarkupList( skeleton )
			+ VmdlAnimation.BindPoseList()
			// AND THE MATERIAL LIST, for the same reason as the two above: the editor writes one,
			// so a sample without it is not the thing being checked. Its absence is exactly what
			// made this file useless for catching the bug where an unbound slot compiled to the
			// missing-material shader - the sample had no slots named at all.
			+ VmdlMaterials.GroupList( mesh, null, null )
			+ "\t\t]\n\t\tmodel_archetype = \"\"\n\t\tprimary_associated_entity = \"\"\n"
			+ "\t\tanim_graph_name = \"\"\n\t\tbase_model_name = \"\"\n\t}\n}\n";

		File.WriteAllText( Path.Combine( outDir, "sample_rigged.vmdl" ), vmdl );

		Check( $"wrote {outDir}/sample_rigged.vmdl - compile it beside sample_rigged.dmx and it should load with {skeleton.Count} bones",
			Count( vmdl, "_class = \"AnimBindPose\"" ) == 1
			&& Count( vmdl, "_class = \"BoneMarkup\"" ) == skeleton.Count
			&& Count( vmdl, "_class = \"MaterialGroupList\"" ) == 1
			&& vmdl.Contains( VmdlMaterials.DefaultMaterial )
			&& Count( vmdl, "{" ) == Count( vmdl, "}" ) );
	}

	static int Count( string text, string needle )
	{
		var count = 0;
		var at = 0;

		while ( (at = text.IndexOf( needle, at, StringComparison.Ordinal )) >= 0 )
		{
			count++;
			at += needle.Length;
		}

		return count;
	}
}
