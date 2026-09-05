using Editor;
using Sandbox;
using System;

namespace Marionette.Tools;

/// <summary>Which drawing an icon button paints. Add as more are designed.</summary>
internal enum RigIcon
{
	/// <summary>A keyframe diamond with a plus punched through it.</summary>
	AddKeyframe,

	/// <summary>A trash can - lid, handle, tapered body, two slots.</summary>
	Delete
}

/// <summary>
/// A button whose icon is drawn rather than looked up in a font.
///
/// s&amp;box ships CLASSIC Material Icons (addons/base/Assets/fonts/MaterialIcons-Regular.ttf), not
/// the newer Material Symbols - so a name from the Symbols set silently renders as nothing. The
/// key button was set to "diamond", which is a Symbols icon and appears nowhere in the editor's
/// own source: it had been drawing blank.
///
/// Painting it removes the guesswork entirely, and lets the icon match the tool - this diamond is
/// the same shape the timeline draws for a Linear keyframe, in the same colour, so the button and
/// the thing it makes are visibly one idea.
/// </summary>
internal sealed class RigIconButton : Widget
{
	private readonly RigIcon _icon;
	private readonly Color _color;
	private readonly string _label;
	private readonly string _hint;
	private readonly Action _clicked;

	private bool _pressed;

	public RigIconButton( Widget parent, RigIcon icon, Color color, string label, string hint, Action clicked )
		: base( parent )
	{
		_icon = icon;
		_color = color;
		_label = label;
		_hint = hint;
		_clicked = clicked;

		ToolTip = hint;
		Cursor = CursorShape.Finger;
		MouseTracking = true;

		FixedHeight = 22f;
		FixedWidth = string.IsNullOrEmpty( label ) ? 28f : 58f;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;

		var hovered = IsUnderMouse;

		// Background follows the editor's own button states so it sits in a toolbar without
		// looking like a foreign object.
		Paint.ClearPen();
		Paint.SetBrush( _pressed
			? Theme.ControlBackground.Darken( 0.2f )
			: hovered ? Theme.ControlBackground.Lighten( 0.4f ) : Theme.ControlBackground );

		Paint.DrawRect( LocalRect, 3f );

		var iconCenter = string.IsNullOrEmpty( _label )
			? LocalRect.Center
			: new Vector2( 15f, LocalRect.Center.y );

		PaintIcon( iconCenter, hovered ? _color : _color.WithAlpha( 0.85f ) );

		if ( string.IsNullOrEmpty( _label ) )
			return;

		Paint.SetDefaultFont( 7, 600 );
		Paint.SetPen( _color.WithAlpha( hovered ? 1f : 0.85f ) );
		Paint.DrawText( new Rect( 26f, 0f, Width - 30f, Height ), _label, TextFlag.LeftCenter );
	}

	private void PaintIcon( Vector2 center, Color color )
	{
		switch ( _icon )
		{
			case RigIcon.AddKeyframe:
			{
				const float radius = 9f;

				Paint.ClearPen();
				Paint.SetBrush( color );
				Paint.DrawPolygon(
					center + new Vector2( 0f, -radius ),
					center + new Vector2( radius, 0f ),
					center + new Vector2( 0f, radius ),
					center + new Vector2( -radius, 0f ) );

				// The plus is PUNCHED OUT in the background colour rather than drawn on top, so it
				// reads as a hole through the diamond the way the mock does.
				Paint.SetBrush( Theme.ControlBackground );

				const float arm = 5.4f;
				const float thickness = 2.6f;

				Paint.DrawRect( new Rect( center.x - thickness * 0.5f, center.y - arm, thickness, arm * 2f ), 0.6f );
				Paint.DrawRect( new Rect( center.x - arm, center.y - thickness * 0.5f, arm * 2f, thickness ), 0.6f );
				return;
			}

			case RigIcon.Delete:
			{
				Paint.ClearPen();
				Paint.SetBrush( color );

				// Handle, then lid. Both rounded bars - at this size the roundness is most of what
				// makes it read as a drawn icon rather than a stack of rectangles.
				Paint.DrawRect( new Rect( center.x - 2.6f, center.y - 9f, 5.2f, 1.9f ), 0.9f );
				Paint.DrawRect( new Rect( center.x - 8f, center.y - 7f, 16f, 2.6f ), 1.3f );

				// Body, tapering in toward the bottom like the mock. Drawn as a filled trapezoid
				// with a smaller one punched out of it, which gives a thick even outline - simpler
				// and more predictable at 18px than trying to stroke a tapered path.
				const float topHalf = 6.6f;
				const float bottomHalf = 5.1f;
				const float top = -3.4f;
				const float bottom = 9f;

				Paint.DrawPolygon(
					center + new Vector2( -topHalf, top ),
					center + new Vector2( topHalf, top ),
					center + new Vector2( bottomHalf, bottom ),
					center + new Vector2( -bottomHalf, bottom ) );

				Paint.SetBrush( Theme.ControlBackground );

				const float inset = 2.1f;

				Paint.DrawPolygon(
					center + new Vector2( -topHalf + inset, top + inset ),
					center + new Vector2( topHalf - inset, top + inset ),
					center + new Vector2( bottomHalf - inset, bottom - inset ),
					center + new Vector2( -bottomHalf + inset, bottom - inset ) );

				// The two slots.
				Paint.SetBrush( color );
				Paint.DrawRect( new Rect( center.x - 2.9f, center.y - 0.6f, 1.9f, 5.6f ), 0.9f );
				Paint.DrawRect( new Rect( center.x + 1.0f, center.y - 0.6f, 1.9f, 5.6f ), 0.9f );
				return;
			}
		}
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		_pressed = true;
		Update();
		e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( !_pressed )
			return;

		_pressed = false;
		Update();

		// Only fires if released while still over the button - dragging off to cancel is what
		// every other button does.
		if ( IsUnderMouse )
			_clicked?.Invoke();
	}

	protected override void OnMouseEnter()
	{
		base.OnMouseEnter();
		RigStatusBar.Show( _hint );
		Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();
		RigStatusBar.Clear( _hint );

		_pressed = false;
		Update();
	}
}
