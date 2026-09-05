using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The brush that lays an existing material onto faces, rather than colour onto vertices.
///
/// WHY IT IS NOT A MODE ON PaintSession, which it otherwise resembles closely. The two write
/// completely different things: a paint stroke is a stroke, replayed onto vertex colours every
/// rebuild and living on the PaintFeature; a material dab is an EDIT TO THE HISTORY, the same
/// FaceMaterialFeature assignment that dropping a material makes. Nothing about a material dab is
/// replayed, nothing about it is a colour, and the one thing it shares - "which faces did the
/// brush touch" - is a BVH query both can call. Sharing a session would have meant one class
/// holding two unrelated kinds of state and a mode flag deciding which half was live.
///
/// SO THIS HOLDS NO RESULT AT ALL. It answers where the cursor is and which faces a dab covers;
/// the caller does the assignment through <see cref="MaterialDrop.Brush"/> and rebuilds. That is
/// what keeps it testable without an editor, and what lets the dab be undone as an ordinary
/// history edit rather than as a special paint undo.
///
/// FACE GRANULARITY IS THE POINT, not a limitation to apologise for. A material belongs to a face
/// - see FaceMaterialEdit for why faces carry a slot number - so a dab covers whole faces or none
/// of them, and the brush ring is a way of picking several at once rather than a soft edge. On a
/// coarse box that means a click paints a whole side; subdivide first if you want the edge to
/// follow the brush, which is the same answer colour painting gives for its own resolution.
/// </summary>
public sealed class MaterialBrushSession
{
	readonly PolyMesh _mesh;
	readonly MeshBVH _bvh;
	readonly List<int> _found = new();

	/// <summary>Brush radius in model units. The same meaning it has on the colour brush, so the
	/// two feel like one tool with two payloads.</summary>
	public float Radius = 0.1f;

	public MaterialBrushSession( PolyMesh mesh )
	{
		_mesh = mesh ?? throw new System.ArgumentNullException( nameof( mesh ) );
		_bvh = MeshBVH.Build( mesh );
	}

	/// <summary>The mesh the dabs land on, exposed so the editor can draw the cursor against the
	/// same surface the brush is querying.</summary>
	public PolyMesh Mesh => _mesh;

	/// <summary>A radius that is a sensible fraction of the part, so the brush is usable before
	/// anybody touches the number. The same twelfth of the bounds diagonal the colour brush
	/// starts at, deliberately - switching tools should not change the size of the ring.</summary>
	public float SuggestedRadius
	{
		get
		{
			var diagonal = _mesh.BoundsDiagonal;
			return diagonal > 1e-6f ? diagonal / 12f : 0.25f;
		}
	}

	/// <summary>Where the cursor sits on the surface, or null if the ray missed.</summary>
	public MeshHit? Hover( Vec3 origin, Vec3 direction )
	{
		var dir = direction.Normal;

		if ( dir.Length < 1e-6f )
			return null;

		return _bvh.Raycast( _mesh, origin, dir );
	}

	/// <summary>
	/// The faces one dab at <paramref name="hit"/> covers.
	///
	/// The far side of a thin wall is rejected by its normal, the same test PaintReplay's dab
	/// makes and for the same reason: the sphere reaches through the wall, and painting the back
	/// of what you are pointing at is never what was meant. A face exactly perpendicular belongs
	/// to its neighbour's dab rather than this one.
	///
	/// The returned list is REUSED between calls - a brush queries this every frame it moves, and
	/// a fresh allocation per frame is the kind of garbage a held drag makes thousands of. Copy it
	/// if you need to keep it.
	/// </summary>
	public IReadOnlyList<int> FacesAt( MeshHit hit )
	{
		_found.Clear();
		_bvh.FacesInRadius( _mesh, hit.Point, Radius, _found );

		var kept = 0;

		for ( var i = 0; i < _found.Count; i++ )
		{
			var faceIndex = _found[i];

			if ( faceIndex < 0 || faceIndex >= _mesh.Faces.Count )
				continue;

			if ( Vec3.Dot( _mesh.FaceNormal( _mesh.Faces[faceIndex] ), hit.Normal ) <= 0f )
				continue;

			_found[kept++] = faceIndex;
		}

		_found.RemoveRange( kept, _found.Count - kept );

		return _found;
	}
}
