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
/// A DAB PER FRAME, AN UNDO STEP PER STROKE. The faces under the ring change as it moves, and each
/// batch has to reach the document while the drag is still happening or the model would not follow
/// the brush — so dabs are frequent. What a person expects to take back is the GESTURE, though, so
/// undo is recorded once, at the press, by <see cref="MaterialStrokeStarted"/>. Recording it per
/// dab instead would turn one sweep into fifty Ctrl+Z presses.
/// </summary>
internal sealed partial class EffigyViewport
{
	public MaterialBrushSession MaterialBrush { get; private set; }

	public bool IsMaterialBrushing => MaterialBrush is not null;

	/// <summary>Raised with the faces one dab covered. The window assigns them and rebuilds; the
	/// list is reused between dabs, so it must be consumed rather than kept.</summary>
	public Action<IReadOnlyList<int>> MaterialDabbed { get; set; }

	/// <summary>
	/// Raised once when a drag begins, BEFORE its first dab, so the window can record undo.
	///
	/// ONCE PER STROKE AND NOT PER DAB, which is the whole reason it is a separate signal. A dab
	/// fires every frame the brush moves, so recording undo there would make one sweep across a
	/// part into fifty Ctrl+Z presses; recording it nowhere - which is what this did at first -
	/// leaves the stroke unundoable and sends Ctrl+Z back past it to whatever came before. The
	/// gesture is the unit a person would expect to take back, so the gesture is what is recorded.
	/// </summary>
	public Action MaterialStrokeStarted { get; set; }

	/// <summary>
	/// Whether a material is actually chosen. The window sets it from the Materials browser.
	///
	/// THE RING HAS TO SAY SO. Without this the brush draws its ring, outlines the faces under it
	/// and then does nothing at all when dragged, because there is no material to lay down - which
	/// looks exactly like a broken tool rather than an unloaded one. Unloaded, the ring goes grey
	/// and stops outlining faces it is not going to take.
	/// </summary>
	public bool MaterialBrushLoaded { get; set; }

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

			// Before the first dab, so the snapshot is the state the stroke is about to change.
			if ( Gizmo.WasLeftMousePressed && !_materialStroking )
			{
				_materialStroking = true;
				MaterialStrokeStarted?.Invoke();
			}

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
		var colour = MaterialBrushLoaded ? MaterialCursorColor : UnloadedCursorColor;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = colour.WithAlpha( 0.5f );
		Gizmo.Draw.LineThickness = 1.5f;

		// Only when there is something to lay down: outlining faces the brush will not touch is a
		// promise it cannot keep.
		foreach ( var faceIndex in MaterialBrushLoaded ? MaterialBrush.FacesAt( hit ) : NoFaces )
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

		Gizmo.Draw.Color = colour;

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

	/// <summary>Grey, for a brush with nothing loaded - the ring still tracks the surface so the
	/// tool is visibly alive, it just does not claim it is about to paint anything.</summary>
	private static readonly Color UnloadedCursorColor = new( 0.6f, 0.6f, 0.6f, 0.7f );

	private static readonly IReadOnlyList<int> NoFaces = new List<int>();
}
