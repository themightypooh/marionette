using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Paint mode in the viewport: rays in, vertex colours out.
///
/// AS THIN AS EffigyViewport.Sculpting.cs, AND FOR THE SAME REASON. Everything with a decision in it —
/// where the cursor sits, whether the pointer has travelled far enough to earn a sample, the dab
/// itself, the falloff — lives in <see cref="PaintSession"/> and <see cref="PaintReplay"/> in the
/// kernel, where a test can see it. This file converts Vector3 to Vec3, calls four methods, rebuilds
/// the model when colours change, and draws a ring.
///
/// VERTEX COLOURS, NOT A TEXTURE. Paint lives on the mesh's vertices, so the engine composites it
/// over whatever material each face wears — no atlas, no unwrap, no dynamic texture. The cost is that
/// a dab changes the vertex buffer, so the model rebuilds while a stroke is live, exactly the way the
/// sculpt preview rebuilds while a sculpt stroke moves geometry.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The live paint, or null when not in paint mode.</summary>
	public PaintSession PaintSession { get; private set; }

	public bool IsPainting => PaintSession is not null;

	/// <summary>Raised after a stroke commits, carrying the committed stroke so the window can append
	/// it to the feature. Not raised per sample — a stroke is one edit.</summary>
	public Action<PaintStroke> PaintStrokeFinished { get; set; }

	/// <summary>Raised when the viewport itself changes a brush setting, so the paint bar can catch up.</summary>
	public Action PaintSettingsChanged { get; set; }

	/// <summary>Where the brush ring is drawn this frame, or null when the cursor is off the model.</summary>
	private MeshHit? _paintCursor;

	/// <summary>The floating bars that belong to a brush — the colour one and the material one.
	/// A list rather than a field because there are two now and both have to be kept off the
	/// canvas hit test: a click meant for a control must never also land a dab.</summary>
	private readonly List<Widget> _paintBarOverlays = new();

	private bool _paintPreviewStale;

	// The material resolver the paint preview builds with, so the paint composes over the materials
	// the user dropped rather than over a flat placeholder. Held from BeginPaint for the frame loop.
	private Func<int, string> _paintMaterialForSlot;

	public void AddPaintOverlay( Widget bar )
	{
		_paintBarOverlays.Add( bar );
		bar.Position = OverlayMargin + new Vector2( 0f, 46f );
		bar.Visible = false;
	}

	public void BeginPaint( PaintSession session, Func<int, string> materialForSlot )
	{
		PaintSession = session ?? throw new ArgumentNullException( nameof( session ) );
		_paintMaterialForSlot = materialForSlot;
		_paintPreviewStale = true;
	}

	public void EndPaint()
	{
		// A stroke left running when the mode ends would be a half-finished mark nobody committed.
		// Cancel rather than commit, the same choice sculpt makes.
		if ( PaintSession is { IsStroking: true } )
			PaintSession.CancelStroke();

		PaintSession = null;
		_paintCursor = null;
	}

	/// <summary>Push the painted surface into the viewport, replacing the model in place. The colour
	/// array is copied onto the mesh so the preview's vertex buffer picks it up.</summary>
	public void RefreshPaintPreview()
	{
		if ( PaintSession is null )
			return;

		PaintSession.Mesh.VertexColors = PaintSession.Colors;

		var model = EffigyPreview.Build( PaintSession.Mesh, _paintMaterialForSlot );

		if ( model is null )
			return;

		// Swapped rather than SetModel, for the same reason sculpt swaps: SetModel destroys and
		// rebuilds the GameObject, which is fine once and not fine on every rebuild.
		if ( _renderer is not null )
			_renderer.Model = model;
		else
			SetModel( model, frameCamera: false );
	}

	private void PaintFrame()
	{
		if ( PaintSession is null )
			return;

		_paintCursor = null;

		var stroking = PaintSession.IsStroking;

		// The pointer leaving the canvas does NOT end a stroke, the same rule sculpt keeps: dragging
		// off the model and back on is ordinary, and ending there would make the tool drop the gesture.
		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			_paintCursor = PaintSession.Hover( origin, direction );

			if ( !stroking && Gizmo.WasLeftMousePressed )
			{
				if ( PaintSession.BeginStroke( origin, direction ) )
				{
					stroking = true;
					_paintPreviewStale = true;
				}
			}
			else if ( stroking && Gizmo.IsLeftMouseDown )
			{
				if ( PaintSession.MoveTo( origin, direction ) > 0 )
					_paintPreviewStale = true;
			}
		}

		// Released. Same inference as sculpt: the end of a stroke is the frame the button is no
		// longer down.
		if ( stroking && !Gizmo.IsLeftMouseDown )
		{
			var stroke = PaintSession.EndStroke();

			if ( stroke is not null )
				PaintStrokeFinished?.Invoke( stroke );
		}

		if ( _paintPreviewStale )
		{
			RefreshPaintPreview();
			_paintPreviewStale = false;
		}

		DrawPaintCursor();
	}

	/// <summary>The brush ring on the surface, lying in the surface's own plane — the same footprint
	/// the dab will paint, and the same reason sculpt draws it flat rather than camera-facing.</summary>
	private void DrawPaintCursor()
	{
		if ( _paintCursor is not { } hit )
			return;

		var normal = new Vector3( hit.Normal.x, hit.Normal.y, hit.Normal.z ).Normal;
		var centre = new Vector3( hit.Point.x, hit.Point.y, hit.Point.z );

		var reference = MathF.Abs( normal.z ) > 0.9f ? new Vector3( 1f, 0f, 0f ) : new Vector3( 0f, 0f, 1f );
		var right = Vector3.Cross( normal, reference ).Normal;
		var up = Vector3.Cross( normal, right ).Normal;

		var radius = PaintSession.Radius;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Color = PaintCursorColor;

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

		Gizmo.Draw.Line( centre, centre + normal * (radius * 0.35f) );
	}

	/// <summary>A paint ring is a paint ring — a colour distinct from sculpt's blue, so the two
	/// modes are never confused when a brush is armed.</summary>
	private static readonly Color PaintCursorColor = new( 1f, 0.45f, 0.75f, 0.9f );

	/// <summary>X for symmetry, the one toggle worth reaching for without leaving the model. Same
	/// letter as sculpt, same reason — it is the convention every brush tool uses.</summary>
	public bool HandlePaintKey( KeyEvent e )
	{
		if ( PaintSession is null )
			return false;

		if ( e.Key != KeyCode.X )
			return false;

		PaintSession.MirrorX = !PaintSession.MirrorX;
		PaintSettingsChanged?.Invoke();
		e.Accepted = true;

		return true;
	}
}
