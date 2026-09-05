using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The material brush's floating bar: which material is loaded, and how big the ring is.
///
/// THE MATERIAL IS SHOWN, NOT CHOSEN. It comes from the Materials browser, which the Paint
/// workspace already opens — putting a second picker here would be two controls answering one
/// question, and the browser is the one with thumbnails. So this reads it back instead, because a
/// brush that does not say what it is loaded with is a brush you have to test on the model to
/// identify.
///
/// Same shape and height as EffigyPaintBar and EffigySculptBar, so the three brushes put their
/// controls in the same place on screen.
/// </summary>
internal sealed class EffigyMaterialBrushBar : Widget
{
	public const float BarHeight = 28f;

	private readonly Editor.Label _material;
	private readonly EffigyNumericField _radius;

	private MaterialBrushSession _session;

	/// <summary>Raised when the radius moved, so the viewport redraws its ring at the new size.</summary>
	public Action Changed { get; set; }

	public EffigyMaterialBrushBar( Widget parent ) : base( parent )
	{
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		FixedWidth = 460f;

		Layout = Layout.Row();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		Layout.Add( new Editor.Label( "Material" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_material = new Editor.Label( "none" ) { Color = Theme.TextControl };
		Layout.Add( _material, 1 );

		Layout.Add( new Editor.Label( "Radius" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_radius = new EffigyNumericField( this, 0.25f, "u" )
		{
			Min = 1e-4f,
			ValueEdited = OnRadiusEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _radius );
	}

	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( MaterialBrushSession session )
	{
		_session = session;
		Visible = session is not null;

		if ( session is not null )
			Refresh();
	}

	/// <summary>The loaded material, named the way the prompt names it — the filename, because the
	/// full asset path is mostly folders and the bar has one line.</summary>
	public void SetMaterial( string material )
	{
		if ( !_material.IsValid() )
			return;

		_material.Text = string.IsNullOrWhiteSpace( material )
			? "none — pick one in the Materials browser"
			: System.IO.Path.GetFileNameWithoutExtension( material );

		_material.Color = string.IsNullOrWhiteSpace( material )
			? Theme.Yellow.WithAlpha( 0.9f )
			: Theme.TextControl;
	}

	public void Refresh()
	{
		if ( _session is null )
			return;

		_radius.SetValue( _session.Radius );
	}

	private void OnRadiusEdited( float value )
	{
		if ( _session is null )
			return;

		// Clamped rather than refused, the same call the paint bar makes: a zero radius is a brush
		// that covers nothing, and a tool that stops working because a box was cleared is not one.
		_session.Radius = MathF.Max( value, 1e-4f );
		Changed?.Invoke();
	}
}
