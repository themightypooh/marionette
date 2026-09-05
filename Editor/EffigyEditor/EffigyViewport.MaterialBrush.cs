using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Brushing an existing material onto faces.
///
/// THE SAME GESTURE AS THE COLOUR BRUSH AND A DIFFERENT RESULT. The ring, the hover, the
/// press-drag-release are all copied from <see cref="PaintFrame"/> deliberately: switching between
/// the two should feel like changing what the brush is loaded with, not like learning a tool. What
/// differs is what a dab does — colour blends into vertices here and now, a material dab is an
/// edit to the feature history, so it has to go out to the window, change the document and come
/// back as a rebuild.
///
/// WHICH IS WHY IT REPORTS RATHER THAN APPLIES. The viewport owns no studio and must not: it is
/// handed a mesh to look at. So a dab raises <see cref="MaterialDabbed"/> with the faces it
/// covered and the window does the assignment through MaterialDrop.Brush. That keeps the undo
/// story ordinary — a material dab is the same kind of edit as dropping a material, and undoes the
/// same way — instead of inventing a second undo path that only the brush knows about.
///
/// A DAB PER FRAME, NOT PER STROKE. The faces under the ring change as it moves, and each batch
/// has to reach the document while the drag is still happening or the model would not follow the
/// brush. MaterialDrop.Brush reports no change for faces already on the slot, which is what stops
/// a held brush from putting an undo step on the stack every frame it does not move.
/// </summary>
internal sealed partial class EffigyViewport
{
	public MaterialBrushSession MaterialBrush { get; private set; }

	public bool IsMaterialBrushing => MaterialBrush is not null;

	/// <summary>Raised with the faces one dab covered. The window assigns them and rebuilds; the
	/// list is reused between dabs, so it must be consumed rather than kept.</summary>
	public Action<IReadOnlyList<int>> MaterialDabbed { get; set; }

	private MeshHit? _materialCursor;
	private bool _materialStroking;

	public void BeginMaterialBrush( MaterialBrushSession session )
	{
		MaterialBrush = session ?? throw new ArgumentNullException( nameof( session ) );
		_materialStroking = false;
	}

	public void EndMaterialBrush()
	{
		MaterialBrush = null;
		_materialCursor = null;
		_materialStroking = false;
	}

	private void MaterialBrushFrame()
	{
		if ( MaterialBrush is null )
			return;

		_materialCursor = null;

		// Leaving the canvas does not end the drag, the same rule paint and sculpt keep: dragging
		// off the model and back on is ordinary.
		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			_materialCursor = MaterialBrush.Hover( origin, direction );

			if ( Gizmo.WasLeftMousePressed )
				_materialStroking = true;

			if ( _materialStroking && Gizmo.IsLeftMouseDown && _materialCursor is { } hit )
			{
				var faces = MaterialBrush.FacesAt( hit );

				if ( faces.Count > 0 )
					MaterialDabbed?.Invoke( faces );
			}
		}

		if ( _materialStroking && !Gizmo.IsLeftMouseDown )
			_materialStroking = false;

		DrawMaterialCursor();
	}

	/// <summary>
	/// The ring, plus the faces it is about to take.
	///
	/// HIGHLIGHTING THE FACES IS THE WHOLE POINT HERE, in a way it is not for colour. A material dab
	/// covers whole faces, so what the ring encloses and what the dab takes are not the same shape —
	/// on a coarse box a small ring still paints an entire side. Showing the faces means the tool
	/// says what it is about to do rather than letting the first click explain it.
	/// </summary>
	private void DrawMaterialCursor()
	{
		if ( _materialCursor is not { } hit )
			return;

		var mesh = MaterialBrush.Mesh;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = MaterialCursorColor.WithAlpha( 0.5f );
		Gizmo.Draw.LineThickness = 1.5f;

		foreach ( var faceIndex in MaterialBrush.FacesAt( hit ) )
		{
			if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count )
				continue;

			var face = mesh.Faces[faceIndex];

			for ( var c = 0; c < face.Count; c++ )
			{
				var a = mesh.Positions[face.Indices[c]];
				var b = mesh.Positions[face.Indices[(c + 1) % face.Count]];

				Gizmo.Draw.Line( new Vector3( a.x, a.y, a.z ), new Vector3( b.x, b.y, b.z ) );
			}
		}

		var normal = new Vector3( hit.Normal.x, hit.Normal.y, hit.Normal.z ).Normal;
		var centre = new Vector3( hit.Point.x, hit.Point.y, hit.Point.z );

		var reference = MathF.Abs( normal.z ) > 0.9f ? new Vector3( 1f, 0f, 0f ) : new Vector3( 0f, 0f, 1f );
		var right = Vector3.Cross( normal, reference ).Normal;
		var up = Vector3.Cross( normal, right ).Normal;

		var radius = MaterialBrush.Radius;

		Gizmo.Draw.Color = MaterialCursorColor;

		var lift = normal * (radius * 0.01f);
		const int Segments = 40;
		var previous = centre + right * radius + lift;

		for ( var i = 1; i <= Segments; i++ )
		{
			var angle = i / (float)Segments * MathF.PI * 2f;
			var point = centre + (right * MathF.Cos( angle ) + up * MathF.Sin( angle )) * radius + lift;

			Gizmo.Draw.Line( previous, point );
			previous = point;
		}
	}

	/// <summary>Amber, so it is neither sculpt's blue nor paint's pink — the three brushes are told
	/// apart by the colour of their ring before anything else.</summary>
	private static readonly Color MaterialCursorColor = new( 1f, 0.75f, 0.25f, 0.9f );
}
