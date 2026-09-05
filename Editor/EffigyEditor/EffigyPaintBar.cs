using Editor;
using Effigy;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>
/// The controls a paint stroke needs on screen: the colour, the brush radius, and how hard it
/// presses. The same shape as EffigySculptBar and for the same reason — these are values about the
/// STROKE, not tools, so they float near the model rather than living on the stage bar.
///
/// The colour is a swatch that opens s&amp;box's own ColorPicker on click, rather than a second
/// hand-rolled colour wheel: the editor library already ships one, and this tool's rule is to spend
/// its own code only on things the engine does not have.
/// </summary>
internal sealed class EffigyPaintBar : Widget
{
	public const float BarHeight = 28f;

	private readonly PaintSwatch _swatch;
	private readonly EffigyNumericField _radius;
	private readonly EffigyNumericField _strength;
	private readonly ComboBox _blend;

	private PaintSession _session;

	/// <summary>
	/// The layer being painted, which is where Blend lives.
	///
	/// The other three controls are values about the STROKE and belong to the session; Blend is one
	/// answer for the whole layer, so it is kept here rather than copied onto every stroke. The bar
	/// is the only place it can be reached at all — selecting a paint feature enters painting
	/// instead of opening a parameter dialog, so nothing else on screen would ever show it.
	/// </summary>
	private PaintFeature _feature;

	/// <summary>Set while Refresh is writing the controls, so a ComboBox firing its own selection
	/// callback on AddItem does not read back as the user having chosen something.</summary>
	private bool _refreshing;

	/// <summary>Raised when something here changed a value the viewport should redraw for.</summary>
	public Action Changed { get; set; }

	public EffigyPaintBar( Widget parent ) : base( parent )
	{
		// The same two flags every floating widget in this tool sets — a plain Widget paints the
		// system background, which is a white slab on the 3D view.
		TranslucentBackground = true;
		NoSystemBackground = true;

		Visible = false;
		FixedHeight = BarHeight;
		// Wide enough for the Blend dropdown that joined the three stroke controls; the row does not
		// wrap, so a short bar clips the last thing on it rather than pushing it onto a second line.
		FixedWidth = 560f;

		Layout = Layout.Row();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 0 );

		_swatch = new PaintSwatch( this ) { Picked = OnColorPicked };
		Layout.Add( _swatch );

		Layout.Add( new Editor.Label( "Radius" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_radius = new EffigyNumericField( this, 0.25f, "u" )
		{
			Min = 1e-4f,
			ValueEdited = OnRadiusEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _radius );

		Layout.Add( new Editor.Label( "Strength" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_strength = new EffigyNumericField( this, 1f )
		{
			ValueEdited = OnStrengthEdited,
			FixedWidth = 90f,
		};

		Layout.Add( _strength );

		Layout.Add( new Editor.Label( "Blend" ) { Color = Theme.TextControl.WithAlpha( 0.6f ) } );

		_blend = new ComboBox( this )
		{
			MinimumWidth = 90f,
			ToolTip = "Tint multiplies the paint into the material underneath, so the surface shows "
				+ "through. Replace paints onto white, so what you brushed is the colour that "
				+ "renders. Either way a face you dropped a material on keeps that material.",
		};

		Layout.Add( _blend );
	}

	/// <summary>The viewport's background, so the gaps between controls disappear into the 3D view.</summary>
	public Color GapColor { get; set; } = Theme.ControlBackground;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( GapColor );
		Paint.DrawRect( LocalRect );
	}

	public void Bind( PaintSession session, PaintFeature feature = null )
	{
		_session = session;
		_feature = feature;
		Visible = session is not null;

		if ( session is not null )
			Refresh();
	}

	public void Refresh()
	{
		if ( _session is null )
			return;

		_swatch.Color = new Color( _session.R, _session.G, _session.B, _session.A );
		_swatch.Update();

		_radius.SetValue( _session.Radius );
		_strength.SetValue( _session.Strength );

		RefreshBlend();
	}

	/// <summary>
	/// Rebuilt rather than re-selected, because ComboBox has no "set index without telling anyone"
	/// and AddItem takes the selected flag. The guard is what keeps that from writing the value
	/// back onto the feature and marking the document dirty for a control nobody touched.
	/// </summary>
	private void RefreshBlend()
	{
		if ( _blend is null )
			return;

		_blend.Enabled = _feature is not null;

		_refreshing = true;

		try
		{
			_blend.Clear();

			if ( _feature is null )
				return;

			var options = _feature.Blend.Options;

			for ( var i = 0; i < options.Length; i++ )
			{
				var index = i;

				_blend.AddItem( options[index], onSelected: () => OnBlendPicked( index ),
					selected: index == _feature.Blend.Index );
			}
		}
		finally
		{
			_refreshing = false;
		}
	}

	private void OnBlendPicked( int index )
	{
		if ( _refreshing || _feature is null || _feature.Blend.Index == index )
			return;

		_feature.Blend.Index = index;

		// Changed drives the viewport redraw; the document also has to know it has an unsaved edit,
		// which is the trap the rig panel already paid for - a value changed nowhere near the
		// studio leaves the title bar claiming there is nothing to save.
		BlendChanged?.Invoke();
	}

	/// <summary>Raised when Blend moved. Separate from <see cref="Changed"/> because this edits the
	/// DOCUMENT rather than the brush, so it has to mark unsaved and rebuild, not just redraw.
	/// </summary>
	public Action BlendChanged { get; set; }

	private void OnColorPicked( Color color )
	{
		if ( _session is null )
			return;

		_session.R = color.r;
		_session.G = color.g;
		_session.B = color.b;
		_session.A = color.a;

		Changed?.Invoke();
	}

	private void OnRadiusEdited( float value )
	{
		if ( _session is null )
			return;

		// Clamped rather than refused: a zero radius makes BeginStroke throw, and a tool that throws
		// because somebody cleared a box is not a tool.
		_session.Radius = MathF.Max( value, 1e-4f );
		Changed?.Invoke();
	}

	private void OnStrengthEdited( float value )
	{
		if ( _session is null )
			return;

		_session.Strength = Math.Clamp( value, 0f, 1f );
		Changed?.Invoke();
	}
}

/// <summary>
/// A coloured square that opens the colour picker on click. A plain painted widget rather than a
/// Button, because a Button wants a label or an icon and this is neither — it is the colour, and the
/// colour is the whole control.
/// </summary>
internal sealed class PaintSwatch : Widget
{
	public Color Color = Color.White;
	public Action<Color> Picked;

	public PaintSwatch( Widget parent ) : base( parent )
	{
		Cursor = CursorShape.Finger;
		FixedWidth = 28f;
		FixedHeight = 22f;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Color );
		Paint.DrawRect( LocalRect.Shrink( 2f ), 4f );

		// A hairline so a near-white or near-black swatch does not vanish into the viewport.
		Paint.SetPen( Theme.Text.WithAlpha( 0.4f ), 1f );
		Paint.DrawRect( LocalRect.Shrink( 2f ), 4f );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		e.Accepted = true;

		var picker = ColorPicker.OpenColorPopup( Color, c =>
		{
			Color = c;
			Picked?.Invoke( c );
			Update();
		} );

		picker.HasAlpha = true;
		picker.IsHDR = false;
	}
}
