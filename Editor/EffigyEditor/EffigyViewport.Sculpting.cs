using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// Sculpt mode in the viewport: rays in, brush strokes out.
///
/// DELIBERATELY THE THINNEST PART OF THE SCULPT TOOL. Everything with a decision in it — where the
/// cursor sits on the surface, whether the pointer has travelled far enough to earn a sample, how a
/// fast drag gets filled in, what a stroke commits, undo — lives in <see cref="SculptSession"/> in
/// the kernel, where a test can see it. The editor cannot be compiled outside s&amp;box, and reading
/// editor code is how a bug that made every parameter edit a no-op survived long enough to look
/// like three unrelated UI faults. So this file converts Vector3 to Vec3, calls four methods, and
/// draws a circle.
///
/// Anything that starts to look like logic here belongs one floor down.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>The live sculpt, or null when not in sculpt mode.</summary>
	public SculptSession SculptSession { get; private set; }

	public bool IsSculpting => SculptSession is not null;

	/// <summary>Raised after a stroke commits, so the window can mark the feature dirty and
	/// rebuild. Not raised per sample — a stroke is one edit.</summary>
	public Action SculptStrokeFinished { get; set; }

	/// <summary>Raised when the viewport itself changes a brush setting — the X and M shortcuts —
	/// so the strip's ticks and the bar's readout can catch up with it.</summary>
	public Action SculptSettingsChanged { get; set; }

	/// <summary>The mesh the preview was last built from, so a frame that changed nothing does not
	/// rebuild a model.</summary>
	private bool _sculptPreviewStale;

	/// <summary>Where the brush ring is drawn this frame, or null when the cursor is off the
	/// model.</summary>
	private MeshHit? _sculptCursor;

	// The floating number bar, held for the same reason the result strip is: the frame loop has to
	// keep camera drags out of it, or dragging the radius slider also flies the view.
	private Widget _sculptBarOverlay;

	/// <summary>
	/// Put the sculpt number bar on the canvas.
	///
	/// The brushes themselves are stages on the tool bar now. This is the one sculpt control that
	/// stayed floating, because it is about the STROKE - radius, strength, the level you are on -
	/// rather than about which tool is armed, and it wants to be near the thing being brushed.
	/// </summary>
	public void AddSculptOverlay( Widget bar )
	{
		_sculptBarOverlay = bar;
		bar.Position = OverlayMargin + new Vector2( 0f, 46f );
		bar.Visible = false;
	}

	public void BeginSculpt( SculptSession session )
	{
		SculptSession = session ?? throw new ArgumentNullException( nameof( session ) );
		_sculptPreviewStale = true;
	}

	public void EndSculpt()
	{
		// A stroke left running when the mode ends would hold a working mesh nobody will ever
		// commit. Cancel rather than commit: leaving the mode is not a way to finish a stroke.
		if ( SculptSession is { IsStroking: true } )
			SculptSession.CancelStroke();

		SculptSession = null;
		_sculptCursor = null;
	}

	/// <summary>Push the sculpted surface into the viewport, replacing the model in place.</summary>
	public void RefreshSculptPreview()
	{
		if ( SculptSession is null )
			return;

		var model = EffigyPreview.Build( SculptSession.DisplayMesh );

		if ( model is null )
			return;

		// The renderer's model is swapped rather than SetModel called: SetModel destroys and rebuilds
		// the GameObject, which is fine once per feature edit and not fine several times a second
		// during a stroke.
		if ( _renderer is not null )
			_renderer.Model = model;
		else
			SetModel( model, frameCamera: false );
	}

	private void SculptFrame()
	{
		if ( SculptSession is null )
			return;

		_sculptCursor = null;

		var stroking = SculptSession.IsStroking;

		// The pointer leaving the canvas does NOT end a stroke — see SculptSession.MoveTo on why
		// dragging off the model and back has to keep working. It only stops new samples.
		if ( _canvasHasCursor )
		{
			var ray = Gizmo.CurrentRay;
			var origin = new Vec3( ray.Position.x, ray.Position.y, ray.Position.z );
			var direction = new Vec3( ray.Forward.x, ray.Forward.y, ray.Forward.z );

			_sculptCursor = SculptSession.Hover( origin, direction );

			if ( !stroking && Gizmo.WasLeftMousePressed )
			{
				if ( SculptSession.BeginStroke( origin, direction ) )
				{
					stroking = true;
					_sculptPreviewStale = true;
				}
			}
			else if ( stroking && Gizmo.IsLeftMouseDown )
			{
				if ( SculptSession.MoveTo( origin, direction ) > 0 )
					_sculptPreviewStale = true;
			}
		}

		// Released. There is no WasLeftMouseReleased in the Gizmo input this editor uses, so the
		// end of a stroke is the frame the button is no longer down — which is the same thing and
		// needs no API that might not be there.
		if ( stroking && !Gizmo.IsLeftMouseDown )
		{
			SculptSession.EndStroke();
			_sculptPreviewStale = true;
			SculptStrokeFinished?.Invoke();
		}

		if ( _sculptPreviewStale )
		{
			RefreshSculptPreview();
			_sculptPreviewStale = false;
		}

		DrawBrushCursor();
	}

	/// <summary>
	/// The ring on the surface, lying in the surface's own plane rather than facing the camera.
	///
	/// A camera-facing ring is easier to draw and lies about what the brush will do: the radius is
	/// in world units along the surface, so on a face turned away from the viewer a screen-facing
	/// circle covers far more of the model than it claims. Drawn flat on the surface it reads as the
	/// footprint it actually is.
	/// </summary>
	private void DrawBrushCursor()
	{
		if ( _sculptCursor is not { } hit )
			return;

		var normal = new Vector3( hit.Normal.x, hit.Normal.y, hit.Normal.z ).Normal;
		var centre = new Vector3( hit.Point.x, hit.Point.y, hit.Point.z );

		// Any two perpendiculars will do; the ring has no orientation to get wrong.
		var reference = MathF.Abs( normal.z ) > 0.9f ? new Vector3( 1f, 0f, 0f ) : new Vector3( 0f, 0f, 1f );
		var right = Vector3.Cross( normal, reference ).Normal;
		var up = Vector3.Cross( normal, right ).Normal;

		var radius = SculptSession.Radius;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;
		Gizmo.Draw.Color = SculptSession.Masking ? MaskCursorColor : BrushCursorColor;

		// Lifted off the surface by a whisker so it is not z-fighting the face it sits on.
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

		// A stub along the normal, so the brush reads as sitting ON the surface rather than floating
		// somewhere near it — the one thing a flat ring on a curved model is genuinely ambiguous
		// about.
		Gizmo.Draw.Line( centre, centre + normal * (radius * 0.35f) );
	}

	/// <summary>Ordinary brush: the same blue the rest of this editor uses for "you can act here".
	/// </summary>
	private static readonly Color BrushCursorColor = new( 0.35f, 0.75f, 1f, 0.9f );

	/// <summary>Masking is a different job and gets a different colour, or a stroke that protects
	/// looks exactly like one that sculpts.</summary>
	private static readonly Color MaskCursorColor = new( 1f, 0.85f, 0.3f, 0.9f );

	/// <summary>
	/// The two toggles worth reaching for without leaving the model: X for symmetry, M for masking.
	///
	/// LETTERS ONLY, AND THAT IS DELIBERATE. Every sculpting tool in the world puts brush radius on
	/// the bracket keys, and this one does not, because nothing in this editor has ever named a
	/// KeyCode outside letters, Escape, Enter, Delete and Backspace — so the bracket names are a
	/// guess, and a guessed enum member is a compile error at best and a dead key at worst. Radius
	/// and strength live on the sculpt bar instead, where they are also more discoverable. Put the
	/// brackets back once somebody has read the real KeyCode enum out of the shipped assembly.
	///
	/// X and M follow the convention every other sculpting tool uses, and the W/E/R bone shortcuts
	/// in this same viewport already prove letters work.
	/// </summary>
	public bool HandleSculptKey( KeyEvent e )
	{
		if ( SculptSession is null )
			return false;

		switch ( e.Key )
		{
			case KeyCode.X:
				SculptSession.MirrorX = !SculptSession.MirrorX;
				break;

			case KeyCode.M:
				SculptSession.Masking = !SculptSession.Masking;
				break;

			default:
				return false;
		}

		SculptSettingsChanged?.Invoke();
		e.Accepted = true;

		return true;
	}
}
