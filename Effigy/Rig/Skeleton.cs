using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One bone: a name, a parent, and where it sits in the bind pose.
///
/// Local is relative to the parent, which is what every skeletal format stores and what makes a
/// chain behave when a parent moves. World bind transforms are derived by walking up the parents
/// rather than stored, so there is exactly one source of truth and no chance of the two drifting
/// apart after an edit.
///
/// Length exists because a bone needs a tail as well as a head. The head is the transform's
/// origin; the tail is Length along the bone's own +Y. That is Blender's convention, and it is
/// what auto-weighting needs — a bone is a SEGMENT to measure distance to, not a point. It is also
/// what <see cref="SoftSolver"/> simulates: the tail is the particle, the head follows the parent.
/// </summary>
public sealed class Bone
{
	public string Name;

	/// <summary>Index into Skeleton.Bones, or -1 for a root. Always less than this bone's own
	/// index — see Skeleton.AddBone.</summary>
	public int Parent;

	/// <summary>Bind pose relative to the parent.</summary>
	public Xform Local;

	public float Length;

	/// <summary>
	/// Physical softness, or null for a rigid bone - which is nearly all of them, and why this
	/// hangs off the bone rather than adding four fields to every one. See <see cref="SoftBone"/>.
	/// </summary>
	public SoftBone Soft;

	public Bone( string name, int parent, Xform local, float length )
	{
		Name = name;
		Parent = parent;
		Local = local;
		Length = length;
	}

	public Bone Clone() => new( Name, Parent, Local, Length ) { Soft = Soft?.Clone() };
}

/// <summary>
/// A bone hierarchy with a bind pose. Engine-free, like everything else in here — the s&amp;box and
/// Godot sides convert at the boundary.
///
/// BONES ARE STORED IN TOPOLOGICAL ORDER: a bone's parent always has a lower index. AddBone
/// enforces it by refusing a parent that does not exist yet, which makes cycles unrepresentable
/// rather than merely invalid, and means WorldBind can never loop forever. It also happens to be
/// what SMD's node block wants, but that is a consequence, not the reason.
/// </summary>
public sealed class Skeleton
{
	public List<Bone> Bones = new();

	public int Count => Bones.Count;

	/// <summary>
	/// Add a bone under an existing parent (-1 for a root). Returns its index.
	///
	/// Throws if the parent does not already exist. That is the constraint that keeps the list
	/// topologically ordered and the hierarchy acyclic.
	/// </summary>
	public int AddBone( string name, int parent, Xform local, float length = 1f )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A bone needs a name — every consuming format keys on it" );

		if ( parent < -1 || parent >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( parent ),
				$"Parent {parent} does not exist yet. Add parents before children." );

		if ( IndexOf( name ) >= 0 )
			throw new ArgumentException( $"A bone called '{name}' already exists" );

		Bones.Add( new Bone( name, parent, local, length ) );
		return Bones.Count - 1;
	}

	/// <summary>
	/// Add a bone from a head and tail point in WORLD bind space, which is what drawing a bone
	/// chain in a viewport produces.
	///
	/// The bone's +Y is aimed head→tail; the other two axes are any stable perpendicular pair,
	/// since nothing here has a concept of roll yet. If roll ever matters — it will, the moment
	/// anyone hand-authors a twist — LocalFromWorldPoints is the function that grows a parameter,
	/// and nothing else has to change.
	/// </summary>
	public int AddBoneFromPoints( string name, int parent, Vec3 head, Vec3 tail )
	{
		var (local, length) = LocalFromWorldPoints( parent, head, tail, name );
		return AddBone( name, parent, local, length );
	}

	/// <summary>
	/// Move an existing bone's head and tail in WORLD bind space — a numeric edit standing in for
	/// the click that placed it, or a correction after the fact. Recomputes Local the same way a
	/// fresh placement would, against the bone's CURRENT parent.
	///
	/// Children are not touched here, and do not need to be: their own Local is relative to THIS
	/// bone, and WorldBind always walks the parent chain fresh rather than caching a result, so
	/// they follow automatically the moment this bone's Local changes underneath them.
	/// </summary>
	public void SetHeadTail( int index, Vec3 head, Vec3 tail )
	{
		if ( index < 0 || index >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		var (local, length) = LocalFromWorldPoints( Bones[index].Parent, head, tail, Bones[index].Name );

		Bones[index].Local = local;
		Bones[index].Length = length;
	}

	/// <summary>Shared math behind AddBoneFromPoints and SetHeadTail — a bone's Local and Length
	/// from a head/tail pair in world space and the parent it will sit under. One copy so a future
	/// change (roll, most likely) cannot land in one caller and not the other.</summary>
	(Xform local, float length) LocalFromWorldPoints( int parent, Vec3 head, Vec3 tail, string boneNameForError )
	{
		var along = tail - head;
		var length = along.Length;

		if ( length < 1e-6f )
			throw new ArgumentException(
				$"Bone '{boneNameForError}' has zero length — head and tail are the same point" );

		var y = along / length;

		// Any axis not parallel to y works as a seed; picking the one y leans on least keeps the
		// cross product well-conditioned.
		var seed = MathF.Abs( y.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
		var x = Vec3.Cross( seed, y ).Normal;
		var z = Vec3.Cross( x, y );

		var world = new Xform( x, y, z, head );
		var local = parent < 0 ? world : WorldBind( parent ).Inverse * world;

		return (local, length);
	}

	public int IndexOf( string name )
	{
		for ( var i = 0; i < Bones.Count; i++ )
		{
			if ( Bones[i].Name == name )
				return i;
		}

		return -1;
	}

	/// <summary>Bind transform in world space, from walking up the parents. Cheap enough to call
	/// per bone; the chains here are tens of bones, not thousands.</summary>
	public Xform WorldBind( int index )
	{
		var x = Bones[index].Local;
		var p = Bones[index].Parent;

		while ( p >= 0 )
		{
			x = Bones[p].Local * x;
			p = Bones[p].Parent;
		}

		return x;
	}

	public Vec3 HeadWorld( int index ) => WorldBind( index ).Origin;

	/// <summary>Tail is Length along the bone's own +Y — see Bone.</summary>
	public Vec3 TailWorld( int index )
	{
		var w = WorldBind( index );
		return w.TransformPoint( new Vec3( 0, Bones[index].Length, 0 ) );
	}

	public IEnumerable<int> Children( int index )
	{
		for ( var i = index + 1; i < Bones.Count; i++ )
		{
			if ( Bones[i].Parent == index )
				yield return i;
		}
	}

	public Skeleton Clone()
	{
		var s = new Skeleton();

		foreach ( var b in Bones )
			s.Bones.Add( b.Clone() );

		return s;
	}

	/// <summary>
	/// Remove a bone, reparenting its direct children to ITS parent so deleting one from the
	/// middle of a chain does not orphan everything past it — a mis-click while placing bones is
	/// the common case this exists for.
	///
	/// Every surviving bone keeps its WORLD bind transform; only the stored Local of the removed
	/// bone's children changes, recomputed against their new parent. Topological order (parent
	/// index &lt; child index) is preserved because indices only ever shift down to fill the gap.
	/// </summary>
	public void RemoveBone( int index )
	{
		if ( index < 0 || index >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		var removedParent = Bones[index].Parent;

		// Captured before anything is rebuilt, so reparenting reads world transforms rather than
		// composing through a partially-rebuilt list.
		var worlds = new Xform[Bones.Count];
		for ( var i = 0; i < Bones.Count; i++ )
			worlds[i] = WorldBind( i );

		var newBones = new List<Bone>( Bones.Count - 1 );
		var oldToNew = new int[Bones.Count];

		for ( var i = 0; i < Bones.Count; i++ )
		{
			if ( i == index )
				continue;

			var bone = Bones[i];
			var oldParent = bone.Parent;

			// A direct child of the removed bone re-parents to what the removed bone's parent
			// was; everything else keeps its parent unchanged.
			var newParent = oldParent == index ? removedParent : oldParent;

			// newParent, when not -1, is always an index already visited — it is either less
			// than `index`, or it is `removedParent` which is itself less than `index` — so its
			// mapping exists by now.
			var mappedParent = newParent < 0 ? -1 : oldToNew[newParent];

			var local = mappedParent < 0 ? worlds[i] : worlds[mappedParent].Inverse * worlds[i];

			newBones.Add( new Bone( bone.Name, mappedParent, local, bone.Length ) );
			oldToNew[i] = newBones.Count - 1;
		}

		Bones = newBones;
	}

	/// <summary>
	/// Mirror a bone and everything beneath it across the plane through the origin with the given
	/// normal, appended as new bones under `newParent` (-1 for a new root, or an existing bone —
	/// mirroring an arm should graft onto the spine bone the original arm hangs from, not become
	/// its own root).
	///
	/// Reflects the WORLD head and tail of each bone and rebuilds it from those two points, the
	/// same way AddBoneFromPoints always builds a bone. That sidesteps the usual mirrored-bone
	/// handedness problem entirely: there is no roll stored anywhere in this format to come out
	/// backwards, so reflecting the two points a bone is defined by is exactly right rather than a
	/// shortcut that happens to work.
	///
	/// Naming swaps a trailing _L/_R, _l/_r, .L/.R (Blender's own convention); anything else gets
	/// "_mirrored" appended. A collision with an existing name — mirroring twice, or a name that
	/// already looks mirrored — gets a numeric suffix rather than throwing, since this is meant to
	/// be safe to lean on while roughing out a rig.
	///
	/// Returns the index of the new mirrored root.
	/// </summary>
	public int MirrorSubtree( int root, Vec3 planeNormal, int newParent = -1 )
	{
		if ( root < 0 || root >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( root ) );

		if ( newParent < -1 || newParent >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( newParent ) );

		var n = planeNormal.Normal;

		if ( n.LengthSquared < 0.5f )
			throw new ArgumentException( "A mirror plane needs a normal", nameof( planeNormal ) );

		Vec3 Reflect( Vec3 p ) => p - n * (2f * Vec3.Dot( p, n ));

		var newRootIndex = -1;

		void Walk( int sourceIndex, int mirroredParent )
		{
			var bone = Bones[sourceIndex];
			var head = Reflect( HeadWorld( sourceIndex ) );
			var tail = Reflect( TailWorld( sourceIndex ) );

			var newIndex = AddBoneFromPoints( UniqueName( MirroredName( bone.Name ) ), mirroredParent, head, tail );

			if ( sourceIndex == root )
				newRootIndex = newIndex;

			// Snapshotted before recursing: Children scans live off Bones, which is growing with
			// every mirrored bone Walk adds, and the source subtree's children are exactly the set
			// this reads once, up front.
			var kids = new List<int>();
			foreach ( var child in Children( sourceIndex ) )
				kids.Add( child );

			foreach ( var child in kids )
				Walk( child, newIndex );
		}

		Walk( root, newParent );
		return newRootIndex;
	}

	static string MirroredName( string name )
	{
		if ( name.EndsWith( "_L" ) ) return name[..^2] + "_R";
		if ( name.EndsWith( "_R" ) ) return name[..^2] + "_L";
		if ( name.EndsWith( "_l" ) ) return name[..^2] + "_r";
		if ( name.EndsWith( "_r" ) ) return name[..^2] + "_l";
		if ( name.EndsWith( ".L" ) ) return name[..^2] + ".R";
		if ( name.EndsWith( ".R" ) ) return name[..^2] + ".L";
		return name + "_mirrored";
	}

	string UniqueName( string baseName )
	{
		if ( IndexOf( baseName ) < 0 )
			return baseName;

		var n = 1;
		while ( IndexOf( $"{baseName}_{n}" ) >= 0 )
			n++;

		return $"{baseName}_{n}";
	}

	/// <summary>Rename a bone in place, with the same validation AddBone applies to a new one.</summary>
	public void RenameBone( int index, string name )
	{
		if ( index < 0 || index >= Bones.Count )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A bone needs a name — every consuming format keys on it" );

		var existing = IndexOf( name );

		if ( existing >= 0 && existing != index )
			throw new ArgumentException( $"A bone called '{name}' already exists" );

		Bones[index].Name = name;
	}

	/// <summary>
	/// A single root bone at the origin. Every static model exported as a skinned format needs
	/// one — a mesh with no bones at all is not something SMD can express, so "static" is really
	/// "everything weighted to one root".
	/// </summary>
	public static Skeleton SingleRoot( string name = "root" )
	{
		var s = new Skeleton();
		s.AddBone( name, -1, Xform.Identity );
		return s;
	}

	public override string ToString() => $"Skeleton, {Bones.Count} bones";
}
