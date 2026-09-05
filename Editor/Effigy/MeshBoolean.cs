using System;

namespace Effigy;

/// <summary>What a boolean does to the two meshes.</summary>
public enum BooleanOp
{
	/// <summary>Everything inside either one, with the shared interface cut away. Different from
	/// MeshTransform.Append, which combines the meshes and leaves the interface in place.</summary>
	Union,

	/// <summary>The target with the tool's volume taken out of it. This is a cut.</summary>
	Subtract,

	/// <summary>Only the volume both share.</summary>
	Intersect,
}

/// <summary>
/// Something that can actually perform a mesh boolean.
///
/// AN INTERFACE RATHER THAN AN IMPLEMENTATION, and that is a decision this repo made a while ago
/// and wrote down: robust mesh CSG is a decades-old problem — coplanar faces, floating-point
/// robustness, self-intersection — and a half-working one is worse than none, because it fails on
/// the interesting cases and does so by producing a mesh rather than an error. s&amp;box ships
/// PolygonMesh.PerformBoolean, so the plan of record is an engine-backed implementation there, and
/// a portable one only if it is ever genuinely needed.
///
/// The kernel therefore knows what a boolean IS without knowing how to do one. That keeps the
/// engine-free promise intact — nothing in here references an engine type — while letting a cut
/// work wherever a provider has been installed.
/// </summary>
public interface IMeshBoolean
{
	/// <summary>
	/// Apply the operation, or explain why not.
	///
	/// Returning false with a reason rather than throwing, because "this pair of meshes cannot be
	/// booleaned" is an ordinary outcome — two solids that do not touch, a cut that would remove
	/// everything — and the feature turns the reason into its own error message. A provider that
	/// throws is caught and treated the same way, since an engine call failing is not something a
	/// rebuild should die on.
	/// </summary>
	bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error );
}

/// <summary>
/// Where the boolean provider is installed, and the one place features go through to use it.
///
/// A static slot rather than something threaded through FeatureContext: there is exactly one
/// answer per process — the engine's, or none — and the alternative is every feature signature
/// carrying a parameter that is the same value every time.
/// </summary>
public static class MeshBoolean
{
	/// <summary>The installed provider, or null where there is none — a bare console runner, or
	/// the test project. Set once at startup by whatever host knows how to do a boolean.</summary>
	public static IMeshBoolean Provider { get; set; }

	public static bool Available => Provider is not null;

	/// <summary>
	/// What a host wants said when there is no provider, if it knows something more useful than the
	/// kernel does.
	///
	/// The kernel's own message can only say that no boolean is installed, because that is all it
	/// knows. A host knows more — the editor knows the engine HAS one and what is needed to reach
	/// it — and the difference between "unavailable" and "unavailable, here is the next step" is
	/// the difference between a dead end and a task.
	/// </summary>
	public static string UnavailableReason { get; set; }

	/// <summary>
	/// Apply a boolean, throwing with something a user can act on if it cannot be done.
	///
	/// Every failure here ends up as one feature's Error, which is what the dialog turns red over
	/// and what the user reads. So each message says what could not be done AND what to do about it
	/// — "unavailable" on its own leaves someone wondering whether they broke something.
	/// </summary>
	public static PolyMesh Apply( BooleanOp op, PolyMesh target, PolyMesh tool )
	{
		if ( target is null || tool is null )
		{
			throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				"A boolean needs two solids",
				"One of the inputs was missing, so there is nothing to combine.",
				remedies: new[] { "Make sure both the target body and the tool solid exist" } ) );
		}

		if ( Provider is null )
		{
			var problem = UnavailableReason is { Length: > 0 } reason
				? $"{Name( op )} needs a mesh boolean. {reason}"
				: $"{Name( op )} needs a mesh boolean, and none is installed in this build. The kernel does "
					+ "not carry its own — see MeshBoolean for why.";

			throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				problem,
				"This build has no boolean provider, so cuts and unions cannot run.",
				remedies: new[] { "Run this in the s&box editor, where the engine boolean is installed" } ) );
		}

		bool ok;
		PolyMesh result;
		string error;

		try
		{
			ok = Provider.TryApply( op, target, tool, out result, out error );
		}
		catch ( Exception e )
		{
			// An engine call throwing is a failed boolean, not a failed rebuild. Everything else in
			// the tree should still build and still be on screen while this one feature complains.
			throw DiagnoseBoolean( op, target, tool, e.Message, engineThrew: true );
		}

		if ( !ok )
			throw DiagnoseBoolean( op, target, tool, error, engineThrew: false );

		if ( result is null || result.FaceCount == 0 )
		{
			// An empty result is a real answer to some inputs — cutting a solid with something that
			// swallows it whole — and it is never a useful one, because a body with no faces is
			// indistinguishable from a broken feature everywhere downstream.
			throw new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"{Name( op )} left nothing behind. The cut probably covers the whole part — check the profile and the distance.",
				"The boolean returned a mesh with no faces, which would blank every feature below this one.",
				remedies: new[] { "Reduce the distance so the cut does not swallow the part", "Check the profile sits inside the body" } ) );
		}

		return result;
	}

	/// <summary>
	/// The engine's refusal is a dead end on its own. Before handing it up, say whether the two
	/// bodies even overlap, and whether either is open — those are things we can see without
	/// asking the engine, and they are the usual reasons it says no.
	/// </summary>
	static FeatureException DiagnoseBoolean( BooleanOp op, PolyMesh target, PolyMesh tool, string error, bool engineThrew )
	{
		var targetCheck = MeshValidator.Validate( target );
		var toolCheck = MeshValidator.Validate( tool );
		var targetOpen = !targetCheck.IsClosed;
		var toolOpen = !toolCheck.IsClosed;

		if ( targetOpen || toolOpen )
		{
			var which = targetOpen && toolOpen ? "both solids are open"
				: targetOpen ? $"the target has {targetCheck.BoundaryEdges} boundary edge(s)"
				: $"the tool has {toolCheck.BoundaryEdges} boundary edge(s)";

			return new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"{Name( op )} failed: one of the solids is not closed",
				$"{which}, so there is no inside to {Name( op ).ToLowerInvariant()}. {error ?? ""}".Trim(),
				remedies: new[] { "Close the open mesh first", "Avoid cutting with a surface that has a boundary" } ) );
		}

		if ( BoundsGap( target, tool, out var axis, out var gap ) )
		{
			var how = gap > 1e-6f
				? $"they miss each other by {gap:0.###} along {axis}"
				: $"they only touch along {axis}, enclosing no common volume";

			return new FeatureException( new FeatureDiagnostic(
				DiagnosticSeverity.Error,
				$"{Name( op )} failed: the two solids do not overlap",
				$"{how}. {error ?? ""}".Trim(),
				remedies: new[] { "Move the tool so it cuts into the part", "Increase the distance", "Check Flip direction" } ) );
		}

		return new FeatureException( new FeatureDiagnostic(
			DiagnosticSeverity.Error,
			$"{Name( op )} failed: {error ?? "the solids could not be combined"}",
			engineThrew
				? "The boolean engine threw rather than returning a mesh."
				: "The solids overlap and are closed, so the refusal is in the geometry — self-intersection, or a case the engine cannot cut.",
			remedies: new[] { "Check neither solid self-intersects", "Try a slightly different position or distance" } ) );
	}

	static bool BoundsGap( PolyMesh a, PolyMesh b, out string axis, out float gap )
	{
		axis = "X";
		gap = 0f;

		if ( a.VertexCount == 0 || b.VertexCount == 0 )
			return false;

		Extent( a, out var aMin, out var aMax );
		Extent( b, out var bMin, out var bMax );

		var gapX = SpanGap( aMin.x, aMax.x, bMin.x, bMax.x );
		var gapY = SpanGap( aMin.y, aMax.y, bMin.y, bMax.y );
		var gapZ = SpanGap( aMin.z, aMax.z, bMin.z, bMax.z );
		var worst = MathF.Max( gapX, MathF.Max( gapY, gapZ ) );

		if ( worst < -1e-6f )
			return false;

		axis = worst == gapX ? "X" : worst == gapY ? "Y" : "Z";
		gap = worst;
		return true;
	}

	static float SpanGap( float aMin, float aMax, float bMin, float bMax ) =>
		MathF.Max( bMin - aMax, aMin - bMax );

	static void Extent( PolyMesh mesh, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
	}

	static string Name( BooleanOp op ) => op switch
	{
		BooleanOp.Union => "Union",
		BooleanOp.Subtract => "Remove",
		BooleanOp.Intersect => "Intersect",
		_ => "Boolean"
	};
}
