using System;

namespace ModelKernel;

/// <summary>
/// Marks creases automatically from geometry, rather than requiring every edge to be picked by
/// hand — there is no viewport in this kernel yet to pick one with anyway.
///
/// This is the fix for the problem the first PNG previews surfaced: with no creases at all, a
/// subdivided cube rounds off into a ball and a cylinder's cap puckers around its centre. Neither
/// is wrong — that's the literal Catmull-Clark limit surface — but it defeats the entire premise of
/// CAD-then-sculpt, where the CAD stage's crispness has to survive into the sculpt stage.
/// </summary>
public static class CreaseTools
{
	/// <summary>
	/// Marks every edge sharp where the angle between its two adjacent face normals is at least
	/// `thresholdDegrees`. The standard "mark sharp by angle" / auto-smooth approach — a box's 90°
	/// edges get marked, a 16-segment cylinder's ~22.5°-apart side seams do not, so the barrel stays
	/// round while its flat caps (90° from the sides) stay flat.
	///
	/// Non-manifold and boundary edges are left untouched — boundary sharpness is already implicit,
	/// and an edge with other than exactly two faces has no single angle to measure.
	/// </summary>
	public static void MarkSharpByAngle( PolyMesh mesh, float thresholdDegrees, float sharpness = float.PositiveInfinity )
	{
		// Comparing cosines rather than angles avoids an acos per edge. Angle is a decreasing
		// function of dot product over [0, 180], so "angle >= threshold" becomes "dot <= cos(threshold)".
		var cosThreshold = MathF.Cos( thresholdDegrees * MathF.PI / 180f );
		var normals = new Vec3[mesh.FaceCount];

		for ( var i = 0; i < mesh.FaceCount; i++ )
			normals[i] = mesh.FaceNormal( mesh.Faces[i] );

		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count != 2 )
				continue;

			var dot = Vec3.Dot( normals[faces[0]], normals[faces[1]] );

			if ( dot <= cosThreshold )
				mesh.MarkSharp( key, sharpness );
		}
	}
}
