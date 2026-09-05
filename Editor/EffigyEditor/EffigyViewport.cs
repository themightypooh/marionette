using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The 3D viewport for Effigy — a live view of the PartStudio's output, with Onshape-style
/// reference planes (Top/Front/Right intersecting at the origin), a selectable origin point,
/// and a fly camera.
///
/// PLANES ARE DRAWN AS WIREFRAME RECTANGLES using Gizmo.Draw.Line, oriented to s&amp;box's
/// coordinate system (+x forward, +y left, +z up):
///
///   Top   = XY plane at z=0,  horizontal, normal +Z
///   Front = XZ plane at y=0,  vertical facing camera, normal +Y
///   Right = YZ plane at x=0,  vertical to the right, normal +X
///
/// The origin point can be selected by clicking it, then moved with a position gizmo (three
/// colored arrows for X/Y/Z), matching Onshape's interactable origin. Double-click to reset.
/// </summary>
internal sealed partial class EffigyViewport : Widget
{
	private readonly SceneRenderingWidget _canvas;
	private readonly CameraComponent _camera;
	private readonly Gizmo.Instance _gizmoInstance;

	private GameObject _modelObject;
	private ModelRenderer _renderer;

	/// <summary>Half-width a reference plane starts at, in world units. Each plane can be dragged
	/// to its own size from there — see <see cref="_planeHalfSize"/>.</summary>
	private const float PlaneSize = 128f;

	/// <summary>
	/// Half-width of each reference plane — Top, Front, Right — in world units.
	///
	/// PER PLANE rather than the single shared constant this used to be. A plane is a drawing
	/// surface, and the one you are about to sketch on wants to be big enough to work on while the
	/// other two want to be out of the way. One number could not do both.
	/// </summary>
	private readonly float[] _planeHalfSize = { PlaneSize, PlaneSize, PlaneSize };

	/// <summary>How small a plane may be dragged. Below this the corner handles land on top of
	/// each other and there is no way to grab one to make it big again.</summary>
	private const float MinPlaneHalfSize = 8f;

	/// <summary>
	/// Radius of the origin handle dot, in SCREEN PIXELS.
	///
	/// It was four WORLD units, which is a different object at every scale: a boulder sitting in
	/// the middle of a thirty-unit part, and invisible on a thousand-unit one. Onshape's origin is
	/// a few pixels across at any zoom and that is what makes it a marker rather than geometry.
	/// Same reasoning, and the same conversion, as the sketch snapping tolerances.
	/// </summary>
	private const float OriginHandlePixels = 2.5f;

	/// <summary>The dot's radius in world units at its current distance from the camera.</summary>
	private float OriginHandleRadius() => WorldRadiusAt( OriginPosition, OriginHandlePixels );

	/// <summary>What a screen-pixel radius is worth in world units at some point in the scene. The
	/// origin dot wanted this first; the plane corner handles want it at twelve more places, each
	/// at its own distance from the camera.</summary>
	private float WorldRadiusAt( Vector3 point, float pixels )
	{
		var distance = MathF.Max( (point - _camera.WorldPosition).Length, 0.01f );
		var halfHeight = MathF.Tan( _camera.FieldOfView.DegreeToRadian() * 0.5f ) * distance;

		return halfHeight / MathF.Max( _canvas.Size.y * 0.5f, 1f ) * pixels;
	}

	// --- origin state -----------------------------------------------------------------------

	/// <summary>The origin's current world position. Reference planes and axis lines are drawn
	/// relative to this, so dragging it shifts the whole coordinate frame.</summary>
	public Vector3 OriginPosition { get; private set; } = Vector3.Zero;

	/// <summary>True while the user is dragging the origin gizmo.</summary>
	private bool _draggingOrigin;

	/// <summary>Position the origin was at when a drag started, so the accumulated delta can be
	/// added to the correct base — same pattern as RigViewport's _propDragStart.</summary>
	private Vector3 _originDragStart;

	/// <summary>Accumulated movement since drag began — the Position gizmo at Vector3.Zero
	/// returns a per-frame displacement, so total drag is the sum.</summary>
	private Vector3 _originDragDelta;

	/// <summary>Whether the origin is selected (showing the position gizmo).</summary>
	public bool OriginSelected { get; private set; }

	/// <summary>Raised when the origin is moved, so the parameter panel can update.</summary>
	public Action OriginMoved { get; set; }

	/// <summary>Raised when the loaded model changes, so the status bar can update.</summary>
	public Action<string> ModelInfoChanged { get; set; }

	/// <summary>Raised when the origin selection state changes.</summary>
	public Action<bool> OriginSelectionChanged { get; set; }

	/// <summary>Current model stats for the status bar.</summary>
	public string ModelInfo { get; private set; } = "";

	// --- bone selection / drag state --------------------------------------------------------

	/// <summary>Index of the currently selected bone in the rig skeleton, or -1 for none.</summary>
	private int _selectedBoneIndex = -1;

	/// <summary>What dragging the selected bone does. Rotate is the default because skeletal
	/// animation rotates joints. Move translates, Scale adjusts bone length.</summary>
	public enum BoneDragMode { Rotate, Move, Scale }

	/// <summary>Current drag mode — E flips to the other mode while held.</summary>
	private BoneDragMode _boneDragMode = BoneDragMode.Rotate;

	/// <summary>True while a drag is in progress (mouse down and control reporting).</summary>
	private bool _boneDragging;

	/// <summary>The bone's world pose when the drag started — position, rotation, and length
	/// are all captured here so live values are never fed back into themselves.</summary>
	private Vector3 _dragStartPos;
	private Rotation _dragStartRot;
	private float _dragStartLength;

	/// <summary>Accumulated position delta since drag began (for Move mode).</summary>
	private Vector3 _moveDelta;

	/// <summary>Raised when the selected bone changes from viewport interaction, so the rig
	/// panel can sync its tree selection. The int is the bone index, or -1 for deselected.</summary>
	public Action<int> BoneSelectionChanged { get; set; }

	/// <summary>The cubemap the reflection probe lights off. s&amp;box's own default scene uses this
	/// one, which is the point — the reflections in the viewport are the reflections in game.</summary>
	private const string SkyCubemap = "textures/cubemaps/default2.vtex";

	private Color _backgroundColor = new( 0.82f, 0.84f, 0.86f, 1f );

	/// <summary>
	/// Viewport background, driven by the active palette.
	///
	/// This was an auto-property, and the camera read it exactly once - in this constructor,
	/// before any palette had been applied. So every palette in the View menu changed this field
	/// and nothing else, and all four themes rendered identically. The setter is the whole fix.
	/// </summary>
	public Color BackgroundColor
	{
		get => _backgroundColor;
		set
		{
			_backgroundColor = value;

			if ( _camera.IsValid() )
				_camera.BackgroundColor = value;
		}
	}

	/// <summary>
	/// Chrome colour drawn over the viewport, driven by the active palette so it stays legible
	/// against whatever the background happens to be.
	///
	/// This used to be the reference planes' grid colour, which is where the name comes from. The
	/// planes are outlines only now and their outlines keep their per-axis hues — Top orange, Front
	/// blue, Right green — because that is how you tell them apart. What is left on this is the
	/// faded interior grid.
	/// </summary>
	public Color PlaneColor { get; set; } = new( 0.55f, 0.58f, 0.61f, 1f );
	public bool OriginVisible { get; set; } = true;
	public bool TopPlaneVisible { get; set; } = true;
	public bool FrontPlaneVisible { get; set; } = true;
	public bool RightPlaneVisible { get; set; } = true;
	public Effigy.Skeleton RigSkeleton { get; set; }

	/// <summary>
	/// The Rig workspace is open, so BONES ARE THE ONLY THING IN HERE THAT CAN BE CLICKED.
	///
	/// WHY THIS HAS TO EXIST. A bone's hit target is a handful of spheres strung along a shape a
	/// few units thick. The part it sits on is a wall of triangles filling the screen behind it,
	/// and idle selection picks a face from wherever the cursor is, so the face wins essentially
	/// everywhere the bone is not. Worse, it wins INVISIBLY: the face lights up, the bone does not,
	/// and the click lands on the face - so the rig looks unresponsive rather than obstructed. The
	/// origin handle, the lamps and the face-drag arrow are the same problem in miniature.
	///
	/// Turning the competition off is the only fix that holds. Making the bone hitboxes bigger
	/// helps and is done too (see DrawBoneHandle), but it is a race that cannot be won by degrees:
	/// any bone inside the silhouette of the part still has a face behind every pixel of it.
	///
	/// NOT A NEW KIND OF STATE. It is the workspace, pushed down to the one place that has to act
	/// on it - see EffigyWindow.Workspaces.cs, which sets it from BarMode and nowhere else.
	/// </summary>
	public bool RigMode { get; set; }

	public EffigyViewport( Widget parent ) : base( parent )
	{
		MinimumSize = 200;
		Layout = Layout.Column();

		_canvas = new SceneRenderingWidget( this );
		_canvas.OnPreFrame += OnPreFrame;
		_canvas.FocusMode = FocusMode.Click;
		_canvas.Scene = Scene.CreateEditorScene();

		using ( _canvas.Scene.Push() )
		{
			_camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
			_camera.BackgroundColor = BackgroundColor;
			_camera.ZNear = 0.5f;
			_camera.ZFar = 8192;
			_camera.FieldOfView = 45f;
			// Post processing is what runs the tonemapper below. Explicit rather than relying on
			// the default, because everything under it is chosen to match a runtime scene and
			// silently losing the tonemapper puts the washed-out look straight back.
			_camera.EnablePostProcessing = true;
			_camera.Enabled = true;

			BuildRuntimeLighting();

			_canvas.Camera = _camera;
		}

		_gizmoInstance = _canvas.GizmoInstance;

		// Materials dragged out of the browser land here - see EffigyViewport.MaterialDrop.cs.
		EnableMaterialDrops();

		// The canvas is NOT added to the layout here - the tool strip has to go above it and does
		// not exist yet. BuildToolbar calls CompleteLayout to fill this widget's existing column
		// layout in the right order.

		FrameCamera();
	}

	/// <summary>
	/// Build the studio lighting rig — one sun, a dim ambient, a cubemap, a pinned tonemapper —
	/// then apply whichever mode the viewport is in.
	///
	/// STUDIO is how a material looks in game: the values are the ones s&amp;box's own default
	/// scene ships with. FULL BRIGHT is how you model: even light from every side, so a face you
	/// are about to sketch on does not disappear into the unlit side of the sun. Full bright is
	/// the default; studio is the setting. See <see cref="ApplyLighting"/>.
	///
	/// The rig this first replaced was a light box (full-strength sun, a 0.6 fill, a 0.6 ambient)
	/// with no tonemapper, which is why a 0.5 grey rendered at about 0.88 and every pastel arrived
	/// as near-white. Studio keeps the tonemapper and drops the fill. Full bright drops the sun.
	/// </summary>
	private void BuildRuntimeLighting()
	{
		// Key light. Kept at the viewport's own 45/45 rather than the template's angle: this one is
		// aimed to read a part sitting on the origin from the default camera, and the colour is
		// what makes the difference to a material, not the direction.
		_sun = new GameObject( true, "sun" ).GetOrAddComponent<DirectionalLight>( false );
		_sun.WorldRotation = Rotation.From( 45, 45, 0 );
		_sun.LightColor = new Color( 0.914f, 0.980f, 1f, 1f );
		// The legacy sky term, off. Ambient comes from the AmbientLight below — s&amp;box's own
		// tooltip on this property says to do it that way, and doubling the two is how the old rig
		// ended up over-lit.
		_sun.SkyColor = Color.Black;
		_sun.Enabled = true;

		_ambient = new GameObject( true, "ambient" ).GetOrAddComponent<AmbientLight>( false );
		_ambient.Color = new Color( 0.237f, 0.237f, 0.237f, 1f );
		_ambient.Enabled = true;

		// The sky, for REFLECTIONS ONLY — deliberately a probe and not a SkyBox2D. A 2D sky takes
		// over the camera's background (its own docs say the background colour applies only when
		// there is no 2D sky in the scene), and the background here belongs to the View menu's
		// palette. A probe pointed at the same cubemap lights off it without drawing it.
		var sky = new GameObject( true, "sky" ).GetOrAddComponent<EnvmapProbe>( false );
		sky.Texture = Texture.Load( SkyCubemap );
		// Bounds are the probe's reach, and they have to cover wherever the camera can fly, which is
		// the far plane. Everything else about the probe is left at its shipped default on purpose.
		sky.Bounds = new BBox( new Vector3( -8192f ), new Vector3( 8192f ) );
		sky.Enabled = true;

		// Tonemapping, WITH AUTO EXPOSURE OFF. The tonemapper is the half that has to match: runtime
		// rolls its highlights off and the viewport used to clip them, which is why a pastel came
		// out white. Auto exposure is the half deliberately not copied — it re-exposes the shot as
		// geometry appears and disappears, so the same material would render a different colour
		// depending on what else happened to be on screen. That is the exact complaint this rig
		// exists to answer, so exposure is pinned.
		var tonemap = _camera.GameObject.GetOrAddComponent<Tonemapping>( false );
		tonemap.AutoExposureEnabled = false;
		tonemap.MinimumExposure = 1f;
		tonemap.MaximumExposure = 1f;
		tonemap.ExposureCompensation = 0f;
		tonemap.Enabled = true;

		// Honour the default (full bright) on the first frame, before Settings has a chance to
		// restore a saved choice. RestoreSettings overwrites this a moment later if it has to.
		ApplyLighting();
	}

	// --- layout helpers ---------------------------------------------------------------------

	/// <summary>The 3D canvas, exposed so the window can parent floating overlays (the ADD/REMOVE
	/// strip, the sculpt number bar) onto it rather than into the layout.</summary>
	public Widget Canvas => _canvas;

	/// <summary>
	/// Stack <paramref name="toolBar"/> above the canvas and give the canvas everything left, then
	/// float <paramref name="resultOverlay"/> on top of it at the top-left. Called once from
	/// BuildToolbar, after the bar is built.
	///
	/// THE TOOL BAR IS A LAYOUT ROW ABOVE THE CANVAS, and it took an argument to get there. The
	/// note that used to sit here said a row "takes a band off the top of the viewport and paints
	/// window chrome across it" and that parenting to the canvas instead let the 3D scene fill the
	/// widget with the buttons sitting on it. The first half was true and the second half was not:
	/// a widget that declines to paint keeps whatever was in the frame buffer, so the floating
	/// strip had to fill its own rect with the viewport's clear colour and was an opaque band over
	/// the model the whole time — a band that also sat exactly where a part's top-left corner is.
	/// It cost the same pixels and covered the geometry as well. See EffigyStageBar.
	///
	/// The overlays that ARE still parented to the canvas are the ones that belong to the model
	/// rather than to the toolset: the ADD/REMOVE strip and the sculpt number bar.
	///
	/// Note this fills the layout the constructor already made rather than assigning a fresh one.
	/// It runs after DockManager.SetCentralWidget has sized the viewport, and replacing the layout
	/// at that point orphans the canvas: it keeps whatever tiny geometry it had and renders the
	/// whole 3D scene into a sliver, leaving the rest of the viewport black.
	/// </summary>
	public void CompleteLayout( Widget workspaceBar, Widget toolBar, Widget resultOverlay = null )
	{
		// Outermost first, then the bar, then the canvas taking everything left. Two rows of docked
		// chrome, in the order the questions get asked: which part of the pipeline (workspace),
		// then which handful of tools (stage), then the tools. One bar per question, so there is no
		// longer any question of two pieces of tool chrome being visible at once — that used to be
		// enforced by three Visible flags nobody could see the state of.
		if ( workspaceBar is not null )
			Layout.Add( workspaceBar );

		if ( toolBar is not null )
			Layout.Add( toolBar );

		Layout.Add( _canvas, 1 );

		// Still floating, and still parented to the canvas: this one is about the feature being
		// EDITED rather than about which tool is armed, so it belongs next to the model.
		if ( resultOverlay is not null )
		{
			_resultOverlay = resultOverlay;
			resultOverlay.Position = OverlayMargin;
		}
	}

	/// <summary>Inset of a floating overlay from the canvas's top-left corner.</summary>
	private static readonly Vector2 OverlayMargin = new( 10f, 10f );

	/// <summary>
	/// The grid switch that lives at the end of the tool row, so the frame loop can show it only
	/// while a sketch is open.
	///
	/// The bar owns its own position - it is a child of the tool row, not a floating overlay - so
	/// the only thing kept here is when it is on screen. That has to be driven from the frame loop
	/// rather than from EnterSketch and FinishSketch, because a sketch also ends by being deleted,
	/// undone, or rolled past, and every one of those would need its own line.
	/// </summary>
	public Widget SketchGridBar { get; set; }

	/// <summary>The floating overlays, so the frame loop can keep camera drags out of them. The
	/// tool bar is not among them any more — it is a layout row outside the canvas, so the canvas
	/// never reports the cursor as being over it in the first place.</summary>
	private Widget _resultOverlay;

	// --- model management -------------------------------------------------------------------

	/// <summary>
	/// Load a compiled .vmdl model into the viewport. Null clears the viewport.
	///
	/// Uses Model.Load on an asset path, same pattern as RigControlWindow's LoadAsset and
	/// EffigyTool's own export path. The ModelRenderer (not SkinnedModelRenderer) is correct
	/// here because Effigy produces static meshes — no bones, no animation.
	/// </summary>
	/// <param name="model">The model to show, or null to clear the viewport.</param>
	/// <param name="frameCamera">Reframe to fit the new model. Off for a live rebuild: the
	/// preview is regenerated on every slider tick, and snapping the camera back mid-drag makes
	/// the part impossible to look at while you adjust it.</param>
	public void SetModel( Model model, bool frameCamera = true )
	{
		using var scope = _canvas.Scene.Push();

		_modelObject?.Destroy();
		_modelObject = null;
		_renderer = null;

		if ( model is null )
		{
			ModelInfo = "";
			ModelInfoChanged?.Invoke( ModelInfo );
			return;
		}

		_modelObject = new GameObject( true, "effigy_model" );
		_renderer = _modelObject.GetOrAddComponent<ModelRenderer>( false );
		_renderer.Model = model;
		_renderer.Enabled = true;

		var meshCount = model.MeshCount;
		var bounds = model.Bounds;
		var size = bounds.Size;
		// Say "units" outright and keep the fractions. This is the only place the part's real
		// size is stated, so it is what settles an argument with whatever the surface happens
		// to look like it is.
		ModelInfo = $"{meshCount} mesh{(meshCount != 1 ? "es" : "")} · "
			+ $"{size.x:0.##} × {size.y:0.##} × {size.z:0.##} units";
		ModelInfoChanged?.Invoke( ModelInfo );

		if ( frameCamera )
			FrameCamera();
	}

	/// <summary>
	/// Frame whatever is on screen from an isometric-ish front-right-top angle, like a fresh
	/// Onshape document.
	///
	/// It has to FIT the model rather than sit at a fixed distance. Effigy's units are
	/// dimensionless — a default Box is one unit on a side, next to reference planes 128 units
	/// wide — so a fixed 320-unit pullback renders a freshly added primitive as a speck, which
	/// reads as the button having done nothing.
	/// </summary>
	public void FrameCamera()
	{
		var dir = new Vector3( 1f, -1f, 0.65f ).Normal;
		var center = Vector3.Zero;

		// No model: frame the reference planes, which is all there is to look at.
		var radius = PlaneSize * 1.25f;

		if ( _renderer.IsValid() && _renderer.Model is { } model )
		{
			var bounds = model.Bounds;
			center = bounds.Center;

			// Half the diagonal, so the part fits from any angle. Floored because a zero-size
			// body (a degenerate feature) would otherwise put the camera inside it.
			radius = MathF.Max( bounds.Size.Length * 0.5f, 1f );
		}

		// Fit the bounding sphere in the vertical FOV, with a margin so it is not edge to edge.
		var distance = radius / MathF.Tan( _camera.FieldOfView.DegreeToRadian() * 0.5f ) * 1.4f;

		_camera.WorldPosition = center + dir * distance;
		_camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );

		// A one-unit part needs to be able to get closer than the 0.5 near plane the planes want.
		_camera.ZNear = Math.Clamp( distance * 0.01f, 0.01f, 8f );
	}

	// --- size reference ---------------------------------------------------------------------

	/// <summary>
	/// The stand-in body: the citizen from the base addon, so it is there for everyone rather than
	/// being something of mine, and it is the same figure anything built here ends up standing next
	/// to in a scene.
	/// </summary>
	public const string SizeReferenceModelPath = "models/citizen/citizen.vmdl";

	private GameObject _referenceObject;
	private bool _showSizeReference;

	/// <summary>
	/// Whether the citizen stands at the origin as a ruler.
	///
	/// EFFIGY'S UNITS DO NOT SAY HOW BIG ANYTHING IS. A default Box is one unit on a side and the
	/// reference planes are 128 across, so a part on its own fills the view at every scale and looks
	/// the same doing it — the status bar's numbers are the only answer to "how big is this", and a
	/// number is not a size. A body of known height beside it is one: a door is a head taller than
	/// the citizen, a mug is lost at its feet, and you can see which you have made.
	/// </summary>
	public bool ShowSizeReference
	{
		get => _showSizeReference;
		set
		{
			if ( _showSizeReference == value )
				return;

			_showSizeReference = value;
			UpdateSizeReference();
		}
	}

	/// <summary>Height of the loaded reference in world units, or zero when none is loaded. Read by
	/// the settings window, which prints it under the switch — the figure is only a ruler if it says
	/// what it measures.</summary>
	public float SizeReferenceHeight { get; private set; }

	/// <summary>
	/// Build or tear down the stand-in.
	///
	/// Destroyed rather than hidden when it is off. A hidden character still sits in a scene that
	/// ticks every frame, and rebuilding it is one Model.Load off the asset cache the next time the
	/// switch goes on.
	/// </summary>
	private void UpdateSizeReference()
	{
		using var scope = _canvas.Scene.Push();

		if ( !_showSizeReference )
		{
			_referenceObject?.Destroy();
			_referenceObject = null;
			SizeReferenceHeight = 0f;
			return;
		}

		if ( _referenceObject.IsValid() )
			return;

		var model = Model.Load( SizeReferenceModelPath );

		if ( model is null || model.IsError )
		{
			// The citizen is a base addon, so this is close to impossible — but an unmounted one
			// would otherwise leave the switch sitting on with nothing on screen and no reason
			// given for it.
			Log.Warning( $"Effigy: size reference model '{SizeReferenceModelPath}' could not be loaded." );

			_showSizeReference = false;
			SizeReferenceHeight = 0f;
			return;
		}

		_referenceObject = new GameObject( true, "effigy_size_reference" );

		// SKINNED, unlike the part. SetModel is right to use a plain ModelRenderer — Effigy makes
		// static meshes — but citizen.vmdl is a rigged character, and the renderer that poses bones
		// is the one that draws a rigged character standing up. Same component RigViewport uses on
		// the same model.
		var renderer = _referenceObject.GetOrAddComponent<SkinnedModelRenderer>( false );

		renderer.Model = model;
		renderer.Enabled = true;

		SizeReferenceHeight = model.Bounds.Size.z;

		PlaceSizeReference();
	}

	/// <summary>
	/// Stand the figure on the origin, feet on the Top plane.
	///
	/// Every frame rather than once at load, because the origin handle is draggable and the planes
	/// are drawn relative to it. A reference that stayed at the world origin while the coordinate
	/// frame moved out from under it would be measuring against nothing.
	/// </summary>
	private void PlaceSizeReference()
	{
		if ( !_referenceObject.IsValid() )
			return;

		_referenceObject.WorldPosition = OriginPosition;
	}

	// --- origin interaction -----------------------------------------------------------------

	/// <summary>Reset the origin back to (0,0,0). Called from double-click or parameter panel.</summary>
	public void ResetOrigin()
	{
		OriginPosition = Vector3.Zero;
		OriginMoved?.Invoke();
	}

	/// <summary>Set origin programmatically (from parameter panel number fields).</summary>
	public void SetOrigin( Vector3 position )
	{
		OriginPosition = position;
		OriginMoved?.Invoke();
	}

	/// <summary>
	/// Draw the origin handle: a colored dot at the origin with a hitbox for selection, and a
	/// position gizmo (three axis arrows) when selected.
	///
	/// Clicking the dot selects the origin, showing the gizmo. Dragging an arrow moves the origin
	/// along that axis. The reference planes follow. Click empty space or press Escape to deselect.
	/// </summary>
	private void DrawOrigin()
	{
		if ( !OriginVisible )
			return;

		var radius = OriginHandleRadius();

		using var scope = Gizmo.Scope( "origin", new Transform( OriginPosition ) );

		// --- when selected: position gizmo first, so its handles take priority over the dot ---
		if ( OriginSelected )
		{
			// Position gizmo: three colored arrows for X/Y/Z, world-aligned.
			// Same pattern as RigViewport's DragReferenceProp — gizmo at Vector3.Zero,
			// accumulate the per-frame displacement, add to the drag-start base position.
			using var ctrlScope = Gizmo.Scope( "origin-control", new Transform( Vector3.Zero ) );

			Gizmo.Hitbox.DepthBias = 0.01f;

			if ( Gizmo.Control.Position( "origin-move", Vector3.Zero, out var displacement, Rotation.Identity ) )
			{
				if ( !_draggingOrigin )
				{
					_draggingOrigin = true;
					_originDragStart = OriginPosition;
					_originDragDelta = Vector3.Zero;
				}

				_originDragDelta += displacement;
				OriginPosition = _originDragStart + _originDragDelta;
				OriginMoved?.Invoke();
			}
			else if ( _draggingOrigin )
			{
				// Drag ended — the position is already final from the last frame's update
				_draggingOrigin = false;
			}

			// Draw the dot larger and brighter when selected
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 1f ); // bright yellow
			Gizmo.Draw.SolidSphere( 0f, radius * 1.4f, 12, 12 );
			Gizmo.Draw.IgnoreDepth = false;

			return;
		}

		// --- not selected: draw the dot and check for click ---

		// Draw origin dot — Onshape-style small circle
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.85f ); // warm yellow
		Gizmo.Draw.SolidSphere( 0f, radius, 10, 10 );
		Gizmo.Draw.IgnoreDepth = false;

		// Hitbox for selection — slightly larger than the visual dot for easier clicking
		Gizmo.Hitbox.DepthBias = 0.01f;
		Gizmo.Hitbox.Sphere( new Sphere( Vector3.Zero, radius * 2.8f ) );

		if ( Gizmo.IsHovered )
		{
			// Highlight on hover
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.35f );
			Gizmo.Draw.SolidSphere( 0f, radius * 2.8f, 10, 10 );
			Gizmo.Draw.IgnoreDepth = false;

			if ( Gizmo.WasLeftMousePressed )
			{
				OriginSelected = true;
				OriginSelectionChanged?.Invoke( true );
				DeselectLight();
			}
		}
	}

	/// <summary>Deselect the origin — called from the window when clicking empty space or pressing
	/// Escape.</summary>
	public void DeselectOrigin()
	{
		if ( !OriginSelected )
			return;

		OriginSelected = false;
		OriginSelectionChanged?.Invoke( false );
	}

	// --- reference planes -------------------------------------------------------------------

	/// <summary>
	/// Draws the three Onshape-style reference planes as wireframe rectangles intersecting at
	/// the origin. Each plane gets its own faint color so you can tell them apart at a glance,
	/// matching Onshape's convention:
	///
	///   Top   (XY) — orange tint
	///   Front (XZ) — blue tint
	///   Right (YZ) — green tint
	///
	/// All three are drawn as outlined rectangles with edge subdivisions, like Onshape's
	/// default plane visualization — faint enough not to compete with the model.
	///
	/// Planes follow OriginPosition — they are drawn relative to it, not at the world origin,
	/// so dragging the origin shifts the entire coordinate frame.
	/// </summary>
	private void DrawReferencePlanes()
	{
		// Before anything is drawn, so a plane being dragged this frame is drawn at the size the
		// cursor is asking for rather than one frame behind it.
		UpdatePlaneResize();

		var center = OriginPosition;
		var s = PlaneSize;

		// DEPTH-TESTED. The reference planes are 128 units across and were drawn straight through
		// whatever the part is, so a finished solid had a grid laid over it and read as a glass
		// box rather than as material. A plane behind the part now goes behind the part.
		Gizmo.Draw.IgnoreDepth = false;

		// OUTLINES BY DEFAULT, GRID ON REQUEST. Each plane used to be filled with an 8x8 lattice
		// unconditionally, and three of those overlapping at the origin was most of what you saw on
		// opening the editor: the part sat inside a wire cage. The outline alone says where a plane
		// is and how big it is, which is all it has to say most of the time — but the lattice is a
		// ruler when you want one, so it is a setting rather than a deletion. Edit > Settings.
		var grid = PlaneColor.WithAlpha( PlaneColor.a * 0.5f );

		for ( var index = 0; index < 3; index++ )
		{
			if ( !PlaneVisible( index ) )
				continue;

			var (right, up, colour) = PlaneAxes( index );
			var half = _planeHalfSize[index];

			DrawPlaneOutline( center, right, up, half, colour );

			if ( !ShowPlaneGrid )
				continue;

			// A plane seen edge-on is a line, and its grid collapses into that line as a bright
			// smear across everything behind it. Three planes meet at right angles, so from any
			// camera angle at least one of them is close to edge-on and it was always the one
			// making the middle of the view unreadable. Fading it out by how square-on it is means
			// you only ever see the grids you are actually looking at.
			var facing = MathF.Abs( Vector3.Dot( Vector3.Cross( right, up ), _camera.WorldRotation.Forward ) );
			var viewFade = MathF.Min( facing / EdgeOnFade, 1f );

			if ( viewFade <= 0.01f )
				continue;

			DrawPlaneGrid( center, right, up, half, DrawnGridStep( center, half ),
				grid.WithAlpha( grid.a * viewFade ) );
		}

		// An offset sketch lives on a parallel plane, not on the origin reference plane. Keep the
		// normal reference planes visible, but draw the active sketch plane where the sketch math
		// actually places its geometry so the user never has to infer why it appears to float.
		if ( IsSketching && ActiveSketch?.Plane is { } sketchPlane )
		{
			// The one plane that still draws through everything: you are working on it, and a
			// sketch plane you cannot see because a body is in front of it is not usable.
			Gizmo.Draw.IgnoreDepth = true;

			var sketchCenter = center + new Vector3( sketchPlane.Origin.x, sketchPlane.Origin.y, sketchPlane.Origin.z );
			var sketchX = new Vector3( sketchPlane.XAxis.x, sketchPlane.XAxis.y, sketchPlane.XAxis.z );
			var sketchY = new Vector3( sketchPlane.YAxis.x, sketchPlane.YAxis.y, sketchPlane.YAxis.z );
			var sketchColor = new Color( 0.95f, 0.82f, 0.25f, 0.65f );

			// A SKETCH ON A FACE FILLS THAT FACE, not a rectangle around it. The plane is
			// infinite; the square was only ever a stand-in for it, and on a face it was the
			// wrong square - a fixed 128 units across, so it hung out past a small face and was
			// swallowed by a large one. Either way the ruled paper stopped somewhere that means
			// nothing, and the eye reads that edge as a boundary of the work. The face already
			// has a boundary, drawn in green a few lines from here, so the grid is clipped to
			// it and the two say one thing.
			if ( !DrawSketchFaceGrid( sketchColor ) )
			{
				DrawPlaneOutline( sketchCenter, sketchX, sketchY, s, sketchColor );

				if ( ShowPlaneGrid )
				{
					DrawPlaneGrid( sketchCenter, sketchX, sketchY, s, DrawnGridStep( sketchCenter, s ),
						sketchColor.WithAlpha( 0.3f ) );
				}
			}

			Gizmo.Draw.IgnoreDepth = false;
		}

		DrawPlaneCornerHandles();

		if ( !OriginVisible )
		{
			Gizmo.Draw.IgnoreDepth = false;
			DrawPlaneHitboxes();
			DrawHoveredPlaneHighlight();
			return;
		}

		// --- Origin axes (colored lines) ---
		var axisLen = s * 0.35f;
		Gizmo.Draw.LineThickness = 2f;

		// X axis — red (forward)
		Gizmo.Draw.Color = new Color( 0.9f, 0.25f, 0.25f, 0.7f );
		Gizmo.Draw.Line( center, center + Vector3.Forward * axisLen );

		// Y axis — green (left)
		Gizmo.Draw.Color = new Color( 0.25f, 0.8f, 0.35f, 0.7f );
		Gizmo.Draw.Line( center, center + Vector3.Left * axisLen );

		// Z axis — blue (up)
		Gizmo.Draw.Color = new Color( 0.3f, 0.45f, 0.9f, 0.7f );
		Gizmo.Draw.Line( center, center + Vector3.Up * axisLen );

		// Axis labels at the ends — using WorldText for 3D placement
		Gizmo.Draw.Color = new Color( 0.9f, 0.25f, 0.25f, 0.8f );
		Gizmo.Draw.WorldText( "X", new Transform( center + Vector3.Forward * (axisLen + 8f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.Color = new Color( 0.25f, 0.8f, 0.35f, 0.8f );
		Gizmo.Draw.WorldText( "Y", new Transform( center + Vector3.Left * (axisLen + 8f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.Color = new Color( 0.3f, 0.45f, 0.9f, 0.8f );
		Gizmo.Draw.WorldText( "Z", new Transform( center + Vector3.Up * (axisLen + 8f) ), "Roboto", 10f, TextFlag.Center );

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.IgnoreDepth = false;

		DrawPlaneHitboxes();
		DrawHoveredPlaneHighlight();
	}

	/// <summary>Draw the four edges of a plane rectangle as a wireframe outline.</summary>
	private static void DrawPlaneOutline( Vector3 center, Vector3 right, Vector3 up, float halfSize, Color color )
	{
		Gizmo.Draw.Color = color;

		var a = center + right * halfSize + up * halfSize;
		var b = center - right * halfSize + up * halfSize;
		var c = center - right * halfSize - up * halfSize;
		var d = center + right * halfSize - up * halfSize;

		Gizmo.Draw.Line( a, b );
		Gizmo.Draw.Line( b, c );
		Gizmo.Draw.Line( c, d );
		Gizmo.Draw.Line( d, a );
	}

	/// <summary>
	/// The most grid lines a plane may draw across itself in one direction.
	///
	/// A CAP, NOT A DENSITY. Spacing is now a real distance in units rather than a count of
	/// subdivisions, which means a fine grid on a plane dragged out to a thousand units asks for
	/// tens of thousands of lines and takes the frame rate with it. Past this the step is widened
	/// until it fits, so a grid that would be an unreadable smear is drawn coarse instead.
	/// </summary>
	private const int MaxGridLines = 160;

	/// <summary>How square-on a plane has to be before its grid is at full strength — the cosine of
	/// the angle between its normal and the view. Below this it fades out proportionally, reaching
	/// nothing when exactly edge-on. 0.35 is about twenty degrees of tilt.</summary>
	private const float EdgeOnFade = 0.35f;

	/// <summary>
	/// The step to draw a plane's lattice at — the same one the cursor snaps to, so the lines mean
	/// something, widened if that would put more than <see cref="MaxGridLines"/> across the plane.
	///
	/// On Automatic the step comes from the camera: WorldRadiusAt with a one-pixel radius IS the
	/// units-per-pixel at that point, which is exactly what AutoGridStep wants. That is why the
	/// reference planes can have an adaptive grid outside a sketch, where there is no sketch plane
	/// to measure against.
	/// </summary>
	private float DrawnGridStep( Vector3 center, float halfSize )
	{
		var step = GridStep( WorldRadiusAt( center, 1f ) );

		if ( step <= 0f )
			step = halfSize * 0.25f;

		return MathF.Max( step, halfSize * 2f / MaxGridLines );
	}

	/// <summary>
	/// Whether planes draw a grid inside their outline — the three reference planes AND the active
	/// sketch plane, together.
	///
	/// ONE SWITCH FOR ALL FOUR. It governed only the sketch plane at first, which made it look
	/// broken: the sketch plane is drawn only while a sketch is open, so flipping the setting
	/// anywhere else changed nothing on screen and there was no way to tell that from a dead
	/// control.
	///
	/// Snapping is unaffected either way — SketchSnapper rounds to a step it works out for itself
	/// and never consults this — so turning the grid off means drawing against an invisible ruler.
	/// </summary>
	public bool ShowPlaneGrid { get; set; }

	/// <summary>
	/// Draw a plane's lattice, stepping OUT FROM THE CENTRE rather than in from one edge.
	///
	/// That is not cosmetic. Starting at -halfSize put the lines at whatever the plane's width
	/// happened to leave over, so with a 1-unit spacing on a 128.5-unit plane none of them landed on
	/// a whole number — the grid was half a unit off the coordinates the cursor was snapping to.
	/// Walking out from zero puts every line on an exact multiple of the step, which is what makes
	/// it the same grid the snap uses.
	///
	/// Two things keep three overlapping planes from reading as a wire cage. The centre lines are
	/// skipped, because those are the origin axes and they are already drawn in their own colours —
	/// three planes meeting at the origin were putting six coincident grey lines over three
	/// coloured ones. And the lines fade as they get further out, so the lattice thins toward the
	/// edge instead of ending in a hard grid to the last row.
	/// </summary>
	private static void DrawPlaneGrid( Vector3 center, Vector3 right, Vector3 up,
		float halfSize, float step, Color color )
	{
		if ( step <= 0f || halfSize <= 0f )
			return;

		var count = (int)(halfSize / step);

		for ( var i = 1; i <= count; i++ )
		{
			var offset = i * step;

			// Quadratic rather than linear: a linear ramp still reads as a solid sheet most of the
			// way out and then stops. This is near full weight around the origin, where the work
			// happens, and a quarter of it at the rim.
			var t = offset / halfSize;
			var faded = color.WithAlpha( color.a * (1f - 0.75f * t * t) );

			Gizmo.Draw.Color = faded;

			foreach ( var sign in Signs )
			{
				var d = offset * sign;

				Gizmo.Draw.Line( center + up * d - right * halfSize, center + up * d + right * halfSize );
				Gizmo.Draw.Line( center + right * d - up * halfSize, center + right * d + up * halfSize );
			}
		}
	}

	/// <summary>
	/// Fill the face a sketch is sitting on with a grid, clipped to that face's own outline.
	///
	/// THE FACE IS THE PAPER. A sketch derived from a face is about that face, and the ruled grid
	/// is only useful where the face is - so it stops where the face stops, including around a
	/// hole through it, rather than at the edge of an arbitrary rectangle. Crossings are counted
	/// even-odd along each grid line, which is why a hole comes out as a gap for free: its rim is
	/// in the outline like any other edge.
	///
	/// SAME STEP AS THE SNAP, through DrawnGridStep, and aligned to the sketch plane's origin
	/// rather than to the face - so an intersection you can see is an intersection the cursor
	/// lands on, which is the only reason to draw the lines at all.
	///
	/// SIZED AND FADED FROM THE FACE ITSELF, because "any face that can be sketched on" runs from
	/// a two-unit tab to a two-thousand-unit floor. A step that fits one is a solid sheet of ink on
	/// the other, so the step is widened until the face can hold the lines and the ink is thinned
	/// as the lines close up on screen. A face seen nearly edge-on fades out entirely, the way the
	/// reference planes already do - it is a bright smear across the middle of the view and never
	/// a ruler.
	/// </summary>
	/// <returns>False when there is no face underneath - a sketch on a global plane - in which
	/// case the caller falls back to drawing the plane as a rectangle.</returns>
	private bool DrawSketchFaceGrid( Color color )
	{
		if ( ActiveSketchReference is not { IsEmpty: false } reference || ActiveSketch?.Plane is null )
			return false;

		// The face is handled, and the rectangle stays gone even with the grid switched off: the
		// green outline already says where the face is, and the yellow square said nothing the
		// switch was ever about.
		if ( !ShowPlaneGrid )
			return true;

		// The same measurement the snap uses - see TryFaceExtent, which exists so the lines drawn
		// and the intervals landed on cannot come from two different walks over the same points.
		if ( !TryFaceExtent( out var min, out var max, out var span, out var world ) )
			return true;

		var half = span * 0.5f;
		var step = FaceGridStep( world, span );

		if ( step <= 0f )
			return true;

		// The cap belongs here rather than inside the line walk, which used to answer "too many
		// lines" by drawing NONE of them - so a face big enough relative to the step simply had no
		// grid, with nothing on screen to say why. Doubling until it fits is the same answer the
		// reference planes give, and it always leaves something to read.
		//
		// Bounded, because this runs on geometry rather than on a promise: a face whose extent came
		// back infinite through a broken rebuild would otherwise spin here forever, and a viewport
		// that hangs is worse than one drawing a coarse grid.
		for ( var i = 0; i < 64 && (LinesAcross( min.x, max.x, step ) > MaxGridLines
			|| LinesAcross( min.y, max.y, step ) > MaxGridLines); i++ )
		{
			step *= 2f;
		}

		var units = WorldRadiusAt( world, 1f );

		// How far apart the lines land ON SCREEN, which is the only thing that decides whether a
		// grid reads as a ruler or as a wash. DrawnGridStep aims for GridPixels and misses it
		// whenever the cap above has taken over, or whenever the camera is a long way from a face
		// large enough to keep its own step.
		var spacing = units > 0f ? step / units : GridPixels;
		var density = MathF.Min( spacing / GridPixels, 1f );

		// Straight down the sketch plane's normal, so a face turned nearly edge-on fades out
		// instead of collapsing into a bright line across everything behind it.
		var normal = new Vector3( ActiveSketch.Plane.Normal.x, ActiveSketch.Plane.Normal.y,
			ActiveSketch.Plane.Normal.z );
		var facing = MathF.Abs( Vector3.Dot( normal, _camera.WorldRotation.Forward ) );
		var viewFade = MathF.Min( facing / EdgeOnFade, 1f );

		var alpha = color.a * 0.45f * density * viewFade;

		if ( alpha <= 0.01f )
			return true;

		Gizmo.Draw.LineThickness = 1f;
		Gizmo.Draw.Color = color.WithAlpha( alpha );

		DrawFaceGridLines( reference, min.x, max.x, step, true );
		DrawFaceGridLines( reference, min.y, max.y, step, false );

		return true;
	}

	/// <summary>How many grid lines at <paramref name="step"/> fall between two coordinates.
	/// Negative when the span is too narrow to hold one, which is a legitimate answer for the short
	/// axis of a long thin face.</summary>
	private static int LinesAcross( float low, float high, float step ) =>
		(int)MathF.Floor( high / step ) - (int)MathF.Ceiling( low / step ) + 1;

	/// <summary>
	/// Roughly how many squares the grid should put across a face, on Automatic.
	///
	/// A DIVISION COUNT, NOT A DISTANCE, because the faces this has to serve differ by three orders
	/// of magnitude in the same document — the 2.75-unit dial on this grill and the 72-unit slab it
	/// is set into. A fixed distance is graph paper on one and a solid wash on the other. Around a
	/// dozen squares reads as ruled paper at any size, which is what a grid is for.
	/// </summary>
	private const float FaceGridDivisions = 12f;

	/// <summary>
	/// The step to rule a FACE at: fitted to the face, then held back to what the screen can
	/// actually show.
	///
	/// The reference planes size their grid from the camera alone (see DrawnGridStep), which is
	/// right for a plane — it has no size of its own worth speaking of, it is a stand-in for an
	/// infinite one. A face does have a size, and it is the thing being drawn on, so the grid
	/// belongs to it: the step is the 1/2/5 round number nearest span/FaceGridDivisions, which puts
	/// about a dozen squares across whatever you picked and lands the lines on whole numbers rather
	/// than on whatever the face's width happened to leave over.
	///
	/// THEN THE CAMERA GETS A VETO, one way only. Fitting to the face alone would rule a 72-unit
	/// slab at 5 units and keep ruling it at 5 units as you zoom out to the whole grill, where those
	/// lines are a pixel apart and the face fills with ink. Taking whichever step is COARSER means
	/// the face decides while you are working on it and the camera takes over once the face is too
	/// small on screen to hold that many lines. It never goes the other way, so zooming in cannot
	/// make the grid finer than the face asked for — the squares you measured against stay the
	/// squares you measured against.
	///
	/// An explicit GridSpacing skips all of it. A number someone typed is not a suggestion.
	/// </summary>
	private float FaceGridStep( Vector3 centre, float span )
	{
		if ( GridSpacing > 0f )
			return GridSpacing;

		// AutoGridStep answers "what round step is about `pixels` across, at this scale" — feeding
		// it the face's own scale rather than the camera's asks the same question about the face.
		var fitted = SketchSnapper.AutoGridStep( span / FaceGridDivisions, 1f );
		var camera = SketchSnapper.AutoGridStep( WorldRadiusAt( centre, 1f ), GridPixels );

		return MathF.Max( fitted, camera );
	}

	/// <summary>One family of grid lines across a face - the ones at constant u when
	/// <paramref name="alongX"/>, at constant v otherwise. Split out because the two directions
	/// are the same walk with the components swapped, and writing it twice is how one of them
	/// ends up subtly different.</summary>
	private void DrawFaceGridLines( SketchReference reference, float low, float high, float step, bool alongX )
	{
		var first = (int)MathF.Ceiling( low / step );
		var last = (int)MathF.Floor( high / step );

		var crossings = new List<float>();

		for ( var i = first; i <= last; i++ )
		{
			var line = i * step;

			crossings.Clear();

			for ( var e = 0; e < reference.Edges.Count; e++ )
			{
				var (a, b) = reference.Segment( e );

				var from = alongX ? a.x : a.y;
				var to = alongX ? b.x : b.y;

				// Half-open, so a vertex sitting exactly on the line is counted by one of its two
				// edges rather than by both or neither. Both would pair the crossing with itself
				// and leave the span beyond it unfilled.
				if ( from <= line == to <= line )
					continue;

				var t = (line - from) / (to - from);

				crossings.Add( alongX
					? a.y + (b.y - a.y) * t
					: a.x + (b.x - a.x) * t );
			}

			if ( crossings.Count < 2 )
				continue;

			crossings.Sort();

			// In pairs: inside the face between the first and second crossing, outside between the
			// second and third, and so on around a hole and back.
			for ( var c = 0; c + 1 < crossings.Count; c += 2 )
			{
				var a = alongX ? new Vec2( line, crossings[c] ) : new Vec2( crossings[c], line );
				var b = alongX ? new Vec2( line, crossings[c + 1] ) : new Vec2( crossings[c + 1], line );

				Gizmo.Draw.Line( PlaneToWorld( a ), PlaneToWorld( b ) );
			}
		}
	}

	/// <summary>Both sides of the centre line, walked together so each pair shares one alpha.
	/// </summary>
	private static readonly float[] Signs = { 1f, -1f };

	/// <summary>Whether a plane index — 0 Top, 1 Front, 2 Right — is currently shown.</summary>
	private bool PlaneVisible( int index ) => index switch
	{
		0 => TopPlaneVisible,
		1 => FrontPlaneVisible,
		_ => RightPlaneVisible,
	};

	/// <summary>
	/// The two in-plane axes and the edge colour for a plane index, in Onshape's convention:
	/// Top (XY) orange, Front (XZ) blue, Right (YZ) green.
	///
	/// One definition rather than the three switch statements the outline, the hover wash and the
	/// corner handles each used to carry. They disagreed once already — the hover wash is the
	/// reason DrawPlaneHitboxes lives beside the wireframe rather than apart from it.
	/// </summary>
	private static (Vector3 Right, Vector3 Up, Color Colour) PlaneAxes( int index ) => index switch
	{
		0 => (Vector3.Forward, Vector3.Left, new Color( 0.85f, 0.55f, 0.25f, 0.55f )),
		1 => (Vector3.Forward, Vector3.Up, new Color( 0.25f, 0.5f, 0.85f, 0.55f )),
		_ => (Vector3.Left, Vector3.Up, new Color( 0.25f, 0.78f, 0.45f, 0.55f )),
	};

	// --- resizing a plane by its corners --------------------------------------------------------

	/// <summary>Radius of a plane's corner handle in SCREEN PIXELS, for the same reason the origin
	/// dot is measured that way: a world-unit handle is a boulder on a small part and invisible on
	/// a large one.</summary>
	private const float PlaneCornerPixels = 5f;

	/// <summary>Which plane is being resized right now, or -1. Held across frames because a drag
	/// is a gesture, not an event — the cursor leaves the handle the moment it starts moving.
	/// </summary>
	private int _resizingPlane = -1;

	/// <summary>
	/// A grab handle at each corner of each plane, shown only when the cursor is on it.
	///
	/// HOVER-ONLY because twelve permanent dots around the origin is the clutter the grid was just
	/// taken out for. The hitbox is always registered — it has to be, or there would be nothing to
	/// hover — but nothing is drawn until the cursor finds it, and then the corner being dragged
	/// stays lit for as long as the drag lasts.
	///
	/// Not while sketching or while a plane is armed for picking: in both of those a click on a
	/// plane already means something, and a handle sitting on the corner would eat it.
	/// </summary>
	private void DrawPlaneCornerHandles()
	{
		if ( IsSketching || PlanePickMode )
			return;

		var center = OriginPosition;

		for ( var index = 0; index < 3; index++ )
		{
			if ( !PlaneVisible( index ) )
				continue;

			var (right, up, colour) = PlaneAxes( index );
			var half = _planeHalfSize[index];

			for ( var corner = 0; corner < 4; corner++ )
			{
				// 0 (+,+), 1 (-,+), 2 (-,-), 3 (+,-) — the same walk around the rectangle
				// DrawPlaneOutline makes, so a handle always sits on a drawn corner.
				var x = corner is 0 or 3 ? half : -half;
				var y = corner is 0 or 1 ? half : -half;

				var position = center + right * x + up * y;
				var radius = WorldRadiusAt( position, PlaneCornerPixels );

				using var scope = Gizmo.Scope( $"plane-corner-{index}-{corner}", new Transform( position ) );

				Gizmo.Hitbox.DepthBias = 0.01f;
				Gizmo.Hitbox.Sphere( new Sphere( Vector3.Zero, radius * 2f ) );

				var dragging = _resizingPlane == index;

				if ( !Gizmo.IsHovered && !dragging )
					continue;

				// Through everything, including the part. A handle you cannot see because the solid
				// you are building sits in front of it is a handle you cannot grab.
				Gizmo.Draw.IgnoreDepth = true;
				Gizmo.Draw.Color = colour.WithAlpha( dragging ? 0.9f : 0.5f );
				Gizmo.Draw.SolidSphere( 0f, radius, 10, 10 );
				Gizmo.Draw.IgnoreDepth = false;

				if ( Gizmo.IsHovered && Gizmo.WasLeftMousePressed )
					_resizingPlane = index;
			}
		}
	}

	/// <summary>
	/// Carry a corner drag, sizing the plane to wherever the cursor is on it.
	///
	/// The cursor is put back on the plane rather than tracked in screen space, so the corner stays
	/// under the pointer at any camera angle — the same ray-into-plane projection sketching uses to
	/// place a point (CursorToPlane). The new half-size is the LARGER of the two in-plane distances,
	/// which keeps the plane square the way it has always been drawn; the corner therefore tracks
	/// the cursor exactly along a diagonal and approximately elsewhere, which is what a square
	/// constraint costs.
	/// </summary>
	private void UpdatePlaneResize()
	{
		if ( _resizingPlane < 0 )
			return;

		// Released anywhere, over the handle or not. A drag that only ended when the cursor
		// happened to be back on the corner would never end.
		if ( !Gizmo.IsLeftMouseDown )
		{
			_resizingPlane = -1;
			return;
		}

		var (right, up, _) = PlaneAxes( _resizingPlane );
		var normal = Vector3.Cross( right, up );

		var ray = Gizmo.CurrentRay;
		var denom = Vector3.Dot( ray.Forward, normal );

		// Edge-on: the plane is a line from here and there is no meaningful hit. Hold the size it
		// had rather than snapping it to something arbitrary.
		if ( MathF.Abs( denom ) < 1e-5f )
			return;

		var t = Vector3.Dot( OriginPosition - ray.Position, normal ) / denom;

		if ( t <= 0f )
			return;

		var offset = ray.Position + ray.Forward * t - OriginPosition;

		var half = MathF.Max( MathF.Abs( Vector3.Dot( offset, right ) ), MathF.Abs( Vector3.Dot( offset, up ) ) );

		_planeHalfSize[_resizingPlane] = MathF.Max( half, MinPlaneHalfSize );
	}

	// --- standard views ----------------------------------------------------------------------

	/// <summary>Named camera poses, reachable from the View menu. A fly camera does not need a
	/// corner cube to stay oriented, but snapping to a plane is still useful.</summary>
	public enum StandardView
	{
		Top,
		Bottom,
		Front,
		Back,
		Left,
		Right,
		Isometric,
	}

	/// <summary>
	/// Point the camera down a named axis, keeping whatever the current framing distance is.
	///
	/// s&amp;box is +x forward, +y left, +z up, so "Front" looks along -x at the XZ plane and "Right"
	/// looks along +y at the YZ plane — matching how DrawReferencePlanes names the same three.
	/// </summary>
	public void SetStandardView( StandardView view )
	{
		var dir = view switch
		{
			StandardView.Top => new Vector3( 0f, 0f, 1f ),
			StandardView.Bottom => new Vector3( 0f, 0f, -1f ),
			StandardView.Front => new Vector3( 1f, 0f, 0f ),
			StandardView.Back => new Vector3( -1f, 0f, 0f ),
			StandardView.Left => new Vector3( 0f, 1f, 0f ),
			StandardView.Right => new Vector3( 0f, -1f, 0f ),
			_ => new Vector3( 1f, -1f, 0.65f ).Normal,
		};

		// Looking straight down needs an up vector that is not also straight down.
		var up = MathF.Abs( dir.z ) > 0.99f ? Vector3.Forward : Vector3.Up;

		PointCameraAt( CurrentFocus(), dir, up );
	}

	/// <summary>
	/// Look square at the active sketch plane — Onshape's N.
	///
	/// This is bound to a key rather than fired on sketch entry on purpose. Onshape does NOT
	/// rotate the view when you pick a plane (automating it is a standing request on their forum,
	/// not shipped behaviour), and taking the camera away from someone who deliberately set up a
	/// three-quarter view to sketch against existing geometry is worse than one keypress.
	/// </summary>
	public void ViewNormalToSketchPlane()
	{
		if ( ActiveSketch?.Plane is not { } plane )
			return;

		var normal = ToWorldDir( plane.Normal );
		var up = ToWorldDir( plane.YAxis );
		var centre = OriginPosition + ToWorldDir( plane.Origin );

		// Second press flips to the far side, the way Onshape's N does.
		if ( Vector3.Dot( _camera.WorldPosition - centre, normal ) > 0f )
			normal = -normal;

		PointCameraAt( centre, normal, up );
	}

	/// <summary>What the camera is currently looking at, so a view change rotates around the part
	/// rather than throwing it off screen.</summary>
	private Vector3 CurrentFocus()
	{
		if ( _renderer.IsValid() && _renderer.Model is { } model )
			return model.Bounds.Center;

		return Vector3.Zero;
	}

	private void PointCameraAt( Vector3 focus, Vector3 direction, Vector3 up )
	{
		var distance = (_camera.WorldPosition - focus).Length;

		// A camera sitting exactly on the focus has no distance to preserve, which happens before
		// anything has been framed. Fall back to the reference planes' own scale.
		if ( distance < 1f )
			distance = PlaneSize * 1.25f;

		_camera.WorldPosition = focus + direction.Normal * distance;
		_camera.WorldRotation = Rotation.LookAt( -direction.Normal, up );
	}

	// --- per-frame tick ---------------------------------------------------------------------

	/// <summary>Whether the cursor is over the 3D canvas and not driving the camera. Read by the
	/// sketch pass, which has no hitbox of its own to hover.</summary>
	private bool _canvasHasCursor;

	private void OnPreFrame()
	{
		if ( _canvas.Scene is { } scene )
			scene.EditorTick( RealTime.Now, RealTime.Delta );

		// The floating overlays sit inside the canvas, so "cursor over the canvas" is true while
		// you are aiming at one. Without excluding them, pressing a control also grabs the orbit
		// camera, the click drags the view, and sketch tools place points on the plane.
		// Only while there is a sketch to grid. Outside one the switch still means something - the
		// reference planes read it too - but it would be a control on the sketch toolbar for a mode
		// nobody is in, and Edit > Settings is its home for that case.
		if ( SketchGridBar.IsValid() )
			SketchGridBar.Visible = IsSketching;

		var overAnyOverlay = (_resultOverlay?.IsUnderMouse ?? false)
			|| (_sculptBarOverlay?.IsUnderMouse ?? false)
			|| _paintBarOverlays.Any( b => b.IsValid() && b.IsUnderMouse );
		var overCanvas = _canvas.IsUnderMouse && !overAnyOverlay;

		_gizmoInstance.Input.IsHovered = IsActiveWindow && overCanvas;

		var flying = _gizmoInstance.FirstPersonCamera( _camera, _canvas );

		if ( flying )
			_gizmoInstance.Input.IsHovered = false;

		// Whether this right-press has actually moved the view yet — see EffigyViewport.FaceMenu.cs,
		// which uses it to tell a right-click apart from the end of an orbit.
		NoteCameraFlight( flying );

		// After FirstPersonCamera has had its say this means "the cursor is over the canvas and we
		// are not flying the camera" - which is exactly the condition a sketch click needs. Without
		// it, left-dragging to orbit scatters points across the plane.
		_canvasHasCursor = _gizmoInstance.Input.IsHovered;

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		// Held for the right-click menu, which has no frame of its own to build a ray in.
		CaptureCursorRay();

		// BEFORE the planes, not with the rest of the picking below. The planes decide whether to
		// take this click by comparing against the face under the cursor, so the face has to be
		// known by the time they ask — see ResolveFacePick.
		ResolveFacePick();

		// The stand-in follows the origin handle, so it moves in the same frame the handle does.
		PlaceSizeReference();

		// Draw planes first (behind everything else)
		DrawReferencePlanes();
		DrawCommittedSketches();
		ShadeMaterialSlotsFrame();
		MaterialDropFrame();
		SketchPickFrame();
		FacePickFrame();
		EdgePickFrame();
		BodyPickFrame();
		// Before the draw, so what is drawn is this frame's solve rather than last frame's.
		SoftPreviewFrame();

		DrawRigSkeleton();
		BoneToolFrame();

		SketchFrame();
		SculptFrame();
		PaintFrame();
		MaterialBrushFrame();

		// AFTER the pick passes and after sculpting, so a note is drawn over everything it is about
		// and an erase click is resolved against a hover the other modes have already declined.
		// DrawNotes runs unconditionally where NoteFrame returns early: notes are visible whether or
		// not the pen is armed — see EffigyViewport.Notes.cs.
		NoteFrame();
		DrawNotes();

		// AFTER SketchFrame and outside it, because SketchFrame returns early when no sketch is
		// open and "no sketch is open" is one of the answers the probe exists to give. Off unless
		// `effigy_probe_sketch 1` has been run.
		SketchProbe();

		// Origin and lamps on top of the planes. Hidden while sketching or picking anything - they
		// sit where first clicks land, and stealing them was the first thing that broke.
		// RigMode joins the list for the same reason every other entry is on it: these sit where
		// first clicks land, and in the rig workspace every first click is meant for a bone.
		if ( !IsSketching && !PlanePickMode && !SketchPickMode && !FacePickMode && !EdgePickMode
			&& !BodyPickMode && !BoneToolActive && !RigMode )
		{
			DrawViewportLights();
			DrawOrigin();

			// HasHovered, not IsHovered: the move gizmo's arrows are Control hitboxes, and
			// IsHovered only sees Hitbox.Sphere. Clicking an arrow used to count as empty space.
			if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered )
			{
				if ( OriginSelected )
					DeselectOrigin();

				if ( LightSelected )
					DeselectLight();
			}
		}

		// AFTER the origin, so a click on the origin handle is not also a click on the face sitting
		// behind it. Idle hover/click only runs when no dialog owns the mouse.
		IdleSelectionFrame();

		// AFTER the selection, because the handle belongs to whatever that pass just settled on, and
		// before nothing in particular - Gizmo.Control registers its own hitbox, which is what stops the
		// click that grabs an arrow from also landing on the face behind it.
		// Not in the rig workspace. The arrow hangs off the idle face selection, which RigMode has
		// already stopped happening, so this is belt and braces - but the handle is a Gizmo.Control
		// that would keep registering a hitbox over a selection made before the workspace changed,
		// and a stray arrow over the model is exactly the kind of thing that eats a bone click.
		if ( !RigMode )
			FaceDragFrame();

		// BoneToolActive and BodyPickMode: the same "you can click here" signal every other live
		// pick mode already gets from Gizmo.HasHovered/_hoveredSketchId/_hoveredFaceBodyId. Without
		// it, placing a bone or assigning a body was the only click-to-act mode in the whole tool
		// that left the cursor a plain arrow the entire time.
		Cursor = Gizmo.HasHovered || IsSketching || IsPainting || IsMaterialBrushing || _hoveredSketchId is not null || _hoveredFaceBodyId is not null
			|| BoneToolActive || BodyPickMode || FacePickMode || EdgePickMode
			? CursorShape.Finger : CursorShape.Arrow;
	}

	/// <summary>Draw all bones as dog-bone shapes with selection and pose gizmo.</summary>
	private void DrawRigSkeleton()
	{
		if ( RigSkeleton is null || RigSkeleton.Count == 0 )
			return;

		// The selected bone's gizmo needs IgnoreDepth=false to match normal editor gizmos.
		Gizmo.Draw.IgnoreDepth = true;

		for ( var i = 0; i < RigSkeleton.Count; i++ )
			DrawBoneHandle( i );

		// The selected bone's gizmo runs after the loop, in its own scope, so its hitboxes
		// do not fight with the bone hitboxes.
		if ( _selectedBoneIndex >= 0 && _selectedBoneIndex < RigSkeleton.Count )
			DrawSelectedBoneGizmo();

		Gizmo.Draw.IgnoreDepth = false;

		// Click empty space to deselect — AFTER the gizmo so Gizmo.HasHovered covers both
		// our bone hitboxes AND the gizmo control's own hitboxes. Using !Gizmo.IsHovered
		// here was the bug: IsHovered only sees Hitbox.Sphere calls, not Control hitboxes,
		// so clicking the gizmo counted as empty space and deselected immediately.
		if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered && _selectedBoneIndex >= 0 && !_boneDragging )
		{
			_selectedBoneIndex = -1;
			BoneSelectionChanged?.Invoke( -1 );
		}
	}

	/// <summary>Base radius of a bone's head sphere in world units.</summary>
	private const float BoneHandleRadius = 0.8f;

	/// <summary>
	/// A dog bone's knob radius as a fraction of its length — the widest part of the drawn shape.
	///
	/// SHARED BY THE DRAWING AND THE HIT TEST, which is the point of it being a named constant
	/// rather than the bare 0.16 it used to be inside DrawDogBone. The two used different numbers
	/// for as long as both existed, so how much of a bone you could actually click depended on how
	/// long the bone was.
	/// </summary>
	private const float DogBoneKnobScale = 0.16f;

	/// <summary>
	/// The smallest a bone's hit target may get, whatever its length.
	///
	/// A very short bone draws a very small dog bone, and a hit target faithful to it would be a
	/// few pixels across. Fidelity is worth having right up to the point where the thing becomes
	/// unclickable; below that a slightly generous target is the lesser problem, and a short bone
	/// has little around it to steal from anyway.
	/// </summary>
	private const float MinBonePickRadius = 0.6f;

	/// <summary>Draw one bone as a dog-bone: a knobby ball at the head, a knobby ball at the
	/// tail, and a thin shaft between them.</summary>
	private void DrawBoneHandle( int index )
	{
		// BoneWorld, not WorldBind: while the soft preview runs this is where the bone actually IS,
		// which is where it has to be drawn and where its hit sphere has to sit. See
		// EffigyViewport.SoftPreview.cs.
		var world = BoneWorld( index );
		var bone = RigSkeleton.Bones[index];

		var head = new Vector3( world.Origin.x, world.Origin.y, world.Origin.z );
		var tailVec = world.TransformPoint( new Vec3( 0, bone.Length, 0 ) );
		var tail = new Vector3( tailVec.x, tailVec.y, tailVec.z );

		// Cross-section axes from the Xform basis — shows roll of the bone.
		var xAxis = new Vector3( world.X.x, world.X.y, world.X.z );
		var zAxis = new Vector3( world.Z.x, world.Z.y, world.Z.z );

		// MEASURED, not bone.Length. The two agree for a well-formed bone, and where they do not
		// the drawing follows this one — DrawDogBone derives everything from the head-to-tail
		// vector — so the hit target has to follow it as well or the pair drift apart again.
		var boneLen = (tail - head).Length;

		var isSelected = index == _selectedBoneIndex;

		// SOFT BONES ARE BLUE. Which bones in a chain are simulated and which are welded is not
		// otherwise visible anywhere in the viewport - it is a tick in a panel on a bone you have to
		// select one at a time - and it is the first thing you want to know when a rig wobbles
		// wrongly. Selection still wins over it: yellow means "this is the one the gizmo will move",
		// which is a fact about right now, where softness is a fact about the rig.
		Gizmo.Draw.Color = isSelected
			? new Color( 1f, 0.85f, 0.2f, 1f )
			: bone.Soft is not null
				? new Color( 0.35f, 0.7f, 1f, 0.85f )
				: new Color( 0.95f, 0.35f, 0.2f, 0.8f );

		DrawDogBone( head, tail, xAxis, zAxis );

		// While placing new bones, an existing bone's hitbox would steal the click instead of
		// letting it land on the mesh underneath.
		if ( BoneToolActive )
			return;

		// A degenerate bone draws nothing (DrawDogBone bails at the same threshold), and a hit
		// target for a shape that is not on screen is a click landing on nothing visible.
		if ( boneLen < 0.01f )
			return;

		// EVERY BONE NEEDS ITS OWN NAMED SCOPE, and the lack of one is why clicking a bone in this
		// viewport did nothing at all.
		//
		// Gizmo.IsHovered does not answer "is the cursor over the last shape I registered" - it
		// answers "is the cursor over THIS GIZMO OBJECT", and the object is the scope. Registering
		// every bone's hitbox at the root scope put all of them into one object, so the question
		// each bone asked was really "is the cursor over any bone at all", and the answer could not
		// pick one out. RigControlEditor's RigViewport has done this correctly since it was written
		// - see the $"Bone{bone.Index}" scope in its DrawBones.
		//
		// THE SCOPE CARRIES THE BONE'S ROTATION, not just its position, which is what lets the
		// hitbox below be a single box that lies along the bone instead of a string of spheres
		// approximating one. ExtractRotation maps the kernel's basis the same way the pose gizmo
		// does - the bone's own axis becomes the scope's FORWARD - so local +X runs head to tail.
		var hovered = false;

		using ( Gizmo.Scope( $"EffigyBone{index}", new Transform( head, ExtractRotation( world ) ) ) )
		{
			// THE HITBOX IS SIZED FROM THE DRAWING, and that is the whole fix for a hit target that
			// felt arbitrary.
			//
			// DrawDogBone sizes its knobs off the bone's LENGTH (knobR = boneLen * 0.16), while the
			// hitbox was a fixed 0.8 units regardless. So the two agreed at exactly one bone length
			// and diverged in both directions from there: on a long bone the drawn shape was far
			// bigger than the target, so most of the bone you could see did nothing, and on a short
			// one the target stuck out well past the bone. Same constant, same expression, one
			// source of truth - the thing you can see is the thing you can hit.
			var knobR = MathF.Max( boneLen * DogBoneKnobScale, MinBonePickRadius );

			// THE SELECTED BONE KEEPS A HITBOX, just one that loses to its own pose gizmo.
			//
			// It used to have none at all, on the reasoning that the gizmo registers its own - and
			// that is the bug behind "the gizmo disappears". The deselect test at the end of
			// DrawRigSkeleton treats a press with nothing hovered as a click on empty space, so
			// clicking the selected bone anywhere off the gizmo's arrows read as empty space and
			// threw the selection away. The gizmo did not fail to appear; it was being dismissed by
			// the click aimed at it.
			//
			// The depth bias is the whole mechanism. Gizmo.Control's handles register at 0.01,
			// biased toward the camera; leaving this bone at 0 means the arrows win everywhere they
			// overlap it - which is RigViewport's finding in its own words, that a sphere "biased in
			// front, no less" beat the control and stopped the drag ever starting. Off the arrows,
			// this box is all there is, so the bone stays hovered and the selection survives.
			Gizmo.Hitbox.DepthBias = isSelected ? 0f : 0.01f;

			Gizmo.Hitbox.BBox( new BBox(
				new Vector3( 0f, -knobR, -knobR ),
				new Vector3( boneLen, knobR, knobR ) ) );

			hovered = Gizmo.IsHovered;

			// Selecting what is already selected would only re-fire the callbacks - and one of them
			// rebuilds the tree selection, which is not free.
			if ( hovered && !isSelected && Gizmo.WasLeftMousePressed )
			{
				_selectedBoneIndex = index;
				BoneSelectionChanged?.Invoke( index );
			}
		}

		// The highlight is the BONE, not a blob near it, and it is drawn out here in world space
		// where DrawDogBone works. A sphere at the head was the old feedback and it was actively
		// misleading: it said the head was the target when the target is the whole bone.
		// Not on the selected bone: it is already drawn in the selection colour, and a hover
		// highlight over it would say the click is about to do something when it is about to do
		// nothing.
		if ( hovered && !isSelected )
		{
			Gizmo.Draw.Color = new Color( 1f, 0.85f, 0.2f, 0.9f );
			DrawDogBone( head, tail, xAxis, zAxis );
		}
	}

	/// <summary>
	/// Pose gizmo for the selected bone. Mode is set by W (move), E (rotate), R (scale).
	/// Follows RigViewport's pattern: Position gives per-frame delta (accumulate), Rotate
	/// gives cumulative-since-grab (assign). The start pose is captured once and everything
	/// is applied to that — the live transform is never fed back.
	/// </summary>
	private void DrawSelectedBoneGizmo()
	{
		var world = BoneWorld( _selectedBoneIndex );
		var bone = RigSkeleton.Bones[_selectedBoneIndex];
		var head = new Vector3( world.Origin.x, world.Origin.y, world.Origin.z );
		var headRot = ExtractRotation( world );

		var startPos = _boneDragging ? _dragStartPos : head;
		var startRot = _boneDragging ? _dragStartRot : headRot;

		using var scope = Gizmo.Scope( $"BoneCtrl{_selectedBoneIndex}", new Transform( startPos, startRot ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		switch ( _boneDragMode )
		{
			case BoneDragMode.Rotate:
			{
				if ( !Gizmo.Control.Rotate( $"bone{_selectedBoneIndex}-rot", Rotation.Identity, out var rotation ) )
				{
					EndBoneDragIfReleased();
					return;
				}

				BeginBoneDrag( head, headRot, bone.Length );
				_boneDragging = true;

				// Rotate is CUMULATIVE since the grab — assign, don't accumulate.
				var newRot = rotation * _dragStartRot;

				ApplyBoneTransform( _selectedBoneIndex, _dragStartPos, newRot, _dragStartLength );
				break;
			}

			case BoneDragMode.Move:
			{
				if ( !Gizmo.Control.Position( $"bone{_selectedBoneIndex}-pos", Vector3.Zero, out var delta, Rotation.Identity ) )
				{
					EndBoneDragIfReleased();
					return;
				}

				BeginBoneDrag( head, headRot, bone.Length );
				_boneDragging = true;

				// Position is PER-FRAME DELTA — accumulate.
				_moveDelta += delta;

				ApplyBoneTransform( _selectedBoneIndex, _dragStartPos + _moveDelta, _dragStartRot, _dragStartLength );
				break;
			}

			case BoneDragMode.Scale:
			{
				// Scale adjusts bone length via a vertical drag.
				if ( !Gizmo.Control.Position( $"bone{_selectedBoneIndex}-scl", Vector3.Zero, out var delta, Rotation.Identity ) )
				{
					EndBoneDragIfReleased();
					return;
				}

				BeginBoneDrag( head, headRot, bone.Length );
				_boneDragging = true;

				// Use the vertical (Y) component of the drag as the length change.
				var localDelta = _dragStartRot.Inverse * delta;
				var newLength = MathF.Max( _dragStartLength + localDelta.y, 0.5f );

				ApplyBoneTransform( _selectedBoneIndex, _dragStartPos, _dragStartRot, newLength );
				break;
			}
		}
	}

	private void BeginBoneDrag( Vector3 head, Rotation headRot, float length )
	{
		if ( _boneDragging )
			return;

		_boneDragging = true;
		_dragStartPos = head;
		_dragStartRot = headRot;
		_dragStartLength = length;
		_moveDelta = Vector3.Zero;
	}

	private void EndBoneDragIfReleased()
	{
		if ( Gizmo.IsLeftMouseDown )
			return;

		_boneDragging = false;
	}

	/// <summary>
	/// A literal dog-bone: a knobby ball at each end, joined by a thin shaft. This is the
	/// shape a "bone" reads as at a glance, unlike Blender's tapering-diamond convention
	/// which this replaces.
	/// </summary>
	private static void DrawDogBone( Vector3 head, Vector3 tail, Vector3 xAxis, Vector3 zAxis )
	{
		var boneDir = tail - head;
		var boneLen = boneDir.Length;
		if ( boneLen < 0.01f )
			return;

		var axis = boneDir / boneLen;

		// The knobs are wider than the shaft — that contrast is what makes the shape read
		// as a bone rather than a dumbbell bar. Inset the shaft so it disappears inside the
		// knobs rather than poking out past them.
		var knobR = boneLen * DogBoneKnobScale;
		var shaftR = knobR * 0.35f;
		var inset = knobR * 0.6f;

		Gizmo.Draw.SolidSphere( head, knobR, 8, 8 );
		Gizmo.Draw.SolidSphere( tail, knobR, 8, 8 );

		var shaftStart = head + axis * inset;
		var shaftEnd = tail - axis * inset;

		if ( (shaftEnd - shaftStart).Length > 0.01f )
			DrawShaft( shaftStart, shaftEnd, xAxis, zAxis, shaftR );
	}

	/// <summary>A thin cylinder between two points, wound both ways per face so it reads
	/// solid from either side — same trick the old diamond body used.</summary>
	private static void DrawShaft( Vector3 a, Vector3 b, Vector3 xAxis, Vector3 zAxis, float radius, int segments = 8 )
	{
		for ( var i = 0; i < segments; i++ )
		{
			var t0 = i / (float)segments * MathF.Tau;
			var t1 = (i + 1) / (float)segments * MathF.Tau;

			var o0 = xAxis * (MathF.Cos( t0 ) * radius) + zAxis * (MathF.Sin( t0 ) * radius);
			var o1 = xAxis * (MathF.Cos( t1 ) * radius) + zAxis * (MathF.Sin( t1 ) * radius);

			var a0 = a + o0;
			var a1 = a + o1;
			var b0 = b + o0;
			var b1 = b + o1;

			Gizmo.Draw.SolidTriangle( a0, b0, a1 );
			Gizmo.Draw.SolidTriangle( a1, b0, b1 );

			Gizmo.Draw.SolidTriangle( a0, a1, b0 );
			Gizmo.Draw.SolidTriangle( a1, b1, b0 );
		}
	}

	/// <summary>Extract an s&amp;box Rotation from an Effigy Xform's basis columns.
	/// Xform.Y is bone forward (+Y convention), Xform.Z is bone up.</summary>
	private static Rotation ExtractRotation( Xform xform )
	{
		var forward = new Vector3( xform.Y.x, xform.Y.y, xform.Y.z );
		var up = new Vector3( xform.Z.x, xform.Z.y, xform.Z.z );
		return Rotation.LookAt( forward, up );
	}

	/// <summary>
	/// Write a world-space pose back into the skeleton. Updates position, orientation, and
	/// optionally length of the bone, converting back to parent-local space. Children follow
	/// automatically because their Local transforms are relative.
	/// </summary>
	private void ApplyBoneTransform( int index, Vector3 newHeadWorld, Rotation newWorldRot, float newLength )
	{
		var bone = RigSkeleton.Bones[index];

		// Decompose the new world rotation into the Xform basis columns.
		var fwd = newWorldRot.Forward;
		var right = newWorldRot.Right;
		var up = newWorldRot.Up;

		var newX = new Vec3( right.x, right.y, right.z );
		var newY = new Vec3( fwd.x, fwd.y, fwd.z );
		var newZ = new Vec3( up.x, up.y, up.z );
		var newOrigin = new Vec3( newHeadWorld.x, newHeadWorld.y, newHeadWorld.z );

		if ( bone.Parent < 0 )
		{
			bone.Local = new Xform( newX, newY, newZ, newOrigin );
		}
		else
		{
			// WorldBind here, NOT BoneWorld, and this is the one place the difference matters. This
			// converts a world transform back into a parent-relative BIND transform to store on the
			// bone. Measuring it against a parent that is currently swinging would bake the wobble
			// into the bind pose itself - drag a bone while previewing and the rig would slowly
			// drift into whatever shape the springs happened to be in.
			var parentWorld = RigSkeleton.WorldBind( bone.Parent );
			var inv = parentWorld.Inverse;
			bone.Local = new Xform(
				inv.TransformDirection( newX ),
				inv.TransformDirection( newY ),
				inv.TransformDirection( newZ ),
				inv.TransformPoint( newOrigin ) );
		}

		bone.Length = newLength;

		// Fires every frame of a drag, not just on release — a numeric inspector reading these
		// same bones live is the reason: without this it goes stale the instant a drag starts and
		// stays wrong until the bone is reselected, which is worse than not showing numbers at all.
		BonePosed?.Invoke( index );
	}

	/// <summary>Raised whenever the pose gizmo writes a new transform into a bone — see
	/// ApplyBoneTransform. Carries the bone's index so a listener only watching one bone (an
	/// inspector panel, say) can ignore edits to any other.</summary>
	public Action<int> BonePosed { get; set; }

	/// <summary>Deselect the bone — called from the rig panel or Escape key.</summary>
	public void DeselectBone()
	{
		if ( _selectedBoneIndex < 0 )
			return;

		_selectedBoneIndex = -1;
		_boneDragging = false;
		BoneSelectionChanged?.Invoke( -1 );

		// Same reason as SelectBone's: on demand, so a deselection nobody repaints stays on screen.
		Update();
	}

	/// <summary>
	/// Select a bone by index — called from the rig panel's tree view. Does not invoke the
	/// BoneSelectionChanged callback, to avoid feedback loops.
	///
	/// THE Update() IS THE POINT, not an afterthought. Without it this wrote a field and stopped:
	/// the viewport is repainted on demand rather than continuously, so clicking a bone in the tree
	/// changed nothing on screen - no yellow bone, no pose gizmo - until some unrelated thing
	/// happened to ask for a frame, usually the next mouse move over the canvas. The selection had
	/// in fact worked every time; there was simply nothing to look at that said so.
	/// </summary>
	public void SelectBone( int index )
	{
		_selectedBoneIndex = index >= 0 && index < RigSkeleton?.Count ? index : -1;

		Update();
	}

	/// <summary>Escape backs out of the half-drawn entity, then out of the tool - the same two
	/// stages every CAD sketcher uses. W/E/R switch bone drag modes when a bone is selected.</summary>
	protected override void OnKeyPress( KeyEvent e )
	{
		// A dimension box up on screen owns the keyboard first - digits, Enter and its own Escape.
		// It has to come before the Escape branch below or dismissing the number would also back
		// out of the tool you are drawing with.
		if ( HandleDimensionKey( e ) )
			return;

		// Sculpting owns X and M while it is running, and owns nothing at all when it is not.
		if ( HandleSculptKey( e ) )
			return;

		// Painting owns X while it is running, and nothing when it is not. The same letter as sculpt —
		// one brush tool should not need a different key for symmetry than the other.
		if ( HandlePaintKey( e ) )
			return;

		// The pen owns E and H while it is armed, and nothing when it is not. Same shape as the
		// line above, and it has to sit above the bone shortcuts for the same reason that one does:
		// a mode you are actively in gets first refusal on a letter.
		if ( HandleNoteKey( e ) )
			return;

		// The spline is the one tool with no fixed number of clicks, so Enter is how it ends. After
		// the dimension box, which owns Enter whenever it is up.
		if ( HandleSketchToolKey( e ) )
			return;

		// W/E/R switch bone drag mode while a bone is selected.
		if ( _selectedBoneIndex >= 0 )
		{
			switch ( e.Key )
			{
				case KeyCode.W:
					_boneDragMode = BoneDragMode.Move;
					e.Accepted = true;
					return;
				case KeyCode.E:
					_boneDragMode = BoneDragMode.Rotate;
					e.Accepted = true;
					return;
				case KeyCode.R:
					_boneDragMode = BoneDragMode.Scale;
					e.Accepted = true;
					return;
			}
		}

		if ( LightSelected && (e.Key == KeyCode.Delete || e.Key == KeyCode.Backspace) )
		{
			RemoveSelectedLight();
			e.Accepted = true;
			return;
		}

		if ( e.Key != KeyCode.Escape )
		{
			base.OnKeyPress( e );
			return;
		}

		if ( IsSketching )
		{
			// A selection is the shallower thing to back out of, so Escape drops it first and only
			// cancels the tool on a second press. Otherwise picking three things and hitting Escape
			// to undo the third would abandon the tool as well.
			if ( HasSketchSelection )
				ClearSketchSelection();
			else
				CancelSketchTool();

			e.Accepted = true;
			return;
		}

		// Escape stands down an armed selection box. The viewport owns the key press; the dialog
		// owns the boxes' painted state, so it is told through PickModeCancelled. Sketch picking
		// itself stays live while a consumer dialog is open — the dialog turns it off.
		if ( PlanePickMode || SketchPickMode || BodyPickMode || FacePickMode || EdgePickMode )
		{
			PlanePickMode = false;
			BodyPickMode = false;
			FacePickMode = false;
			EdgePickMode = false;
			PickModeCancelled?.Invoke();
			e.Accepted = true;
			return;
		}

		// Same two-stage back-out as the sketch tools: the panel owns which stage it is, since it
		// is the one holding whether a chain is currently open.
		if ( BoneToolActive )
		{
			BoneToolEscape?.Invoke();
			e.Accepted = true;
			return;
		}

		if ( _selectedBoneIndex >= 0 )
		{
			DeselectBone();
			e.Accepted = true;
			return;
		}

		if ( HasIdleSelection )
		{
			ClearIdleSelection();
			e.Accepted = true;
			return;
		}

		if ( LightSelected )
		{
			DeselectLight();
			e.Accepted = true;
			return;
		}

		if ( OriginSelected )
		{
			DeselectOrigin();
			e.Accepted = true;
		}
	}
}
