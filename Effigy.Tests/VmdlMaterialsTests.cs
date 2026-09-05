using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// The MaterialGroupList node, which is the step that had been missing since face materials could
/// be assigned: the slots were right, the exporters named them, and the .vmdl bound none of them.
///
/// WHAT THESE CAN AND CANNOT CHECK. Whether the ENGINE honours a remap is a compile, and the keys
/// here were not guessed — they are the ones this project's lightswitch and first-person arms
/// models already ship. What a headless test can say is that the node names the slots the mesh
/// uses, that it never asks ModelDoc to replace everything with default, and that a part nobody
/// painted still writes a list so the compiler cannot fill one in.
/// </summary>
public static class VmdlMaterialsTests
{
	public static void Run()
	{
		Section( "vmdl materials: a bound slot reaches the node" );
		TestBoundSlot();

		Section( "vmdl materials: the whole part, which is slot 0" );
		TestBaseMaterial();

		Section( "vmdl materials: the node, and what it does with nothing" );
		TestAlwaysWritesTheList();
		TestDisplayNameIsNotARemap();

		Section( "vmdl materials: a painted part binds a material that reads paint" );
		TestPaintedFallback();

		Section( "vmdl materials: tint or cover" );
		TestBlendChoosesTheFallback();

		Section( "vmdl materials: several slots, several spellings" );
		TestAliases();
		TestTwoSlots();
	}

	static void TestBoundSlot()
	{
		var studio = Painted( out var mesh, slot: 2, "materials/diner/diner_tile_floor.vmat" );
		var text = VmdlMaterials.GroupList( studio, mesh );

		Check( "it is a DefaultMaterialGroup inside a MaterialGroupList",
			CountOf( text, "_class = \"DefaultMaterialGroup\"" ) == 1
			&& CountOf( text, "_class = \"MaterialGroupList\"" ) == 1 );

		Check( "braces balance", CountOf( text, "{" ) == CountOf( text, "}" ),
			$"{CountOf( text, "{" )} open, {CountOf( text, "}" )} close" );
		Check( "and brackets balance", CountOf( text, "[" ) == CountOf( text, "]" ) );

		Check( "the node is a complete child entry, comma and all",
			text.TrimEnd( '\n' ).EndsWith( "}," ) );

		Check( "use_global_default is false — true is how every slot becomes default.vmat",
			text.Contains( "use_global_default = false" )
			&& !text.Contains( "use_global_default = true" ) );

		// THE FIELD, not the string anywhere in the node. default.vmat now appears legitimately as
		// the `to` of an unbound slot's own remap, which is the opposite of the failure this
		// guards: a GLOBAL default replaces every slot at once, a per-slot remap replaces one.
		Check( "and there is no global default material waiting to replace them",
			text.Contains( "global_default_material = \"\"" )
			&& !text.Contains( $"global_default_material = \"{VmdlMaterials.DefaultMaterial}\"" ) );

		Check( "the bound vmat is the remap target",
			text.Contains( "to = \"materials/diner/diner_tile_floor.vmat\"" ) );
	}

	static void TestBaseMaterial()
	{
		// Double-click in the Materials dock binds slot 0, which is every face nobody has painted.
		// A remap that only walked FaceMaterialFeature would miss it entirely, and the whole part
		// would compile as default — the original complaint, for the most ordinary assignment.
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;
		studio.MaterialNames[0] = "materials/wood/oak.vmat";
		studio.Rebuild();

		var mesh = studio.ToMesh();
		var text = VmdlMaterials.GroupList( studio, mesh );

		Check( "slot 0 is remapped when it carries a vmat",
			text.Contains( "to = \"materials/wood/oak.vmat\"" ) );

		Check( "and every face is still on slot 0, so the whole part is that material",
			mesh.Faces.All( f => f.Material == 0 ) );
	}

	static void TestAlwaysWritesTheList()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;
		studio.Rebuild();

		var text = VmdlMaterials.GroupList( studio, studio.ToMesh() );

		Check( "an unpainted part still writes a MaterialGroupList",
			text.Contains( "_class = \"MaterialGroupList\"" ) );

		// NOT "no remaps". An unpainted part used to write an empty list, which left the compiled
		// model asking for `material_0` - no asset answers to that, so it rendered in the bright
		// red missing-material shader. Every slot the mesh uses names a real asset now.
		var bare = VmdlMaterials.Remaps( studio.ToMesh(), studio.NameForSlot, studio.MaterialNames );

		Check( "its one slot is remapped rather than left dangling", bare.Count == 1,
			string.Join( ", ", bare.Select( r => $"{r.From}->{r.To}" ) ) );

		Check( "from the name the mesh writers emit", bare[0].From == "material_0", bare[0].From );

		Check( "to a material that exists, so it renders plain instead of red",
			bare[0].To == VmdlMaterials.DefaultMaterial, bare[0].To );

		Check( "and still refuses the global default, so ModelDoc cannot fill one in",
			text.Contains( "use_global_default = false" ) );
	}

	static void TestDisplayNameIsNotARemap()
	{
		// A name that is not a vmat path is what the mesh writers already emit. Pointing `to` at
		// it would be a remap to an asset that does not exist.
		var studio = Painted( out var mesh, slot: 3, "anodised" );
		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );

		Check( "a hand-typed display name is never a remap TARGET",
			remaps.All( r => r.To != "anodised" ),
			string.Join( ", ", remaps.Select( r => $"{r.From}->{r.To}" ) ) );

		// It still has to go somewhere: the slot is on the mesh, so the compiled model will ask
		// for it by name, and "anodised" is no more of an asset than "material_3" is. The part
		// also carries an untouched slot 0, so this is every slot falling back, not just the one.
		Check( "it falls back to the default instead",
			remaps.Any( r => r.From == "anodised" )
			&& remaps.All( r => r.To == VmdlMaterials.DefaultMaterial ),
			string.Join( ", ", remaps.Select( r => $"{r.From}->{r.To}" ) ) );
	}

	/// <summary>
	/// A mesh carrying vertex colours must bind the one material that reads them.
	///
	/// complex.shader - default.vmat, white.vmat, every ordinary material - does not look at the
	/// COLOR stream at all, so paint bound to any of them is discarded by the shader. That is why
	/// painting appeared to do nothing at all rather than appearing subtle.
	/// </summary>
	static void TestPaintedFallback()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;
		studio.Rebuild();

		var mesh = studio.ToMesh();

		Check( "an unpainted mesh takes the plain default",
			VmdlMaterials.FallbackFor( studio, mesh ) == VmdlMaterials.DefaultMaterial );

		// Paint it: colours parallel to the positions is all HasVertexColors asks for.
		mesh.VertexColors = new Vec4[mesh.Positions.Count];

		for ( var i = 0; i < mesh.VertexColors.Length; i++ )
			mesh.VertexColors[i] = new Vec4( 1f, 0f, 0f, 1f );

		Check( "a painted mesh takes the vertex-colour material instead",
			VmdlMaterials.FallbackFor( studio, mesh ) == VmdlMaterials.PaintedMaterial,
			VmdlMaterials.FallbackFor( studio, mesh ) );

		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames,
			VmdlMaterials.FallbackFor( studio, mesh ) );

		Check( "and that is what the remap list carries",
			remaps.Count > 0 && remaps.All( r => r.To == VmdlMaterials.PaintedMaterial ),
			string.Join( ", ", remaps.Select( r => $"{r.From}->{r.To}" ) ) );

		// A slot somebody dropped a material on is still that material - paint does not seize it.
		var bound = Painted( out var boundMesh, slot: 1, "materials/diner/diner_tile_floor.vmat" );
		var boundRemaps = VmdlMaterials.Remaps( boundMesh, bound.NameForSlot, bound.MaterialNames,
			VmdlMaterials.PaintedMaterial );

		Check( "a dropped material survives on a painted part",
			boundRemaps.Any( r => r.To == "materials/diner/diner_tile_floor.vmat" ) );
	}

	/// <summary>
	/// Paint's Blend choice decides what an unbound slot compiles to.
	///
	/// ON AN UNPAINTED MESH ONLY, now. A mesh actually carrying vertex colours has to bind the
	/// material that reads them whichever way Blend is set - see TestPaintedFallback - because
	/// telling Tint from Replace needs a shader combining a base texture with the vertex colour,
	/// and nothing shipped does one.
	/// </summary>
	static void TestBlendChoosesTheFallback()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 2f;

		var paint = studio.Add( new PaintFeature() );
		studio.Rebuild();

		Check( "a paint layer tints by default", paint.Blend.Value == "Tint", paint.Blend.Value );

		Check( "so an unbound slot takes the default material",
			VmdlMaterials.FallbackFor( studio ) == VmdlMaterials.DefaultMaterial );

		paint.Blend.Index = Array.IndexOf( paint.Blend.Options, "Replace" );

		Check( "asking it to cover swaps the surface for white",
			VmdlMaterials.FallbackFor( studio ) == VmdlMaterials.ReplaceMaterial,
			VmdlMaterials.FallbackFor( studio ) );

		var remaps = VmdlMaterials.Remaps( studio.ToMesh(), studio.NameForSlot, studio.MaterialNames,
			VmdlMaterials.FallbackFor( studio ) );

		Check( "and that is what reaches the remap list",
			remaps.Count == 1 && remaps[0].To == VmdlMaterials.ReplaceMaterial,
			string.Join( ", ", remaps.Select( r => $"{r.From}->{r.To}" ) ) );

		// A BOUND SLOT IS NOT TOUCHED. Dropping a material on a face is a deliberate choice, and
		// covering it is not what Replace is for - it moves the slots that had nothing.
		var bound = Painted( out var mesh, slot: 1, "materials/diner/diner_tile_floor.vmat" );
		var boundRemaps = VmdlMaterials.Remaps( mesh, bound.NameForSlot, bound.MaterialNames,
			VmdlMaterials.ReplaceMaterial );

		Check( "a slot with a material dropped on it still points at that material",
			boundRemaps.Any( r => r.To == "materials/diner/diner_tile_floor.vmat" ),
			string.Join( ", ", boundRemaps.Select( r => $"{r.From}->{r.To}" ) ) );

		// A layer that produced nothing does not get a vote.
		paint.Suppressed = true;

		Check( "a suppressed paint layer stops asking for it",
			VmdlMaterials.FallbackFor( studio ) == VmdlMaterials.DefaultMaterial );
	}

	static void TestAliases()
	{
		var studio = Painted( out var mesh, slot: 1, "materials/halo/characters/elite/halo_3.vmat" );
		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );
		var froms = remaps.Select( r => r.From ).ToList();

		Check( "the full path the exporters write is a from",
			froms.Contains( "materials/halo/characters/elite/halo_3.vmat" ) );

		Check( "so is the filename, which is what the lightswitch files remap from",
			froms.Contains( "halo_3.vmat" ) );

		Check( "and both with .vmat stripped, because ModelDoc drops everything after a period",
			froms.Contains( "halo_3" ) && froms.Contains( "materials/halo/characters/elite/halo_3" ) );

		// Excluding the fallback: the part's other slot is unbound and remaps to the default, which
		// is not an alias of this vmat and never was.
		Check( "every alias points at the same vmat",
			remaps.Where( r => r.To != VmdlMaterials.DefaultMaterial )
				.All( r => r.To == "materials/halo/characters/elite/halo_3.vmat" ),
			string.Join( ", ", remaps.Select( r => $"{r.From}->{r.To}" ) ) );
	}

	static void TestTwoSlots()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );
		var side = FaceIndexFacing( body.Mesh, new Vec3( 1, 0, 0 ) );

		var paintTop = studio.Add( new FaceMaterialFeature() );
		paintTop.Material.Value = 1;
		paintTop.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );

		var paintSide = studio.Add( new FaceMaterialFeature() );
		paintSide.Material.Value = 2;
		paintSide.Faces.Add( FacePlane.Capture( body, side, body.Mesh.FaceCentroid( body.Mesh.Faces[side] ) ) );

		studio.MaterialNames[0] = "materials/wood/oak.vmat";
		studio.MaterialNames[1] = "materials/metal/brushed.vmat";
		studio.MaterialNames[2] = "materials/rubber/grip.vmat";
		studio.Rebuild();

		var mesh = studio.ToMesh();
		var remaps = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );
		var targets = remaps.Select( r => r.To ).Distinct().OrderBy( t => t ).ToList();

		Check( "three bound slots make three remap targets",
			targets.Count == 3,
			string.Join( ", ", targets ) );

		Check( "oak, brushed and grip are all there",
			targets.Contains( "materials/wood/oak.vmat" )
			&& targets.Contains( "materials/metal/brushed.vmat" )
			&& targets.Contains( "materials/rubber/grip.vmat" ) );

		// A name sitting on a slot no face wears must not appear. Slot 7 is named and unused.
		studio.MaterialNames[7] = "materials/unused/spare.vmat";
		var after = VmdlMaterials.Remaps( mesh, studio.NameForSlot, studio.MaterialNames );

		Check( "a named slot with no faces is not remapped",
			after.All( r => r.To != "materials/unused/spare.vmat" ) );
	}

	// --- helpers ----------------------------------------------------------------------------------

	static PartStudio Painted( out PolyMesh mesh, int slot, string name )
	{
		var studio = new PartStudio();

		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = 4f;
		box.SizeY.Value = 3f;
		box.SizeZ.Value = 2f;
		studio.Rebuild();

		var body = studio.Bodies.Single();
		var top = FaceIndexFacing( body.Mesh, new Vec3( 0, 0, 1 ) );

		var paint = studio.Add( new FaceMaterialFeature() );
		paint.Material.Value = slot;
		paint.Faces.Add( FacePlane.Capture( body, top, body.Mesh.FaceCentroid( body.Mesh.Faces[top] ) ) );
		studio.MaterialNames[slot] = name;
		studio.Rebuild();

		mesh = studio.ToMesh();
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

	static int CountOf( string text, string needle )
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
