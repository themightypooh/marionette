using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// CollisionBuilder's shapes, written as the PhysicsShapeList node a .vmdl carries them in.
///
/// WHAT THIS CLOSES. The shapes have been correct and tested for a while and had nowhere to go: the
/// editor could only report them to the console, because putting them in the .vmdl means writing
/// ModelDoc's KV3 and nothing here had seen that schema. A guessed node was the wrong risk to take
/// — the failure is a model that stops loading, not a model without collision.
///
/// EVERY KEY BELOW WAS MEASURED, NOT GUESSED. Each shape was written into a probe .vmdl, compiled by
/// the engine, and the compiled model's own physics bounds read back. That is what settles the
/// questions no amount of reading could:
///
/// - `PhysicsShapeBox.dimensions` is the box's FULL size, not its half-extents. A 32 wrote 32 across.
/// - `PhysicsShapeBox` is placed by `origin` — NOT by `center`, `translation` or `position`, all
///   three of which compile happily and leave the box at the model origin. `angles` works on it too,
///   and is written for completeness even though nothing reaches this with a rotated box: a rotated
///   Transform spoils the whole decomposition upstream (see CollisionBuilder).
/// - `PhysicsShapeSphere` is placed by `center`, and by nothing else. It is the one shape whose
///   placement key is not the box's.
/// - `PhysicsShapeCapsule` and `PhysicsShapeCylinder` carry `point0`/`point1` plus a `radius`, and
///   the points are where they go — the shape has no separate placement key and needs none. Read off
///   citizen_physicsshapelist.vmdl_prefab, which ships as source, and confirmed the same way.
/// - `PhysicsShapeHull.hull_vertices` takes points in MODEL SPACE and is exact: a hull written from
///   a 20-unit cube offset along x measured 20 across, so nothing is being re-centred underneath.
///
/// Base keys `parent_bone`, `surface_prop` and `collision_tags` come off the same citizen prefab.
///
/// WHY THE KERNEL AND NOT THE EDITOR. It is text, it has no engine types in it, and it is the half
/// that can be checked without s&amp;box — which is the half that was worth testing. The editor's job is
/// reduced to splicing the node into the document it already builds.
/// </summary>
public static class VmdlPhysics
{
	/// <summary>
	/// The whole PhysicsShapeList node, indented to sit among a RootNode's children, or an empty
	/// string when there is nothing to write.
	///
	/// EMPTY RATHER THAN AN EMPTY LIST. A PhysicsShapeList with no children is a model that declares
	/// it has collision and has none, which is worse than a model that says nothing: the first reads
	/// as a physics bug and the second reads as a missing step.
	/// </summary>
	public static string ShapeList( IReadOnlyList<CollisionShape> shapes,
		string surfaceProp = "default", string collisionTags = "solid" )
	{
		if ( shapes is null || shapes.Count == 0 )
			return "";

		var body = new StringBuilder();
		var written = 0;

		foreach ( var shape in shapes )
		{
			var node = Shape( shape, surfaceProp, collisionTags );

			if ( node is null )
				continue;

			body.Append( node );
			written++;
		}

		if ( written == 0 )
			return "";

		var sb = new StringBuilder();

		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"PhysicsShapeList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		sb.Append( body );
		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );

		return sb.ToString();
	}

	/// <summary>
	/// The node this repo already ships on every hand-authored model: build the physics from the
	/// render geometry.
	///
	/// Kept beside the exact shapes because it is the honest answer for a part whose history cannot
	/// be read AND whose hulls would be a poor likeness — a tube, say, whose bore a hull fills in.
	/// It is what Assets/models/tamagotchi and Assets/models/lightswitch use, so it is proven in this
	/// project rather than merely proven in general.
	/// </summary>
	public static string MeshFromRender( string surfaceProp = "default", string collisionTags = "solid" ) =>
		"\t\t\t{\n"
		+ "\t\t\t\t_class = \"PhysicsShapeList\"\n"
		+ "\t\t\t\tchildren = \n"
		+ "\t\t\t\t[\n"
		+ "\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"PhysicsMeshFromRender\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ $"\t\t\t\t\t\tsurface_prop = \"{surfaceProp}\"\n"
		+ $"\t\t\t\t\t\tcollision_tags = \"{collisionTags}\"\n"
		+ "\t\t\t\t\t},\n"
		+ "\t\t\t\t]\n"
		+ "\t\t\t},\n";

	/// <summary>One shape, or null for one this cannot describe.</summary>
	static string Shape( CollisionShape shape, string surfaceProp, string collisionTags )
	{
		if ( shape is null )
			return null;

		var sb = new StringBuilder();

		switch ( shape.Kind )
		{
			case CollisionKind.Box:
				Open( sb, "PhysicsShapeBox" );
				// Doubled: CollisionShape.Size is half-extents and `dimensions` is the full size.
				// Getting this backwards halves every collision box in the model and nothing about
				// the compile complains.
				Line( sb, "dimensions", Vector( shape.Size * 2f ) );
				Line( sb, "origin", Vector( shape.Position ) );
				break;

			case CollisionKind.Sphere:
				Open( sb, "PhysicsShapeSphere" );
				Line( sb, "radius", Number( shape.Size.x ) );
				// `center`, and only `center`. See the class comment.
				Line( sb, "center", Vector( shape.Position ) );
				break;

			case CollisionKind.Cylinder:
				// Size.z is the half-height, and the axis is z - CollisionBuilder cannot produce a
				// cylinder on any other axis, because a rotated Transform spoils the decomposition
				// before it gets here.
				Open( sb, "PhysicsShapeCylinder" );
				Line( sb, "radius", Number( shape.Size.x ) );
				Line( sb, "point0", Vector( shape.Position - new Vec3( 0, 0, shape.Size.z ) ) );
				Line( sb, "point1", Vector( shape.Position + new Vec3( 0, 0, shape.Size.z ) ) );
				break;

			case CollisionKind.Hull:
				if ( shape.Points is not { Count: >= 4 } )
					return null;

				Open( sb, "PhysicsShapeHull" );
				sb.Append( "\t\t\t\t\t\thull_vertices = \n" );
				sb.Append( "\t\t\t\t\t\t[\n" );

				foreach ( var p in shape.Points )
					sb.Append( $"\t\t\t\t\t\t\t{Vector( p )},\n" );

				sb.Append( "\t\t\t\t\t\t]\n" );
				break;

			default:
				return null;
		}

		sb.Append( "\t\t\t\t\t},\n" );
		return sb.ToString();

		void Open( StringBuilder into, string cls )
		{
			into.Append( "\t\t\t\t\t{\n" );
			into.Append( $"\t\t\t\t\t\t_class = \"{cls}\"\n" );
			into.Append( "\t\t\t\t\t\tparent_bone = \"\"\n" );
			into.Append( $"\t\t\t\t\t\tsurface_prop = \"{surfaceProp}\"\n" );
			into.Append( $"\t\t\t\t\t\tcollision_tags = \"{collisionTags}\"\n" );
		}

		static void Line( StringBuilder into, string key, string value ) =>
			into.Append( $"\t\t\t\t\t\t{key} = {value}\n" );
	}

	static string Vector( Vec3 v ) => $"[ {Number( v.x )}, {Number( v.y )}, {Number( v.z )} ]";

	/// <summary>
	/// A KV3 float, always with a decimal point and never with an exponent.
	///
	/// InvariantCulture is not a nicety here: a machine set to a comma decimal separator would write
	/// `[ 1,5, 0,0, 0,0 ]`, which is a six-element array of integers as far as the parser is
	/// concerned, and the compile would either fail or silently take the wrong numbers. And "R" or a
	/// plain ToString can produce `1E-05`, which KV3 does not read as a number at all.
	/// </summary>
	static string Number( float value )
	{
		if ( !float.IsFinite( value ) )
			value = 0f;

		var text = value.ToString( "0.0######", CultureInfo.InvariantCulture );

		// "-0.0" is a valid float and an eyesore in a file people read.
		return text == "-0.0" ? "0.0" : text;
	}
}
