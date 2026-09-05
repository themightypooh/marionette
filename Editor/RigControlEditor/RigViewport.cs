using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>What dragging a bone does. Held E flips between rotate and move for as long as it's
/// down, so the common case (rotate) needs no modifier and the occasional one is still one key
/// away. Scale is reached through the toolbar only - it's the rare case, and a bone scale that
/// isn't deliberate is a good way to ruin a rig.</summary>
internal enum BoneDragMode
{
	Rotate,
	Move,
	Scale
}

/// <summary>
/// The 3D panel - a skinned model in its own editor scene, with an FK rotate ring per bone
/// (see the Blue/Yellow circles on the Citizen in the reference screenshot) and a move handle
/// on whichever bone is selected.
///
/// Posing goes through MovieBoneAnimatorSystem.SetParentSpaceBone - the same entry point
/// MovieMaker's own bone dragging uses (Session/InverseKinematics.cs) to actually deform a
/// SkinnedModelRenderer's mesh. This was ruled out early on because the type isn't referenceable
/// without an explicit assembly reference - MovieMaker compiles to its own package assembly
/// (package.local.moviemaker.dll), which midnight_am.sbproj now lists under
/// Metadata.Compiler.AssemblyReferences. Without this, "posing" only ever moved a detached
/// GameObject (CreateBoneObjects, or GetBoneObject's returned proxy) that never fed back into
/// the render - confirmed directly: s&amp;box's own docs call CreateBoneObjects legacy, saying it
/// only ever worked as an intermediate step MovieMaker's *own* internal baking read from, never
/// as a live "move this and the mesh follows" control on its own.
/// </summary>
internal sealed class RigViewport : Widget
{
	private readonly SceneRenderingWidget _canvas;
	private readonly CameraComponent _camera;
	private readonly Gizmo.Instance _gizmoInstance;

	private GameObject _modelObject;
	private SkinnedModelRenderer _renderer;
	private float _boneHandleRadius = 1f;

	public Action<string> BoneSelected { get; set; }

	/// <summary>The bone dot / hitbox scale the user set, or 1 for the model-derived default.
	/// Persisted in an EditorCookie so it survives the window - the right handle size is a property
	/// of you and your rig, not of the clip you happen to have open.</summary>
	public float BoneHandleScale
	{
		get => _boneHandleScale;
		set
		{
			_boneHandleScale = value;
			EditorCookie.Set( "marionette.bonehandle.scale", value );
		}
	}

	private float _boneHandleScale = 1f;

	/// <summary>The per-bone dot and hitbox radius, scaled by the user's handle-size setting.</summary>
	private float HandleRadius => _boneHandleRadius * BoneHandleScale;

	/// <summary>The control rig whose constraints apply while posing. Null poses plain FK.</summary>
	public RigDocument Rig { get; set; }

	/// <summary>What dragging a bone's dot does.
	///
	/// Rotate is the default because skeletal animation is rotation - joints pivot, they don't
	/// slide. Translating a bone stretches the skin and pulls the joint off its parent, which is
	/// the single most common way a first pose ends up looking broken. Move is still here because
	/// it's genuinely needed for root/IK-target bones, but it's the exception.</summary>
	public BoneDragMode DragMode { get; set; } = BoneDragMode.Rotate;

	/// <summary>Fired every frame a bone's pose actually changes from dragging - the current
	/// frame's keyframe is written on every call rather than once at drag-end, since
	/// BoneTrack.SetKeyframe overwriting the same frame repeatedly is harmless and there's no
	/// confirmed "drag finished" signal from Gizmo.Control itself to hook instead.</summary>
	public Action<string, Transform> BonePosed { get; set; }

	/// <summary>Fired once when a bone drag begins, before anything has moved, and once when it
	/// ends. Undo hangs off these: BonePosed fires every frame of a drag, so recording there would
	/// bury the stack under hundreds of one-pixel entries. One drag is one undo step.</summary>
	public Action<string> BoneDragStarted { get; set; }

	public Action BoneDragEnded { get; set; }

	private bool _suppressAutoKey;

	// VIEWMODEL MODE
	//
	// First-person arms are not posed in world space - in game they hang off the camera, so the
	// only framing that tells you whether a pose reads is the one the player actually gets. These
	// defaults mirror ViewArmsComponent, which parents the arms to the camera object and sets
	// LocalPosition = (0,0,-8): the camera sits at the origin looking forward and the model hangs
	// below it. They are the numbers to tune, not laws - the correct offset depends on the FOV and
	// the model, which is exactly why they're editable while you watch.

	// THE FOUR VIEW MODES SET THEIR OWN CHECKBOXES. Each one writes through a backing field and
	// calls RefreshViewToggles, rather than the strip being synced by whoever happened to change
	// the value. Modes that alter what you can see and click - MoveWholeModel suppresses every
	// bone handle - are only safe if their state is impossible to miss, and a display that some
	// call sites remember to update is a display that will eventually lie.

	/// <summary>Frames the model as a viewmodel hanging off the camera instead of as a world prop.</summary>
	public bool ViewmodelMode
	{
		get => _viewmodelMode;
		set
		{
			_viewmodelMode = value;

			// The camera lock is meaningless with nothing to frame, so it can't outlive the mode
			// it belongs to - otherwise the view stays pinned with no visible reason why.
			if ( !value )
				_lockCameraToView = false;

			RefreshViewToggles();
		}
	}

	/// <summary>Pins the camera to the player's eye instead of free-flying it. Only meaningful in
	/// viewmodel mode, and it takes the mouse away from camera control - which is the point.</summary>
	public bool LockCameraToView
	{
		get => _lockCameraToView;
		set { _lockCameraToView = value; RefreshViewToggles(); }
	}

	private bool _viewmodelMode;
	private bool _lockCameraToView;
	private bool _showBoneHandles = true;
	private bool _moveWholeModel;

	/// <summary>Zero by default. The model places itself - its camera bone says where the eye
	/// goes - so nothing needs shifting unless you deliberately want the arms sitting off from
	/// where the rig thinks they should.</summary>
	public Vector3 ViewmodelOffset { get; set; }
	public Angles ViewmodelRotation { get; set; }
	public float ViewmodelFov { get; set; } = 90f;

	// What ApplyViewmodelFraming last wrote, so it can skip writing an identical value.
	private Vector3 _appliedOffset = new( float.NaN, float.NaN, float.NaN );
	private Angles _appliedRotation = new( float.NaN, float.NaN, float.NaN );

	/// <summary>Hides the per-bone dots. A first-person rig puts a hundred handles between you and
	/// the two you're actually moving; this is the way out of that.</summary>
	public bool ShowBoneHandles
	{
		get => _showBoneHandles;
		set { _showBoneHandles = value; RefreshViewToggles(); }
	}

	/// <summary>One gizmo on the model root instead of a handle per bone - for placing the whole
	/// model, which in viewmodel mode is how you set where the arms sit relative to the eye.</summary>
	public bool MoveWholeModel
	{
		get => _moveWholeModel;
		set { _moveWholeModel = value; RefreshViewToggles(); }
	}

	public Action ViewmodelChanged { get; set; }

	/// <summary>Reveals hidden bones without unhiding them - the way back when you've hidden
	/// something you now need.</summary>
	public bool ShowHiddenBones { get; set; }

	private string _hoveredBone;

	public bool IsHidden( string bone ) => Rig is not null && Rig.IsBoneHidden( bone );

	/// <summary>
	/// Whether twist bones get a handle at all. Off by default.
	///
	/// TWIST BONES SIT EXACTLY ON THE JOINT THEY TWIST, so their dot lands on top of - or a pixel
	/// under - the dot for the bone you actually want. Clicking the shoulder gets you
	/// arm_upper_R_twist about half the time, and because a twist bone rotates the mesh in a way
	/// that looks vaguely like what you asked for, you don't necessarily notice you're posing the
	/// wrong thing until the pose is wrong in a way you can't undo by eye.
	///
	/// Nothing here is animation data: this only controls whether a handle is drawn, exactly like
	/// bone hiding. Twist bones are still driven by EvaluatePose and their keyframes still play.
	/// </summary>
	public bool ShowTwistBones { get; set; }

	/// <summary>
	/// Twist and roll helpers, matched by name.
	///
	/// Name matching rather than skeleton analysis, because the thing that actually identifies a
	/// twist bone - that it shares a position with its parent and only ever rotates about one axis
	/// - is not reliably detectable from a bind pose, and every rig that has them names them. The
	/// list is deliberately short and additive: a bone wrongly classed as a twist is still fully
	/// posable, it just needs the checkbox turned on, which is a far cheaper failure than a real
	/// twist bone going unrecognised and continuing to steal clicks.
	/// </summary>
	public static bool IsTwistBone( string bone ) =>
		!string.IsNullOrEmpty( bone )
		&& (bone.Contains( "twist", StringComparison.OrdinalIgnoreCase )
			|| bone.Contains( "roll", StringComparison.OrdinalIgnoreCase )
			|| bone.Contains( "helper", StringComparison.OrdinalIgnoreCase ));

	/// <summary>Right-click a bone dot to hide it. Hiding a whole chain in one go matters more
	/// than it sounds: the useless bones on a rig are almost always a whole subtree - a weapon
	/// root and everything parented under it - and hiding thirty of them one at a time is enough
	/// friction that nobody bothers.</summary>
	protected override void OnContextMenu( ContextMenuEvent e )
	{
		// RIGHT-DRAG IS THE LOOK-AROUND CONTROL, so right-click in empty space must do nothing.
		//
		// The menu used to open anywhere in the viewport, falling back to the selected bone when
		// the cursor wasn't over one. That made the camera unusable: every attempt to look around
		// popped a menu over the thing you were trying to look at.
		//
		// Requiring the cursor to be ON a dot also makes the menu mean something more specific -
		// it acts on what you pointed at, not on whatever happened to be selected somewhere else
		// on screen.
		if ( _hoveredBone is not { } target )
			return;

		if ( Rig is null )
		{
			RigStatusBar.Show( "Hiding bones needs a Control Rig - set one in the BonesObject tab first" );
			return;
		}

		var menu = new Menu( this );

		menu.AddHeading( target );

		if ( IsHidden( target ) )
		{
			menu.AddOption( "Unhide", "visibility", () => SetHidden( target, false, false ) );
		}
		else
		{
			menu.AddOption( "Hide Bone", "visibility_off", () => SetHidden( target, true, false ) );
			menu.AddOption( "Hide Bone And Children", "layers_clear", () => SetHidden( target, true, true ) )
				.StatusTip = "Hides this bone and everything parented under it";
		}

		menu.AddSeparator();

		var showHidden = menu.AddOption( "Show Hidden Bones", "visibility", () =>
		{
			ShowHiddenBones = !ShowHiddenBones;
			ViewmodelChanged?.Invoke();
		} );

		showHidden.Checkable = true;
		showHidden.Checked = ShowHiddenBones;

		var unhideAll = menu.AddOption( "Unhide All", "restart_alt", () =>
		{
			Rig.HiddenBones.Clear();
			BoneVisibilityChanged?.Invoke();
		} );

		unhideAll.Enabled = Rig.HiddenBones.Count > 0;
		unhideAll.Text = $"Unhide All ({Rig.HiddenBones.Count})";

		menu.OpenAtCursor();
	}

	public Action BoneVisibilityChanged { get; set; }

	private void SetHidden( string bone, bool hidden, bool includeChildren )
	{
		Apply( bone );

		if ( includeChildren )
		{
			foreach ( var child in DescendantsOf( bone ) )
				Apply( child );
		}

		// Hiding a selected bone would otherwise leave its control floating with nothing under
		// it - the dot is gone but the gizmo isn't. Drop any hidden bones out of the selection.
		if ( hidden && !ShowHiddenBones )
		{
			var removed = _selectedBones.RemoveWhere( IsHidden );

			if ( removed > 0 )
			{
				if ( _selectedBones.Count == 0 )
					_primaryBone = null;
				else if ( _primaryBone is null || !_selectedBones.Contains( _primaryBone ) )
					_primaryBone = _selectedBones.First();

				BoneSelected?.Invoke( SelectedBone );
			}
		}

		BoneVisibilityChanged?.Invoke();

		void Apply( string name )
		{
			if ( hidden )
			{
				if ( !Rig.HiddenBones.Contains( name ) )
					Rig.HiddenBones.Add( name );

				return;
			}

			Rig.HiddenBones.Remove( name );
		}
	}

	/// <summary>Every bone under this one, at any depth.</summary>
	private IEnumerable<string> DescendantsOf( string parent )
	{
		foreach ( var child in ChildBoneNames( parent ).ToList() )
		{
			yield return child;

			foreach ( var deeper in DescendantsOf( child ) )
				yield return deeper;
		}
	}

	/// <summary>Toggled by the rig_debug_drag console command. Off by default; this logs once per
	/// frame of a drag, which is far too noisy to leave on.</summary>
	public static bool DebugDrag { get; private set; }

	[ConCmd( "rig_debug_drag" )]
	public static void SetDebugDrag( int enabled )
	{
		DebugDrag = enabled != 0;
		Log.Info( $"[rigdrag] drag logging {(DebugDrag ? "ON - drag a bone, then paste the log" : "off")}" );
	}

	public RigViewport( Widget parent ) : base( parent )
	{
		_boneHandleScale = EditorCookie.Get( "marionette.bonehandle.scale", 1f );

		MinimumSize = 200;
		Layout = Layout.Column();

		BuildViewToggleBar();

		_canvas = new SceneRenderingWidget( this );
		_canvas.OnPreFrame += OnPreFrame;
		_canvas.FocusMode = FocusMode.Click;
		_canvas.Scene = Scene.CreateEditorScene();

		using ( _canvas.Scene.Push() )
		{
			_camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
			_camera.BackgroundColor = Theme.ControlBackground;
			_camera.ZFar = 4096;
			_camera.Enabled = true;

			var sun = new GameObject( true, "sun" ).GetOrAddComponent<DirectionalLight>( false );
			sun.WorldRotation = Rotation.From( 45, 45, 0 );
			sun.LightColor = Color.White;
			sun.Enabled = true;

			var ambient = new GameObject( true, "ambient" ).GetOrAddComponent<AmbientLight>( false );
			ambient.Color = Theme.ControlBackground * 0.6f;
			ambient.Enabled = true;

			_canvas.Camera = _camera;
		}

		_gizmoInstance = _canvas.GizmoInstance;

		Layout.Add( _canvas, 1 );
	}

	private Checkbox _firstPersonToggle;
	private Checkbox _wholeModelToggle;
	private Checkbox _showBonesToggle;
	private Checkbox _showTwistToggle;
	private Checkbox _armShaderToggle;

	/// <summary>
	/// A permanently visible strip of checkboxes above the viewport.
	///
	/// This used to be a single "videocam" button opening a menu, on the reasoning that these are
	/// set-once-per-session options that shouldn't cost screen space. That reasoning was wrong,
	/// and it was wrong in a specific way worth recording: two of these toggles change what is
	/// VISIBLE AND CLICKABLE in the viewport. Move Whole Model suppresses every bone handle. With
	/// the state hidden inside a menu, the symptom is "all my bones are gone" and the cause is
	/// three clicks away - the tool looks broken rather than configured.
	///
	/// A mode that changes what you can see has to show that it's on, without being asked. The
	/// cost is one row of screen space, which is the correct trade.
	/// </summary>
	private void BuildViewToggleBar()
	{
		var bar = Layout.AddRow();
		bar.Margin = new Sandbox.UI.Margin( 6, 4 );
		bar.Spacing = 8;

		// ONE TOGGLE, NOT TWO. "Using Arms" gated "Lock Camera" behind it, which meant two clicks
		// and a rule to learn for what is really a single question: am I looking through the
		// player's eyes or not. Turning this on does both.
		_firstPersonToggle = new Checkbox( "First Person View" )
		{
			ToolTip = "Look through the player's eye at the game's field of view. You can still look around - Reset Camera returns you to looking straight ahead."
		};

		_firstPersonToggle.Toggled = () => SetViewMode( () =>
		{
			ViewmodelMode = _firstPersonToggle.Value;
			LockCameraToView = _firstPersonToggle.Value;
		} );

		bar.Add( _firstPersonToggle );

		bar.AddSpacingCell( 12 );

		_wholeModelToggle = new Checkbox( "Move Whole Model" )
		{
			ToolTip = "One gizmo on the model root instead of a handle per bone - for placing the arms relative to the eye. Drag to move, hold E to rotate. Hides the bone handles while it's on."
		};
		_wholeModelToggle.Toggled = () => SetViewMode( () => MoveWholeModel = _wholeModelToggle.Value );
		bar.Add( _wholeModelToggle );

		_showBonesToggle = new Checkbox( "Show Bone Handles" )
		{
			ToolTip = "Hide the per-bone dots when the rig is dense enough that they're in the way"
		};
		_showBonesToggle.Toggled = () => SetViewMode( () => ShowBoneHandles = _showBonesToggle.Value );
		bar.Add( _showBonesToggle );

		_showTwistToggle = new Checkbox( "Twist Bones" )
		{
			ToolTip = "Twist bones sit on top of the joint they twist, so their dots overlap the bone you're aiming for. Off by default; they still animate either way."
		};
		_showTwistToggle.Toggled = () => SetViewMode( () => ShowTwistBones = _showTwistToggle.Value );
		bar.Add( _showTwistToggle );

		bar.AddSpacingCell( 12 );

		// The bone dot / handle size, in one place. On a dense rig the dots overlap and every
		// click grabs the wrong bone, so this is a first-class setting rather than a hidden one.
		bar.Add( new Editor.Label( "Handle Size" ) { FixedWidth = 62, ToolTip = "Size of the bone dots and their clickable area" } );

		var sizeSlider = new FloatSlider( this )
		{
			Minimum = 0.25f,
			Maximum = 3f,
			Step = 0.05f,
			Value = BoneHandleScale,
			FixedWidth = 110,
			ToolTip = "Size of the bone dots and their clickable area. Smaller makes adjacent bones easier to pick apart."
		};

		sizeSlider.OnValueEdited = () => BoneHandleScale = sizeSlider.Value;
		bar.Add( sizeSlider );

		bar.AddSpacingCell( 12 );

		_armShaderToggle = new Checkbox( "Arm Shader" )
		{
			ToolTip = "Draw the model in the first-person arms material. On automatically for viewmodel arms, off for everything else - turn it off if it's painting a model that isn't arms."
		};
		_armShaderToggle.Toggled = () => SetViewMode( () => PixelStyle = _armShaderToggle.Value );
		bar.Add( _armShaderToggle );

		bar.AddStretchCell();

		// In first person this straightens your head rather than dropping you out of the mode -
		// otherwise the only way back from having looked around would be to leave and re-enter,
		// which loses the framing you were judging.
		bar.Add( new Button( "Reset Camera", "restart_alt" )
		{
			Clicked = () => SetViewMode( () =>
			{
				if ( LockCameraToView )
				{
					_camera.WorldRotation = Rotation.Identity;
					return;
				}

				FrameCamera();
			} ),
			ToolTip = "First person: look straight ahead again. Otherwise: frame the whole model."
		} );

		RefreshViewToggles();
	}

	/// <summary>Applies a view-mode change and tells the window about it. The property setters
	/// handle putting the checkboxes right; this is only about the one notification that used to
	/// be hand-written after every toggle.</summary>
	private void SetViewMode( Action change )
	{
		change();
		ViewmodelChanged?.Invoke();
	}

	/// <summary>Guards against the refresh that a refresh causes: writing Value fires Toggled on
	/// some widget versions, which would write the property, which would refresh again.</summary>
	private bool _refreshingToggles;

	/// <summary>Pulls every checkbox back into line with the real state. Called from the property
	/// setters, so it covers changes that didn't come from clicking the strip - loading a document,
	/// Reset Camera, or Using Arms switching the camera lock off underneath you.</summary>
	private void RefreshViewToggles()
	{
		if ( _refreshingToggles || !_firstPersonToggle.IsValid() )
			return;

		_refreshingToggles = true;

		try
		{
			_firstPersonToggle.Value = _viewmodelMode;
			_wholeModelToggle.Value = _moveWholeModel;
			_showBonesToggle.Value = _showBoneHandles;
			_showTwistToggle.Value = ShowTwistBones;

			// Nothing to add twist dots to when there are no dots at all.
			_showTwistToggle.Enabled = _showBoneHandles && !_moveWholeModel;

			_armShaderToggle.Value = PixelStyle;

			// Says why the dots are gone at the moment they go, rather than leaving you to work
			// out which of the two toggles did it.
			_showBonesToggle.Enabled = !_moveWholeModel;
			_showBonesToggle.ToolTip = _moveWholeModel
				? "Hidden while Move Whole Model is on - there's one gizmo on the model root instead"
				: "Hide the per-bone dots when the rig is dense enough that they're in the way";
		}
		finally
		{
			_refreshingToggles = false;
		}
	}

	/// <summary>Puts the model where the game puts it: hanging off a camera at the origin. Applied
	/// every frame rather than once, so dragging the root or editing the offset shows immediately.</summary>
	/// <summary>
	/// Puts the camera back on the eye after free-fly has had its turn, leaving the rotation
	/// free-fly gave it.
	///
	/// The eye is the model's own `camera` bone where it has one - a viewmodel carries that bone
	/// precisely to say where the player's head goes, so reading it beats any offset we could
	/// invent. Falls back to the model's origin.
	/// </summary>
	private void PinEyePosition()
	{
		if ( !LockCameraToView || !_camera.IsValid() )
			return;

		_camera.WorldPosition = EyePosition();
		_camera.FieldOfView = ViewmodelFov;
	}

	/// <summary>
	/// THE EYE COMES FROM THE MODEL'S OWN CAMERA BONE, not from a number.
	///
	/// Viewmodels carry a bone marking where the player's head goes - first_person_arms_preview
	/// has one called "camera", at the model's origin. Reading it means the framing is right for
	/// any viewmodel, including ones whose eye isn't at their origin, with nothing to tune.
	///
	/// This replaced parking the camera at the world origin and shoving the model down 8 units, a
	/// figure lifted from ViewArmsComponent - where it means something quite different, because
	/// there the arms hang off the camera rather than the camera being placed against the arms.
	/// The result was an eye 8 units above the real viewpoint with the arms below the bottom of
	/// frame, which reads as the lock doing nothing at all.
	/// </summary>
	private Vector3 EyePosition()
	{
		if ( FindBoneData( "camera" ) is { } eye && _renderer.TryGetBoneTransform( eye, out var eyeWorld ) )
			return eyeWorld.Position;

		return _modelObject.IsValid() ? _modelObject.WorldPosition : Vector3.Zero;
	}

	private void ApplyViewmodelFraming()
	{
		if ( !ViewmodelMode )
			return;

		// ONLY WRITTEN WHEN IT ACTUALLY CHANGES. Assigning the renderer's transform every frame
		// re-drives the whole model, which is a plausible way to lose the bone overrides posing
		// depends on - and is pointless work regardless, since the value is usually identical to
		// what's already there.
		// Compared against what WE last applied, rather than against the object's current
		// transform - reading a rotation back and comparing it needs an equality test on
		// quaternions, and Angles is a plain struct that compares exactly.
		if ( _modelObject.IsValid() && (_appliedOffset != ViewmodelOffset || _appliedRotation != ViewmodelRotation) )
		{
			_modelObject.WorldPosition = ViewmodelOffset;
			_modelObject.WorldRotation = ViewmodelRotation.ToRotation();

			_appliedOffset = ViewmodelOffset;
			_appliedRotation = ViewmodelRotation;
		}

		// The camera itself is placed in PinEyePosition, which runs AFTER free-fly rather than
		// before it - writing the camera here as well would overwrite the rotation the moment you
		// tried to look around, and nothing on screen would explain why the mouse did nothing.
	}

	public void SetModel( Model model )
	{
		using var scope = _canvas.Scene.Push();

		if ( _renderer.IsValid() )
		{
			_renderer.Enabled = false;
			_renderer.Model = null;
		}

		_modelObject?.Destroy();
		_modelObject = null;
		_renderer = null;
		_pixelMaterial = null;
		Select( null );

		if ( model is null )
			return;

		_modelObject = new GameObject( true, "rig" );
		_renderer = _modelObject.GetOrAddComponent<SkinnedModelRenderer>( false );

		_renderer.Model = model;
		_renderer.UseAnimGraph = false;
		_renderer.Enabled = true;

		_boneHandleRadius = (model.Bounds.Size.Length * 0.012f).Clamp( 0.15f, 3f );

		// THE ARM SHADER IS FOR ARMS. It used to default on for every model, which meant loading
		// anything else - a Halo grunt, say - drew that model in the viewmodel arms' skin. It
		// looked like the tool had corrupted the model.
		//
		// It exists to work around ONE model: the preview arms resolve to materials/dev/gray_25,
		// a placeholder, so without an override they render as a white mannequin that reads as a
		// missing material. Every other model already has materials worth showing, so the correct
		// default for them is off.
		//
		// Guessed from the path rather than asked, because being asked on every load is worse than
		// being wrong occasionally - and the checkbox in the toolbar makes being wrong a one-click
		// problem that you can see.
		PixelStyle = LooksLikeViewmodelArms( model );

		RefreshViewToggles();

		FrameCamera();
	}

	/// <summary>Whether a model is first-person arms, by resource path. A guess, deliberately -
	/// there is no flag on a model saying "I am a viewmodel", and the alternative (inspecting
	/// materials for the dev placeholder) is both slower and no more certain.</summary>
	private static bool LooksLikeViewmodelArms( Model model ) =>
		model?.ResourcePath is { } path
		&& (path.Contains( "first_person", StringComparison.OrdinalIgnoreCase )
			|| path.Contains( "viewmodel", StringComparison.OrdinalIgnoreCase ));

	public SkinnedModelRenderer Renderer => _renderer;

	/// <summary>Every selected bone. A plain replace leaves just <paramref name="bone"/>; an
	/// additive select toggles it in or out, which is how Shift-click builds up a finger-full of
	/// bones to pose together.</summary>
	private readonly HashSet<string> _selectedBones = new();

	/// <summary>The bone the Inspector / Timeline / tutorial read - the most recently touched one
	/// still in the selection, or null when nothing is selected.</summary>
	private string _primaryBone;

	public string SelectedBone => _selectedBones.Count > 0 ? _primaryBone : null;

	public IReadOnlyCollection<string> SelectedBones => _selectedBones;

	public void Select( string bone, bool additive = false )
	{
		if ( additive && bone is not null )
		{
			// Toggle: Shift-clicking a selected bone takes it back out of the set.
			if ( !_selectedBones.Remove( bone ) )
				_selectedBones.Add( bone );
		}
		else
		{
			_selectedBones.Clear();

			if ( bone is not null )
				_selectedBones.Add( bone );
		}

		// Selecting a bone drops any prop selection, so there is never more than one gizmo on
		// screen competing for the same drag. Guarded on null so clearing the bone selection -
		// which is what selecting a PROP does - can't immediately clear the prop again.
		if ( bone is not null )
			SelectedReferenceProp = -1;

		if ( bone is not null && _selectedBones.Contains( bone ) )
			_primaryBone = bone;

		if ( _selectedBones.Count == 0 )
			_primaryBone = null;
		else if ( _primaryBone is null || !_selectedBones.Contains( _primaryBone ) )
			_primaryBone = _selectedBones.First();

		BoneSelected?.Invoke( SelectedBone );
	}

	private void FrameCamera()
	{
		if ( !_renderer.IsValid() || _renderer.Model is null )
			return;

		// Loading a model while the camera is pinned to the eye must not yank it away - the lock
		// is the more specific intent, so it wins.
		if ( LockCameraToView )
			return;

		var bounds = _renderer.Model.Bounds;
		var distance = MathX.SphereCameraDistance( MathF.Max( bounds.Size.Length * 0.6f, 32f ), _camera.FieldOfView );

		_camera.WorldPosition = bounds.Center + new Vector3( -1, -1, 0.5f ).Normal * distance;
		_camera.WorldRotation = Rotation.LookAt( bounds.Center - _camera.WorldPosition, Vector3.Up );
	}

	/// <summary>Every bone in the model's own skeleton, with its current world transform - read
	/// via TryGetBoneTransform, not a GameObject. No GameObject proxy is involved anywhere in this
	/// file anymore; posing writes go straight through MovieBoneAnimatorSystem instead.</summary>
	private IEnumerable<(BoneCollection.Bone Bone, Transform World)> LiveBones()
	{
		if ( !_renderer.IsValid() || _renderer.Model?.Bones is not { } bones )
			yield break;

		foreach ( var bone in bones.AllBones )
		{
			if ( _renderer.TryGetBoneTransform( bone, out var world ) )
				yield return (bone, world);
		}
	}

	public IEnumerable<string> BoneNames() => LiveBones().Select( x => x.Bone.Name );

	/// <summary>Bones with no parent in the skeleton itself - the citizen_human rig has more than
	/// one of these (pelvis, and a separate root_IK utility chain), so this is a list.</summary>
	public IEnumerable<string> RootBoneNames() =>
		LiveBones().Where( x => x.Bone.Parent is null ).Select( x => x.Bone.Name );

	public IEnumerable<string> ChildBoneNames( string parentName ) =>
		LiveBones().Where( x => x.Bone.Parent?.Name == parentName ).Select( x => x.Bone.Name );

	private BoneCollection.Bone FindBoneData( string name ) =>
		_renderer.IsValid() && _renderer.Model?.Bones is { } bones ? bones.GetBone( name ) : null;

	public bool TryGetWorldTransform( string name, out Transform world )
	{
		world = default;

		if ( !_renderer.IsValid() || FindBoneData( name ) is not { } bone )
			return false;

		return _renderer.TryGetBoneTransform( bone, out world );
	}

	/// <summary>A bone's current pose in parent space - the form keyframes are stored in. This is
	/// what "key this bone where it is right now" needs, independent of any drag.</summary>
	public bool TryGetLocalTransform( string name, out Transform local )
	{
		local = default;

		if ( !_renderer.IsValid() || FindBoneData( name ) is not { } bone )
			return false;

		if ( !_renderer.TryGetBoneTransform( bone, out var world ) )
			return false;

		local = ParentWorld( bone ).ToLocal( world );
		return true;
	}

	/// <summary>
	/// Pose a bone by parent-space value rather than by dragging it - what the inspector's number
	/// fields write through.
	///
	/// Deliberately the same tail as a gizmo drag: clamp to limits, write, carry the descendants,
	/// then announce it. Typing 40 into a field and dragging the ring to 40 have to produce the
	/// same result, and the only way to be sure of that is for them to run the same code. An
	/// earlier plan had this write the keyframe directly and skip the viewport, which would have
	/// been a second posing path to keep in step with the first.
	/// </summary>
	public void SetLocalTransform( string name, Transform local )
	{
		if ( !_renderer.IsValid() || FindBoneData( name ) is not { } bone )
			return;

		var world = ApplyLimits( bone, ParentWorld( bone ).ToWorld( local ) );

		ApplyWorldTransform( bone, world );
		PropagateToDescendants( bone, world );
		NotifyPosed( bone, world );
	}

	/// <summary>Write a bone's new world-space transform.
	///
	/// SetBoneTransform is the right call and it does work - verified headlessly (rig_test_pose):
	/// it registers a bone override that is still in place after a scene tick. What it does NOT do
	/// is take effect immediately - reading the bone back in the same frame you wrote it still
	/// returns the old value, because overrides are only folded into the pose during the scene's
	/// bone update. That one-frame lag is expected here, not a bug: the drag re-reads the bone
	/// fresh each frame, so it picks up the previous frame's write.
	///
	/// SceneModel.SetBoneWorldTransform is the trap to avoid - it's the one that reads back
	/// instantly, which makes it look correct, but it sets no override and the very next tick
	/// stomps it back to the bind pose.</summary>
	private void ApplyWorldTransform( BoneCollection.Bone bone, Transform world )
	{
		_renderer.SetBoneTransform( bone, world );
	}

	/// <summary>
	/// The bone's pose with no animation on it, read straight off the model.
	///
	/// This is the "no keyframe" value - what a bone looks like when the clip says nothing about
	/// it - and it comes from BoneCollection.Bone.LocalTransform, which is model data and cannot
	/// be affected by anything the renderer is currently showing.
	///
	/// It used to be snapshotted from the live renderer on the first frame after a model loaded,
	/// and that was subtly, badly wrong: opening a clip that already HAS keyframes applies them
	/// before that first frame, so the snapshot captured the posed arm and called it the rest
	/// pose. Undo then restored to a "default" with the forearm already bent, because that WAS
	/// the recorded default. Reading the model has no such timing to get wrong.
	///
	/// BONE.LOCALTRANSFORM IS MODEL-SPACE, NOT PARENT-SPACE, despite the name. It has to be
	/// converted, and this is the conversion. Returning it raw exploded the mesh into spikes the
	/// moment anything was dragged: every caller feeds this to parentWorld.ToWorld(), so each
	/// bone had its entire model-space offset stacked on top of its parent's world transform, and
	/// the error multiplied with depth - a shoulder barely moved, the fingers ended up in orbit.
	///
	/// Verified against the two places the engine reads it, which both convert exactly this way
	/// and only call the RESULT a bind pose:
	///
	///   MovieMaker UpgradeProceduralBoneTracks:  parentBindPose.ToLocal( bone.LocalTransform )
	///   MovieMaker InverseKinematics:            bone.Parent.LocalTransform.ToLocal( bone.LocalTransform )
	///
	/// The property name was found by reflection-dumping the type, which confirms a name exists
	/// and says nothing about what space it's in. That gap is what this cost.
	/// </summary>
	private static Transform BindPoseFor( BoneCollection.Bone bone ) =>
		bone.Parent is { } parent
			? parent.LocalTransform.ToLocal( bone.LocalTransform )
			: bone.LocalTransform;

	/// <summary>A bone's parent's world transform, falling back to the model's own for roots.</summary>
	private Transform ParentWorld( BoneCollection.Bone bone ) =>
		bone.Parent is { } parent && _renderer.TryGetBoneTransform( parent, out var parentTx )
			? parentTx
			: _renderer.WorldTransform;

	/// <summary>Run a world-space pose through any Limit constraints on that bone. Limits are
	/// authored in parent space (that's the only space a joint angle means anything in), so this
	/// converts down, clamps, and converts back.</summary>
	private Transform ApplyLimits( BoneCollection.Bone bone, Transform world )
	{
		if ( Rig is null )
			return world;

		var parentWorld = ParentWorld( bone );
		var local = RigConstraintSolver.ClampToLimits( Rig, bone.Name, parentWorld.ToLocal( world ) );

		return parentWorld.ToWorld( local );
	}

	/// <summary>Push a keyed pose onto every bone that has one for this frame, skipping whichever
	/// bone is being actively dragged so playback can't fight the hand doing the dragging.
	/// _draggingBone (set inside DrawSelectedBoneControls, the only place it's safe to read
	/// Gizmo.IsLeftMouseDown) stands in for the raw Gizmo property - EvaluatePose is called from
	/// RigControlWindow.OnScrub, outside any active Gizmo context, and Gizmo.IsLeftMouseDown
	/// throws a NullReferenceException unconditionally when read from there. That exception fired
	/// on every single scrub and every playback tick, silently, the entire time.</summary>
	private readonly List<GameObject> _referenceObjects = new();
	private List<ReferenceProp> _referenceProps;

	/// <summary>
	/// Rebuilds the static reference models - the switch, the weapon, whatever the hands are
	/// working against.
	///
	/// Rebuilt wholesale when the list changes and only transform-updated otherwise, because
	/// destroying and respawning a model every frame would thrash the scene for no reason. A prop
	/// following a bone is re-read every frame, since the bone moves.
	/// </summary>
	public void SetReferenceProps( List<ReferenceProp> props )
	{
		_referenceProps = props;

		// Deleting a prop must not leave the gizmo pointing at an index that no longer exists, or
		// at whatever prop happens to have shifted into that slot.
		if ( SelectedReferenceProp >= (props?.Count ?? 0) )
			SelectedReferenceProp = -1;

		_propDragIndex = -1;

		using var scope = _canvas.Scene.Push();

		foreach ( var existing in _referenceObjects )
			existing?.Destroy();

		_referenceObjects.Clear();

		if ( props is null )
			return;

		foreach ( var prop in props )
		{
			var models = prop?.AllModels.ToList();

			if ( models is null || models.Count == 0 )
			{
				// A placeholder keeps indices lined up with the list, so the transform pass can
				// pair them up without re-searching.
				_referenceObjects.Add( null );
				continue;
			}

			var go = new GameObject( true, string.IsNullOrWhiteSpace( prop.Name ) ? "reference" : prop.Name );

			// ONE PARENT, ONE CHILD PER MODEL, rather than a renderer on the parent itself. The
			// parent carries the placement and the children carry the meshes, so a prop made of
			// three models is dragged, followed and hidden as one thing - which is the whole
			// reason for a prop holding more than one model.
			foreach ( var model in models )
			{
				var part = new GameObject( true, model.ResourceName ?? "part" );
				part.Parent = go;

				var renderer = part.GetOrAddComponent<ModelRenderer>( false );

				renderer.Model = model;
				renderer.Enabled = true;
			}

			_referenceObjects.Add( go );
		}
	}

	/// <summary>Placement, every frame, so dragging a number in the panel moves the prop while
	/// you watch rather than on some later refresh.</summary>
	private void ApplyReferenceProps()
	{
		if ( _referenceProps is null )
			return;

		for ( var i = 0; i < _referenceProps.Count && i < _referenceObjects.Count; i++ )
		{
			var prop = _referenceProps[i];
			var go = _referenceObjects[i];

			if ( prop is null || !go.IsValid() )
				continue;

			go.Enabled = prop.Visible;

			if ( !prop.Visible )
				continue;

			var local = prop.LocalTransform;

			// Following a bone puts the prop in the hand and keeps it there while the hand moves,
			// which is what you want for a weapon that's already held.
			if ( !string.IsNullOrWhiteSpace( prop.FollowBone ) && TryGetWorldTransform( prop.FollowBone, out var boneWorld ) )
			{
				var followed = boneWorld.ToWorld( local );

				go.WorldPosition = followed.Position;
				go.WorldRotation = followed.Rotation;
				go.WorldScale = followed.Scale;
				continue;
			}

			go.WorldPosition = local.Position;
			go.WorldRotation = local.Rotation;
			go.WorldScale = local.Scale;
		}
	}

	/// <summary>Which reference prop has the gizmo, as an index into the prop list. -1 for none.
	/// An index rather than the object, because the list is rebuilt from the document whenever it
	/// changes and holding a stale instance would keep a deleted prop selected.</summary>
	public int SelectedReferenceProp { get; private set; } = -1;

	/// <summary>Fired every frame a prop is dragged, and once each side of the drag - the same
	/// three-signal shape bone dragging uses, so undo can record one step per drag rather than one
	/// per frame.</summary>
	public Action ReferencePropMoved { get; set; }

	public Action ReferencePropDragStarted { get; set; }

	public Action ReferencePropDragEnded { get; set; }

	private int _propDragIndex = -1;
	private Transform _propDragStart;
	private Vector3 _propMoveDelta;

	/// <summary>
	/// Click a prop to select it, then drag its gizmo - move by default, hold E to rotate, the
	/// same contract as a bone.
	///
	/// Props were placeable only by typing numbers into the panel, which is a poor way to answer
	/// "is the switch within reach", since that question is about where the hand ends up and is
	/// only answerable by looking.
	/// </summary>
	private void DrawReferenceProps()
	{
		if ( _referenceProps is null || MoveWholeModel || !ShowBoneHandles )
			return;

		// The selected prop's control runs AFTER the loop, outside every per-prop scope. Inside
		// one, the scope carries the prop's own rotation, so the control's world-aligned basis
		// wouldn't be world-aligned and its delta would come back in rotated space to be added to
		// a world position - the same mismatch that had bone drags travelling up Z whichever
		// arrow was grabbed.
		(int Index, ReferenceProp Prop, Transform World)? selected = null;

		for ( var i = 0; i < _referenceProps.Count && i < _referenceObjects.Count; i++ )
		{
			var prop = _referenceProps[i];
			var go = _referenceObjects[i];

			if ( prop is null || !go.IsValid() || !prop.Visible )
				continue;

			var isSelected = i == SelectedReferenceProp;
			var world = new Transform( go.WorldPosition, go.WorldRotation );

			using var scope = Gizmo.Scope( $"RefProp{i}", world );

			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = isSelected ? Theme.Yellow : Theme.Green.WithAlpha( 0.7f );
			Gizmo.Draw.SolidSphere( 0f, HandleRadius * (isSelected ? 0.6f : 0.45f), 8, 8 );
			Gizmo.Draw.IgnoreDepth = false;

			if ( isSelected )
			{
				selected = (i, prop, world);
				continue;
			}

			// THE WHOLE PROP IS THE TARGET, not a dot beside it. Clicking the switch to grab the
			// switch is the obvious behaviour, and the dot alone was close to unclickable anyway:
			// _boneHandleRadius is derived from the ARMS model's bounds, so on a first-person rig
			// it's a fraction of a unit - a handle you have to hunt for.
			//
			// Same rule as bones about the selected one: no hitbox of ours on it, or it wins the
			// hover test against the control's own handles and the drag never starts.
			Gizmo.Hitbox.DepthBias = 0.01f;

			if ( prop.Model is { } propModel )
				Gizmo.Hitbox.BBox( propModel.Bounds.Grow( 0.5f ) );
			else
				Gizmo.Hitbox.Sphere( new Sphere( 0f, HandleRadius ) );

			if ( !Gizmo.IsHovered )
				continue;

			RigStatusBar.Show( $"{prop.Name}  -  click to select, then drag to move. Hold E to rotate." );

			if ( Gizmo.WasLeftMousePressed )
			{
				SelectedReferenceProp = i;

				// One gizmo on screen at a time - a bone and a prop both showing handles is two
				// things claiming the same drag.
				Select( null );
			}
		}

		if ( selected is { } sel )
			DragReferenceProp( sel.Index, sel.Prop, sel.World );
	}

	private void DragReferenceProp( int index, ReferenceProp prop, Transform world )
	{
		var dragging = _propDragIndex == index;
		var start = dragging ? _propDragStart : world;

		// Positioned at the prop but NOT rotated with it, so the arrows stay world-aligned like the
		// scene editor's in global space, and the delta comes back in the space it's applied in.
		using var scope = Gizmo.Scope( $"RefPropControl{index}", new Transform( start.Position ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		Transform moved;

		if ( Editor.Application.IsKeyDown( KeyCode.E ) )
		{
			if ( !Gizmo.Control.Rotate( "prop-rotate", Rotation.Identity, out var rotation ) )
			{
				EndPropDragIfReleased();
				return;
			}

			BeginPropDrag( index, world, ref start );
			moved = new Transform( start.Position, rotation * start.Rotation, start.Scale );
		}
		else
		{
			if ( !Gizmo.Control.Position( "prop-move", Vector3.Zero, out var delta, Rotation.Identity ) )
			{
				EndPropDragIfReleased();
				return;
			}

			BeginPropDrag( index, world, ref start );
			_propMoveDelta += delta;

			moved = new Transform( start.Position + _propMoveDelta, start.Rotation, start.Scale );
		}

		// A prop that follows a bone stores its placement RELATIVE TO THAT BONE - that's what
		// makes it stay in the hand. Writing the world transform into it would put it in the hand
		// exactly once, then send it flying the moment the hand moved.
		if ( !string.IsNullOrWhiteSpace( prop.FollowBone ) && TryGetWorldTransform( prop.FollowBone, out var boneWorld ) )
			moved = boneWorld.ToLocal( moved );

		prop.Position = moved.Position;
		prop.Rotation = moved.Rotation.Angles();

		ReferencePropMoved?.Invoke();
	}

	private void BeginPropDrag( int index, Transform world, ref Transform start )
	{
		if ( _propDragIndex == index )
			return;

		ReferencePropDragStarted?.Invoke();

		_propDragIndex = index;
		_propDragStart = world;
		_propMoveDelta = Vector3.Zero;

		start = world;
	}

	/// <summary>Same reasoning as EndDragIfReleased for bones - a control reports false on any
	/// frame its value didn't change, including frames where the button is still held.</summary>
	private void EndPropDragIfReleased()
	{
		if ( Gizmo.IsLeftMouseDown || _propDragIndex < 0 )
			return;

		_propDragIndex = -1;
		ReferencePropDragEnded?.Invoke();
	}

	/// <summary>The last pose lookup this viewport was given, kept so a drag can re-resolve the
	/// dragged bone's descendants without the window having to hand it over again.</summary>
	private Func<string, Transform?> _poseLookup;

	/// <summary>
	/// Rewrites every bone under <paramref name="root"/> from its own local pose and the parent's
	/// new world transform.
	///
	/// NEEDED BECAUSE EVERY BONE IS PINNED IN WORLD SPACE. EvaluatePose writes a world-space
	/// override for each bone, and a bone with a world override no longer inherits anything from
	/// its parent - so rotating a shoulder moved the shoulder and left the arm behind. It worked
	/// exactly once, before the children had been pinned, which is what made it look intermittent.
	///
	/// Parent-space writes would avoid this entirely, but SceneModel.SetParentSpaceBone is
	/// internal, so world space is the only way in - and the cost of that is having to carry the
	/// hierarchy ourselves, here.
	/// </summary>
	private void PropagateToDescendants( BoneCollection.Bone root, Transform rootWorld )
	{
		if ( !_renderer.IsValid() )
			return;

		var resolved = new Dictionary<string, Transform> { [root.Name] = rootWorld };

		foreach ( var (bone, world) in LiveBones() )
		{
			if ( bone.Parent is not { } parent || !resolved.TryGetValue( parent.Name, out var parentWorld ) )
				continue;

			// Its own pose is unchanged - only where its parent is has moved. There's always an
			// answer now: a keyframe if the clip has one, the model's bind pose if it doesn't.
			var local = _poseLookup?.Invoke( bone.Name ) ?? BindPoseFor( bone );

			var clamped = RigConstraintSolver.ClampToLimits( Rig, bone.Name, local );
			var worldPose = parentWorld.ToWorld( clamped );

			resolved[bone.Name] = worldPose;
			ApplyWorldTransform( bone, worldPose );
		}
	}

	public void EvaluatePose( Func<string, Transform?> poseForBone )
	{
		_poseLookup = poseForBone;
		_suppressAutoKey = true;

		try
		{
			// EVERY BONE IS DRIVEN EXPLICITLY, from its keyframe if it has one and from the bind
			// pose if it doesn't. Nothing is left to whatever the renderer happens to be holding.
			//
			// Two bugs live here, and the fix for the first caused the second:
			//
			// Posing a bone leaves an override on the renderer. Originally this loop skipped bones
			// with no keyframe, so a bone kept the override from the last time it was dragged even
			// after undo removed its keyframes - the document changed and the screen didn't, which
			// is why undo looked broken.
			//
			// Clearing all overrides first fixed that and broke dragging: the clear also wiped the
			// bone being dragged, which this loop then deliberately skips, so it snapped to bind
			// pose and the drag re-applied it the next frame - flickering between two poses.
			// Clearing was too blunt an instrument. Driving each bone to a known value has neither
			// hole: nothing is ever undefined, so nothing needs wiping.
			// THE HIERARCHY IS RESOLVED HERE, not read back off the renderer.
			//
			// Keyframes are parent-space and the write API takes world space, so each bone needs
			// its parent's world transform to convert. Reading that back from the renderer is
			// wrong: bone writes only land on the next tick, so mid-loop the parent still reports
			// its PREVIOUS pose. Every child was being placed against a stale parent and the
			// error compounded down each chain - which is why undo appeared to mangle the model
			// rather than simply restore the wrong pose.
			//
			// It was survivable while this loop only touched keyframed bones, because a stale
			// parent that isn't itself moving converts correctly. Driving every bone made it
			// obvious.
			//
			// So world transforms are accumulated in a local map as the loop walks down: a bone's
			// parent is whatever THIS pass computed, not whatever the renderer last drew.
			var resolved = new Dictionary<string, Transform>();

			foreach ( var (bone, world) in LiveBones() )
			{
				// Dragged bones keep their live transform, and must still be resolvable as a
				// parent for anything under them.
				if ( _draggingBone && _selectedBones.Contains( bone.Name ) )
				{
					resolved[bone.Name] = world;
					continue;
				}

				// Keyframe if the clip has one, the model's bind pose if it doesn't. Never
				// undefined, so nothing is left holding a stale override.
				var local = poseForBone( bone.Name ) ?? BindPoseFor( bone );

				var clamped = RigConstraintSolver.ClampToLimits( Rig, bone.Name, local );

				// Skeletons list parents before children, so the parent is normally already
				// resolved. The readback fallback only covers a rig that doesn't, where a stale
				// parent is still better than none.
				var parentWorld = bone.Parent is { } parent
					? (resolved.TryGetValue( parent.Name, out var computed ) ? computed : ParentWorld( bone ))
					: _renderer.WorldTransform;

				var worldPose = parentWorld.ToWorld( clamped );

				resolved[bone.Name] = worldPose;
				ApplyWorldTransform( bone, worldPose );
			}
		}
		finally
		{
			_suppressAutoKey = false;
		}
	}

	/// <summary>SceneRenderingWidget renders its scene but never updates it, so a tool hosting its
	/// own editor scene has to tick it. Bone overrides are only folded into the render pose during
	/// that tick (rig_test_pose: a write reads back stale in the same frame and correct only after
	/// a tick), so without this the model sat frozen and no amount of correct posing could have
	/// shown up.
	///
	/// RealTime.Now/RealTime.Delta, which is what ShaderGraph's Preview passes. This used to run
	/// off a hand-rolled Stopwatch - unnecessary, and a second clock to drift out of step with the
	/// one the rest of the editor animates against.</summary>
	private void TickScene()
	{
		if ( _canvas.Scene is not { } scene )
			return;

		scene.EditorTick( RealTime.Now, RealTime.Delta );
	}

	/// <summary>Wear the game's pixel-arms look instead of whatever the model ships with. The
	/// preview arms model resolves to materials/dev/gray_25.vmat - a dev placeholder - which is
	/// why it renders as a white mannequin, indistinguishable from a missing material.</summary>
	public bool PixelStyle { get; set; } = true;

	private Material _pixelMaterial;
	private Texture _pixelSkin;
	private bool _pixelSkinResolved;

	private void ApplyPixelStyle()
	{
		if ( !_renderer.IsValid() )
			return;

		if ( !PixelStyle )
		{
			// Only clear an override we put there.
			if ( _pixelMaterial is not null && _renderer.MaterialOverride == _pixelMaterial )
				_renderer.MaterialOverride = null;

			return;
		}

		_pixelMaterial ??= PixelArmsStyle.LoadMaterial();

		if ( _pixelMaterial is null )
			return;

		if ( _renderer.MaterialOverride != _pixelMaterial )
			_renderer.MaterialOverride = _pixelMaterial;

		if ( !_pixelSkinResolved )
		{
			_pixelSkinResolved = true;
			_pixelSkin = PixelArmsStyle.ResolveSkinTexture();
		}

		// Read the scene object AFTER setting the override - swapping materials rebuilds it, and
		// it's null until the renderer has been drawn once anyway.
		new PixelArmsStyle
		{
			ColorTexture = _pixelSkin,
			// Both arms: this is a rig editor, not a shot.
			HideSide = 0f,
			// The vertex snap is in screen pixels, and in a tool window that's this widget.
			ScreenSize = _canvas.Size
		}.ApplyTo( _renderer.SceneObject );
	}

	private void OnPreFrame()
	{
		TickScene();
		ApplyPixelStyle();
		ApplyViewmodelFraming();
		ApplyReferenceProps();

		_gizmoInstance.Input.IsHovered = IsActiveWindow && _canvas.IsUnderMouse;

		// FREE-FLY RUNS EVEN WHEN LOCKED, and the eye position is re-pinned afterwards.
		//
		// It used to be skipped outright, which pinned the view rigidly forward - and this model's
		// bind pose has the arms hanging at its sides, below and behind the eye, so first person
		// showed an empty screen with no way to go and look. Turning the lock off to find them and
		// back on to judge them is exactly the back-and-forth this mode exists to remove.
		//
		// Letting the camera rotate but not travel keeps what the lock is actually for: the eye
		// stays where the player's eye is, so distances and framing remain honest, while you can
		// still turn your head. Reset Camera returns you to looking straight ahead.
		if ( _gizmoInstance.FirstPersonCamera( _camera, _canvas ) )
			_gizmoInstance.Input.IsHovered = false;

		PinEyePosition();

		_canvas.UpdateGizmoInputs( _gizmoInstance.Input.IsHovered );

		// The ground grid is a world-space cue and actively misleading on a viewmodel, where
		// nothing is standing on anything.
		if ( !ViewmodelMode )
			Gizmo.Draw.Grid( 0, Gizmo.GridAxis.XY );

		DrawBoneHandles();
		DrawReferenceProps();
		DrawSelectedBoneReadout();

		Cursor = Gizmo.HasHovered ? CursorShape.Finger : CursorShape.Arrow;
	}

	/// <summary>The mesh doesn't visually bend when you pose it (see the class header) - this is
	/// the compensating feedback: the selected bone's actual numbers, live, so posing isn't done
	/// fully blind.</summary>
	private void DrawSelectedBoneReadout()
	{
		if ( SelectedBone is null )
			return;

		if ( !TryGetWorldTransform( SelectedBone, out var world ) )
			return;

		var bone = FindBoneData( SelectedBone );
		var parentWorld = bone?.Parent is { } parent && _renderer.TryGetBoneTransform( parent, out var parentTx )
			? parentTx
			: _renderer.WorldTransform;

		var local = parentWorld.ToLocal( world );

		// Quiet, and only the numbers that change while posing. The bone name is the one thing
		// worth reading at a glance, so it keeps full contrast; the values sit back.
		var angles = local.Rotation.Angles();

		// Scale only shows once it's not 1 - it's noise for the ninety-nine-in-a-hundred poses
		// that don't touch it, and the thing you're actively judging when you are scaling.
		var scale = local.Scale == Vector3.One
			? ""
			: $"\nscl  {local.Scale.x:0.#}  {local.Scale.y:0.#}  {local.Scale.z:0.#}";

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.85f );
		Gizmo.Draw.ScreenText( SelectedBone, new Vector2( 12, 12 ), size: 13, flags: TextFlag.LeftTop );

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.4f );
		Gizmo.Draw.ScreenText(
			$"rot  {angles.pitch:0.#}  {angles.yaw:0.#}  {angles.roll:0.#}\n" +
			$"pos  {local.Position.x:0.#}  {local.Position.y:0.#}  {local.Position.z:0.#}{scale}",
			new Vector2( 12, 30 ), size: 11, flags: TextFlag.LeftTop );
	}

	private bool _draggingBone;

	/// <summary>One dot per bone, click to select, Shift-click to add or remove, click-and-drag to
	/// pose - hold E to flip rotate/move, or use the toolbar for scale. A single selected bone gets
	/// a gizmo on itself; several selected bones get one gizmo on their centroid that poses the lot
	/// together. Unselected bones get a plain click-to-select hitbox. The selected bone gets no
	/// hitbox of ours at all - only Gizmo.Control, which brings its own. Registering both is what
	/// broke dragging for so long; see the comment on the selected branch below.</summary>
	private void DrawBoneHandles()
	{
		// Selected bones are collected through the loop, then the control is run after it in its
		// own top-level scope, so it isn't nested inside a bone's rotated drawing scope.
		var selectedBones = new List<(BoneCollection.Bone Bone, Transform World)>();
		string hovered = null;

		// Placing the whole model is its own mode with its own single handle. Bone dots are
		// suppressed while it's on, so there's exactly one thing on screen to grab - the whole
		// reason this mode exists.
		if ( MoveWholeModel )
		{
			RigStatusBar.Show( Editor.Application.IsKeyDown( KeyCode.E )
				? "Whole model  -  drag to rotate. Let go of E to move instead."
				: "Whole model  -  drag to move. Hold E to rotate instead." );

			DragWholeModel();
			return;
		}

		if ( !ShowBoneHandles )
		{
			// Handles hidden, but the selected bones still get their control - otherwise turning
			// dots off would silently take away the ability to pose.
			foreach ( var name in _selectedBones )
			{
				if ( FindBoneData( name ) is { } bone && _renderer.TryGetBoneTransform( bone, out var world ) )
					selectedBones.Add( (bone, world) );
			}

			if ( selectedBones.Count == 1 )
				DragSelectedBone( selectedBones[0].Bone, selectedBones[0].World );
			else if ( selectedBones.Count > 1 )
				DragSelectedBones( selectedBones );

			return;
		}

		// X-ray the skeleton. Most of a rig sits inside the mesh, so depth-tested dots are both
		// invisible and unclickable - the hitboxes are already depth-biased to the front, so
		// without this the clickable spot and the visible dot disagree.
		Gizmo.Draw.IgnoreDepth = true;

		foreach ( var (bone, world) in LiveBones() )
		{
			// Hidden bones are skipped HERE and nowhere else - EvaluatePose still drives them and
			// their keyframes still play. This is the only place hiding is allowed to mean
			// anything, so it can never cost you animation.
			if ( IsHidden( bone.Name ) && !ShowHiddenBones )
				continue;

			var isSelected = _selectedBones.Contains( bone.Name );
			var isTwist = IsTwistBone( bone.Name );

			// A twist bone that's actually selected keeps its handle regardless - selecting one
			// from the bone tree and then finding it has no gizmo would be the same class of
			// invisible-mode bug the toggle bar exists to prevent.
			if ( isTwist && !ShowTwistBones && !isSelected )
				continue;

			if ( isSelected )
				selectedBones.Add( (bone, world) );

			using var boneScope = Gizmo.Scope( $"Bone{bone.Index}", world );

			var radius = HandleRadius;

			if ( bone.Parent is { } parentBone && _renderer.TryGetBoneTransform( parentBone, out var parentWorld ) )
			{
				Gizmo.Draw.Color = Theme.Blue.WithAlpha( 0.8f );
				Gizmo.Draw.Line( 0f, world.PointToLocal( parentWorld.Position ) );
			}

			// Solid dot, not a hollow ring - the reference draws bones as filled white dots.
			// The selected one is drawn fatter as well as yellow, so it's still findable in a
			// dense area of the rig where a colour change alone is easy to lose.
			//
			// Twist bones, when shown, are drawn small and dim: they read as a satellite of the
			// joint rather than as a peer of it. Size carries this rather than colour alone,
			// because the two dots are at the same point - a colour difference between two
			// coincident dots of equal size is invisible, since one simply covers the other.
			Gizmo.Draw.Color = isSelected ? Theme.Yellow
				: Gizmo.IsHovered ? Theme.Green
				: isTwist ? Theme.Blue.WithAlpha( 0.55f )
				: Color.White;

			Gizmo.Draw.SolidSphere( 0f, radius * (isSelected ? 0.5f : isTwist ? 0.16f : 0.35f), 8, 8 );

			// No hitbox of our own on a single selected bone. Gizmo.Control registers its own
			// hitboxes for its handles, and a sphere sitting at the same scope origin - depth-biased
			// in front, no less - wins the hover test against them, so the control never sees the
			// press and the drag never starts. That was the actual reason dragging did nothing.
			//
			// With a group selected the control lives at the centroid, away from any one bone, so
			// the selected bones keep their hitboxes - that's what lets Shift-click take one back
			// out of the set.
			if ( isSelected && _selectedBones.Count == 1 )
				continue;

			Gizmo.Hitbox.DepthBias = 0.01f;

			// THE SMALLER HITBOX IS THE ACTUAL FIX, not the smaller dot. Two coincident spheres of
			// equal radius make the winner of a click arbitrary; shrinking the twist bone's means
			// the joint underneath wins everywhere except dead centre, so the bone you meant to
			// grab is the one you get even with both shown.
			Gizmo.Hitbox.Sphere( new Sphere( 0f, isTwist ? radius * 0.3f : radius ) );

			if ( Gizmo.IsHovered )
			{
				hovered = bone.Name;

				if ( Gizmo.WasLeftMousePressed )
					Select( bone.Name, Editor.Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) );
			}
		}

		// Kept for the right-click menu, which has no gizmo context of its own to hit-test in.
		_hoveredBone = hovered;

		// Named after the loop so the hint reflects this frame, not last frame's hover.
		if ( hovered is not null )
		{
			RigStatusBar.Show( DragHint( hovered ) );
		}
		else if ( _selectedBones.Count > 1 )
		{
			RigStatusBar.Show( $"{_selectedBones.Count} bones selected  -  drag the gizmo to pose them together" );
		}
		else if ( SelectedBone is not null )
		{
			RigStatusBar.Show( $"{SelectedBone} selected  -  drag the gizmo to pose it" );
		}
		else
		{
			RigStatusBar.Clear();
		}

		// Hand the control back normal depth handling so it looks and behaves like the scene
		// editor's own move gizmo.
		Gizmo.Draw.IgnoreDepth = false;

		if ( selectedBones.Count == 1 )
			DragSelectedBone( selectedBones[0].Bone, selectedBones[0].World );
		else if ( selectedBones.Count > 1 )
			DragSelectedBones( selectedBones );

		DeselectOnEmptyClick( hovered );
	}

	/// <summary>Status-bar copy for a hovered bone - names the current drag mode and reminds that
	/// Shift-click extends the selection rather than replacing it.</summary>
	private string DragHint( string subject )
	{
		var (verb, flip) = DragMode switch
		{
			BoneDragMode.Rotate => ("rotate", "Hold E to move instead."),
			BoneDragMode.Move => ("move", "Hold E to rotate instead."),
			_ => ("scale", "")
		};

		return $"{subject}  -  click to select, Shift-click to add, drag to {verb}. {flip}".TrimEnd();
	}

	/// <summary>
	/// Click empty space, lose the selection - and with it the gizmo.
	///
	/// RUN AFTER DragSelectedBone, NOT BEFORE. The selected bone's control registers its own
	/// hitboxes inside that call, and a click on one of them only counts as "something is
	/// hovered" once it has. Checking first would treat every grab of the rotate ring as a click
	/// on nothing and drop the selection the instant you tried to pose it.
	///
	/// Gizmo.HasHovered covers both kinds of target - our per-bone hitboxes and the control's own
	/// handles - so this needs no list of what is clickable.
	/// </summary>
	private void DeselectOnEmptyClick( string hovered )
	{
		if ( MoveWholeModel )
			return;

		if ( SelectedBone is null && SelectedReferenceProp < 0 )
			return;

		// A drag that happens to finish over empty space is still a drag, not a click on nothing.
		if ( _draggingBone || _dragBoneName is not null || _groupDragTargets is not null || _propDragIndex >= 0 )
			return;

		if ( hovered is not null || Gizmo.HasHovered )
			return;

		if ( !Gizmo.WasLeftMousePressed )
			return;

		SelectedReferenceProp = -1;
		Select( null );
	}

	private bool _draggingModel;
	private Vector3 _modelDragStart;
	private Rotation _modelRotateStart;
	private Vector3 _modelMoveDelta;

	/// <summary>The whole-model handle. Same contract as bone dragging, and the same as
	/// PositionEditorTool: zero in, per-frame delta out, accumulated onto the position captured
	/// when the drag began, with the handle basis passed explicitly.</summary>
	private void DragWholeModel()
	{
		if ( !_modelObject.IsValid() )
			return;

		var start = _draggingModel ? _modelDragStart : _modelObject.WorldPosition;

		using var scope = Gizmo.Scope( "ModelRoot", new Transform( start ) );

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = Theme.Green;
		Gizmo.Draw.SolidSphere( 0f, HandleRadius * 0.7f, 8, 8 );
		Gizmo.Draw.IgnoreDepth = false;

		Gizmo.Hitbox.DepthBias = 0.01f;

		var startRotation = _draggingModel ? _modelRotateStart : _modelObject.WorldRotation;

		// Same hold-to-flip as bones: E borrows the other mode for as long as it's held. Move is
		// the default here rather than rotate, because placing the arms relative to the eye is
		// what this mode is for - the inverse of a bone, where rotation is the common case.
		if ( Editor.Application.IsKeyDown( KeyCode.E ) )
		{
			// Cumulative since the grab, assigned rather than accumulated - see DragSelectedBone
			// for why the two controls differ.
			if ( !Gizmo.Control.Rotate( "model-rotate", Rotation.Identity, out var rotation ) )
			{
				if ( !Gizmo.IsLeftMouseDown )
					_draggingModel = false;

				return;
			}

			BeginModelDrag( ref start, ref startRotation );
			ApplyModelTransform( start + _modelMoveDelta, rotation * startRotation );

			return;
		}

		if ( !Gizmo.Control.Position( "model-move", Vector3.Zero, out var delta, Rotation.Identity ) )
		{
			if ( !Gizmo.IsLeftMouseDown )
				_draggingModel = false;

			return;
		}

		BeginModelDrag( ref start, ref startRotation );

		_modelMoveDelta += delta;

		ApplyModelTransform( start + _modelMoveDelta, startRotation );
	}

	/// <summary>Latches where the model was when the drag began. Both position and rotation are
	/// captured together, so releasing E mid-drag can't leave one of them measured from a
	/// different starting point than the other.</summary>
	private void BeginModelDrag( ref Vector3 start, ref Rotation startRotation )
	{
		if ( _draggingModel )
			return;

		_draggingModel = true;
		_modelDragStart = _modelObject.WorldPosition;
		_modelRotateStart = _modelObject.WorldRotation;
		_modelMoveDelta = Vector3.Zero;

		start = _modelDragStart;
		startRotation = _modelRotateStart;
	}

	/// <summary>
	/// Writes the model's placement to whichever thing actually owns it.
	///
	/// In viewmodel mode the offset and rotation ARE the authored values - ApplyViewmodelFraming
	/// derives the object's transform from them every frame, so writing the object directly would
	/// be overwritten before you saw it.
	/// </summary>
	private void ApplyModelTransform( Vector3 position, Rotation rotation )
	{
		if ( ViewmodelMode )
		{
			ViewmodelOffset = position;
			ViewmodelRotation = rotation.Angles();
			ViewmodelChanged?.Invoke();

			return;
		}

		_modelObject.WorldPosition = position;
		_modelObject.WorldRotation = rotation;
	}

	private string _dragBoneName;

	// The pose the drag started from, and the accumulated position delta - the same two pieces of
	// state PositionEditorTool keeps (startPoints and moveDelta). Both are reset when a drag
	// begins, and the live transform is never fed back into either.
	private Transform _dragStart;
	private Vector3 _moveDelta;

	// Group drag state. _groupDragTargets holds the top-most selected bones (a selected bone with
	// a selected ancestor is carried by that ancestor, not transformed directly), captured with
	// their start transforms; _groupPivot is the centroid the gizmo and the transform act around.
	private List<(BoneCollection.Bone Bone, Transform Start)> _groupDragTargets;
	private Vector3 _groupPivot;
	private Vector3 _groupMoveDelta;

	/// <summary>Latches the drag's starting pose on its first frame. Called only after a control
	/// has reported movement, so a hover never counts as a drag.</summary>
	private void BeginDrag( BoneCollection.Bone bone, Transform world, ref Transform start )
	{
		if ( _dragBoneName == bone.Name )
			return;

		// Announced before the first write lands, so whatever is listening can record the
		// pre-drag pose.
		BoneDragStarted?.Invoke( bone.Name );

		_dragBoneName = bone.Name;
		_dragStart = world;
		_moveDelta = Vector3.Zero;

		start = world;
	}

	/// <summary>
	/// THE DRAG ENDS WHEN THE BUTTON IS RELEASED, not when the control stops reporting.
	///
	/// Gizmo.Control.Position/Rotate returns false on any frame its value didn't change - which
	/// includes frames where the mouse is still held down but hasn't moved. Treating that as the
	/// end of the drag was the rubberbanding: the anchor was dropped mid-drag and re-taken from
	/// the already-moved position, while the control went on reporting the TOTAL offset since
	/// mouse-down, so the very next frame added that whole offset again. Hold still for one frame
	/// and the bone jumped; keep moving and it behaved. That is exactly the "inconsistent and
	/// rubberbandy" symptom, and it applies to the model-root handle for the same reason.
	///
	/// Safe to read Gizmo.IsLeftMouseDown here: this runs inside an active Gizmo context. Reading
	/// it from outside one - which EvaluatePose used to do - throws.
	/// </summary>
	private void EndDragIfReleased()
	{
		if ( Gizmo.IsLeftMouseDown )
			return;

		EndDrag();
	}

	private void EndDrag()
	{
		var wasDragging = _dragBoneName is not null || _groupDragTargets is not null;

		_dragBoneName = null;
		_groupDragTargets = null;
		_draggingBone = false;

		// Only fire on an actual drag ending - this runs every frame the control isn't active.
		if ( wasDragging )
			BoneDragEnded?.Invoke();
	}

	/// <summary>Drag the selected bone with a normal editor-style gizmo.
	///
	/// THE TWO CONTROLS DO NOT AGREE WITH EACH OTHER, which is the thing to know here. Taken from
	/// the engine's own shipped tools rather than from the parameter names, which mislead - both
	/// out params are named as if they were absolute values and neither one is:
	///
	///   Position (PositionEditorTool) hands back a PER-FRAME DELTA that you accumulate. You pass
	///   Vector3.Zero, not the current position:  Control.Position( n, Vector3.Zero, out var
	///   delta, basis )  then  moveDelta += delta.
	///
	///   Rotate (RotationEditorTool, and WidgetGallery's RotationTest) hands back the rotation
	///   CUMULATIVE SINCE THE GRAB, which you assign rather than accumulate:  moveDelta = delta.
	///
	/// Both are applied to a start pose captured on the drag's first frame, which is what makes
	/// the difference survivable: neither one is ever fed back into itself.
	///
	/// Every sensitivity bug in this function came from getting that wrong. Passing Vector3.Zero
	/// for position made the control work against the world origin instead of the bone, so a huge
	/// mouse movement produced a tiny one - "can't move it unless I drag crazy far". Treating the
	/// rotation delta as cumulative against a frozen anchor threw away all but one frame of it.
	/// An earlier version had it the other way round and compounded the whole drag every frame.
	///
	/// There is no frozen anchor any more: absolute-out feeds from the live value, and delta-out
	/// accumulates onto it. That is what the engine does, and it is self-correcting rather than
	/// dependent on state we maintain.</summary>
	private void DragSelectedBone( BoneCollection.Bone bone, Transform world )
	{
		var dragging = _dragBoneName == bone.Name;

		// The start pose is captured on the first frame of the drag and everything is applied to
		// THAT, exactly as PositionEditorTool applies its accumulated delta to startPoints. The
		// live transform is never fed back in, so nothing compounds.
		var start = dragging ? _dragStart : world;

		// Handle basis. Identity means world-aligned arrows, like the scene editor in global
		// space - and it is passed EXPLICITLY, because Control.Position takes the basis as a
		// fourth argument and leaving it out is what made every axis drag along the same one.
		var handleRotation = Rotation.Identity;

		using var scope = Gizmo.Scope( $"BoneControl{bone.Index}", new Transform( start.Position ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		// E is a hold-to-flip, not a toggle - it borrows the other mode for as long as it's down
		// and springs back, so you can nudge a bone's position mid-rotation-pass without losing
		// your place.
		var mode = EffectiveDragMode();

		Transform newWorld;

		switch ( mode )
		{
			case BoneDragMode.Rotate:
			{
				// Rotate's value is CUMULATIVE since the grab - RotationEditorTool assigns it rather
				// than accumulating, and applies it to the start rotation. Position's is per-frame.
				// The two controls genuinely differ; this is not a typo.
				if ( !Gizmo.Control.Rotate( "bone-rotate", Rotation.Identity, out var rotation ) )
				{
					EndDragIfReleased();
					return;
				}

				BeginDrag( bone, world, ref start );

				var basis = handleRotation;
				var applied = basis * rotation * basis.Inverse;

				newWorld = new Transform( start.Position, applied * start.Rotation, start.Scale );
				break;
			}

			case BoneDragMode.Move:
			{
				if ( !Gizmo.Control.Position( "bone-move", Vector3.Zero, out var delta, handleRotation ) )
				{
					EndDragIfReleased();
					return;
				}

				BeginDrag( bone, world, ref start );

				// Per-frame delta, accumulated - "moveDelta += delta" - then applied to the start.
				_moveDelta += delta;

				newWorld = new Transform( start.Position + _moveDelta, start.Rotation, start.Scale );
				break;
			}

			default:
			{
				// Scale, like Rotate, reports CUMULATIVE since the grab - the value passed in is
				// the baseline (1), the value handed back is the factor to multiply by. Uniform,
				// because Source2 scale is 1D and a joint scale that isn't uniform skews the mesh
				// in a way no rig wants.
				if ( !Gizmo.Control.Scale( "bone-scale", 1f, out var scale ) )
				{
					EndDragIfReleased();
					return;
				}

				BeginDrag( bone, world, ref start );

				var s = scale.Clamp( 0.01f, 100f );

				// A bone scales in place about its own joint - position and rotation hold, the
				// scale multiplies.
				newWorld = new Transform( start.Position, start.Rotation, start.Scale * s );
				break;
			}
		}

		_draggingBone = true;

		// rig_debug_drag 1. Answers the one question staring at the code can't: does the write
		// land and stay, or is something else putting the bone back? "wrote" is what this frame
		// asked for; "readback" is what the renderer actually had at the START of this frame,
		// i.e. the result of last frame's write. If readback tracks wrote one frame behind,
		// posing is fine and the problem is elsewhere. If readback never moves, something is
		// overwriting it every frame.
		if ( DebugDrag )
		{
			Log.Info( $"[rigdrag] {bone.Name} mode={mode} " +
				$"readback={world.Position} wrote={newWorld.Position} " +
				$"viewmodel={ViewmodelMode} locked={LockCameraToView}" );
		}

		// IK first: if this bone is an enabled IK target, dragging it should bend the chain
		// behind it rather than tear the effector off its parent.
		if ( RigConstraintSolver.FindIkFor( Rig, bone.Name ) is { } ik
			&& RigConstraintSolver.TrySolveTwoBone( _renderer, bone, newWorld.Position, ik.PoleDirection, out var chain ) )
		{
			var weight = ik.Weight.Clamp( 0f, 1f );

			// What this pass actually wrote, keyed by bone. THE WHOLE CHAIN MOVES IN THIS ONE
			// FRAME, and bone writes don't land until the next tick - so a chain bone's keyframe
			// has to be converted against its parent's NEW pose from this map, not against the
			// renderer's readback, which still holds the pre-drag pose. Same reasoning as the
			// `resolved` map in EvaluatePose, and the same failure if it's skipped: mid and end are
			// keyed relative to a stale root, the error compounds down the chain, and the arm comes
			// back mangled the next time the clip is evaluated.
			var solved = new Dictionary<string, Transform>();

			foreach ( var (chainBone, chainWorld) in chain )
			{
				var blended = chainWorld;

				// Weight blends the solve against where the bone already was, so an IK constraint
				// can be dialled in rather than being all-or-nothing.
				if ( weight < 1f && _renderer.TryGetBoneTransform( chainBone, out var currentWorld ) )
					blended = Transform.Lerp( currentWorld, chainWorld, weight, true );

				// Recorded before the notify below reads it - the solver hands the chain back
				// root-first, so a bone's parent is already in here by the time it's needed.
				solved[chainBone.Name] = blended;

				ApplyWorldTransform( chainBone, blended );

				// Same reason as the plain drag: each solved bone has to carry whatever hangs off
				// it. For an arm IK that's the hand and every finger under the wrist.
				PropagateToDescendants( chainBone, blended );

				NotifyPosed( chainBone, blended,
					chainBone.Parent is { } chainParent && solved.TryGetValue( chainParent.Name, out var solvedParent )
						? solvedParent
						: null );
			}

			return;
		}

		newWorld = ApplyLimits( bone, newWorld );

		ApplyWorldTransform( bone, newWorld );

		// Everything below this bone has to be carried with it - see PropagateToDescendants.
		// Without it, rotating a shoulder rotates only the shoulder.
		PropagateToDescendants( bone, newWorld );

		NotifyPosed( bone, newWorld );
	}

	/// <summary>The drag mode the gizmo should act in this frame, with E's hold-to-flip applied.
	/// E flips rotate/move and never implies scale - scale is deliberate, reached from the toolbar.</summary>
	private BoneDragMode EffectiveDragMode()
	{
		if ( !Editor.Application.IsKeyDown( KeyCode.E ) )
			return DragMode;

		return DragMode switch
		{
			BoneDragMode.Rotate => BoneDragMode.Move,
			BoneDragMode.Move => BoneDragMode.Rotate,
			_ => DragMode
		};
	}

	/// <summary>
	/// Drag several selected bones as a group, from one gizmo at their centroid.
	///
	/// Only the TOP-MOST selected bones are transformed and keyed: a selected child is carried by
	/// its selected parent through PropagateToDescendants, so transforming both would apply the
	/// parent's rotation and then the child's own on top - double motion. The transform itself is
	/// applied about the centroid (rotate and scale move each bone around it), which is what makes
	/// a handful of fingers curl as one hand rather than each bone spinning on its own joint.
	///
	/// IK is deliberately not run here - it's a single-bone solve, and a group drag is plain FK.
	/// </summary>
	private void DragSelectedBones( List<(BoneCollection.Bone Bone, Transform World)> targets )
	{
		var pivot = _groupDragTargets is not null ? _groupPivot : Centroid( targets );

		using var scope = Gizmo.Scope( "BoneGroup", new Transform( pivot ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		var mode = EffectiveDragMode();

		// Rotate and Scale hand back values CUMULATIVE since the grab; Position hands back a
		// per-frame delta. Same contract as DragSelectedBone - see the note there.
		var rotation = Rotation.Identity;
		var moveDelta = Vector3.Zero;
		var scale = 1f;

		switch ( mode )
		{
			case BoneDragMode.Rotate:
				if ( !Gizmo.Control.Rotate( "bone-rotate", Rotation.Identity, out rotation ) )
				{
					EndDragIfReleased();
					return;
				}
				break;

			case BoneDragMode.Move:
				if ( !Gizmo.Control.Position( "bone-move", Vector3.Zero, out moveDelta, Rotation.Identity ) )
				{
					EndDragIfReleased();
					return;
				}
				break;

			default:
				if ( !Gizmo.Control.Scale( "bone-scale", 1f, out scale ) )
				{
					EndDragIfReleased();
					return;
				}

				scale = scale.Clamp( 0.01f, 100f );
				break;
		}

		BeginGroupDrag( targets );

		// Position's delta accumulates onto the frozen start, after BeginGroupDrag has zeroed it.
		if ( mode == BoneDragMode.Move )
			_groupMoveDelta += moveDelta;

		_draggingBone = true;

		foreach ( var (bone, start) in _groupDragTargets )
		{
			var newWorld = TransformGroup( start, _groupPivot, rotation, _groupMoveDelta, scale, mode );

			newWorld = ApplyLimits( bone, newWorld );

			ApplyWorldTransform( bone, newWorld );

			// Carries unselected descendants - and any selected ones nested under this bone -
			// with the moved parent.
			PropagateToDescendants( bone, newWorld );

			NotifyPosed( bone, newWorld );
		}
	}

	/// <summary>Latches the group drag's targets and centroid on its first frame of movement.
	/// Captured as the TOP-MOST bones only, so a parent and its selected children are transformed
	/// once, not once each.</summary>
	private void BeginGroupDrag( List<(BoneCollection.Bone Bone, Transform World)> targets )
	{
		if ( _groupDragTargets is not null )
			return;

		var tops = TopMost( targets );

		_groupDragTargets = tops;
		_groupPivot = Centroid( tops );
		_groupMoveDelta = Vector3.Zero;

		BoneDragStarted?.Invoke( SelectedBone ?? (tops.Count > 0 ? tops[0].Bone.Name : null) );
	}

	/// <summary>The selected bones with no selected ancestor - those are the ones the group
	/// transform is applied to; anything under one follows it.</summary>
	private static List<(BoneCollection.Bone Bone, Transform World)> TopMost( List<(BoneCollection.Bone Bone, Transform World)> bones )
	{
		var names = new HashSet<string>( bones.Select( t => t.Bone.Name ) );

		return bones.Where( t => t.Bone.Parent is null || !names.Contains( t.Bone.Parent.Name ) ).ToList();
	}

	private static Vector3 Centroid( List<(BoneCollection.Bone Bone, Transform World)> bones )
	{
		if ( bones.Count == 0 )
			return Vector3.Zero;

		var sum = Vector3.Zero;

		foreach ( var (_, world) in bones )
			sum += world.Position;

		return sum / (float)bones.Count;
	}

	/// <summary>Apply a group transform to one bone's start pose. Rotate and scale act about the
	/// group pivot; move is a plain translation of every bone by the same delta.</summary>
	private static Transform TransformGroup( Transform start, Vector3 pivot, Rotation rotation, Vector3 move, float scale, BoneDragMode mode ) => mode switch
	{
		BoneDragMode.Rotate => new Transform( pivot + rotation * (start.Position - pivot), rotation * start.Rotation, start.Scale ),
		BoneDragMode.Move => new Transform( start.Position + move, start.Rotation, start.Scale ),
		_ => new Transform( pivot + (start.Position - pivot) * scale, start.Rotation, start.Scale * scale )
	};

	/// <summary>The toolbar's Link icon - off, dragging a bone poses it live without writing a
	/// keyframe, same as scrubbing between existing keys does.</summary>
	public bool AutoKeyEnabled { get; set; } = true;

	/// <summary>
	/// Announce a bone's new pose as the parent-space value a keyframe is stored in.
	///
	/// <paramref name="parentWorld"/> is the parent's transform AS OF THIS FRAME'S WRITES, and any
	/// caller that moved the parent in the same frame must pass it. Reading it back off the
	/// renderer instead - which is what the fallback does - returns the parent's PREVIOUS pose,
	/// because bone writes only fold into the pose on the next tick. That's harmless for a plain FK
	/// drag, where only the dragged bone moves and its parent is genuinely still, and wrong for an
	/// IK solve, where the whole chain moves at once and each bone would be keyed against a stale
	/// parent.
	/// </summary>
	private void NotifyPosed( BoneCollection.Bone bone, Transform newWorld, Transform? parentWorld = null )
	{
		if ( _suppressAutoKey || !AutoKeyEnabled )
			return;

		BonePosed?.Invoke( bone.Name, (parentWorld ?? ParentWorld( bone )).ToLocal( newWorld ) );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		_canvas.Scene?.Destroy();
	}
}
