using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Tests;

/// <summary>
/// Verification for the rigging half: skeleton maths, weight generation, weights surviving
/// subdivision, and SMD export.
///
/// The theme is the same as the geometry tests — check things that fail loudly. A rig that is
/// subtly wrong still deforms smoothly and still looks plausible; it just bends around slightly
/// the wrong place. What catches that is arithmetic: weights that must sum to 1, a bind pose that
/// must survive a round trip through Euler angles, and a binding that must still name the same
/// body after the model rebuilds underneath it.
/// </summary>
public static class RigTests
{
	public static void Run()
	{
		Section( "skeleton hierarchy and bind pose" );
		TestSkeleton();

		Section( "removing and renaming a bone" );
		TestRemoveRenameBone();

		Section( "mirroring a bone subtree" );
		TestMirrorSubtree();

		Section( "editing a bone's head/tail numerically" );
		TestSetHeadTail();

		Section( "Euler conversion round-trips" );
		TestEuler();

		Section( "auto-binding produces valid weights" );
		TestBinding();

		Section( "weights survive subdivision" );
		TestWeightsThroughSubdivision();

		Section( "feature binding survives a rebuild" );
		TestFeatureBinding();

		Section( "SMD export" );
		TestSmd();

		Section( "rig: regressions from review" );
		TestReviewRegressions();
	}

	// --- skeleton -----------------------------------------------------------------------

	static Skeleton TwoBoneChain()
	{
		var s = new Skeleton();
		s.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 2, 0 ) );
		s.AddBoneFromPoints( "upper", 0, new Vec3( 0, 2, 0 ), new Vec3( 0, 4, 0 ) );
		return s;
	}

	/// <summary>
	/// A chain aimed down Z, matching how Primitives builds a cylinder — it runs along Z, centred
	/// on the origin, which is the Source/s&amp;box convention.
	///
	/// Worth being explicit about: a chain that does not lie along the shape it is skinning binds
	/// every vertex to whichever bone happens to be nearest, which is a perfectly correct answer to
	/// the wrong question. Test geometry has to be aimed at the model.
	/// </summary>
	static Skeleton CylinderChain( float height = 4f )
	{
		var half = height * 0.5f;
		var s = new Skeleton();
		s.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -half ), new Vec3( 0, 0, 0 ) );
		s.AddBoneFromPoints( "upper", 0, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, half ) );
		return s;
	}

	static void TestSkeleton()
	{
		var s = TwoBoneChain();

		Check( "two bones", s.Count == 2 );
		Check( "parent recorded", s.Bones[1].Parent == 0 );
		Check( "lookup by name", s.IndexOf( "upper" ) == 1 && s.IndexOf( "nope" ) < 0 );

		// The child's local transform is relative to its parent, but its WORLD head must land back
		// on the point it was authored from. That is the whole contract of the parent chain.
		Check( "child head is world-correct", Near( s.HeadWorld( 1 ), new Vec3( 0, 2, 0 ) ),
			s.HeadWorld( 1 ).ToString() );
		Check( "child tail is world-correct", Near( s.TailWorld( 1 ), new Vec3( 0, 4, 0 ) ),
			s.TailWorld( 1 ).ToString() );
		Check( "bone length measured", MathF.Abs( s.Bones[1].Length - 2f ) < 1e-4f );
		Check( "children enumerate", s.Children( 0 ).SequenceEqual( new[] { 1 } ) );

		// A chain that is not axis-aligned is where a wrong parent composition stops being
		// invisible: with an identity-ish rig every convention agrees.
		var angled = new Skeleton();
		angled.AddBoneFromPoints( "a", -1, new Vec3( 1, 0, 0 ), new Vec3( 3, 1, 0 ) );
		angled.AddBoneFromPoints( "b", 0, new Vec3( 3, 1, 0 ), new Vec3( 4, 3, 1 ) );
		Check( "angled chain keeps world head", Near( angled.HeadWorld( 1 ), new Vec3( 3, 1, 0 ) ),
			angled.HeadWorld( 1 ).ToString() );
		Check( "angled chain keeps world tail", Near( angled.TailWorld( 1 ), new Vec3( 4, 3, 1 ) ),
			angled.TailWorld( 1 ).ToString() );

		var threw = false;
		try { angled.AddBone( "c", 7, Xform.Identity ); } catch ( ArgumentOutOfRangeException ) { threw = true; }
		Check( "a forward parent reference is refused", threw );

		threw = false;
		try { angled.AddBone( "a", -1, Xform.Identity ); } catch ( ArgumentException ) { threw = true; }
		Check( "a duplicate bone name is refused", threw );

		threw = false;
		try { angled.AddBoneFromPoints( "z", -1, Vec3.Zero, Vec3.Zero ); } catch ( ArgumentException ) { threw = true; }
		Check( "a zero-length bone is refused", threw );
	}

	/// <summary>Three-bone chain: root -> mid -> tip. Deleting the middle one is the case that
	/// matters — it is the one where a naive delete would either orphan the tip or move it.</summary>
	static Skeleton ThreeBoneChain()
	{
		var s = new Skeleton();
		s.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 2, 0 ) );
		s.AddBoneFromPoints( "mid", 0, new Vec3( 0, 2, 0 ), new Vec3( 0, 4, 0 ) );
		s.AddBoneFromPoints( "tip", 1, new Vec3( 0, 4, 0 ), new Vec3( 0, 5, 0 ) );
		return s;
	}

	static void TestRemoveRenameBone()
	{
		var s = ThreeBoneChain();
		var tipHeadBefore = s.HeadWorld( 2 );
		var tipTailBefore = s.TailWorld( 2 );

		s.RemoveBone( 1 ); // delete "mid"

		Check( "one bone gone", s.Count == 2 );
		Check( "root survives at 0", s.IndexOf( "root" ) == 0 );
		Check( "mid is gone", s.IndexOf( "mid" ) < 0 );

		var tipIndex = s.IndexOf( "tip" );
		Check( "tip survives", tipIndex >= 0 );
		Check( "tip re-parents to root", s.Bones[tipIndex].Parent == s.IndexOf( "root" ) );
		Check( "tip keeps its world head", Near( s.HeadWorld( tipIndex ), tipHeadBefore ),
			s.HeadWorld( tipIndex ).ToString() );
		Check( "tip keeps its world tail", Near( s.TailWorld( tipIndex ), tipTailBefore ),
			s.TailWorld( tipIndex ).ToString() );

		// A root with no parent at all — removing it should not throw reaching for Parent -1.
		var rootOnly = new Skeleton();
		rootOnly.AddBoneFromPoints( "only", -1, Vec3.Zero, new Vec3( 0, 1, 0 ) );
		rootOnly.RemoveBone( 0 );
		Check( "removing the only bone leaves an empty skeleton", rootOnly.Count == 0 );

		var threw = false;
		try { s.RemoveBone( 99 ) ; } catch ( ArgumentOutOfRangeException ) { threw = true; }
		Check( "removing an out-of-range index is refused", threw );

		// Rename.
		var chain = ThreeBoneChain();
		chain.RenameBone( 1, "elbow" );
		Check( "renamed bone is found under its new name", chain.IndexOf( "elbow" ) == 1 );
		Check( "old name is gone", chain.IndexOf( "mid" ) < 0 );

		threw = false;
		try { chain.RenameBone( 0, "elbow" ); } catch ( ArgumentException ) { threw = true; }
		Check( "renaming onto an existing name is refused", threw );

		// Renaming a bone onto ITS OWN existing name is not a collision.
		chain.RenameBone( 1, "elbow" );
		Check( "renaming onto its own name is a no-op, not an error", chain.IndexOf( "elbow" ) == 1 );

		threw = false;
		try { chain.RenameBone( 0, "  " ); } catch ( ArgumentException ) { threw = true; }
		Check( "a blank name is refused", threw );
	}

	/// <summary>A spine root with one arm off to the +X side — the shape mirroring an arm across
	/// the character's centreline actually has to handle.</summary>
	static Skeleton SpineWithOneArm()
	{
		var s = new Skeleton();
		s.AddBoneFromPoints( "spine", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 2 ) );
		s.AddBoneFromPoints( "upper_arm_L", 0, new Vec3( 1, 0, 2 ), new Vec3( 3, 0, 2 ) );
		s.AddBoneFromPoints( "forearm_L", 1, new Vec3( 3, 0, 2 ), new Vec3( 5, 0, 1 ) );
		return s;
	}

	static void TestMirrorSubtree()
	{
		var s = SpineWithOneArm();
		var spine = s.IndexOf( "spine" );
		var upperL = s.IndexOf( "upper_arm_L" );

		var newRoot = s.MirrorSubtree( upperL, new Vec3( 1, 0, 0 ), spine );

		Check( "mirror returns a valid index", newRoot >= 0 && newRoot < s.Count );
		Check( "five bones total now", s.Count == 5 );
		Check( "_L became _R", s.Bones[newRoot].Name == "upper_arm_R" );
		Check( "mirrored bone grafts onto the requested parent", s.Bones[newRoot].Parent == spine );

		var headL = s.HeadWorld( upperL );
		var headR = s.HeadWorld( newRoot );
		Check( "X is negated", MathF.Abs( headR.x + headL.x ) < 1e-4f, $"{headL} vs {headR}" );
		Check( "Y and Z untouched by an X-normal mirror",
			MathF.Abs( headR.y - headL.y ) < 1e-4f && MathF.Abs( headR.z - headL.z ) < 1e-4f );

		var tailL = s.TailWorld( upperL );
		var tailR = s.TailWorld( newRoot );
		Check( "tail is mirrored too", Near( tailR, new Vec3( -tailL.x, tailL.y, tailL.z ) ), tailR.ToString() );

		Check( "length survives the mirror",
			MathF.Abs( s.Bones[newRoot].Length - s.Bones[upperL].Length ) < 1e-4f );

		var forearmR = s.IndexOf( "forearm_R" );
		Check( "the child came along", forearmR >= 0 );
		Check( "the child re-parents to the mirrored parent, not the original", s.Bones[forearmR].Parent == newRoot );

		var forearmL = s.IndexOf( "forearm_L" );
		Check( "the original subtree is untouched",
			Near( s.HeadWorld( forearmL ), new Vec3( 3, 0, 2 ) ) && s.Bones[forearmL].Parent == upperL );

		// Mirroring again must not collide with the name it just made.
		var second = s.MirrorSubtree( upperL, new Vec3( 1, 0, 0 ), spine );
		Check( "a second mirror gets a de-duplicated name, not a crash",
			s.Bones[second].Name != s.Bones[newRoot].Name, s.Bones[second].Name );

		// A bone with no _L/_R convention in its name.
		var plain = new Skeleton();
		plain.AddBoneFromPoints( "antenna", -1, Vec3.Zero, new Vec3( 1, 0, 0 ) );
		var mirroredPlain = plain.MirrorSubtree( 0, new Vec3( 1, 0, 0 ) );
		Check( "a name with no L/R convention gets _mirrored appended",
			plain.Bones[mirroredPlain].Name == "antenna_mirrored" );
		Check( "newParent -1 makes a new root", plain.Bones[mirroredPlain].Parent == -1 );

		var threw = false;
		try { s.MirrorSubtree( 99, new Vec3( 1, 0, 0 ) ); } catch ( ArgumentOutOfRangeException ) { threw = true; }
		Check( "mirroring an out-of-range bone is refused", threw );

		threw = false;
		try { s.MirrorSubtree( upperL, Vec3.Zero ); } catch ( ArgumentException ) { threw = true; }
		Check( "a zero-length mirror normal is refused", threw );
	}

	static void TestSetHeadTail()
	{
		var s = ThreeBoneChain(); // root(0) -> mid(1) -> tip(2)

		var rootHeadBefore = s.HeadWorld( 0 );
		var rootTailBefore = s.TailWorld( 0 );
		var midHeadBefore = s.HeadWorld( 1 );
		var tipHeadBefore = s.HeadWorld( 2 );
		var tipTailBefore = s.TailWorld( 2 );

		// A PURE TRANSLATION of the root — same direction and length, just moved — has to carry
		// every descendant by the identical rigid delta. This is not a Blender-style "connected"
		// bone that stretches to chase a moving tail: a child's Local is frozen relative to its
		// parent's BASIS, and a translation leaves that basis's rotation untouched, so the whole
		// subtree rides along exactly.
		var delta = new Vec3( 3, -1, 2 );
		s.SetHeadTail( 0, rootHeadBefore + delta, rootTailBefore + delta );

		Check( "root moved by the delta", Near( s.HeadWorld( 0 ), rootHeadBefore + delta ), s.HeadWorld( 0 ).ToString() );
		Check( "root's length is unchanged by a pure translation", MathF.Abs( s.Bones[0].Length - 2f ) < 1e-4f );
		Check( "mid carried by the same rigid delta", Near( s.HeadWorld( 1 ), midHeadBefore + delta ), s.HeadWorld( 1 ).ToString() );
		Check( "tip carried by the same rigid delta", Near( s.HeadWorld( 2 ), tipHeadBefore + delta ), s.HeadWorld( 2 ).ToString() );
		Check( "tip's tail carried too", Near( s.TailWorld( 2 ), tipTailBefore + delta ), s.TailWorld( 2 ).ToString() );

		// Editing the MIDDLE of the chain must not disturb the root above it. It also does NOT drag
		// the tip's head along to chase mid's new tail — the tip's own Local is frozen relative to
		// mid's basis at whatever length mid had when the tip was placed, exactly the same "not
		// connected" rule the translation case above relies on, just now visible because THIS edit
		// also changes mid's length. What WorldBind guarantees is that the tip is recomputed fresh,
		// not stale — its world position has to move even though nothing about the tip itself was
		// touched, because it composes against mid's new Local.
		var rootHeadNow = s.HeadWorld( 0 );
		var tipHeadBeforeMidEdit = s.HeadWorld( 2 );
		var newMidTail = new Vec3( 1, 8, 2 );
		s.SetHeadTail( 1, s.HeadWorld( 1 ), newMidTail );

		Check( "editing mid leaves root alone", Near( s.HeadWorld( 0 ), rootHeadNow ), s.HeadWorld( 0 ).ToString() );
		Check( "mid's own head/tail land exactly where requested",
			Near( s.HeadWorld( 1 ), midHeadBefore + delta ) && Near( s.TailWorld( 1 ), newMidTail ),
			s.TailWorld( 1 ).ToString() );
		Check( "mid's length is measured from the new tail, not left stale",
			MathF.Abs( s.Bones[1].Length - (newMidTail - ( midHeadBefore + delta )).Length ) < 1e-3f );
		Check( "the tip is recomputed fresh rather than cached",
			(s.HeadWorld( 2 ) - tipHeadBeforeMidEdit).Length > 0.5f, s.HeadWorld( 2 ).ToString() );

		var threw = false;
		try { s.SetHeadTail( 0, Vec3.Zero, Vec3.Zero ); } catch ( ArgumentException ) { threw = true; }
		Check( "a zero-length edit is refused", threw );

		threw = false;
		try { s.SetHeadTail( 99, Vec3.Zero, new Vec3( 0, 1, 0 ) ); } catch ( ArgumentOutOfRangeException ) { threw = true; }
		Check( "editing an out-of-range index is refused", threw );
	}

	static void TestEuler()
	{
		// Euler order is the classic silent-wrongness bug: any single-axis rotation round-trips
		// under every convention, so only compound rotations can tell them apart.
		var rng = new Random( 12345 );
		var worst = 0f;

		for ( var i = 0; i < 200; i++ )
		{
			var axis = new Vec3(
				(float)rng.NextDouble() * 2 - 1,
				(float)rng.NextDouble() * 2 - 1,
				(float)rng.NextDouble() * 2 - 1 );

			if ( axis.LengthSquared < 1e-4f )
				continue;

			var r = Xform.Rotate( axis, (float)rng.NextDouble() * MathF.PI * 1.9f - MathF.PI * 0.95f );
			var back = Xform.FromEulerXyz( r.ToEulerXyz() );

			worst = MathF.Max( worst, (back.X - r.X).Length );
			worst = MathF.Max( worst, (back.Y - r.Y).Length );
			worst = MathF.Max( worst, (back.Z - r.Z).Length );
		}

		Check( "200 random rotations survive matrix→Euler→matrix", worst < 1e-3f, $"worst axis error {worst:0.######}" );

		// Straight up is the gimbal pole, where cos(pitch) is zero and the other two angles stop
		// being separable. The rebuilt matrix still has to match even though the angles will not.
		var pole = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f ) * Xform.Rotate( new Vec3( 0, 1, 0 ), -MathF.PI / 2f );
		var poleBack = Xform.FromEulerXyz( pole.ToEulerXyz() );
		Check( "the gimbal pole still round-trips",
			(poleBack.X - pole.X).Length < 1e-3f && (poleBack.Y - pole.Y).Length < 1e-3f,
			$"{(poleBack.X - pole.X).Length:0.####}" );

		var inv = Xform.Rotate( new Vec3( 1, 2, 3 ), 0.7f ) * Xform.Translate( new Vec3( 4, -1, 2 ) );
		var round = inv * inv.Inverse;
		Check( "Xform.Inverse is actually an inverse",
			Near( round.TransformPoint( new Vec3( 5, 6, 7 ) ), new Vec3( 5, 6, 7 ) ) );
	}

	// --- binding ------------------------------------------------------------------------

	static void TestBinding()
	{
		var mesh = Primitives.Cylinder( 0.5f, 4f, 12 );
		var skeleton = CylinderChain();

		var rigid = SkinBinder.BindRigid( mesh, skeleton );
		Check( "rigid: one set per vertex", rigid.Count == mesh.VertexCount );
		Check( "rigid: valid", rigid.Validate( mesh.VertexCount, skeleton.Count ).Count == 0,
			string.Join( "; ", rigid.Validate( mesh.VertexCount, skeleton.Count ).Take( 2 ) ) );
		Check( "rigid: single influence each", rigid.Vertices.All( w => w.Length == 1 ) );
		Check( "rigid: both bones used", rigid.Vertices.Any( w => w[0].Bone == 0 ) && rigid.Vertices.Any( w => w[0].Bone == 1 ) );

		var smooth = SkinBinder.BindSmooth( mesh, skeleton );
		Check( "smooth: valid", smooth.Validate( mesh.VertexCount, skeleton.Count ).Count == 0,
			string.Join( "; ", smooth.Validate( mesh.VertexCount, skeleton.Count ).Take( 2 ) ) );
		Check( "smooth: blends across the joint", smooth.Vertices.Any( w => w.Length > 1 ) );

		// A higher falloff exponent must concentrate weight, not spread it. If this inverts, the
		// slider in the UI would work backwards and nothing else would complain.
		float SpreadOf( float falloff )
		{
			var w = SkinBinder.BindSmooth( mesh, skeleton, falloff );
			return (float)w.Vertices.Average( v => v.Length );
		}

		Check( "higher falloff means tighter binding", SpreadOf( 6f ) <= SpreadOf( 1.5f ),
			$"{SpreadOf( 6f ):0.##} vs {SpreadOf( 1.5f ):0.##}" );

		var smoothed = SkinBinder.SmoothWeights( mesh, rigid, 3 );
		Check( "smoothing keeps weights valid", smoothed.Validate( mesh.VertexCount, skeleton.Count ).Count == 0 );
		Check( "smoothing softens a rigid bind", smoothed.Vertices.Any( w => w.Length > 1 ) );

		var threw = false;
		try { SkinBinder.BindRigid( mesh, new Skeleton() ); } catch ( InvalidOperationException ) { threw = true; }
		Check( "binding to an empty skeleton is refused", threw );

		// Pruning is the one lossy step in the whole path, so it has to stay a partition of unity.
		var many = new[]
		{
			new BoneWeight( 0, 0.4f ), new BoneWeight( 1, 0.3f ), new BoneWeight( 2, 0.15f ),
			new BoneWeight( 3, 0.1f ), new BoneWeight( 4, 0.05f )
		};
		var pruned = SkinWeights.Prune( many, 4 );
		Check( "prune drops to the cap", pruned.Length == 4 );
		Check( "prune renormalises to 1", MathF.Abs( pruned.Sum( w => w.Weight ) - 1f ) < 1e-5f );
		Check( "prune keeps the strongest", pruned.Any( w => w.Bone == 0 ) && pruned.All( w => w.Bone != 4 ) );
	}

	// --- subdivision --------------------------------------------------------------------

	static void TestWeightsThroughSubdivision()
	{
		var mesh = Primitives.Cylinder( 0.5f, 4f, 12 );
		var skeleton = CylinderChain();
		mesh.Skin = SkinBinder.BindSmooth( mesh, skeleton );

		Check( "mesh reports as rigged", mesh.IsRigged );

		var level2 = CatmullClark.Subdivide( mesh, 2 );

		Check( "subdivided mesh is still rigged", level2.IsRigged );
		Check( "one weight set per new vertex", level2.Skin.Count == level2.VertexCount,
			$"{level2.Skin.Count} vs {level2.VertexCount}" );

		var errors = level2.Skin.Validate( level2.VertexCount, skeleton.Count );
		Check( "every weight still sums to 1 and is in range", errors.Count == 0,
			string.Join( "; ", errors.Take( 3 ) ) );

		// The affine-combination property is the reason the above holds without a renormalise step.
		// Assert it directly, so a future "optimisation" that breaks it fails here rather than
		// showing up as a model that deforms slightly wrong at level 4.
		var worst = level2.Skin.Vertices.Max( w => MathF.Abs( w.Sum( b => b.Weight ) - 1f ) );
		Check( "no renormalisation needed anywhere", worst < 1e-4f, $"worst drift {worst:0.#######}" );
		Check( "no negative weights appear", level2.Skin.Vertices.All( w => w.All( b => b.Weight >= 0f ) ) );

		// An unrigged mesh must stay unrigged rather than gaining empty weights.
		var plain = CatmullClark.Subdivide( Primitives.Box( 1, 1, 1 ), 1 );
		Check( "an unrigged mesh subdivides without gaining a rig", plain.Skin is null );

		// Level 4 is the working limit the handoff quotes, so check the invariant holds that far.
		var level4 = CatmullClark.Subdivide( mesh, 4 );
		var deepErrors = level4.Skin.Validate( level4.VertexCount, skeleton.Count );
		Check( $"still valid at level 4 ({level4.VertexCount} verts)", deepErrors.Count == 0,
			string.Join( "; ", deepErrors.Take( 2 ) ) );
	}

	// --- feature binding ----------------------------------------------------------------

	static void TestFeatureBinding()
	{
		// Two boxes, so there are two bodies to bind to two bones.
		var studio = new PartStudio();

		var lower = studio.Add( new PrimitiveFeature() );
		lower.SizeX.Value = 1f; lower.SizeY.Value = 1f; lower.SizeZ.Value = 2f;
		lower.Position.Value = new Vec3( 0, 0, 1 );

		var upper = studio.Add( new PrimitiveFeature() );
		upper.SizeX.Value = 1f; upper.SizeY.Value = 1f; upper.SizeZ.Value = 2f;
		upper.Position.Value = new Vec3( 0, 0, 3 );

		studio.Rebuild();

		var (mesh, ranges) = studio.ToMeshWithBodies();
		Check( "two body ranges", ranges.Count == 2 );
		Check( "ranges cover every vertex", ranges.Sum( r => r.Count ) == mesh.VertexCount );
		Check( "ranges do not overlap", ranges[0].Start + ranges[0].Count == ranges[1].Start );

		var skeleton = new Skeleton();
		skeleton.AddBoneFromPoints( "lower", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 2 ) );
		skeleton.AddBoneFromPoints( "upper", 0, new Vec3( 0, 0, 2 ), new Vec3( 0, 0, 4 ) );

		var binding = new Dictionary<string, string>
		{
			[ranges[0].BodyId] = "lower",
			[ranges[1].BodyId] = "upper"
		};

		var weights = SkinBinder.BindBodies( mesh, ranges, binding, skeleton );
		Check( "body binding is valid", weights.Validate( mesh.VertexCount, skeleton.Count ).Count == 0 );

		bool AllBoundTo( BodyRange range, int bone, SkinWeights w ) =>
			Enumerable.Range( range.Start, range.Count )
				.All( i => w[i].Length == 1 && w[i][0].Bone == bone );

		Check( "first body is wholly on its bone", AllBoundTo( ranges[0], 0, weights ) );
		Check( "second body is wholly on its bone", AllBoundTo( ranges[1], 1, weights ) );

		// THE POINT OF THE WHOLE APPROACH: change a parameter, rebuild, and re-apply the SAME
		// binding. Vertex indices have moved; body ids have not, so the rig still lands correctly.
		upper.SizeX.Value = 3f;
		studio.MarkDirty( upper );
		studio.Rebuild();

		var (mesh2, ranges2) = studio.ToMeshWithBodies();
		var weights2 = SkinBinder.BindBodies( mesh2, ranges2, binding, skeleton );

		Check( "rig still valid after a parameter change",
			weights2.Validate( mesh2.VertexCount, skeleton.Count ).Count == 0 );
		Check( "body ids are stable across the rebuild",
			ranges2[0].BodyId == ranges[0].BodyId && ranges2[1].BodyId == ranges[1].BodyId );
		Check( "the widened body is still wholly on its own bone", AllBoundTo( ranges2[1], 1, weights2 ) );

		// A binding naming a bone that does not exist has to be loud, not silently ignored.
		var threw = false;
		try
		{
			SkinBinder.BindBodies( mesh2, ranges2,
				new Dictionary<string, string> { [ranges2[0].BodyId] = "ghost" }, skeleton );
		}
		catch ( InvalidOperationException ) { threw = true; }
		Check( "binding to a missing bone is refused", threw );
	}

	// --- SMD ----------------------------------------------------------------------------

	static void TestSmd()
	{
		var mesh = Primitives.Box( 2, 2, 2 );
		var staticSmd = SmdWriter.Write( mesh );

		Check( "static export needs no skeleton", staticSmd.Contains( "nodes" ) && staticSmd.Contains( "\"root\"" ) );

		var readBack = SmdReader.Read( staticSmd );
		Check( "static: one root bone", readBack.Skeleton.Count == 1 && readBack.Skeleton.Bones[0].Parent == -1 );
		Check( "static: box is 12 triangles", readBack.TriangleCount == 12, $"got {readBack.TriangleCount}" );
		Check( "static: 3 corners per triangle", readBack.Corners.Count == readBack.TriangleCount * 3 );
		Check( "static: everything weighted to the root",
			readBack.Corners.All( c => c.Weights.Length == 1 && c.Weights[0].Bone == 0 && MathF.Abs( c.Weights[0].Weight - 1f ) < 1e-4f ) );

		// A cylinder rigged and subdivided is the real case: many bones per vertex, many vertices.
		var rigged = Primitives.Cylinder( 0.5f, 4f, 12 );
		var skeleton = CylinderChain();
		rigged.Skin = SkinBinder.BindSmooth( rigged, skeleton );
		var dense = CatmullClark.Subdivide( rigged, 2 );

		var smd = SmdWriter.Write( dense, skeleton );
		var back = SmdReader.Read( smd );

		Check( "skinned: bone hierarchy survives", back.Skeleton.Count == 2 && back.Skeleton.Bones[1].Parent == 0 );
		Check( "skinned: bone names survive",
			back.Skeleton.Bones[0].Name == "root" && back.Skeleton.Bones[1].Name == "upper" );

		// The bind pose is written as local position + Euler rotation, so this is the test that
		// would catch a wrong Euler convention in a way a single-axis rig never could.
		Check( "skinned: bind pose round-trips",
			Near( back.Skeleton.HeadWorld( 1 ), skeleton.HeadWorld( 1 ), 1e-3f ),
			$"{back.Skeleton.HeadWorld( 1 )} vs {skeleton.HeadWorld( 1 )}" );

		Check( "skinned: influences are capped at 4",
			back.Corners.All( c => c.Weights.Length is > 0 and <= SmdWriter.MaxInfluences ) );
		Check( "skinned: every vertex's weights sum to 1",
			back.Corners.All( c => MathF.Abs( c.Weights.Sum( w => w.Weight ) - 1f ) < 1e-3f ) );
		Check( "skinned: some vertices genuinely blend two bones",
			back.Corners.Any( c => c.Weights.Length > 1 ) );
		Check( "skinned: triangle count matches the quads",
			back.TriangleCount == dense.Faces.Sum( f => f.Count - 2 ), $"got {back.TriangleCount}" );

		// Material slots have to survive as separate groups, or a two-material prop imports as one.
		var twoTone = Primitives.Box( 1, 1, 1 );
		twoTone.Faces[0].Material = 3;
		var matSmd = SmdReader.Read( SmdWriter.Write( twoTone ) );
		Check( "material slots are written per triangle",
			matSmd.Materials.Distinct().Count() == 2, string.Join( ",", matSmd.Materials.Distinct() ) );

		var threw = false;
		try
		{
			var bad = Primitives.Box( 1, 1, 1 );
			bad.Skin = SkinWeights.AllTo( bad.VertexCount, 9 );
			SmdWriter.Write( bad, Skeleton.SingleRoot() );
		}
		catch ( InvalidOperationException ) { threw = true; }
		Check( "a weight pointing off the end of the skeleton is refused", threw );
	}

	/// <summary>Bugs found by the second review cycle, kept together so they cannot come back.</summary>
	static void TestReviewRegressions()
	{
		// Prune returned short weight sets in whatever order they arrived, but SmdWriter takes
		// weights[0] as the vertex's parent bone on the documented understanding that it is the
		// strongest. A vertex weighted 0.2/0.8 named the wrong bone as its parent.
		var unsorted = new[] { new BoneWeight( 0, 0.2f ), new BoneWeight( 1, 0.8f ) };
		var pruned = SkinWeights.Prune( unsorted, 4 );

		Check( "prune sorts even when it drops nothing", pruned[0].Bone == 1,
			$"parent came out as bone {pruned[0].Bone}" );
		Check( "and returns a copy, not the caller's array", !ReferenceEquals( pruned, unsorted ) );

		// An unrigged body merged into a rigged one used to leave empty influences: IsRigged stayed
		// true, Validate failed on every one of them, and SMD read "no links" as the parent column.
		var skeleton = TwoBoneChain();
		var rigged = Primitives.Box( 1, 1, 1 );
		rigged.Skin = SkinBinder.BindRigid( rigged, skeleton );

		var plain = Primitives.Box( 1, 1, 1 );
		MeshTransform.Apply( plain, Xform.Translate( new Vec3( 3, 0, 0 ) ) );
		MeshTransform.Append( rigged, plain );

		Check( "merging unrigged geometry into a rigged body leaves no unweighted vertices",
			rigged.Skin.Validate( rigged.VertexCount, skeleton.Count ).Count == 0,
			string.Join( "; ", rigged.Skin.Validate( rigged.VertexCount, skeleton.Count ).Take( 2 ) ) );

		// A reused feature's error vanished from the report, so an unrelated downstream edit made a
		// broken model look clean.
		var studio = new PartStudio();

		var broken = studio.Add( new PrimitiveFeature() );
		broken.Shape.Index = 4;              // Tube
		broken.InnerRadius.Value = 9f;       // larger than the radius, so it throws

		var after = studio.Add( new PrimitiveFeature() );

		Check( "a broken feature is reported on the first rebuild", studio.Rebuild().HasErrors );

		after.SizeX.Value = 2f;
		studio.MarkDirty( after );

		Check( "and still reported after an unrelated downstream edit", studio.Rebuild().HasErrors );
	}

	// --- helpers ------------------------------------------------------------------------

	static bool Near( Vec3 a, Vec3 b, float tolerance = 1e-4f ) => (a - b).Length < tolerance;

	static void Section( string title ) => Report.Section( title );

	static void Check( string what, bool ok, string detail = null ) => Report.Check( what, ok, detail );
}
