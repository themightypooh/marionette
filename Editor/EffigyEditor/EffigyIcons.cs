using Editor;
using System.Collections.Generic;
using Sandbox;
using System;

namespace Marionette.EditorTools;

/// <summary>Which drawing a feature-tool button paints. One per creation tool.</summary>
internal enum EffigyIcon
{
	Sketch,
	Primitive,
	Extrude,
	Revolve,
	Sweep,
	Loft,
	Chamfer,
	Fillet,
	Shell,
	Subdivide,
	Mirror,
	LinearPattern,
	CircularPattern,
	Transform,
	UVProject,
	FaceMaterial,
	Boolean,

	// --- sketch tools -------------------------------------------------------------------------
	// These were Material Icon NAMES until now, and generic ones: show_chart (a zigzag line chart)
	// for Line, cached (two refresh arrows) for Arc, crop_square for Rectangle. They said nothing
	// about the operation, and half of them said something actively misleading.
	SelectTool,
	LineTool,
	LineMidpointTool,
	RectangleTool,
	RectangleCentreTool,
	CircleTool,
	CircleThreePointTool,
	ArcTool,
	ArcThreePointTool,
	PolygonTool,
	PolygonCircumscribedTool,
	SlotTool,
	PointTool,
	ConstructionTool,
	ProfileInspectorTool,
	FinishSketchTool,

	// --- sculpt tools -------------------------------------------------------------------------
	// The brushes are drawn as what they DO to a surface rather than as tool shapes: a row of six
	// identical brush heads distinguished by a tiny badge is six ways to pick the wrong one. Every
	// glyph here is a surface line and what the brush does to it.
	Sculpt,
	SculptDraw,
	SculptSmooth,
	SculptInflate,
	SculptGrab,
	SculptFlatten,
	SculptPinch,
	SculptMask,
	SculptLevelDown,
	SculptLevelUp,
	SculptBake,

	// --- paint tools ---------------------------------------------------------------------------
	Paint,
	PaintBrush,
	PaintEraser,

	// --- solid tools that act on picked faces ---------------------------------------------------
	Draft,
	MoveFace,
	Hole,

	// --- the six sketch tools whose kernel half was finished first ------------------------------
	EllipseTool,
	SplineTool,
	TrimTool,
	ExtendTool,
	SketchFilletTool,
	OffsetTool,

	// --- taking the face's own outline into the sketch -------------------------------------------
	UseTool,
	UseAllTool,

	// --- the one sketch tool driven by a drag ----------------------------------------------------
	CutTool,

	// --- grease pencil: annotation, not geometry -------------------------------------------------
	// Both are drawn as the real-world objects rather than as marks, because that is the one thing
	// that says "this is not a modelling operation" before the tooltip gets a chance to. Every other
	// glyph in the bar is a shape being changed; these are stationery.
	NoteTool,
	NoteEraseTool,

	// --- lighting: viewport scenery, not geometry ------------------------------------------------
	// Drawn as lamps and as what a lamp DOES to a shape, for the same reason the notes above are
	// drawn as stationery: nothing on this stage changes the model, and the glyphs should say so
	// before the tooltip gets a chance to. The three rigs are the same sphere lit three ways, which
	// is the only honest way to draw the difference between them.
	LightFullBright,
	LightPoint,
	LightSpot,
	LightSun,
	LightRigThreePoint,
	LightRigRim,
	LightRigTop,
	LightRigKey,
	LightClear,

	// --- rig ---
	Bone,
	BoneBind,
	BoneSoft,
	SoftPreview,
	SoftRest,
}

/// <summary>
/// Painted icons for the feature-creation strip, drawn rather than looked up in a font.
///
/// Same reasoning as RigIconButton (see Editor/RigControlEditor): s&amp;box ships CLASSIC Material
/// Icons, not the newer Material Symbols, so a name from the Symbols set silently renders as
/// nothing — and the strip was leaning on generic names like "square", "flip" and "call_made"
/// that, where they resolved at all, said nothing about the CAD operation behind them. A drawn
/// glyph can show the actual operation: Chamfer cuts a corner off a square, Shell puts a wall
/// inside one, Mirror reflects a solid shape into an outlined one.
///
/// Every icon is drawn around <c>center</c> inside a nominal 18x18 box, so they all read at the
/// same weight, then scaled up as one by the <c>scale</c> argument to fit the button drawing it.
/// </summary>
internal static class EffigyIcons
{
	/// <summary>Stroke width every outline uses, so no icon looks heavier than its neighbours.</summary>
	private const float Stroke = 1.6f;

	// --- the pencil's own colours -------------------------------------------------------------
	//
	// The ONLY icon that does not draw entirely in the colour it is handed. Sketch is the tool
	// every part starts with and the only button in the strip carrying a text label, so it is the
	// one worth making findable at a glance rather than another grey glyph in a row of grey
	// glyphs. A yellow #2 is about as legible as a small object gets.
	//
	// Chosen against a dark viewport: the graphite is a mid grey rather than near-black, because a
	// true graphite point disappears into the background exactly where the icon needs to read.

	private static readonly Color PencilBody = new( 0.96f, 0.76f, 0.15f );
	private static readonly Color PencilFerrule = new( 0.74f, 0.77f, 0.80f );
	private static readonly Color PencilEraser = new( 0.91f, 0.56f, 0.58f );
	private static readonly Color PencilWood = new( 0.87f, 0.68f, 0.44f );
	private static readonly Color PencilGraphite = new( 0.45f, 0.47f, 0.50f );

	/// <summary>Multiplier applied to every coordinate, radius and pen width for the icon being
	/// drawn right now. Every glyph is authored against the nominal 18x18 box, so one factor set
	/// here at the top of Draw is enough to resize all of them together — the strip's buttons grew
	/// past the size the glyphs were drawn for and a fixed-size glyph in a big button reads as a
	/// mistake. Painting only ever happens on the editor UI thread, so a plain static is safe.</summary>
	private static float _scale = 1f;

	public static void Draw( EffigyIcon icon, Vector2 center, Color color, float scale = 1f )
	{
		Editor.Paint.Antialiasing = true;
		_scale = scale;

		switch ( icon )
		{
			case EffigyIcon.Sketch: PaintSketch( center, color ); return;
			case EffigyIcon.Primitive: PaintPrimitive( center, color ); return;
			case EffigyIcon.Extrude: PaintExtrude( center, color ); return;
			case EffigyIcon.Revolve: PaintRevolve( center, color ); return;
			case EffigyIcon.Sweep: PaintSweep( center, color ); return;
			case EffigyIcon.Loft: PaintLoft( center, color ); return;
			case EffigyIcon.Chamfer: PaintChamfer( center, color ); return;
			case EffigyIcon.Fillet: PaintFillet( center, color ); return;
			case EffigyIcon.Shell: PaintShell( center, color ); return;
			case EffigyIcon.Subdivide: PaintSubdivide( center, color ); return;
			case EffigyIcon.Mirror: PaintMirror( center, color ); return;
			case EffigyIcon.LinearPattern: PaintLinearPattern( center, color ); return;
			case EffigyIcon.CircularPattern: PaintCircularPattern( center, color ); return;
			case EffigyIcon.Transform: PaintTransform( center, color ); return;
			case EffigyIcon.UVProject: PaintUVProject( center, color ); return;
			case EffigyIcon.FaceMaterial: PaintFaceMaterial( center, color ); return;
			case EffigyIcon.Boolean: PaintBoolean( center, color ); return;

			case EffigyIcon.SelectTool: PaintSelectTool( center, color ); return;
			case EffigyIcon.LineTool: PaintLineTool( center, color ); return;
			case EffigyIcon.LineMidpointTool: PaintLineMidpointTool( center, color ); return;
			case EffigyIcon.RectangleTool: PaintRectangleTool( center, color ); return;
			case EffigyIcon.RectangleCentreTool: PaintRectangleCentreTool( center, color ); return;
			case EffigyIcon.CircleTool: PaintCircleTool( center, color ); return;
			case EffigyIcon.CircleThreePointTool: PaintCircleThreePointTool( center, color ); return;
			case EffigyIcon.ArcTool: PaintArcTool( center, color ); return;
			case EffigyIcon.ArcThreePointTool: PaintArcThreePointTool( center, color ); return;
			case EffigyIcon.PolygonTool: PaintPolygonTool( center, color ); return;
			case EffigyIcon.PolygonCircumscribedTool: PaintPolygonCircumscribedTool( center, color ); return;
			case EffigyIcon.SlotTool: PaintSlotTool( center, color ); return;
			case EffigyIcon.PointTool: PaintPointTool( center, color ); return;
			case EffigyIcon.ConstructionTool: PaintConstructionTool( center, color ); return;
			case EffigyIcon.ProfileInspectorTool: PaintProfileInspectorTool( center, color ); return;
			case EffigyIcon.FinishSketchTool: PaintFinishSketchTool( center, color ); return;

			case EffigyIcon.Sculpt: PaintSculpt( center, color ); return;
			case EffigyIcon.SculptDraw: PaintSculptDraw( center, color ); return;
			case EffigyIcon.SculptSmooth: PaintSculptSmooth( center, color ); return;
			case EffigyIcon.SculptInflate: PaintSculptInflate( center, color ); return;
			case EffigyIcon.SculptGrab: PaintSculptGrab( center, color ); return;
			case EffigyIcon.SculptFlatten: PaintSculptFlatten( center, color ); return;
			case EffigyIcon.SculptPinch: PaintSculptPinch( center, color ); return;
			case EffigyIcon.SculptMask: PaintSculptMask( center, color ); return;
			case EffigyIcon.SculptLevelDown: PaintSculptLevelDown( center, color ); return;
			case EffigyIcon.SculptLevelUp: PaintSculptLevelUp( center, color ); return;
			case EffigyIcon.SculptBake: PaintSculptBake( center, color ); return;

			case EffigyIcon.Paint: PaintPaint( center, color ); return;
			case EffigyIcon.PaintBrush: PaintPaintBrush( center, color ); return;
			case EffigyIcon.PaintEraser: PaintPaintEraser( center, color ); return;

			case EffigyIcon.Draft: PaintDraft( center, color ); return;
			case EffigyIcon.MoveFace: PaintMoveFace( center, color ); return;
			case EffigyIcon.Hole: PaintHole( center, color ); return;

			case EffigyIcon.NoteTool: PaintNoteTool( center, color ); return;
			case EffigyIcon.NoteEraseTool: PaintNoteEraseTool( center, color ); return;

			case EffigyIcon.LightFullBright: PaintLightFullBright( center, color ); return;
			case EffigyIcon.LightPoint: PaintLightPoint( center, color ); return;
			case EffigyIcon.LightSpot: PaintLightSpot( center, color ); return;
			case EffigyIcon.LightSun: PaintLightSun( center, color ); return;
			case EffigyIcon.LightRigThreePoint: PaintLightRigThreePoint( center, color ); return;
			case EffigyIcon.LightRigRim: PaintLightRigRim( center, color ); return;
			case EffigyIcon.LightRigTop: PaintLightRigTop( center, color ); return;
			case EffigyIcon.LightRigKey: PaintLightRigKey( center, color ); return;
			case EffigyIcon.LightClear: PaintLightClear( center, color ); return;

			case EffigyIcon.EllipseTool: PaintEllipseTool( center, color ); return;
			case EffigyIcon.SplineTool: PaintSplineTool( center, color ); return;
			case EffigyIcon.TrimTool: PaintTrimTool( center, color ); return;
			case EffigyIcon.ExtendTool: PaintExtendTool( center, color ); return;
			case EffigyIcon.SketchFilletTool: PaintSketchFilletTool( center, color ); return;
			case EffigyIcon.OffsetTool: PaintOffsetTool( center, color ); return;

			case EffigyIcon.UseTool: PaintUseTool( center, color ); return;
			case EffigyIcon.UseAllTool: PaintUseAllTool( center, color ); return;

			case EffigyIcon.CutTool: PaintCutTool( center, color ); return;

			case EffigyIcon.Bone: PaintBone( center, color ); return;
			case EffigyIcon.BoneBind: PaintBoneBind( center, color ); return;
			case EffigyIcon.BoneSoft: PaintBoneSoft( center, color ); return;
			case EffigyIcon.SoftPreview: PaintSoftPreview( center, color ); return;
			case EffigyIcon.SoftRest: PaintSoftRest( center, color ); return;
		}
	}

	// --- drawing helpers --------------------------------------------------------------------

	private static void Stroked( Color color, float width = Stroke )
	{
		Editor.Paint.ClearBrush();
		Editor.Paint.SetPen( color, width * _scale );
	}

	private static void Filled( Color color )
	{
		Editor.Paint.ClearPen();
		Editor.Paint.SetBrush( color );
	}

	/// <summary>Closed outline through the given points — DrawPolygon fills, so an outlined shape
	/// has to be walked as lines.</summary>
	private static void Outline( params Vector2[] points )
	{
		for ( var i = 0; i < points.Length; i++ )
			Editor.Paint.DrawLine( points[i], points[(i + 1) % points.Length] );
	}

	/// <summary>An arc as a polyline. There is no arc primitive in Paint, and approximating with
	/// segments is exact enough at icon size.</summary>
	private static void Arc( Vector2 center, float radius, float fromDegrees, float toDegrees, int segments = 14 )
	{
		var previous = Vector2.Zero;

		for ( var i = 0; i <= segments; i++ )
		{
			var t = fromDegrees + (toDegrees - fromDegrees) * (i / (float)segments);
			var radians = t * MathF.PI / 180f;
			var point = center + new Vector2( MathF.Cos( radians ) * radius, MathF.Sin( radians ) * radius ) * _scale;

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}
	}

	/// <summary>An elliptical arc as a polyline. Arc() draws a circle and cannot say "this circle
	/// is lying flat"; that foreshortening is the entire difference between a glyph that reads as
	/// a rotation about an axis and one that reads as a spiral.</summary>
	private static void EllipseArc( Vector2 center, float radiusX, float radiusY,
		float fromDegrees, float toDegrees, int segments = 24 )
	{
		var previous = Vector2.Zero;

		for ( var i = 0; i <= segments; i++ )
		{
			var t = fromDegrees + (toDegrees - fromDegrees) * (i / (float)segments);
			var radians = t * MathF.PI / 180f;
			var point = center + new Vector2( MathF.Cos( radians ) * radiusX, MathF.Sin( radians ) * radiusY ) * _scale;

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}
	}

	/// <summary>A solid triangular arrow head, pointing along <paramref name="direction"/>.</summary>
	private static void ArrowHead( Vector2 tip, Vector2 direction, Color color, float size = 3.4f )
	{
		var d = direction.Normal;
		var side = new Vector2( -d.y, d.x );

		size *= _scale;

		Filled( color );
		Editor.Paint.DrawPolygon(
			tip,
			tip - d * size + side * size * 0.62f,
			tip - d * size - side * size * 0.62f );
	}

	private static Vector2 At( Vector2 center, float x, float y ) => center + new Vector2( x, y ) * _scale;

	/// <summary>A rect in the same nominal icon space At() uses, for the glyphs that need
	/// DrawRect/DrawCircle rather than a walked outline.</summary>
	private static Rect Box( Vector2 center, float x, float y, float width, float height )
		=> new Rect( center.x + x * _scale, center.y + y * _scale, width * _scale, height * _scale );

	// --- the icons --------------------------------------------------------------------------

	/// <summary>
	/// A pencil drawing on a sheet, its point resting ON the paper's top edge.
	///
	/// The pencil used to be a plain parallelogram - blunt at both ends, with one of its corners
	/// landing on the paper line. A pencil reads as a pencil because of the cone at the end, and
	/// the mark reads as DRAWING because that cone touches the paper rather than hovering above
	/// it or crossing through it. So: a solid tapered point that ends exactly on the line, an
	/// outlined barrel behind it, and a band where the ferrule would be.
	///
	/// Every coordinate is derived from the tip and the pencil's axis, laid out along a 45 degree
	/// diagonal, so the point cannot drift off the paper if the proportions are adjusted.
	///
	/// The paper keeps the colour it is handed; the pencil does not (see PencilBody and friends).
	/// </summary>
	private static void PaintSketch( Vector2 c, Color color )
	{
		// Paper: a single flat horizontal line, matching the compact reference glyph. Everything
		// else is placed against PaperY.
		const float PaperY = 4.8f;

		// How thick the pencil is, across the barrel.
		const float BarrelWidth = 1.7f;

		Stroked( color, Stroke );
		Editor.Paint.DrawLine( At( c, -6.6f, PaperY ), At( c, 6.4f, PaperY ) );

		// The sharpened cone, sitting on the paper. Solid, because at 27px a hollow cone is a
		// smudge - the filled wedge is what makes it read as sharpened. Its base is exactly as
		// wide as the barrel's stroke, so the two meet without a step.
		Filled( PencilWood );
		Editor.Paint.DrawPolygon(
			At( c, -4.4f, PaperY ),
			At( c, -1.678f, 3.28f ),
			At( c, -2.88f, 2.078f ) );

		// The exposed lead, the outer 40% of that cone. Drawn over the wood rather than beside it,
		// so the two always agree about where the point is.
		Filled( PencilGraphite );
		Editor.Paint.DrawPolygon(
			At( c, -4.4f, PaperY ),
			At( c, -3.257f, 4.161f ),
			At( c, -3.761f, 3.657f ) );

		// Barrel: ONE STROKED LINE, not an outlined shape. A pencil this slim has a body 1.7 units
		// across, and two outline strokes inside that merge into a blob - the line IS the barrel,
		// and it is the only way to get a thin pencil that still reads at icon size. The ferrule
		// and eraser are further stretches of the same line, which is also why they cannot drift
		// out of alignment with it.
		Stroked( PencilBody, BarrelWidth );
		Editor.Paint.DrawLine( At( c, -2.279f, 2.679f ), At( c, 4.156f, -3.756f ) );

		Stroked( PencilFerrule, BarrelWidth );
		Editor.Paint.DrawLine( At( c, 4.156f, -3.756f ), At( c, 4.948f, -4.548f ) );

		Stroked( PencilEraser, BarrelWidth );
		Editor.Paint.DrawLine( At( c, 4.948f, -4.548f ), At( c, 5.853f, -5.453f ) );
	}

	/// <summary>An isometric cube — the generic "a solid body" mark.</summary>
	private static void PaintPrimitive( Vector2 c, Color color )
	{
		Stroked( color );
		Outline(
			At( c, 0, -7.5f ), At( c, 7, -3.6f ), At( c, 7, 3.6f ),
			At( c, 0, 7.5f ), At( c, -7, 3.6f ), At( c, -7, -3.6f ) );

		// The three edges meeting at the near corner are what make it read as a cube rather than
		// a hexagon.
		Editor.Paint.DrawLine( At( c, 0, 0 ), At( c, 0, 7.5f ) );
		Editor.Paint.DrawLine( At( c, 0, 0 ), At( c, 7, -3.6f ) );
		Editor.Paint.DrawLine( At( c, 0, 0 ), At( c, -7, -3.6f ) );
	}

	/// <summary>
	/// A profile lying flat, and the solid pulled UP off it.
	///
	/// The old glyph had the profile on top with the arrow pointing down, which reads as something
	/// falling rather than as something being drawn out — and at toolbar size the whole thing came
	/// out looking like a plumb bob. Arrow and profile now agree about which way an extrude goes.
	/// </summary>
	private static void PaintExtrude( Vector2 c, Color color )
	{
		// The sketch, in plan, dimmed: it is what the operation starts FROM, not what it makes.
		Stroked( color.WithAlpha( 0.55f ), 1.5f );
		Outline( At( c, -7.5f, 6.5f ), At( c, 0, 9.4f ), At( c, 7.5f, 6.5f ), At( c, 0, 3.6f ) );

		Stroked( color, 2.6f );
		Editor.Paint.DrawLine( At( c, 0, 6.5f ), At( c, 0, -4.2f ) );

		ArrowHead( At( c, 0, -8.6f ), new Vector2( 0, -1 ), color, 4.2f );
	}

	/// <summary>
	/// A sketch sitting on an axis, and the spin that turns it into a solid.
	///
	/// Extrude is a straight arrow off a profile. This is the same grammar bent into a C — axis,
	/// profile, curved arrow — which is what every CAD tool draws for Revolve and what the last
	/// version threw out. That version drew a vase in section and hoped the silhouette would
	/// carry it; at toolbar size it was a lumpy outline with a dashed line through its face.
	/// Fill the profile (same weight as Chamfer and Shell) and let the arrow be the operation.
	/// </summary>
	private static void PaintRevolve( Vector2 c, Color color )
	{
		// ONSHAPE'S OWN REVOLVE ICON: a disc with a wedge taken out of it. po pointed at it, and it
		// is better than either drawing that came before.
		//
		// The first attempt stacked a dashed axis, a filled profile rectangle and a circular arrow
		// on top of each other; the arc swept straight through the rectangle it was meant to be
		// spinning and the three merged into a tall dark blob that read as the letter D. The
		// second replaced it with a lathe - profile beside an axis, ellipse sweeping round it -
		// which was legible but was three small shapes doing the work of one, and small shapes are
		// what stop reading first when the strip is the only chrome on a 3D viewport.
		//
		// A disc with a mouth is ONE shape. It shows the result rather than the mechanism, which
		// is the idiom the rest of this strip already uses - Chamfer cuts a corner off a square,
		// Shell puts a wall inside one. The mouth is what makes it a revolve rather than a circle:
		// the two cut faces meeting at the centre are the start and end of the sweep, and they say
		// "this was swept through an angle" without needing an arrow to explain it. It is also
		// square and solid, so it holds its weight next to Extrude and Loft instead of being the
		// one thin glyph on the row.

		const float Radius = 8.6f;

		// How much of the disc is missing. Wide enough to read as a deliberate mouth at 32px
		// rather than as a nick in the outline, narrow enough that the shape is still a disc.
		const float MouthHalfAngle = 38f;

		// The rim, from one cut face round to the other, then in to the centre. Closing the loop
		// draws the second cut face, so the whole silhouette is one walk.
		var rim = ArcPoints( c, Radius, MouthHalfAngle, 360f - MouthHalfAngle, 30 );
		rim.Add( c );

		var silhouette = rim.ToArray();

		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon( silhouette );

		Stroked( color, 1.6f );
		Outline( silhouette );
	}

	/// <summary>
	/// A profile carried along a path, with the path drawn as the thing that shapes it.
	///
	/// Sweep and Extrude are the same sentence with a different verb — a profile, and where it goes
	/// — so they are drawn with the same grammar: the starting profile dim because it is what the
	/// operation begins FROM rather than what it makes, and an arrow for the operation itself. The
	/// difference between them is the whole point, so here the path is a curve and the solid
	/// follows it instead of standing straight up.
	/// </summary>
	private static void PaintSweep( Vector2 c, Color color )
	{
		// Hub, radius and extent of the path. Everything else is derived from these, so the glyph
		// stays consistent with itself if the arc is retuned.
		const float HubX = -7f;
		const float HubY = -7.5f;
		const float Radius = 12f;

		// Half the profile's width, so the band either side of the path IS the solid.
		const float Half = 2.6f;

		const float From = 0f;
		const float To = 90f;
		const float ArrowAt = To + 10f;

		var hub = At( c, HubX, HubY );

		// The swept solid: the path offset either side of itself. Faint rather than outlined at
		// full weight, so the path stays the strongest line in the glyph.
		Stroked( color.WithAlpha( 0.32f ), 2f );
		Arc( hub, Radius - Half, From, To, 18 );
		Arc( hub, Radius + Half, From, To, 18 );

		Stroked( color, 1.7f );
		Arc( hub, Radius, From, ArrowAt, 20 );

		// Where it starts, and where it arrives.
		SweepStation( hub, Radius, Half, From, color.WithAlpha( 0.55f ), 1.4f );
		SweepStation( hub, Radius, Half, To, color, 1.7f );

		var end = ArrowAt * MathF.PI / 180f;
		var tip = hub + new Vector2( MathF.Cos( end ), MathF.Sin( end ) ) * Radius * _scale;

		ArrowHead( tip, new Vector2( -MathF.Sin( end ), MathF.Cos( end ) ), color, 3.6f );
	}

	/// <summary>The profile at one station of a sweep: a diamond spanning the swept band, drawn
	/// ACROSS the path rather than lying flat, because a sweep takes its profile perpendicular to
	/// where it is going — see SweepFeature.</summary>
	private static void SweepStation( Vector2 hub, float radius, float half, float degrees, Color color, float width )
	{
		var angle = degrees * MathF.PI / 180f;
		var radial = new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) );
		var tangent = new Vector2( -radial.y, radial.x );
		var centre = hub + radial * radius * _scale;

		Stroked( color, width );
		Outline(
			centre + radial * half * _scale,
			centre + tangent * half * 0.62f * _scale,
			centre - radial * half * _scale,
			centre - tangent * half * 0.62f * _scale );
	}

	/// <summary>
	/// Two sections, and the skin ruled between them.
	///
	/// The sections are drawn as flat diamonds — the same plan-view profile Extrude and Sweep use,
	/// so a closed sketch reads the same way everywhere on this strip — one small and one large, so
	/// what lies between them has to be a loft rather than an extrusion. The sides are STRAIGHT,
	/// which is what the kernel actually does: neighbouring sections joined by a ruled surface, not
	/// a spline smoothly through them.
	/// </summary>
	private static void PaintLoft( Vector2 c, Color color )
	{
		const float TopY = -7f;
		const float TopHalf = 3.4f;
		const float BottomY = 6.6f;
		const float BottomHalf = 7.6f;

		// The skin, as a tint between the two sections — same weight as Chamfer and Shell, so it
		// reads as material rather than as two more lines.
		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon(
			At( c, -TopHalf, TopY ), At( c, TopHalf, TopY ),
			At( c, BottomHalf, BottomY ), At( c, -BottomHalf, BottomY ) );

		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, -TopHalf, TopY ), At( c, -BottomHalf, BottomY ) );
		Editor.Paint.DrawLine( At( c, TopHalf, TopY ), At( c, BottomHalf, BottomY ) );

		LoftSection( c, TopY, TopHalf, TopHalf * 0.46f, color );
		LoftSection( c, BottomY, BottomHalf, BottomHalf * 0.32f, color );
	}

	/// <summary>One loft section, in plan: a diamond as wide as the section and shallow enough to
	/// read as lying flat, rather than as the top and bottom edges of a trapezium.</summary>
	private static void LoftSection( Vector2 c, float y, float half, float depth, Color color )
	{
		Stroked( color, 1.5f );
		Outline( At( c, -half, y ), At( c, 0, y - depth ), At( c, half, y ), At( c, 0, y + depth ) );
	}

	/// <summary>
	/// A solid block with its corner cut away, the cut face called out.
	///
	/// The old glyph was an outlined square with a small nick in one corner, which at toolbar size
	/// is a page icon and nothing else. Two changes fix it: fill the body, so it reads as a solid
	/// rather than as a sheet, and cut deep enough that the chamfer is a face rather than a nick.
	/// The faint lines show the corner that was removed.
	/// </summary>
	private static void PaintChamfer( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon(
			At( c, -7, -1.5f ), At( c, -1.5f, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );

		Stroked( color, 1.5f );
		Outline( At( c, -7, -1.5f ), At( c, -1.5f, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );

		// The cut face, in the same amber the sketch pencil draws with — the accent on this strip
		// means "the thing this operation did".
		Stroked( ClickColor, 2.8f );
		Editor.Paint.DrawLine( At( c, -7, -1.5f ), At( c, -1.5f, -7 ) );

		Stroked( color.WithAlpha( 0.3f ), 1f );
		Editor.Paint.DrawLine( At( c, -7, -1.5f ), At( c, -7, -7 ) );
		Editor.Paint.DrawLine( At( c, -7, -7 ), At( c, -1.5f, -7 ) );
	}

	/// <summary>
	/// The chamfer's twin, and deliberately so: the same solid, the same corner gone, the same
	/// ghost of the corner that was removed — the ONLY difference is that the accent is an arc
	/// instead of a straight line.
	///
	/// That is the whole point. These two sit next to each other on the strip and the thing a
	/// person needs to tell apart at 40px is round versus flat, which a shared body makes obvious
	/// and two unrelated drawings would bury.
	/// </summary>
	private static void PaintFillet( Vector2 c, Color color )
	{
		// The arc's centre is the inner corner of the cut, so it runs from (-7,-1.5) to (-1.5,-7)
		// exactly where the chamfer's straight cut does.
		var arc = ArcPoints( At( c, -1.5f, -1.5f ), 5.5f, 180f, 270f, 10 );

		var body = new List<Vector2>( arc );
		body.Add( At( c, 7, -7 ) );
		body.Add( At( c, 7, 7 ) );
		body.Add( At( c, -7, 7 ) );

		Filled( color.WithAlpha( 0.22f ) );
		Editor.Paint.DrawPolygon( body.ToArray() );

		Stroked( color, 1.5f );
		Outline( body.ToArray() );

		Stroked( ClickColor, 2.8f );
		Arc( At( c, -1.5f, -1.5f ), 5.5f, 180f, 270f, 10 );

		Stroked( color.WithAlpha( 0.3f ), 1f );
		Editor.Paint.DrawLine( At( c, -7, -1.5f ), At( c, -7, -7 ) );
		Editor.Paint.DrawLine( At( c, -7, -7 ), At( c, -1.5f, -7 ) );
	}

	/// <summary>The points Arc walks, for a glyph that needs the arc as part of a filled outline
	/// rather than as a stroke. Same maths, so the fill and the stroke cannot drift apart.</summary>
	private static List<Vector2> ArcPoints( Vector2 center, float radius,
		float fromDegrees, float toDegrees, int segments )
	{
		var points = new List<Vector2>( segments + 1 );

		for ( var i = 0; i <= segments; i++ )
		{
			var t = fromDegrees + (toDegrees - fromDegrees) * (i / (float)segments);
			var radians = t * MathF.PI / 180f;

			points.Add( center + new Vector2( MathF.Cos( radians ) * radius, MathF.Sin( radians ) * radius ) * _scale );
		}

		return points;
	}

	/// <summary>
	/// A hollowed solid in section: material on three sides, opening at the top.
	///
	/// A square inside a square is a frame, a border, a picture — it was never going to say
	/// "hollowed to a wall thickness". THE WALL IS THE OBJECT, so the wall is what gets filled and
	/// the void is what gets left out, which is how a section drawing says it.
	/// </summary>
	/// <summary>
	/// Two overlapping squares with the shared region solid — a boolean, drawn as what it operates
	/// on rather than as which of the three it is.
	///
	/// ONE GLYPH FOR ALL THREE OPERATIONS, because the button carries a dropdown and the variant
	/// chosen is named on it. A Venn lens is the universal mark for this and reads at twenty-four
	/// pixels; three near-identical lenses differing only in which part is filled do not, and the
	/// one thing worse than an icon you have to think about is three you have to tell apart.
	///
	/// SQUARES RATHER THAN CIRCLES so the overlap is exact: the intersection of two axis-aligned
	/// rectangles is a rectangle, which DrawRect can fill honestly. Two circles would need the lens
	/// approximated by a polygon, and at this size the approximation is what you would see.
	/// </summary>
	private static void PaintBoolean( Vector2 c, Color color )
	{
		// The shared volume first, so the outlines drawn over it keep their edges crisp.
		Filled( color.WithAlpha( 0.9f ) );
		Editor.Paint.DrawRect( Box( c, -2f, -3f, 4f, 6f ), 0.8f * _scale );

		Stroked( color.WithAlpha( 0.85f ), 1.2f );
		Editor.Paint.DrawRect( Box( c, -7f, -6f, 9f, 9f ), 1.4f * _scale );
		Editor.Paint.DrawRect( Box( c, -2f, -3f, 9f, 9f ), 1.4f * _scale );
	}

	private static void PaintShell( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.9f ) );
		Editor.Paint.DrawPolygon(
			At( c, -7.8f, -7f ), At( c, -3.6f, -7f ), At( c, -3.6f, 3.2f ),
			At( c, 3.6f, 3.2f ), At( c, 3.6f, -7f ), At( c, 7.8f, -7f ),
			At( c, 7.8f, 7.4f ), At( c, -7.8f, 7.4f ) );

		// The opening, as a faint lid line, so the U reads as a container rather than as a letter.
		Stroked( color.WithAlpha( 0.45f ), 1.1f );
		Editor.Paint.DrawLine( At( c, -3.6f, -7f ), At( c, 3.6f, -7f ) );
	}

	/// <summary>
	/// A quad split into four, with one of those four split again — subdivision, drawn literally.
	///
	/// The old glyph was a rounded square with a cross and a dot in the middle, which is the
	/// universal "add" icon and was read as one. Showing one quadrant DENSER than its neighbours is
	/// what the operation actually does, and the density is carried by a tint as well as by lines so
	/// it survives being small — at twenty-four pixels a 4x4 of hairlines is a grey smear.
	/// </summary>
	private static void PaintSubdivide( Vector2 c, Color color )
	{
		Stroked( color, 1.6f );
		Outline( At( c, -8, -8 ), At( c, 8, -8 ), At( c, 8, 8 ), At( c, -8, 8 ) );

		Filled( color.WithAlpha( 0.3f ) );
		Editor.Paint.DrawPolygon( At( c, -8, -8 ), At( c, 0, -8 ), At( c, 0, 0 ), At( c, -8, 0 ) );

		Stroked( color.WithAlpha( 0.9f ), 1.5f );
		Editor.Paint.DrawLine( At( c, 0, -8 ), At( c, 0, 8 ) );
		Editor.Paint.DrawLine( At( c, -8, 0 ), At( c, 8, 0 ) );

		Stroked( color.WithAlpha( 0.85f ), 1.2f );
		Editor.Paint.DrawLine( At( c, -4, -8 ), At( c, -4, 0 ) );
		Editor.Paint.DrawLine( At( c, -8, -4 ), At( c, 0, -4 ) );
	}

	/// <summary>A solid shape and its reflection across a dashed mirror line.</summary>
	private static void PaintMirror( Vector2 c, Color color )
	{
		// Mirror plane, dashed.
		Stroked( color.WithAlpha( 0.5f ), 1.2f );
		for ( var y = -8f; y < 8f; y += 3.6f )
			Editor.Paint.DrawLine( At( c, 0, y ), At( c, 0, y + 2.1f ) );

		// Source: solid.
		Filled( color );
		Editor.Paint.DrawPolygon( At( c, -2.4f, -5.6f ), At( c, -8, 0 ), At( c, -2.4f, 5.6f ) );

		// Reflection: outlined, so the two are not mistaken for a pattern.
		Stroked( color );
		Outline( At( c, 2.4f, -5.6f ), At( c, 8, 0 ), At( c, 2.4f, 5.6f ) );
	}

	/// <summary>One body copied along a direction — first solid, copies outlined and fading.</summary>
	private static void PaintLinearPattern( Vector2 c, Color color )
	{
		// TWO FAULTS, AND THE SECOND IS THE ONE THAT MATTERED. Three 6-unit squares starting at
		// -8.4 ran to +12, so the glyph was 20.4 units wide in a box of 18 - and, worse, its centre
		// of mass sat 1.8 units RIGHT of the button's centre, because the run was never balanced
		// about it. An off-centre glyph in a row of centred ones is visible long before an
		// oversized one is, and neither is visible while looking at this icon on its own.
		//
		// Three 5.2 squares with 1.2 between them is 18 exactly, laid out symmetrically about c.
		const float Square = 5.2f;
		const float Gap = 1.2f;
		const float First = -9f;

		Filled( color );
		Editor.Paint.DrawRect( Box( c, First, -Square / 2f, Square, Square ), 1.2f * _scale );

		Stroked( color.WithAlpha( 0.8f ) );
		Editor.Paint.DrawRect( Box( c, First + Square + Gap, -Square / 2f, Square, Square ), 1.2f * _scale );

		Stroked( color.WithAlpha( 0.45f ) );
		Editor.Paint.DrawRect( Box( c, First + 2f * (Square + Gap), -Square / 2f, Square, Square ), 1.2f * _scale );
	}

	/// <summary>
	/// Copies stepped around an axis, on a ring that can actually be seen.
	///
	/// The old glyph drew the ring as twelve dashes, and at toolbar size twelve dashes are a faint
	/// smudge — which left three small squares floating with nothing to explain them. A solid thin
	/// ring and a dot at the centre cost less ink and say more, and the copies are smaller than they
	/// were so they sit ON the ring instead of swallowing it.
	/// </summary>
	private static void PaintCircularPattern( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.7f ), 1.4f );
		Arc( c, 6.6f, 0f, 360f, 40 );

		Filled( color.WithAlpha( 0.8f ) );
		Editor.Paint.DrawRect( Box( c, -1.3f, -1.3f, 2.6f, 2.6f ), 1.3f * _scale );

		var angles = new[] { -90f, 30f, 150f };

		for ( var i = 0; i < angles.Length; i++ )
		{
			var radians = angles[i] * MathF.PI / 180f;

			var box = Box( c,
				MathF.Cos( radians ) * 6.6f - 2.5f,
				MathF.Sin( radians ) * 6.6f - 2.5f, 5f, 5f );

			// One filled, the rest outlined — the same "this is the original, these are the copies"
			// grammar the linear pattern uses, so the pair read as a family.
			if ( i == 0 )
			{
				Filled( color );
				Editor.Paint.DrawRect( box, 1f * _scale );
			}
			else
			{
				Stroked( color.WithAlpha( 0.9f ), 1.5f );
				Editor.Paint.DrawRect( box, 1f * _scale );
			}
		}
	}

	/// <summary>Move/rotate/scale — a body with four-way translation arrows through it.</summary>
	private static void PaintTransform( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.6f ) );
		Outline( At( c, -4, -4 ), At( c, 4, -4 ), At( c, 4, 4 ), At( c, -4, 4 ) );

		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, 0, -5.4f ), At( c, 0, 5.4f ) );
		Editor.Paint.DrawLine( At( c, -5.4f, 0 ), At( c, 5.4f, 0 ) );

		ArrowHead( At( c, 0, -8.2f ), new Vector2( 0, -1 ), color, 3f );
		ArrowHead( At( c, 0, 8.2f ), new Vector2( 0, 1 ), color, 3f );
		ArrowHead( At( c, -8.2f, 0 ), new Vector2( -1, 0 ), color, 3f );
		ArrowHead( At( c, 8.2f, 0 ), new Vector2( 1, 0 ), color, 3f );
	}

	/// <summary>A UV grid with one texel lit, and the projection arriving from off-surface.</summary>
	private static void PaintUVProject( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -7, -2 ), At( c, 7, -2 ), At( c, 7, 8 ), At( c, -7, 8 ) );

		Stroked( color.WithAlpha( 0.6f ), 1.1f );
		Editor.Paint.DrawLine( At( c, -2.4f, -2 ), At( c, -2.4f, 8 ) );
		Editor.Paint.DrawLine( At( c, 2.4f, -2 ), At( c, 2.4f, 8 ) );
		Editor.Paint.DrawLine( At( c, -7, 3 ), At( c, 7, 3 ) );

		// One lit texel, so the grid reads as a texture rather than a wireframe.
		Filled( color.WithAlpha( 0.8f ) );
		Editor.Paint.DrawRect( Box( c, -6.3f, -1.3f, 3.8f, 3.6f ), 0.6f * _scale );

		// The projection coming down onto it.
		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, 0, -8.4f ), At( c, 0, -4.4f ) );
		ArrowHead( At( c, 0, -2.6f ), new Vector2( 0, 1 ), color, 3f );
	}

	/// <summary>A cube with ONE of its three visible faces filled — the operation is "this face,
	/// not that one", so what the glyph has to show is faces being told apart. A paint pot or a
	/// swatch would say "material" without saying "per face", which is the whole distinction.</summary>
	private static void PaintFaceMaterial( Vector2 c, Color color )
	{
		// Isometric cube: top rhombus, then the two visible side quads.
		var top = At( c, 0, -8 );
		var right = At( c, 8, -3.5f );
		var bottom = At( c, 0, 1 );
		var left = At( c, -8, -3.5f );

		// The lit face, filled. DrawPolygon fills, which is exactly what is wanted here and is why
		// the other faces are walked as lines instead.
		Filled( color.WithAlpha( 0.85f ) );
		Editor.Paint.DrawPolygon( top, right, bottom, left );

		Stroked( color );
		Outline( top, right, bottom, left );

		// The two side faces, left plain so the filled top reads as the odd one out.
		var lowLeft = At( c, -8, 5.5f );
		var lowMid = At( c, 0, 10 );
		var lowRight = At( c, 8, 5.5f );

		Outline( left, bottom, lowMid, lowLeft );
		Outline( bottom, right, lowRight, lowMid );
	}

	// --- sketch tools ---------------------------------------------------------------------------
	//
	// One rule for the whole row: SHOW THE SHAPE THE TOOL MAKES, AND SHOW HOW IT IS PLACED.
	//
	// The second half is what earns its keep. Every family behind a chevron draws the identical
	// shape and differs only in which points you click — a corner rectangle and a centre rectangle
	// are the same rectangle — so the shape alone cannot tell them apart. The shape is the body of
	// the glyph and the click points are accent dots on it, which makes the pair legible side by
	// side without either needing a label.
	//
	// The dots are annotation and must never outweigh the shape. They were half again this size to
	// begin with, which looked right on a large preview and swallowed the geometry at the size these
	// are actually seen at.

	/// <summary>The colour of a click point. Deliberately the one warm accent in a monochrome row,
	/// so "this is where you press" reads before anything else does.</summary>
	private static readonly Color ClickColor = new( 1f, 0.77f, 0.24f, 1f );

	/// <summary>An end that does not join up. Warm rather than red — this is information, not an
	/// error, and a sketch mid-draw is full of them.</summary>
	private static readonly Color LooseEndColor = new( 1f, 0.48f, 0.36f, 1f );

	/// <summary>A guide line — a radius, a diagonal, a centre line. Something the tool uses to place
	/// the shape rather than part of the shape itself.</summary>
	private static Color GuideColor( Color color ) => color.WithAlpha( 0.35f );

	/// <summary>A filled dot in the nominal icon space. DrawRect with a corner radius of half its
	/// own size, since Paint has no circle of its own and this is exact.</summary>
	private static void Dot( Vector2 center, float radius, Color color )
	{
		Filled( color );
		Editor.Paint.DrawRect( Box( center, -radius, -radius, radius * 2f, radius * 2f ), radius * _scale );
	}

	private static void ClickDot( Vector2 p, float radius = 1.8f ) => Dot( p, radius, ClickColor );

	/// <summary>The corners of a regular hexagon, for the two polygon tools.</summary>
	private static Vector2[] Hexagon( Vector2 c, float radius, float rotationDegrees )
	{
		var points = new Vector2[6];

		for ( var i = 0; i < 6; i++ )
		{
			var a = (rotationDegrees + i * 60f) * MathF.PI / 180f;
			points[i] = At( c, MathF.Cos( a ) * radius, MathF.Sin( a ) * radius );
		}

		return points;
	}

	/// <summary>A cursor with a point caught under it. Select drags sketch POINTS, which is what the
	/// dot says and a bare arrow would not.</summary>
	private static void PaintSelectTool( Vector2 c, Color color )
	{
		Filled( color );
		Editor.Paint.DrawPolygon(
			At( c, -4, -8 ), At( c, -4, 4 ), At( c, -1, 1 ), At( c, 1.5f, 6 ),
			At( c, 4, 5 ), At( c, 1.5f, 0.2f ), At( c, 5, -0.5f ) );

		ClickDot( At( c, 5, 5 ), 2.2f );
	}

	private static void PaintLineTool( Vector2 c, Color color )
	{
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -6.5f, 6 ), At( c, 6.5f, -6 ) );

		ClickDot( At( c, -6.5f, 6 ) );
		ClickDot( At( c, 6.5f, -6 ) );
	}

	/// <summary>The same line, marked at its MIDDLE - with the tick mark that means midpoint in every
	/// CAD package there is. One click dot, because the second click is an end and the far one comes
	/// for free.</summary>
	private static void PaintLineMidpointTool( Vector2 c, Color color )
	{
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -6.5f, 6 ), At( c, 6.5f, -6 ) );

		// Across the line rather than along it, so it reads as a mark ON the line instead of a second
		// shorter line beside it.
		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( At( c, -2.1f, -2.3f ), At( c, 2.1f, 2.3f ) );

		ClickDot( c, 1.9f );
		ClickDot( At( c, 6.5f, -6 ) );
	}

	/// <summary>Two opposite corners marked: click one, then the other.</summary>
	private static void PaintRectangleTool( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -6.5f, -5 ), At( c, 6.5f, -5 ), At( c, 6.5f, 5 ), At( c, -6.5f, 5 ) );

		ClickDot( At( c, -6.5f, -5 ) );
		ClickDot( At( c, 6.5f, 5 ) );
	}

	/// <summary>The same rectangle, marked at its CENTRE instead — with the half-diagonal it is
	/// dragged out along.</summary>
	private static void PaintRectangleCentreTool( Vector2 c, Color color )
	{
		Stroked( color );
		Outline( At( c, -6.5f, -5 ), At( c, 6.5f, -5 ), At( c, 6.5f, 5 ), At( c, -6.5f, 5 ) );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( c, At( c, 6.5f, 5 ) );

		ClickDot( c, 1.9f );
	}

	private static void PaintCircleTool( Vector2 c, Color color )
	{
		Stroked( color );
		Arc( c, 6.2f, 0, 360, 28 );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( c, At( c, 6.2f, 0 ) );

		ClickDot( c, 1.9f );
	}

	/// <summary>The same circle with three points ON the rim and no centre — which is precisely the
	/// difference between the two ways of placing it.</summary>
	private static void PaintCircleThreePointTool( Vector2 c, Color color )
	{
		Stroked( color );
		Arc( c, 6.2f, 0, 360, 28 );

		foreach ( var degrees in new[] { -90f, 30f, 150f } )
		{
			var a = degrees * MathF.PI / 180f;
			ClickDot( At( c, MathF.Cos( a ) * 6.2f, MathF.Sin( a ) * 6.2f ) );
		}
	}

	/// <summary>
	/// An arc standing on its centre, with both radii drawn.
	///
	/// It was drawn small and off to one side first, and at the size these are actually used it read
	/// as a tick mark rather than a curve. An arc has to span the box to look like an arc.
	/// </summary>
	private static void PaintArcTool( Vector2 c, Color color )
	{
		// RADIUS 9, NOT 10.5, AND THE HUB SITS AT 4.2 RATHER THAN 5.5. A radius of 10.5 put the
		// guide rails 21 units apart inside a box this file says is 18, which made this the second
		// widest glyph on the strip - and nothing about one icon in isolation shows that. Nine
		// fills the width exactly, and dropping the hub centres the drawing's own height in the
		// button instead of hanging it below the middle.
		var hub = At( c, 0, 4.2f );

		Stroked( color, 1.9f );
		Arc( hub, 9f, 180, 360, 20 );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( hub, At( c, -9f, 4.2f ) );
		Editor.Paint.DrawLine( hub, At( c, 9f, 4.2f ) );

		ClickDot( hub, 1.9f );
	}

	/// <summary>The same arc with no centre at all, marked instead at both ends and the point it
	/// passes through.</summary>
	private static void PaintArcThreePointTool( Vector2 c, Color color )
	{
		// A SMALLER ARC THAN PaintArcTool's, ON PURPOSE. This was the widest glyph on the strip by
		// a distance - 24.6 units against a nominal 18 - and the arc was not what made it so: the
		// three dots are the whole point of the tool, they sit ON the arc's ends, and a 1.8 dot at
		// x = 10.5 reaches 12.3. The dots are part of the drawing, so the arc gives up the room for
		// them rather than the pair of icons disagreeing about how wide an arc glyph is: both now
		// fill exactly 18 units, which is the measurement the eye actually compares.
		var hub = At( c, 0, 4.2f );

		Stroked( color, 1.9f );
		Arc( hub, 7.2f, 180, 360, 20 );

		ClickDot( At( c, -7.2f, 4.2f ) );
		ClickDot( At( c, 0, -3f ) );
		ClickDot( At( c, 7.2f, 4.2f ) );
	}

	/// <summary>
	/// A polygon with its corners ON the circle.
	///
	/// The circle is drawn brighter than a guide normally would be, because where it sits relative
	/// to the polygon IS the whole difference between this and the circumscribed version. Draw it at
	/// guide strength and the two glyphs become the same hexagon.
	/// </summary>
	private static void PaintPolygonTool( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		Arc( c, 7f, 0, 360, 28 );

		Stroked( color, 1.7f );
		Outline( Hexagon( c, 7f, -90f ) );

		ClickDot( c, 1.7f );
	}

	/// <summary>Edges on the circle instead, so it sits visibly inside the polygon — the apothem is
	/// 0.866 of the radius, a gap wide enough to read small.</summary>
	private static void PaintPolygonCircumscribedTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		Outline( Hexagon( c, 7.6f, -90f ) );

		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		Arc( c, 6.6f, 0, 360, 28 );

		ClickDot( c, 1.7f );
	}

	/// <summary>A slot, with the centre line you actually click marked at both ends.</summary>
	private static void PaintSlotTool( Vector2 c, Color color )
	{
		const float r = 4.6f;

		Stroked( color );
		Editor.Paint.DrawLine( At( c, -3, -r ), At( c, 3, -r ) );
		Editor.Paint.DrawLine( At( c, -3, r ), At( c, 3, r ) );
		Arc( At( c, 3, 0 ), r, -90, 90, 12 );
		Arc( At( c, -3, 0 ), r, 90, 270, 12 );

		Stroked( GuideColor( color ), 1f );
		Editor.Paint.DrawLine( At( c, -3, 0 ), At( c, 3, 0 ) );

		ClickDot( At( c, -3, 0 ) );
		ClickDot( At( c, 3, 0 ) );
	}

	/// <summary>Crosshairs around a point. The gap at the centre is what stops it reading as a plus
	/// sign.</summary>
	private static void PaintPointTool( Vector2 c, Color color )
	{
		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, -7, 0 ), At( c, -2.5f, 0 ) );
		Editor.Paint.DrawLine( At( c, 2.5f, 0 ), At( c, 7, 0 ) );
		Editor.Paint.DrawLine( At( c, 0, -7 ), At( c, 0, -2.5f ) );
		Editor.Paint.DrawLine( At( c, 0, 2.5f ), At( c, 0, 7 ) );

		ClickDot( c, 2.2f );
	}

	/// <summary>A dashed line: geometry that guides and never becomes part of a profile. Dashed
	/// because that is how construction geometry is drawn in the viewport, so the button and the
	/// thing it makes look like each other.</summary>
	private static void PaintConstructionTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		foreach ( var (from, to) in new[] { (0f, 0.22f), (0.39f, 0.61f), (0.78f, 1f) } )
		{
			Editor.Paint.DrawLine(
				At( c, -7 + 14 * from, 6 - 12 * from ),
				At( c, -7 + 14 * to, 6 - 12 * to ) );
		}
	}

	/// <summary>
	/// A shaded region with a gap in its outline, and the two loose ends called out.
	///
	/// This is exactly what the inspector shows: which regions closed, and where a chain did not.
	/// Drawn first as a stub with a dot on it, which at the size these are seen reads as a box with
	/// a speck in the corner and says nothing. The fill has to be solid enough to read as shading and
	/// the gap has to be a real hole in the outline.
	/// </summary>
	private static void PaintProfileInspectorTool( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.41f ) );
		Editor.Paint.DrawPolygon( At( c, -6.5f, -5 ), At( c, 6.5f, -5 ), At( c, 6.5f, 5 ), At( c, -6.5f, 5 ) );

		// Walked as an open polyline rather than an outline, because the gap is the point.
		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, 6.5f, -1.6f ), At( c, 6.5f, -5 ) );
		Editor.Paint.DrawLine( At( c, 6.5f, -5 ), At( c, -6.5f, -5 ) );
		Editor.Paint.DrawLine( At( c, -6.5f, -5 ), At( c, -6.5f, 5 ) );
		Editor.Paint.DrawLine( At( c, -6.5f, 5 ), At( c, 6.5f, 5 ) );
		Editor.Paint.DrawLine( At( c, 6.5f, 5 ), At( c, 6.5f, 1.6f ) );

		Dot( At( c, 6.5f, -1.6f ), 1.9f, LooseEndColor );
		Dot( At( c, 6.5f, 1.6f ), 1.9f, LooseEndColor );
	}

	/// <summary>A plain tick. The one glyph in the row that must not be clever: it ends the mode,
	/// and the confirm colour it is painted in already carries the meaning.</summary>
	private static void PaintFinishSketchTool( Vector2 c, Color color )
	{
		Stroked( color, 2.2f );
		Editor.Paint.DrawLine( At( c, -6, 0.5f ), At( c, -1.5f, 5 ) );
		Editor.Paint.DrawLine( At( c, -1.5f, 5 ), At( c, 6.5f, -5 ) );
	}

	// --- sculpt tools ---------------------------------------------------------------------------
	//
	// EVERY ONE OF THESE IS A SURFACE AND WHAT HAPPENS TO IT. The obvious way to draw six brushes is
	// six brush heads with a small badge each, which at 27px is six identical blobs. Drawing the
	// EFFECT instead means the row can be read at a glance without learning it: a bump rising, a
	// ripple flattening, a peak dragged sideways.
	//
	// The surface runs across the lower half so every glyph shares a baseline and the row reads as
	// one family.

	/// <summary>A surface with a bump pushed up out of it, and the brush's ring resting on the
	/// bump. The feature-strip glyph, so it says "sculpting" rather than any one brush.</summary>
	private static void PaintSculpt( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		SurfaceWithBump( c, 4.5f );

		// The brush ring, seen at a slight angle so it reads as sitting ON the surface.
		Stroked( color.WithAlpha( 0.75f ), 1.3f );
		Arc( At( c, 0, -3.4f ), 5.6f, 0f, 360f, 20 );
	}

	/// <summary>A bump, and an arrow pushing outward from it: draw adds material along the normal.</summary>
	private static void PaintSculptDraw( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		SurfaceWithBump( c, 4f );

		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, 0, -1.5f ), At( c, 0, -7f ) );
		ArrowHead( At( c, 0, -8f ), new Vector2( 0, -1 ), color );
	}

	/// <summary>A ripple above, the same surface calmed below. Smooth is the one brush whose whole
	/// meaning is the difference between two lines.</summary>
	private static void PaintSculptSmooth( Vector2 c, Color color )
	{
		// Rippled.
		Stroked( color.WithAlpha( 0.85f ), 1.5f );
		var previous = At( c, -8.5f, -4f );

		for ( var i = 1; i <= 24; i++ )
		{
			var t = i / 24f;
			var x = -8.5f + 17f * t;
			var y = -4f + MathF.Sin( t * MathF.PI * 3f ) * 2.6f;
			var point = At( c, x, y );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}

		// Calmed.
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -8.5f, 5f ), At( c, 8.5f, 5f ) );

		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		Editor.Paint.DrawLine( At( c, 0, -0.5f ), At( c, 0, 2.2f ) );
		ArrowHead( At( c, 0, 3.4f ), new Vector2( 0, 1 ), color.WithAlpha( 0.6f ), 2.8f );
	}

	/// <summary>A closed shape with arrows pushing out all round it — inflate acts everywhere at
	/// once, which is what tells it apart from draw.</summary>
	private static void PaintSculptInflate( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );
		Arc( c, 4.6f, 0f, 360f, 20 );

		for ( var i = 0; i < 4; i++ )
		{
			var radians = (45f + i * 90f) * MathF.PI / 180f;
			var dir = new Vector2( MathF.Cos( radians ), MathF.Sin( radians ) );

			Stroked( color.WithAlpha( 0.9f ), 1.3f );
			Editor.Paint.DrawLine( c + dir * 5.8f * _scale, c + dir * 8f * _scale );
			ArrowHead( c + dir * 9.2f * _scale, dir, color, 2.8f );
		}
	}

	/// <summary>A surface dragged sideways into a lean, with the pull shown as an arrow. Grab moves
	/// what it holds rather than adding to it, so nothing here points along the normal.</summary>
	private static void PaintSculptGrab( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		// A peak that leans right, rather than a symmetric bump.
		Editor.Paint.DrawLine( At( c, -8.5f, 5f ), At( c, -2.5f, 5f ) );
		Editor.Paint.DrawLine( At( c, -2.5f, 5f ), At( c, 2.5f, -3f ) );
		Editor.Paint.DrawLine( At( c, 2.5f, -3f ), At( c, 5.5f, 5f ) );
		Editor.Paint.DrawLine( At( c, 5.5f, 5f ), At( c, 8.5f, 5f ) );

		Stroked( color.WithAlpha( 0.9f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -2f, -6f ), At( c, 3.5f, -6f ) );
		ArrowHead( At( c, 5f, -6f ), new Vector2( 1, 0 ), color );
	}

	/// <summary>A bump with a straight edge laid across it — flatten is a plane meeting a surface.
	/// </summary>
	private static void PaintSculptFlatten( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.65f ), 1.5f );
		SurfaceWithBump( c, 5.5f );

		// The plane it is being cut back to.
		Stroked( color, 2f );
		Editor.Paint.DrawLine( At( c, -8.5f, -2.5f ), At( c, 8.5f, -2.5f ) );
	}

	/// <summary>Two arrows squeezing towards one ridge. Pinch gathers a surface rather than moving
	/// it, so both arrows point inward at the same line.</summary>
	private static void PaintSculptPinch( Vector2 c, Color color )
	{
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, 0, -7.5f ), At( c, 0, 7.5f ) );

		Stroked( color.WithAlpha( 0.9f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -8f, 0 ), At( c, -3.5f, 0 ) );
		ArrowHead( At( c, -2.2f, 0 ), new Vector2( 1, 0 ), color );

		Editor.Paint.DrawLine( At( c, 8f, 0 ), At( c, 3.5f, 0 ) );
		ArrowHead( At( c, 2.2f, 0 ), new Vector2( -1, 0 ), color );
	}

	/// <summary>A patch of the surface hatched off. Masking protects rather than shapes, so this is
	/// the one sculpt glyph that is not a deformation.</summary>
	private static void PaintSculptMask( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.85f ), 1.5f );
		Outline( At( c, -8, -6.5f ), At( c, 8, -6.5f ), At( c, 8, 6.5f ), At( c, -8, 6.5f ) );

		// Hatching, the universal "held back" texture.
		Stroked( color.WithAlpha( 0.7f ), 1.2f );

		for ( var x = -6f; x <= 8f; x += 3.4f )
		{
			var top = MathF.Max( x - 13f, -8f );
			var bottom = MathF.Min( x, 8f );

			Editor.Paint.DrawLine( At( c, bottom, -6.5f ), At( c, top, 6.5f ) );
		}
	}

	/// <summary>A coarse grid with a chevron down: fewer, bigger faces.</summary>
	private static void PaintSculptLevelDown( Vector2 c, Color color ) => PaintSculptLevel( c, color, 2, down: true );

	/// <summary>A fine grid with a chevron up: four times the faces, which is the whole cost.
	/// </summary>
	private static void PaintSculptLevelUp( Vector2 c, Color color ) => PaintSculptLevel( c, color, 4, down: false );

	private static void PaintSculptLevel( Vector2 c, Color color, int divisions, bool down )
	{
		const float Half = 6.5f;

		Stroked( color.WithAlpha( 0.9f ), 1.4f );
		Outline( At( c, -Half, -Half - 1.5f ), At( c, Half, -Half - 1.5f ),
			At( c, Half, Half - 1.5f ), At( c, -Half, Half - 1.5f ) );

		Stroked( color.WithAlpha( 0.65f ), 1f );

		for ( var i = 1; i < divisions; i++ )
		{
			var t = -Half + i * (Half * 2f / divisions);

			Editor.Paint.DrawLine( At( c, t, -Half - 1.5f ), At( c, t, Half - 1.5f ) );
			Editor.Paint.DrawLine( At( c, -Half, t - 1.5f ), At( c, Half, t - 1.5f ) );
		}

		// The chevron, below the grid so the two never overlap at strip size.
		Stroked( color, 1.8f );

		if ( down )
		{
			Editor.Paint.DrawLine( At( c, -3.5f, 6f ), At( c, 0, 9f ) );
			Editor.Paint.DrawLine( At( c, 0, 9f ), At( c, 3.5f, 6f ) );
		}
		else
		{
			Editor.Paint.DrawLine( At( c, -3.5f, 9f ), At( c, 0, 6f ) );
			Editor.Paint.DrawLine( At( c, 0, 6f ), At( c, 3.5f, 9f ) );
		}
	}

	/// <summary>A dense surface collapsing into a flat square: the sculpt becoming a texture, which
	/// is the whole point of the pipeline and the one operation here that produces a file.</summary>
	/// <summary>
	/// A grease pencil laid over a wavy scribble.
	///
	/// NOT THE SKETCH PENCIL, which this sits two buttons away from and must not be mistaken for.
	/// That one is a sharp #2 drawing a straight line and it makes geometry; this is a fat blunt
	/// marker over a loose squiggle, and the squiggle is doing the work — a scribble is what
	/// handwriting looks like at 18px, and nothing that produces a solid in this bar is drawn
	/// scribbly.
	/// </summary>
	private static void PaintNoteTool( Vector2 c, Color color )
	{
		// The scribble first, so the marker sits on top of it the way a pen sits on its own line.
		Stroked( color.WithAlpha( 0.75f ), 1.5f );

		var previous = At( c, -8f, 5.5f );

		for ( var i = 1; i <= 20; i++ )
		{
			var t = i / 20f;
			var point = At( c, -8f + 13f * t, 5.5f + MathF.Sin( t * MathF.PI * 2.2f ) * 1.8f );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}

		// The barrel, drawn as a slab on the diagonal rather than the sketch pencil's thin shaft.
		var tip = At( c, -5.5f, 1.5f );
		var along = (At( c, 7f, -7.5f ) - tip).Normal;
		var across = new Vector2( -along.y, along.x ) * (2.6f * _scale);

		Stroked( color, 1.5f );
		Outline(
			tip + across * 0.35f,
			At( c, 7f, -7.5f ) + across,
			At( c, 8.5f, -8.5f ) + across,
			At( c, 8.5f, -8.5f ) - across,
			At( c, 7f, -7.5f ) - across,
			tip - across * 0.35f );

		// The nib, filled: the one part of a marker that is a different colour from the barrel, and
		// what makes the shape read as pointing at the scribble rather than away from it.
		Filled( color );
		Editor.Paint.DrawPolygon( tip, At( c, -3.2f, -0.4f ) + across * 0.75f, At( c, -3.2f, -0.4f ) - across * 0.75f );
	}

	/// <summary>An eraser on the same scribble, taking a bite out of it. The gap in the line is the
	/// whole glyph — an eraser drawn hovering over an intact scribble is just a second block
	/// shape.</summary>
	private static void PaintNoteEraseTool( Vector2 c, Color color )
	{
		// Left half of the scribble survives; the right half is where the eraser has been.
		Stroked( color.WithAlpha( 0.75f ), 1.5f );

		var previous = At( c, -8.5f, 5.5f );

		for ( var i = 1; i <= 10; i++ )
		{
			var t = i / 20f;
			var point = At( c, -8.5f + 13f * t, 5.5f + MathF.Sin( t * MathF.PI * 2.2f ) * 1.8f );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}

		// The block, tilted so it reads as being pushed along the line rather than parked on it.
		var down = new Vector2( 0.34f, 0.94f );
		var side = new Vector2( -down.y, down.x );
		var centre = At( c, 3f, -1f );
		var half = 4.6f * _scale;
		var length = 5.6f * _scale;

		Stroked( color, 1.5f );
		Outline(
			centre - down * length + side * half,
			centre + down * length + side * half,
			centre + down * length - side * half,
			centre - down * length - side * half );

		// The ferrule line across it, which is what separates an eraser from a plain rectangle.
		Editor.Paint.DrawLine( centre + side * half, centre - side * half );
	}

	private static void PaintSculptBake( Vector2 c, Color color )
	{
		// The sculpted surface, up top.
		Stroked( color.WithAlpha( 0.85f ), 1.5f );
		var previous = At( c, -8.5f, -5f );

		for ( var i = 1; i <= 20; i++ )
		{
			var t = i / 20f;
			var x = -8.5f + 17f * t;
			var y = -5f + MathF.Sin( t * MathF.PI * 2f ) * 2.2f;
			var point = At( c, x, y );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}

		// Into the map.
		Stroked( color.WithAlpha( 0.7f ), 1.2f );
		Editor.Paint.DrawLine( At( c, 0, -1f ), At( c, 0, 1.6f ) );
		ArrowHead( At( c, 0, 2.8f ), new Vector2( 0, 1 ), color.WithAlpha( 0.7f ), 2.8f );

		Stroked( color, 1.6f );
		Outline( At( c, -7, 4f ), At( c, 7, 4f ), At( c, 7, 9f ), At( c, -7, 9f ) );

		Filled( color.WithAlpha( 0.3f ) );
		Editor.Paint.DrawRect( Box( c, -7, 4f, 14f, 5f ) );
	}

	/// <summary>A paint brush: a handle, a ferrule, and a bristle tip dragging a stroke of colour
	/// behind it. The stroke is the point — this is a tool that leaves marks, not one that changes
	/// the surface's shape.</summary>
	private static void PaintPaint( Vector2 c, Color color )
	{
		// The stroke it leaves, drawn first so it sits behind the brush.
		Stroked( color.WithAlpha( 0.7f ), 2.4f );
		Editor.Paint.DrawLine( At( c, 4.5f, 5f ), At( c, -1f, -6f ) );

		// The handle, tilted.
		Stroked( color, 2f );
		Editor.Paint.DrawLine( At( c, -3f, 8f ), At( c, -1.5f, 3.5f ) );

		// The ferrule.
		Editor.Paint.DrawLine( At( c, -2f, 3.5f ), At( c, 2.5f, 1.2f ) );

		// The bristle tip.
		Filled( color );
		Outline( At( c, 1.6f, 1f ), At( c, 3.4f, -2f ), At( c, 4.5f, -3.6f ), At( c, 6.4f, -1.8f ) );
	}

	/// <summary>The same brush as PaintPaint, with the ferrule separated by a hair so the stroke
	/// reads as a wide band rather than a line.</summary>
	private static void PaintPaintBrush( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.7f ), 3.2f );
		Editor.Paint.DrawLine( At( c, 5f, 4f ), At( c, -1f, -7f ) );

		Stroked( color, 2f );
		Editor.Paint.DrawLine( At( c, -3f, 8f ), At( c, -1.5f, 3.5f ) );
		Editor.Paint.DrawLine( At( c, -2f, 3.5f ), At( c, 2.5f, 1.2f ) );

		Filled( color );
		Outline( At( c, 1.6f, 1f ), At( c, 3.4f, -2f ), At( c, 4.5f, -3.6f ), At( c, 6.4f, -1.8f ) );
	}

	/// <summary>An eraser block, worn at one corner, with its band cut away. Drawn as stationery
	/// rather than as a mark, for the same reason the notes are: it removes rather than adds.</summary>
	private static void PaintPaintEraser( Vector2 c, Color color )
	{
		var half = 4.5f;
		var length = 7f;

		Stroked( color.WithAlpha( 0.7f ), 1.8f );
		Editor.Paint.DrawLine( At( c, -6f, 3.5f ), At( c, 3f, -6f ) );

		Stroked( color, 1.8f );
		var topLeft = At( c, -half, -length );
		var topRight = At( c, half, -length );
		var bottomLeft = At( c, -half, length );

		// Worn corner: the bottom-right corner is cut off.
		Outline( topLeft, topRight, At( c, half, 2f ), At( c, 2f, length ), bottomLeft );

		// The band.
		Editor.Paint.DrawLine( At( c, -half, -3f ), At( c, half, -3f ) );
	}


	/// <summary>The shared baseline: a flat surface with one smooth bump in the middle of it. Every
	/// brush glyph starts from this so the row reads as one family acting on one thing.</summary>
	private static void SurfaceWithBump( Vector2 c, float height )
	{
		var previous = At( c, -8.5f, 5f );

		for ( var i = 1; i <= 24; i++ )
		{
			var t = i / 24f;
			var x = -8.5f + 17f * t;

			// A raised cosine, flat at both ends so it meets the surface without a corner.
			var bump = 0.5f * (1f + MathF.Cos( MathF.Max( MathF.Min( x / 5.5f, 1f ), -1f ) * MathF.PI ));
			var point = At( c, x, 5f - bump * height );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}
	}

	/// <summary>
	/// A wall leaning off vertical, with the vertical it leans from left dashed beside it.
	///
	/// The angle IS the operation, so the glyph is the angle. Drawing a moulded part instead would
	/// say "moulding" and leave you guessing which of the six tools on the strip does the leaning.
	/// </summary>
	/// <summary>
	/// A face lifting off the solid it belongs to: the body drawn faintly where it was, the face
	/// itself solid at its new height, and an arrow between them.
	///
	/// THE GHOST IS THE WHOLE GLYPH. Without it this is an arrow over a rectangle, which is what
	/// Transform looks like; with it, the picture is of one face of a part having moved and the rest
	/// having stayed — which is exactly what the tool does and what tells it apart from Draft
	/// standing next to it.
	/// </summary>
	private static void PaintMoveFace( Vector2 c, Color color )
	{
		// Where the face was, and the walls that stretched to follow it.
		Stroked( color.WithAlpha( 0.35f ), 1.1f );
		Outline( At( c, -7f, 1f ), At( c, 7f, 1f ), At( c, 7f, 8f ), At( c, -7f, 8f ) );

		// The face, at its new height.
		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, -7f, -5f ), At( c, 7f, -5f ) );

		// The sides it dragged up with it.
		Stroked( color.WithAlpha( 0.75f ), 1.2f );
		Editor.Paint.DrawLine( At( c, -7f, -5f ), At( c, -7f, 1f ) );
		Editor.Paint.DrawLine( At( c, 7f, -5f ), At( c, 7f, 1f ) );

		// Which way it went.
		Stroked( color, 1.6f );
		Editor.Paint.DrawLine( At( c, 0f, -1f ), At( c, 0f, -8f ) );
		Editor.Paint.DrawLine( At( c, -3f, -5f ), At( c, 0f, -8f ) );
		Editor.Paint.DrawLine( At( c, 3f, -5f ), At( c, 0f, -8f ) );
	}

	private static void PaintDraft( Vector2 c, Color color )
	{
		// The parting line the taper is measured from.
		Stroked( color.WithAlpha( 0.45f ), 1.1f );
		for ( var x = -9f; x < 9f; x += 3.4f )
			Editor.Paint.DrawLine( At( c, x, 0 ), At( c, x + 2f, 0 ) );

		// The tapered wall: narrow at the top, wide at the bottom, closed as an outline.
		Stroked( color, 1.7f );
		Outline( At( c, -3.4f, -8 ), At( c, 3.4f, -8 ), At( c, 6.6f, 8 ), At( c, -6.6f, 8 ) );

		// The vertical it is leaning away from, so the lean reads as deliberate rather than as a
		// wonky rectangle.
		Stroked( color.WithAlpha( 0.5f ), 1f );
		Editor.Paint.DrawLine( At( c, 3.4f, -8 ), At( c, 3.4f, 8 ) );
	}

	/// <summary>
	/// A counterbore in section: a wide mouth stepping down to a narrow shaft, through a plate.
	///
	/// Drawn as a SECTION rather than as a circle on a surface, because a circle is what every other
	/// round thing on this strip already looks like from above - and the step is the whole reason
	/// this is a feature rather than a sketched circle.
	/// </summary>
	private static void PaintHole( Vector2 c, Color color )
	{
		// The plate, in section.
		Stroked( color.WithAlpha( 0.8f ), 1.5f );
		Editor.Paint.DrawLine( At( c, -9, -6 ), At( c, -3.2f, -6 ) );
		Editor.Paint.DrawLine( At( c, 3.2f, -6 ), At( c, 9, -6 ) );
		Editor.Paint.DrawLine( At( c, -9, 7 ), At( c, 9, 7 ) );
		Editor.Paint.DrawLine( At( c, -9, -6 ), At( c, -9, 7 ) );
		Editor.Paint.DrawLine( At( c, 9, -6 ), At( c, 9, 7 ) );

		// The bore: wide at the mouth, stepping in to the shaft.
		Stroked( color, 1.7f );
		Editor.Paint.DrawLine( At( c, -3.2f, -6 ), At( c, -3.2f, -1 ) );
		Editor.Paint.DrawLine( At( c, -3.2f, -1 ), At( c, -1.4f, -1 ) );
		Editor.Paint.DrawLine( At( c, -1.4f, -1 ), At( c, -1.4f, 7 ) );

		Editor.Paint.DrawLine( At( c, 3.2f, -6 ), At( c, 3.2f, -1 ) );
		Editor.Paint.DrawLine( At( c, 3.2f, -1 ), At( c, 1.4f, -1 ) );
		Editor.Paint.DrawLine( At( c, 1.4f, -1 ), At( c, 1.4f, 7 ) );

		// The void itself, so the shape reads as absence rather than as a post.
		Filled( color.WithAlpha( 0.18f ) );
		Editor.Paint.DrawPolygon(
			At( c, -3.2f, -6 ), At( c, 3.2f, -6 ), At( c, 3.2f, -1 ), At( c, 1.4f, -1 ),
			At( c, 1.4f, 7 ), At( c, -1.4f, 7 ), At( c, -1.4f, -1 ), At( c, -3.2f, -1 ) );
	}
	// --- the six sketch tools -------------------------------------------------------------------
	//
	// The four EDIT tools all show the same thing: a curve, and what the tool does to it, with the
	// part being removed or added drawn faintly. A trim that showed only the result would be
	// indistinguishable from a plain line at 27 pixels.

	/// <summary>An ellipse, with its long axis marked so it is not mistaken for a circle.</summary>
	private static void PaintEllipseTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		var previous = Vector2.Zero;

		for ( var i = 0; i <= 40; i++ )
		{
			var a = i / 40f * MathF.PI * 2f;
			var point = At( c, MathF.Cos( a ) * 8.6f, MathF.Sin( a ) * 5f );

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}

		Stroked( color.WithAlpha( 0.5f ), 1.1f );
		Editor.Paint.DrawLine( At( c, -8.6f, 0 ), At( c, 8.6f, 0 ) );
	}

	/// <summary>A curve through its control points, which is what a spline IS - the points are the
	/// thing you place and the curve is what follows.</summary>
	private static void PaintSplineTool( Vector2 c, Color color )
	{
		Stroked( color, 1.7f );

		var previous = Vector2.Zero;

		// A cubic-ish wiggle through the three dots below.
		for ( var i = 0; i <= 32; i++ )
		{
			var t = i / 32f;
			var x = -7.5f + 15f * t;
			var y = MathF.Sin( t * MathF.PI * 1.6f + 0.4f ) * 5.2f - 1f;
			var point = At( c, x, y );

			if ( i > 0 )
				Editor.Paint.DrawLine( previous, point );

			previous = point;
		}

		Filled( color );

		// 15 UNITS OF CURVE, NOT 17. The end dots are centred on the curve's own ends and are 3
		// units across, so a curve spanning 17 drew a glyph spanning 20 in a box of 18. Fifteen
		// plus the two half-dots is exactly 18. The dot positions are recomputed from the same
		// expression as the curve rather than being written out, so the two cannot drift apart.
		foreach ( var t in new[] { 0f, 0.5f, 1f } )
		{
			var x = -7.5f + 15f * t;
			var y = MathF.Sin( t * MathF.PI * 1.6f + 0.4f ) * 5.2f - 1f;

			Editor.Paint.DrawRect( Box( c, x - 1.5f, y - 1.5f, 3f, 3f ), 1.5f * _scale );
		}
	}

	/// <summary>Two crossing lines with the stub past the crossing drawn faintly - the piece that
	/// goes. Trim is defined by what it removes, so that is what the glyph shows.</summary>
	private static void PaintTrimTool( Vector2 c, Color color )
	{
		// The cutting line.
		Stroked( color.WithAlpha( 0.55f ), 1.3f );
		Editor.Paint.DrawLine( At( c, 2.5f, -8.5f ), At( c, 2.5f, 8.5f ) );

		// The part that stays.
		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -8.5f, 3f ), At( c, 2.5f, 0f ) );

		// The part that goes, dashed.
		Stroked( color.WithAlpha( 0.35f ), 1.5f );
		for ( var t = 0f; t < 1f; t += 0.28f )
		{
			var a = At( c, 2.5f + 6f * t, -1.6f * t );
			var b = At( c, 2.5f + 6f * (t + 0.16f), -1.6f * (t + 0.16f) );

			Editor.Paint.DrawLine( a, b );
		}
	}

	/// <summary>A line reaching a boundary, with the new length drawn faintly and an arrow head -
	/// the mirror of Trim, and drawn as its mirror so the pair reads as a pair.</summary>
	private static void PaintExtendTool( Vector2 c, Color color )
	{
		// The boundary it reaches to.
		Stroked( color.WithAlpha( 0.55f ), 1.3f );
		Editor.Paint.DrawLine( At( c, 6.5f, -8.5f ), At( c, 6.5f, 8.5f ) );

		// What is there now.
		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -8.5f, 3f ), At( c, -1f, 1f ) );

		// Where it is going.
		Stroked( color.WithAlpha( 0.4f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -1f, 1f ), At( c, 5f, -0.6f ) );
		ArrowHead( At( c, 6.4f, -1f ), new Vector2( 1f, -0.26f ), color.WithAlpha( 0.75f ), 3f );
	}

	/// <summary>A rounded corner with the square one it replaces dashed behind it.</summary>
	private static void PaintSketchFilletTool( Vector2 c, Color color )
	{
		// The corner that was.
		Stroked( color.WithAlpha( 0.35f ), 1.3f );
		Editor.Paint.DrawLine( At( c, -8, -7 ), At( c, 7, -7 ) );
		Editor.Paint.DrawLine( At( c, 7, -7 ), At( c, 7, 8 ) );

		// The corner that is.
		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -8, -7 ), At( c, 0f, -7 ) );
		Arc( At( c, 0f, 0f ), 7f, -90f, 0f, 12 );
		Editor.Paint.DrawLine( At( c, 7, 0f ), At( c, 7, 8 ) );
	}

	/// <summary>A shape and a second one running parallel outside it, which is the whole of what an
	/// offset is - the same curve, held away at a distance.</summary>
	private static void PaintOffsetTool( Vector2 c, Color color )
	{
		// The original.
		Stroked( color, 1.8f );
		Editor.Paint.DrawLine( At( c, -6, 6 ), At( c, -6, -2 ) );
		Arc( At( c, -1.5f, -2f ), 4.5f, 180f, 270f, 10 );
		Editor.Paint.DrawLine( At( c, -1.5f, -6.5f ), At( c, 5, -6.5f ) );

		// Its offset, outside and parallel.
		Stroked( color.WithAlpha( 0.55f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -9.5f, 6 ), At( c, -9.5f, -2 ) );
		Arc( At( c, -1.5f, -2f ), 8f, 180f, 270f, 12 );
		Editor.Paint.DrawLine( At( c, -1.5f, -10f ), At( c, 5, -10f ) );
	}

	/// <summary>The face's outline drawn faint, with ONE of its edges taken - solid, and in the
	/// green the viewport paints reference geometry, so the button and the thing it acts on are
	/// obviously the same thing.</summary>
	private static void PaintUseTool( Vector2 c, Color color )
	{
		Faint( color );
		Outline( At( c, -7, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );

		// The one edge that has been taken.
		Stroked( ReferenceColor, 2.6f );
		Editor.Paint.DrawLine( At( c, -7, 7 ), At( c, 7, 7 ) );

		ClickDot( At( c, 0, 7 ) );
	}

	/// <summary>The same square with every edge taken, which is what the button does in one press.
	/// </summary>
	private static void PaintUseAllTool( Vector2 c, Color color )
	{
		Faint( color );
		Editor.Paint.DrawLine( At( c, -7, 0 ), At( c, 7, 0 ) );

		Stroked( ReferenceColor, 2.6f );
		Outline( At( c, -7, -7 ), At( c, 7, -7 ), At( c, 7, 7 ), At( c, -7, 7 ) );
	}

	/// <summary>
	/// A freehand stroke swept across three lines, with the pieces it went through dashed away.
	///
	/// The STROKE is the subject and is drawn in the red the viewport draws it in, because that is
	/// the thing the button is offering to let you do. Trim's glyph shows one clean crossing; this
	/// one shows a wobble through several, which is the difference between the two tools.
	/// </summary>
	private static void PaintCutTool( Vector2 c, Color color )
	{
		// Three uprights, each with the piece the stroke went through faded out. The gap is what the
		// tool does; the two solid ends are what it leaves.
		for ( var i = 0; i < 3; i++ )
		{
			var x = -6f + i * 6f;

			Stroked( color, 1.8f );
			Editor.Paint.DrawLine( At( c, x, -8.5f ), At( c, x, -2.5f ) );
			Editor.Paint.DrawLine( At( c, x, 3.5f ), At( c, x, 8.5f ) );

			Stroked( color.WithAlpha( 0.28f ), 1.4f );
			Editor.Paint.DrawLine( At( c, x, -2.5f ), At( c, x, 3.5f ) );
		}

		// The stroke itself, sagging so that it passes through all three gaps rather than running
		// straight - straight would be Trim's glyph with two more lines in it.
		Stroked( CutColor, 2.1f );

		var previous = At( c, -9f, -2f );

		for ( var i = 1; i <= 12; i++ )
		{
			var t = i / 12f;
			var point = At( c, -9f + 18f * t, -2f + MathF.Sin( t * MathF.PI ) * 3.5f );

			Editor.Paint.DrawLine( previous, point );
			previous = point;
		}
	}

	/// <summary>The part of a Use glyph that is still only scenery.</summary>
	private static void Faint( Color color ) => Stroked( color.WithAlpha( 0.4f ), 1.3f );

	/// <summary>The green the sketcher paints a face's outline in - see SketchReferenceColor in
	/// EffigyViewport.Sketching.cs. Kept in step by eye rather than shared, because that one carries
	/// an alpha for drawing in the world and this one has to read on a small dark button.</summary>
	private static readonly Color ReferenceColor = new( 0.45f, 1f, 0.6f, 1f );

	/// <summary>The red a cut stroke is drawn in - see SketchCutColor in
	/// EffigyViewport.SketchTools.cs. Kept in step by eye, the same as ReferenceColor above: that
	/// one carries an alpha for drawing in the world and this one has to read on a small dark
	/// button.</summary>
	private static readonly Color CutColor = new( 1f, 0.45f, 0.35f, 1f );

	// --- lighting ------------------------------------------------------------------------------
	//
	// THE THREE RIGS ARE THE SAME SPHERE, LIT THREE WAYS. Three-point, rim and top-down differ only
	// in where the light comes from, so drawing them as three different objects would invent a
	// distinction that is not there. Each is a circle with the lit part filled and the rest left as
	// outline, which is the difference itself rather than a symbol standing in for it.

	/// <summary>The lit sphere the rig glyphs share: an outlined circle with a filled crescent on
	/// whichever side the light is on. <paramref name="from"/> is the direction the light arrives
	/// from, in icon space.</summary>
	private static void LitSphere( Vector2 c, Color color, Vector2 from, float radius = 5.2f )
	{
		var d = from.Normal;

		// The terminator is perpendicular to the light, so the lit cap runs 180 degrees centred on
		// the light's own bearing. Drawn as a filled polygon fan rather than an arc, because an
		// outline would read as a second circle rather than as brightness.
		var bearing = MathF.Atan2( d.y, d.x ) * 180f / MathF.PI;
		var points = new List<Vector2>();

		for ( var i = 0; i <= 18; i++ )
		{
			var t = (bearing - 90f) + 180f * (i / 18f);
			var radians = t * MathF.PI / 180f;
			points.Add( At( c, MathF.Cos( radians ) * radius, MathF.Sin( radians ) * radius ) );
		}

		Filled( color.WithAlpha( 0.85f ) );
		Editor.Paint.DrawPolygon( points.ToArray() );

		Stroked( color, 1.4f );
		Arc( c, radius, 0f, 360f, 28 );
	}

	/// <summary>A short ray coming in toward the sphere, so the glyph says which side the lamp is
	/// on as well as which side is bright.</summary>
	private static void LightRay( Vector2 c, Color color, Vector2 from, float outer = 8.6f, float inner = 6.4f )
	{
		var d = from.Normal;

		Stroked( color.WithAlpha( 0.9f ), 1.3f );
		Editor.Paint.DrawLine( At( c, d.x * outer, d.y * outer ), At( c, d.x * inner, d.y * inner ) );
	}

	/// <summary>Even light from every side: a circle with rays all the way round, none of them
	/// longer than the others. Full bright has no key direction and the glyph must not imply
	/// one — that is the entire difference between this and the sun below.</summary>
	private static void PaintLightFullBright( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.9f ) );
		Editor.Paint.DrawCircle( c, 4.1f * _scale );

		Stroked( color, 1.3f );

		for ( var i = 0; i < 8; i++ )
		{
			var radians = i * 45f * MathF.PI / 180f;
			var d = new Vector2( MathF.Cos( radians ), MathF.Sin( radians ) );

			Editor.Paint.DrawLine( At( c, d.x * 6f, d.y * 6f ), At( c, d.x * 8.4f, d.y * 8.4f ) );
		}
	}

	/// <summary>A bulb: glass, a screw base, and three rays. The one lamp with no direction, drawn
	/// as the object rather than as its effect.</summary>
	private static void PaintLightPoint( Vector2 c, Color color )
	{
		Stroked( color, 1.5f );
		Arc( c, 4.2f, 0f, 360f, 24 );

		// The base, under the glass.
		Editor.Paint.DrawLine( At( c, -2.2f, 5.2f ), At( c, 2.2f, 5.2f ) );
		Editor.Paint.DrawLine( At( c, -1.6f, 7f ), At( c, 1.6f, 7f ) );
		Editor.Paint.DrawLine( At( c, -2.2f, 5.2f ), At( c, -1.6f, 7f ) );
		Editor.Paint.DrawLine( At( c, 2.2f, 5.2f ), At( c, 1.6f, 7f ) );

		Stroked( color.WithAlpha( 0.8f ), 1.2f );
		Editor.Paint.DrawLine( At( c, -7.4f, -4.6f ), At( c, -5.6f, -3.4f ) );
		Editor.Paint.DrawLine( At( c, 0f, -8.2f ), At( c, 0f, -6.2f ) );
		Editor.Paint.DrawLine( At( c, 7.4f, -4.6f ), At( c, 5.6f, -3.4f ) );
	}

	/// <summary>A cone of light widening downward, with the pool it lands in. Aimed, which is the
	/// property that separates it from the bulb.</summary>
	private static void PaintLightSpot( Vector2 c, Color color )
	{
		// The housing.
		Stroked( color, 1.5f );
		Outline(
			At( c, -3f, -7.6f ),
			At( c, 3f, -7.6f ),
			At( c, 2.2f, -4.4f ),
			At( c, -2.2f, -4.4f ) );

		// The cone, dimmer than the lamp that casts it.
		Stroked( color.WithAlpha( 0.75f ), 1.3f );
		Editor.Paint.DrawLine( At( c, -2.2f, -4.4f ), At( c, -6.4f, 5.2f ) );
		Editor.Paint.DrawLine( At( c, 2.2f, -4.4f ), At( c, 6.4f, 5.2f ) );

		// The pool on the floor, flattened so it reads as ground rather than as a ball.
		EllipseArc( At( c, 0f, 5.2f ), 6.4f, 1.9f, 0f, 360f, 26 );
	}

	/// <summary>Parallel rays at an angle: a directional light has no position, only a bearing, and
	/// parallel is the one thing that says "these rays never converge".</summary>
	private static void PaintLightSun( Vector2 c, Color color )
	{
		Filled( color.WithAlpha( 0.9f ) );
		Editor.Paint.DrawCircle( At( c, -3.4f, -3.4f ), 3.2f * _scale );

		Stroked( color.WithAlpha( 0.85f ), 1.3f );

		// Three rays on the same bearing, spaced across the diagonal. Arrow heads because a sun is
		// the only lamp here whose glyph would otherwise be three plain lines.
		for ( var i = -1; i <= 1; i++ )
		{
			var offset = new Vector2( i * 4.6f, -i * 4.6f );
			var tail = At( c, 1.4f + offset.x, 1.4f + offset.y );
			var tip = At( c, 6.6f + offset.x, 6.6f + offset.y );

			Editor.Paint.DrawLine( tail, tip );
			ArrowHead( tip, new Vector2( 1f, 1f ), color.WithAlpha( 0.85f ), 2.8f );
			Stroked( color.WithAlpha( 0.85f ), 1.3f );
		}
	}

	/// <summary>Key, fill and rim: the sphere lit from the upper right, with the two weaker lamps
	/// marked as rays on the sides they come from.</summary>
	private static void PaintLightRigThreePoint( Vector2 c, Color color )
	{
		LitSphere( c, color, new Vector2( 1f, -1f ) );

		LightRay( c, color, new Vector2( 1f, -1f ) );
		LightRay( c, color.WithAlpha( 0.5f ), new Vector2( -1f, -0.2f ) );
		LightRay( c, color.WithAlpha( 0.65f ), new Vector2( -0.3f, 1f ) );
	}

	/// <summary>Lit from behind: the far edge glows and the face is dark. The one rig whose whole
	/// point is the outline rather than the surface.</summary>
	private static void PaintLightRigRim( Vector2 c, Color color )
	{
		Stroked( color.WithAlpha( 0.55f ), 1.4f );
		Arc( c, 5.2f, 0f, 360f, 28 );

		// Only the far crescent, drawn heavy. No fill at all, because a rim light leaves the body
		// of the shape unlit and filling it would be the opposite of what this means.
		Stroked( color, 2.4f );
		Arc( c, 5.2f, -155f, -25f, 20 );

		LightRay( c, color, new Vector2( 0.2f, -1f ) );
	}

	/// <summary>Lit from straight above: the lamp is drawn, and the top of the sphere is bright.</summary>
	private static void PaintLightRigTop( Vector2 c, Color color )
	{
		Stroked( color, 1.4f );
		Editor.Paint.DrawLine( At( c, -4.2f, -8f ), At( c, 4.2f, -8f ) );

		Stroked( color.WithAlpha( 0.8f ), 1.2f );
		Editor.Paint.DrawLine( At( c, -2.6f, -7.2f ), At( c, -3.6f, -5f ) );
		Editor.Paint.DrawLine( At( c, 0f, -7.2f ), At( c, 0f, -5f ) );
		Editor.Paint.DrawLine( At( c, 2.6f, -7.2f ), At( c, 3.6f, -5f ) );

		LitSphere( At( c, 0f, 1.6f ), color, new Vector2( 0f, -1f ), 4.6f );
	}

	/// <summary>One lamp and nothing else: the sphere lit hard from one side, the other side left
	/// empty. No fill ray anywhere, which is the difference from three-point.</summary>
	private static void PaintLightRigKey( Vector2 c, Color color )
	{
		LitSphere( c, color, new Vector2( 1f, -1f ) );
		LightRay( c, color, new Vector2( 1f, -1f ) );
	}

	/// <summary>A bulb with a stroke through it. Clear is the only button on the stage that takes
	/// light away, and a struck-through glyph is the one shape that reads as removal without
	/// needing a colour to say so.</summary>
	private static void PaintLightClear( Vector2 c, Color color )
	{
		var dim = color.WithAlpha( 0.55f );

		Stroked( dim, 1.5f );
		Arc( At( c, 0f, -1f ), 4.2f, 0f, 360f, 24 );

		Editor.Paint.DrawLine( At( c, -2.2f, 4.2f ), At( c, 2.2f, 4.2f ) );
		Editor.Paint.DrawLine( At( c, -1.6f, 6f ), At( c, 1.6f, 6f ) );
		Editor.Paint.DrawLine( At( c, -2.2f, 4.2f ), At( c, -1.6f, 6f ) );
		Editor.Paint.DrawLine( At( c, 2.2f, 4.2f ), At( c, 1.6f, 6f ) );

		Stroked( color, 1.9f );
		Editor.Paint.DrawLine( At( c, -7f, 7f ), At( c, 7f, -7f ) );
	}

	// --- rig ----------------------------------------------------------------------------------

	/// <summary>
	/// The dog bone the viewport already draws, flattened to two dimensions.
	///
	/// SAME SHAPE THE TOOL COMMITS, for the same reason EffigyViewport.Rig.cs previews with the
	/// real DrawDogBone rather than a placeholder dot: the button and the thing it makes should be
	/// recognisably one object. A knob at each end and a tapered shaft between them is the whole
	/// silhouette - it is Blender's octahedral bone seen side-on, which is the form anyone arriving
	/// from a rigging tool already reads as "bone" without being told.
	/// </summary>
	private static void PaintBone( Vector2 c, Color color )
	{
		// Head at the top, tail at the bottom - bones hang downward in every tree view this
		// editor draws, so a vertical glyph matches the direction the rig panel lists them in.
		var head = At( c, 0f, -7f );
		var tail = At( c, 0f, 7f );

		// The shaft, widest a third of the way down from the head. That is where DogBone puts its
		// cross-section, and a diamond with its waist at the midpoint reads as a kite instead.
		Filled( color.WithAlpha( 0.28f ) );
		Editor.Paint.DrawPolygon( head, At( c, -3.4f, -2.4f ), tail, At( c, 3.4f, -2.4f ) );

		Stroked( color );
		Outline( head, At( c, -3.4f, -2.4f ), tail, At( c, 3.4f, -2.4f ) );

		// The joints. Solid, because they are the part you click and drag.
		Filled( color );
		Editor.Paint.DrawCircle( head, 2.6f * _scale );
		Editor.Paint.DrawCircle( tail, 2f * _scale );
	}

	/// <summary>
	/// A bone with a body pinned to it - the bone at the left, a solid at the right, a tie between.
	///
	/// The bone half is deliberately the SAME silhouette as PaintBone rather than a fresh drawing,
	/// only smaller and turned: Add Bone and Assign Body sit on adjacent stages of the rig bar, and
	/// two unrelated glyphs would hide that they are about the same object.
	/// </summary>
	private static void PaintBoneBind( Vector2 c, Color color )
	{
		var head = At( c, -8f, 3f );
		var tail = At( c, -1.5f, -3f );

		Stroked( color );
		Outline( head, At( c, -6.6f, -1.6f ), tail, At( c, -2.9f, 1.6f ) );

		Filled( color );
		Editor.Paint.DrawCircle( head, 2f * _scale );
		Editor.Paint.DrawCircle( tail, 1.5f * _scale );

		// The body it is being pinned to: outlined, the same way PaintMirror outlines the copy, so
		// the bone stays the solid thing and the body stays the thing being attached to it.
		Stroked( color, 1.4f );
		Editor.Paint.DrawRect( Box( c, 2f, -1f, 7f, 7f ), 1f );

		// The tie. Dashed, because the assignment is a named link rather than geometry.
		Stroked( color.WithAlpha( 0.6f ), 1.2f );
		for ( var x = -1.2f; x < 2f; x += 1.6f )
			Editor.Paint.DrawLine( At( c, x, -1.6f ), At( c, x + 0.9f, -1.6f ) );
	}

	/// <summary>
	/// A bone that has swung off its pose, with the pose it left behind still showing.
	///
	/// THE GHOST IS THE WHOLE ICON. A bone drawn bent means nothing on its own - bones are bent.
	/// What says "soft" is the pair: where the animation put it, dashed, and where the spring
	/// actually took it, solid, with the gap between them being exactly the lag the four numbers in
	/// the inspector control. Same trick PaintMirror uses, where the outlined copy is only
	/// meaningful next to the solid original.
	/// </summary>
	private static void PaintBoneSoft( Vector2 c, Color color )
	{
		var head = At( c, -2f, -7f );

		// The pose: where the bone would be if it were rigid. Dashed and faint, because it is the
		// thing that is NOT happening.
		Stroked( color.WithAlpha( 0.4f ), 1.2f );
		for ( var t = 0f; t < 1f; t += 0.28f )
		{
			var a = At( c, -2f, -7f + 14f * t );
			var b = At( c, -2f, -7f + 14f * (t + 0.16f) );
			Editor.Paint.DrawLine( a, b );
		}

		// The bone as the spring left it: swung out and trailing behind. Same knob-shaft-knob
		// silhouette as PaintBone so the two read as the same object.
		var tail = At( c, 6f, 5.5f );

		Stroked( color );
		Outline( head, At( c, -1f, -1f ), tail, At( c, 3.6f, -2.6f ) );

		Filled( color );
		Editor.Paint.DrawCircle( head, 2.4f * _scale );
		Editor.Paint.DrawCircle( tail, 1.9f * _scale );
	}

	/// <summary>A play triangle with the swing drawn behind it — the ordinary "run this" shape,
	/// made specific to what it runs so it is not mistaken for a generic play button on a bar that
	/// has no other one.</summary>
	private static void PaintSoftPreview( Vector2 c, Color color )
	{
		// Motion trails, fading backwards, the way the bone actually arrives at where it is going.
		Stroked( color.WithAlpha( 0.30f ), 1.3f );
		Arc( At( c, -9f, 0f ), 7.5f, -52f, 52f, 10 );

		Stroked( color.WithAlpha( 0.55f ), 1.3f );
		Arc( At( c, -9f, 0f ), 11f, -40f, 40f, 10 );

		Filled( color );
		Editor.Paint.DrawPolygon( At( c, 1.5f, -6.5f ), At( c, 8.5f, 0f ), At( c, 1.5f, 6.5f ) );
	}

	/// <summary>
	/// A bone dropped back onto its line, with the swing it gave up drawn as a fading arc.
	///
	/// NOT A REFRESH ARROW, which is the obvious choice and the wrong one: a circular arrow means
	/// "do it again" everywhere else in an editor, and this does the opposite - it stops the thing
	/// that is happening and puts it back where it started.
	/// </summary>
	private static void PaintSoftRest( Vector2 c, Color color )
	{
		// What is being given up: the arc the tail was swinging through, fading out as it goes.
		Stroked( color.WithAlpha( 0.28f ), 1.2f );
		Arc( At( c, 0f, -7f ), 13f, 62f, 90f, 8 );

		// The bone, back on its pose - straight down, the direction PaintBone draws it.
		var head = At( c, 0f, -7f );
		var tail = At( c, 0f, 6f );

		Stroked( color );
		Outline( head, At( c, -3f, -2.2f ), tail, At( c, 3f, -2.2f ) );

		Filled( color );
		Editor.Paint.DrawCircle( head, 2.4f * _scale );
		Editor.Paint.DrawCircle( tail, 1.8f * _scale );

		// The line it came to rest on, so "rest" is a place rather than only a state.
		Stroked( color.WithAlpha( 0.5f ), 1.4f );
		Editor.Paint.DrawLine( At( c, -6f, 8.6f ), At( c, 6f, 8.6f ) );
	}
}
