using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Effigy.Skeleton, not Sandbox.Skeleton - the engine has a Skeleton type of its own, and every
// Skeleton named here is the CAD one the rig panel builds and the exporters write out.
using Skeleton = Effigy.Skeleton;

namespace Marionette.EditorTools;

// ============================================================================
//  Color palettes for theming — swap at runtime via the toolbar dropdown.
//  Each palette defines every color the UI touches. The Onshape-faithful one
//  is the default; the rest are alternatives.
// ============================================================================

internal sealed class EffigyPalette
{
	public string Name;
	public Color Bg, Chrome, Chrome2, Border, Text, TextDim, Accent, AccentSoft, ViewportBg;

	public static readonly EffigyPalette OnshapeLight = new()
	{
		Name = "Onshape Light",
		Bg = new( 0.914f, 0.922f, 0.929f ),
		Chrome = new( 0.957f, 0.961f, 0.969f ),
		Chrome2 = new( 0.925f, 0.933f, 0.945f ),
		Border = new( 0.827f, 0.843f, 0.863f ),
		Text = new( 0.125f, 0.141f, 0.165f ),
		TextDim = new( 0.439f, 0.467f, 0.498f ),
		Accent = new( 0.039f, 0.518f, 0.780f ),
		AccentSoft = new( 0.867f, 0.925f, 0.973f ),
		ViewportBg = new( 0.867f, 0.878f, 0.894f ),
	};

	public static readonly EffigyPalette OnshapeDark = new()
	{
		Name = "Onshape Dark",
		Bg = new( 0.106f, 0.118f, 0.129f ),
		Chrome = new( 0.149f, 0.161f, 0.176f ),
		Chrome2 = new( 0.173f, 0.188f, 0.204f ),
		Border = new( 0.082f, 0.090f, 0.102f ),
		Text = new( 0.843f, 0.855f, 0.867f ),
		TextDim = new( 0.545f, 0.565f, 0.588f ),
		Accent = new( 0.247f, 0.663f, 0.910f ),
		AccentSoft = new( 0.110f, 0.204f, 0.267f ),
		ViewportBg = new( 0.208f, 0.224f, 0.239f ),
	};

	public static readonly EffigyPalette Blender = new()
	{
		Name = "Blender",
		Bg = new( 0.165f, 0.165f, 0.165f ),
		Chrome = new( 0.208f, 0.208f, 0.208f ),
		Chrome2 = new( 0.235f, 0.235f, 0.235f ),
		Border = new( 0.122f, 0.122f, 0.122f ),
		Text = new( 0.839f, 0.839f, 0.839f ),
		TextDim = new( 0.533f, 0.533f, 0.533f ),
		Accent = new( 0.306f, 0.541f, 0.890f ),
		AccentSoft = new( 0.161f, 0.235f, 0.345f ),
		ViewportBg = new( 0.220f, 0.220f, 0.220f ),
	};

	public static readonly EffigyPalette Fusion = new()
	{
		Name = "Fusion",
		Bg = new( 0.145f, 0.153f, 0.161f ),
		Chrome = new( 0.184f, 0.192f, 0.204f ),
		Chrome2 = new( 0.208f, 0.216f, 0.227f ),
		Border = new( 0.106f, 0.114f, 0.122f ),
		Text = new( 0.878f, 0.886f, 0.894f ),
		TextDim = new( 0.522f, 0.533f, 0.545f ),
		Accent = new( 0.000f, 0.600f, 0.863f ),
		AccentSoft = new( 0.086f, 0.200f, 0.286f ),
		ViewportBg = new( 0.251f, 0.259f, 0.271f ),
	};

	public static readonly EffigyPalette[] All = { OnshapeLight, OnshapeDark, Blender, Fusion };
}

// ============================================================================
//  The main Effigy dock window — Onshape-faithful layout with:
//    Top:    square feature-creation icon buttons floating over the viewport's top edge
//    Left:   flat feature tree (origin/planes → features → bodies)
//    Center: 3D viewport with reference planes, origin, orbit camera
//    Right:  parameter panel for the selected feature
//    Bottom: Part-studio-style tabs
//
//  Registered under Marionette in the Tools menu. Opens from Tools, or by
//  double-clicking a .effigy part studio in the asset browser.
// ============================================================================

[EditorForAssetType( "effigy" )]
[EditorApp( "Effigy", "editor/effigy_icon.png", "Parametric modelling, subdivision, and rig-ready mesh export" )]
public sealed partial class EffigyWindow : DockWindow, IAssetEditor
{
	// --- opening a .effigy from the asset browser -------------------------------------------
	//
	// A part studio is a plain text document rather than a GameResource, so there is no asset
	// type class to hang this off - the attribute above is the whole registration, which is the
	// same way ShaderGraph claims .shdrgrph. Everything below just routes the double-click into
	// the load path the File menu already uses.

	/// <summary>One document per window. Opening a second .effigy reuses this window, which is
	/// what makes the unsaved-work prompt in AssetOpen meaningful.</summary>
	public bool CanOpenMultipleAssets => false;

	public void AssetOpen( Asset asset )
	{
		var path = asset?.AbsolutePath;

		if ( string.IsNullOrEmpty( path ) )
			return;

		Raise();

		// Asked before the open, not after - the work about to be thrown away belongs to the
		// document being replaced, exactly as it does on File > Open.
		ConfirmDiscard( () => LoadDocument( path ) );
	}

	/// <summary>Nothing in a part studio is addressable by member name, so there is nothing to
	/// jump to. Present because IAssetEditor requires it.</summary>
	public void SelectMember( string memberName )
	{
	}

	// --- core state -------------------------------------------------------------------------

	private PartStudio _studio;
	private EffigyViewport _viewport;

	// --- palette / theming ------------------------------------------------------------------

	private EffigyPalette _palette = EffigyPalette.OnshapeDark;
	private int _paletteIndex = 1; // start dark

	// --- panels -----------------------------------------------------------------------------

	/// <summary>Whether the viewport currently holds preview geometry — drives the one-shot
	/// camera framing in RebuildStudio.</summary>
	private bool _hasPreview;

	private EffigyFeatureTreePanel _featureTree;
	private EffigyFeatureDialog _dialog;
	private EffigyPartsPanel _partsPanel;
	private EffigyMaterialsPanel _materialsPanel;
	private EffigyRigPanel _rigPanel;
	private Widget _leftPanel;

	// --- View menu dock toggles ----------------------------------------------------------------

	/// <summary>The View menu's one-per-dock checkable options, kept so their ticks can be pointed
	/// back at what DockManager actually has open. Held as fields because the menu is built before
	/// the docks exist, so the ticks cannot be right at the moment they are created.</summary>
	private Option _featuresDockOption, _materialsDockOption, _rigDockOption, _tutorialDockOption,
		_consoleDockOption;

	/// <summary>Set while SyncDockChecks writes the ticks. Assigning Checked fires Toggled just as
	/// a click does, and without this the sync would turn straight round and re-issue SetDockState
	/// for every dock it was only supposed to be reading.</summary>
	private bool _syncingDockChecks;

	/// <summary>
	/// The tool chrome: a row of stage tabs over the tools belonging to the selected stage, docked
	/// above the viewport.
	///
	/// ONE BAR WHERE THERE WERE THREE STRIPS. EffigyStageBar's header carries the whole argument;
	/// the short of it is that fifty anonymous 54px squares taking turns in one floating spot
	/// could not say which of the three sets was showing, could not afford to label any of them,
	/// and covered the corner of the part while failing to.
	/// </summary>
	private EffigyStageBar _stageBar;

	/// <summary>The ADD/REMOVE mode strip, shown only while a feature that HAS a Result is open.
	/// See EffigyResultStrip for why it is on the canvas rather than in the dialog.</summary>
	private EffigyResultStrip _resultStrip;

	/// <summary>
	/// The stage sets, one per mode, built once at startup and kept.
	///
	/// DATA, NOT WIDGETS. The bar paints from these and never owns them, so entering a sketch is
	/// handing it a different list rather than tearing down and rebuilding a row of buttons — and
	/// a tool's live state (which variant is on its face, whether it is armed) survives every
	/// stage change and every mode swap, because it was never on a widget to begin with.
	/// </summary>
	private List<EffigyStage> _partStages;
	private List<EffigyStage> _sketchStages;
	private List<EffigyStage> _sculptStages;
	private List<EffigyStage> _paintStages;
	private List<EffigyStage> _sculptHomeStages;
	private List<EffigyStage> _paintHomeStages;

	// Which mode's stages the bar is showing lives in EffigyWindow.Workspaces.cs, as the BarMode
	// PROPERTY rather than a plain field: assigning it is also what re-lights the workspace
	// switcher and re-lays the docks, and six methods assign it. See the comment there.

	/// <summary>The sketch tools by the kind they arm, so a tool armed from a shortcut can have its
	/// tick put on the right button — which may be sitting on a stage nobody is looking at.</summary>
	private readonly List<(EffigyStageTool Tool, SketchToolKind Kind, int Variant)> _sketchTools = new();

	private EffigyStageTool _constructionTool;
	private EffigyStageTool _inspectorTool;

	/// <summary>
	/// The feature tools, by the feature they make, so the tutorial can light one up.
	///
	/// SAFE TO HOLD, which the button dictionary this replaced was not. That one had to be cleared
	/// and refilled inside the strip refresh and nowhere else, because the strip was torn down
	/// whenever the document gained its first sketch and every button held from before belonged to
	/// a widget that was gone. These are data objects owned for the life of the window; there is
	/// no torn-down widget left for a stale reference to point at.
	/// </summary>
	private readonly Dictionary<ToolKind, EffigyStageTool> _featureTools = new();

	/// <summary>Which tool the tutorial is currently asking for, if any. Held as the target
	/// rather than as a button for the reason above.</summary>
	private EffigyToolTarget? _highlightedTool;

	private EffigyTutorial _tutorial;
	private EffigyTutorialPanel _tutorialPanel;
	private EffigyConsolePanel _consolePanel;

	private DockWidget _centralDock;
	private StatusBar _statusWidget;
	private Editor.Label _statusInfoLabel;
	private Editor.Label _promptLabel;

	/// <summary>
	/// The open window, for console diagnostics to talk to.
	///
	/// A ConCmd is static and the studio it needs to inspect is not, and there is no other route
	/// from the console to the live document. Only ever read by effigy_dump_tree - nothing in the
	/// tool's own behaviour depends on it, so a stale one after a crash costs a wrong dump and
	/// nothing more.
	/// </summary>
	// Public rather than internal: the diagnostics that read the open document live outside this
	// library, in whatever project mounts it, and this is the only handle they have on it.
	public static EffigyWindow Current;

	/// <summary>The live part studio, for effigy_dump_tree to read. Read-only by intent - the
	/// diagnostic prints, it does not touch the document.</summary>
	public PartStudio DiagnosticStudio => _studio;

	/// <summary>Which stage set the bar is showing, for the sketch probe.
	///
	/// EnterSketch swaps the mode BEFORE it does anything else, so this is the cheapest evidence in
	/// the editor about whether entering a sketch actually happened: still Part means EnterSketch
	/// never ran, Sketch with no active sketch means it ran and BeginSketch did not take. Reading
	/// it off a screenshot is what this replaces, and a screenshot cannot tell those two apart when
	/// the swap itself is the thing in doubt.
	///
	/// Kept in the shape the probe already speaks — two bools — because the modes are exclusive by
	/// construction now and "both true" has become unrepresentable rather than merely unlikely.</summary>
	internal (bool Feature, bool Sketch) DiagnosticStripState
		=> (BarMode == EffigyBarMode.Part, BarMode == EffigyBarMode.Sketch);

	/// <summary>The feature whose sketch is open, if any - so the probe can say whether the window
	/// and the viewport agree about that.</summary>
	internal string DiagnosticSketchFeature => ActiveSketchFeature()?.Name;

	/// <summary>The viewport, for the drop probe — which needs to ask it about its canvas and its
	/// camera from a static console command, with no drag in progress to carry the question.
	/// </summary>
	internal EffigyViewport DiagnosticViewport => _viewport;

	public EffigyWindow()
	{
		Current = this;

		DeleteOnClose = true;
		Size = new Vector2( 1440, 900 );

		if ( AppIcon() is { } icon )
			SetWindowIcon( icon );
		else
			SetWindowIcon( "view_in_ar" );

		_studio = new PartStudio();

		// The engine boolean, in front of the kernel before anything can ask for a cut. Remove was
		// wired end to end and waiting on exactly this one translation; see EffigyMeshBoolean.
		EffigyMeshBoolean.Install();

		BuildMenuBar();
		BuildDocks();
		BuildToolbar();
		BuildStatusBar();

		// Last session's palette and grid choice, now that the viewport exists to receive them.
		// ApplyPalette runs inside this.
		RestoreSettings();

		// A window that has only just opened has nothing to lose, and anything during startup that
		// went through RebuildStudio has already set the flag. Without this, closing an untouched
		// Effigy asks whether to save an empty studio — the fastest way to teach someone to click
		// through the very prompt that exists to save their work.
		MarkClean();

		// Opened, not started. The panel shows its start screen and waits — being dropped into
		// step one of something you never asked for is the reason tutorials get resented, and the
		// checkbox on that screen is how someone says never again.
		if ( EffigyTutorial.OpenOnStartup )
			DockManager.SetDockState( "Tutorial", true );

		// After the docks, because BuildMenuBar ran before them and after StateCookie, because a
		// restored layout is the case where the ticks would otherwise be furthest from the truth.
		SyncDockChecks();

		Show();
	}

	/// <summary>
	/// Green-man / oak-face mark for the window tab. The Tools menu itself only takes a Material
	/// Icon name (see the EditorApp attribute) — a pixmap there would go blank — so this is the
	/// place a custom drawing actually shows.
	/// </summary>
	internal static Pixmap AppIcon()
	{
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrEmpty( root ) )
			return null;

		foreach ( var rel in new[]
		{
			Path.Combine( "Editor", "EffigyEditor", "effigy_icon.png" ),
			Path.Combine( "Assets", "editor", "effigy_icon.png" ),
		} )
		{
			var path = Path.Combine( root, rel );
			if ( File.Exists( path ) )
				return Pixmap.FromFile( path );
		}

		return null;
	}

	// --- menu bar ---------------------------------------------------------------------------

	private void BuildMenuBar()
	{
		var file = MenuBar.FindOrCreateMenu( "File" );
		file.Clear();
		file.AddOption( "New Studio", "common/new.png", NewStudio );
		file.AddOption( "Open...", "folder_open", Open );
		file.AddSeparator();
		file.AddOption( "Save", "common/save.png", Save, "editor.save" );
		file.AddOption( "Save As...", "save_alt", SaveAs );
		file.AddSeparator();
		file.AddOption( "Export OBJ", "file_download", ExportObj );
		file.AddOption( "Compile .vmdl", "build", CompileVmdl );
		file.AddOption( "Animation Clips...", "movie", OpenAnimClips );
		file.AddOption( "Collision Report", "fitness_center", ReportCollision );
		file.AddSeparator();
		file.AddOption( "Close", "close", Close );

		var edit = MenuBar.FindOrCreateMenu( "Edit" );
		edit.Clear();
		edit.AddOption( "Undo", "undo", Undo, "editor.undo" );
		edit.AddOption( "Redo", "redo", Redo, "editor.redo" );
		edit.AddSeparator();

		// The same four the feature tree's own right-click menu carries. They are mirrored here
		// because the menu bar is where someone who has never right-clicked the tree goes looking,
		// and because a feature can be selected while focus is somewhere the tree is not.
		edit.AddOption( "Delete Feature", "delete", DeleteSelectedFeature );
		edit.AddOption( "Move Feature Up", "arrow_upward", MoveFeatureUp );
		edit.AddOption( "Move Feature Down", "arrow_downward", MoveFeatureDown );
		edit.AddOption( "Suppress / Unsuppress Feature", "block", ToggleSuppressFeature );

		// EVERYTHING TO DO WITH THE SCULPT MASK, behind one line. Five flat entries for actions that
		// only work while a Sculpt feature is open was five-fifteenths of this menu given over to a
		// mode most sessions never enter; the submenu keeps them reachable without making them the
		// first thing the menu shows.
		edit.AddSeparator();
		var mask = edit.AddMenu( "Sculpt Mask", "brush" );
		mask.AddOption( "Invert", "flip", InvertSculptMask );
		mask.AddOption( "Clear", "layers_clear", ClearSculptMask );
		mask.AddOption( "Mask Everything", "select_all", ProtectAllSculpt );
		mask.AddSeparator();
		mask.AddOption( "Switch Between Painting and Erasing", "brush", ToggleSculptMaskErase );
		mask.AddOption( "Hide / Show Held Geometry", "visibility_off", ToggleHideMasked );

		edit.AddSeparator();
		edit.AddOption( "Settings...", "settings", OpenSettings );

		// The Help menu exists for exactly one thing, and that is fine. Until it was added there
		// was no way to start the tutorial again after dismissing it, and a tutorial you can only
		// ever see once is one nobody dares skip.
		var help = MenuBar.FindOrCreateMenu( "Help" );
		help.Clear();
		help.AddOption( "Start House Tutorial", "school", StartTutorial );

		var view = MenuBar.FindOrCreateMenu( "View" );
		view.Clear();
		view.AddOption( "Frame Camera", "center_focus_strong", () => _viewport?.FrameCamera() );
		view.AddOption( "Normal to Sketch Plane\tN", "straighten", () => _viewport?.ViewNormalToSketchPlane() );
		view.AddOption( "Shade Material Slots", "palette", ToggleMaterialShading );
		view.AddOption( "Show Sketch Constraints", "rule", ToggleConstraintMarks );
		view.AddOption( "Add Point Light", "wb_incandescent", AddViewportLight );

		// "restart_alt" is a Material SYMBOLS name and s&box ships classic Material Icons, so it
		// was drawing nothing at all - see EffigyIcons for why that whole class of name is unsafe.
		view.AddOption( "Reset Origin", "settings_backup_restore", () => _viewport?.ResetOrigin() );

		// EVERY DOCK GETS A LINE HERE, and the Material Browser was the one that did not have one.
		// It is registered, it is wired up, and until now the only way to see it was a default
		// layout that happened to open it - so anyone who closed the tab had lost it for good.
		//
		// The ticks are set from DockManager itself (SyncDockChecks, run once the layout exists)
		// rather than hardcoded here: this menu is built BEFORE the docks are, and a hardcoded
		// tick is a lie the moment a saved layout restores something different.
		view.AddSeparator();
		_featuresDockOption = AddDockOption( view, "Feature Tree", "account_tree", "Features" );
		_materialsDockOption = AddDockOption( view, "Material Browser", "palette", "Materials" );
		_rigDockOption = AddDockOption( view, "Rig", "polyline", "Rig" );
		_tutorialDockOption = AddDockOption( view, "Tutorial", "school", "Tutorial" );
		_consoleDockOption = AddDockOption( view, "Console", "terminal", "Console" );

		// Named views, same list Onshape puts on the cube. The cube itself is gone — this camera
		// flies rather than orbiting a locked-up model — but snapping to a plane is still useful.
		view.AddSeparator();

		foreach ( var standard in new[]
		{
			EffigyViewport.StandardView.Isometric,
			EffigyViewport.StandardView.Top,
			EffigyViewport.StandardView.Bottom,
			EffigyViewport.StandardView.Front,
			EffigyViewport.StandardView.Back,
			EffigyViewport.StandardView.Left,
			EffigyViewport.StandardView.Right,
		} )
		{
			var v = standard;
			view.AddOption( v.ToString(), "videocam", () => _viewport?.SetStandardView( v ) );
		}

		// The palette list used to sit here as four checkable options. It lives in Edit > Settings
		// now, as a dropdown — one home per setting, because two controls reading the same value
		// is how one of them ends up showing the wrong tick.
	}

	/// <summary>One checkable View entry for one registered dock. The label and the dock title are
	/// separate arguments because they differ: the dock the menu calls "Feature Tree" is registered
	/// as "Features", and SetDockState only answers to the registered name.</summary>
	private Option AddDockOption( Menu menu, string label, string icon, string dockTitle )
	{
		var option = menu.AddOption( label, icon );
		option.Checkable = true;

		option.Toggled += visible =>
		{
			if ( _syncingDockChecks )
				return;

			DockManager.SetDockState( dockTitle, visible );
		};

		return option;
	}

	/// <summary>Point the View menu's ticks at the docks that are actually open.
	///
	/// Called once the layout exists and again after anything here opens a dock. A tick that
	/// disagrees with the screen is worse than no tick: clicking a ticked entry for a closed dock
	/// "closes" it again and the panel never appears, which reads as a dead menu item.</summary>
	private void SyncDockChecks()
	{
		_syncingDockChecks = true;

		try
		{
			SetDockCheck( _featuresDockOption, "Features" );
			SetDockCheck( _materialsDockOption, "Materials" );
			SetDockCheck( _rigDockOption, "Rig" );
			SetDockCheck( _tutorialDockOption, "Tutorial" );
			SetDockCheck( _consoleDockOption, "Console" );
		}
		finally
		{
			_syncingDockChecks = false;
		}
	}

	private void SetDockCheck( Option option, string dockTitle )
	{
		if ( option is not null )
			option.Checked = DockManager.IsDockOpen( dockTitle );
	}

	// --- the stage bar -------------------------------------------------------------------------

	/// <summary>Stage names. Constants because the lock rule below has to name one of them, and a
	/// typo in a string literal there would silently lock the starter stage instead.</summary>
	private const string StageSketch = "Sketch";
	private const string StageSolid = "Solid";
	private const string StageDetail = "Detail";
	private const string StageRepeat = "Repeat";
	private const string StageFinish = "Finish";

	private void BuildToolbar()
	{
		// DOCKED ABOVE THE VIEWPORT, not floating on it. The strips this replaced sat on the canvas
		// at its top-left, which is exactly where a part's own top-left corner is, and they could
		// not be transparent - a widget that declines to paint keeps whatever was in the buffer, so
		// the "floating" strip was an opaque band over the model the whole time. A bar of five or
		// six NAMED buttons is chrome you read rather than a wall you want off your part, so it
		// takes its own band and gives the 3D view back its corner.
		_stageBar = new EffigyStageBar( _viewport ) { StageChanged = OnStageChanged };

		// ABOVE the stage bar, and the outermost ring of chrome in the window: which part of the
		// pipeline you are in is the coarsest question the tool asks, so it is answered furthest
		// out. See EffigyWorkspaceBar and EffigyWindow.Workspaces.cs.
		_workspaceBar = new EffigyWorkspaceBar( _viewport ) { Switched = SetWorkspace };

		// Still on the canvas, and still under the tools: the question it answers - "is this about
		// to cut?" - is asked while looking at the MODEL, not at the parameter list.
		_resultStrip = new EffigyResultStrip( _viewport.Canvas ) { Changed = OnResultStripChanged };

		_viewport.CompleteLayout( _workspaceBar, _stageBar, _resultStrip );

		// The sculpt number bar keeps its floating spot - it belongs to the stroke you are making,
		// not to the tool you picked, and it wants to be near the model rather than up in chrome.
		_sculptBar = new EffigySculptBar( _viewport.Canvas ) { Changed = OnSculptBarChanged };

		_viewport.AddSculptOverlay( _sculptBar );

		// The grid switch, at the right-hand end of the tool row and only while a sketch is open.
		// On the chrome with the tools rather than floating on the model: it belongs to the mode
		// you are in, the way the tool buttons beside it do. Changed saves the setting through the
		// same path the settings dialog uses, so flipping it here is remembered.
		_gridBar = new EffigySketchGridBar( _stageBar, _viewport ) { Changed = OnGridBarChanged };

		_stageBar.SetToolRowTrailing( _gridBar );
		_viewport.SketchGridBar = _gridBar;

		_viewport.SculptStrokeFinished = NoteSculptEdited;
		_viewport.SculptSettingsChanged = OnSculptSettingsChanged;

		// The paint bar, in the same floating spot the sculpt bar keeps — it is about the stroke
		// you are making, not the tool you picked.
		_paintBar = new EffigyPaintBar( _viewport.Canvas )
		{
			Changed = OnPaintBarChanged,
			BlendChanged = OnPaintBlendChanged,
		};

		_materialBrushBar = new EffigyMaterialBrushBar( _viewport.Canvas ) { Changed = OnPaintBarChanged };
		_viewport.AddPaintOverlay( _materialBrushBar );
		_viewport.MaterialDabbed = OnMaterialDabbed;
		_viewport.MaterialStrokeStarted = OnMaterialStrokeStarted;

		_viewport.AddPaintOverlay( _paintBar );

		_viewport.PaintStrokeFinished = OnPaintStrokeFinished;
		_viewport.PaintSettingsChanged = OnPaintSettingsChanged;

		_viewport.NoteChanged = OnNoteEdited;
		_viewport.NoteTextRequested = PromptNoteText;

		// BUILT ONCE, ALL THREE, at startup. The bar paints from these lists and never owns them,
		// so a mode change is an assignment rather than a teardown - which is what lets a tool's
		// armed state and its chosen variant survive leaving a sketch and coming back to it.
		_partStages = BuildPartStages();
		_sketchStages = BuildSketchStages();
		_sculptStages = BuildSculptStages();
		_paintStages = BuildPaintStages();
		_sculptHomeStages = BuildSculptHomeStages();
		_paintHomeStages = BuildPaintHomeStages();
		_rigStages = BuildRigStages();

		// The rig tools read their armed state off the panel, and the panel changes it without
		// being asked — Escape closes a chain, clicking another bone disarms an assign. Wired here
		// rather than in BuildDocks because the tools these ticks live on have only just been made.
		if ( _rigPanel is not null )
			_rigPanel.ToolStateChanged = UpdateRigChecks;

		ShowPartStages( force: true );
	}

	/// <summary>
	/// The bar moved to another stage, so whatever lives on its buttons has to be pushed onto the
	/// ones now showing.
	///
	/// The state itself never moved - it is on the tool objects, not the buttons - but the row only
	/// paints the stage in front of it, and the tutorial highlight has to be re-evaluated because
	/// the button it wants may have just arrived on screen or left it.
	/// </summary>
	private void OnStageChanged()
	{
		if ( BarMode == EffigyBarMode.Part )
			_partStage = _stageBar.SelectedIndex;
		else if ( BarMode == EffigyBarMode.Rig )
			_rigStage = _stageBar.SelectedIndex;

		ApplyToolHighlight();
	}

	/// <summary>Which part-studio stage was last looked at, so leaving a sketch comes back to where
	/// you were rather than to the front of the bar.</summary>
	private int _partStage;

	/// <summary>
	/// Rebuild the three stage tables after a hotload.
	///
	/// EVERY ACTION ON A STAGE TOOL IS A LAMBDA, and the tables holding them outlive the assembly
	/// those lambdas were compiled into. That is not a theoretical worry in this editor: the sketch
	/// strip this replaced was built exactly once and never rebuilt, so on every hotload its
	/// VariantChosen closures rotted, the hotloader logged "Unable to find matching substitution
	/// for a lambda method", and every sketch tool went quietly dead - still highlighting, still
	/// checking, calling nothing. The feature strip escaped it only because RefreshToolStrip tore
	/// the whole thing down and rebuilt the closures whenever a sketch was finished.
	///
	/// Stages are never torn down by ordinary use, so there is no refresh to hide behind and the
	/// rebuild has to be asked for outright. Nothing is lost by it: every piece of state on a tool
	/// is a fact about the VIEWPORT - which sketch tool is armed, whether construction is on, what
	/// the sculpt session is doing - so the fresh tables are re-derived from the thing that
	/// actually knows, rather than copied off the objects being thrown away.
	/// </summary>
	[Event( "hotloaded" )]
	private static void OnHotloaded() => Current?.RebuildStages();

	private void RebuildStages()
	{
		if ( _stageBar is null )
			return;

		var stage = _stageBar.SelectedIndex;

		// The registries point at the old tools; clearing them here is what stops the rebuilt ones
		// being added alongside a set of dead duplicates.
		_featureTools.Clear();
		_sketchTools.Clear();
		_brushTools.Clear();

		_partStages = BuildPartStages();
		_sketchStages = BuildSketchStages();
		_sculptStages = BuildSculptStages();
		_paintStages = BuildPaintStages();
		_sculptHomeStages = BuildSculptHomeStages();
		_paintHomeStages = BuildPaintHomeStages();
		_rigStages = BuildRigStages();

		// Same reason as StageChanged below: a lambda compiled into the dead assembly is a rig tool
		// that still highlights and calls nothing.
		if ( _rigPanel is not null )
			_rigPanel.ToolStateChanged = UpdateRigChecks;

		if ( _workspaceBar is not null )
			_workspaceBar.Switched = SetWorkspace;

		// A hotload taken inside the rig workspace: the mode field survived, a newly-added viewport
		// flag did not. See SyncViewportMode.
		SyncViewportMode();

		// A method group rather than a lambda, so this one migrates on its own - but it costs
		// nothing to be certain, and a bar with no StageChanged is a tutorial highlight that
		// silently stops following the reader.
		_stageBar.StageChanged = OnStageChanged;

		switch ( BarMode )
		{
			case EffigyBarMode.Sketch:
				_stageBar.SetFinish( "Finish", FinishSketch );
				_stageBar.SetStages( _sketchStages, stage );

				UpdateSketchToolChecks( _viewport?.SketchTool ?? SketchToolKind.Select );

				if ( _constructionTool is not null )
					_constructionTool.Checked = _viewport?.ConstructionMode ?? false;

				if ( _inspectorTool is not null )
					_inspectorTool.Checked = _viewport?.ProfileInspector ?? true;

				_stageBar.Refresh();
				break;

			case EffigyBarMode.Sculpt:
				if ( _viewport is { IsSculpting: true } )
				{
					_stageBar.SetFinish( "Finish", FinishSculpt );
					_stageBar.SetStages( _sculptStages, stage );

					UpdateSculptChecks();
				}
				else
				{
					// The sculpt workspace's landing bar — Subdivide and Sculpt — when no feature
					// is open. Brushes arrive with a sculpt, not with the workspace.
					_stageBar.SetFinish( null, null );
					_stageBar.SetStages( _sculptHomeStages, stage );
				}
				break;

			case EffigyBarMode.Paint:
				if ( _viewport is { IsPainting: true } )
				{
					_stageBar.SetFinish( "Finish", FinishPaint );
					_stageBar.SetStages( _paintStages, stage );
				}
				else
				{
					_stageBar.SetFinish( null, null );
					_stageBar.SetStages( _paintHomeStages, stage );
				}
				break;

			case EffigyBarMode.Rig:
				// No finish — a rig has no feature to commit to. See EnterRig.
				_stageBar.SetFinish( null, null );
				_stageBar.SetStages( _rigStages, stage );

				UpdateRigChecks();
				break;

			default:
				_partStage = stage;
				ShowPartStages( force: true );
				break;
		}
	}

	// --- part stages ---------------------------------------------------------------------------

	/// <summary>
	/// The creation tools, grouped by the CreateTools table's own Stage column.
	///
	/// GROUPED FROM THE TABLE rather than listed again here, so there is still exactly one place
	/// that says what tools exist. The old strip read the same table and drew all nineteen in a
	/// row with a wider gap every four or five; the gaps were the grouping, they were unlabelled,
	/// and at 30px between buttons they read as uneven spacing rather than as meaning anything.
	/// The stage names are those gaps, said out loud.
	/// </summary>
	private List<EffigyStage> BuildPartStages()
	{
		var stages = new List<EffigyStage>();

		foreach ( var tool in CreateTools )
		{
			var stage = stages.FirstOrDefault( s => s.Name == tool.Stage );

			if ( stage is null )
			{
				stage = new EffigyStage { Name = tool.Stage };
				stages.Add( stage );
			}

			// Only the KIND is captured, never the table entry. An enum carries across a hotload
			// where a reference into a table built by a dead assembly does not.
			var kind = tool.Kind;

			var entry = new EffigyStageTool
			{
				Icon = tool.Icon,
				Label = tool.Label,
				Tip = tool.Tip,
			};

			// Variants OR a plain action, never both: a button with variants runs the one on its
			// face, so a Clicked sitting behind that would be unreachable rather than harmless.
			if ( tool.Choices is { Length: > 0 } )
				entry.Variants = ChoiceVariants( tool, kind );
			else if ( kind == ToolKind.Paint )
				entry.Clicked = AddPaint;
			else
				entry.Clicked = () => AddFeature( NewFeature( kind, -1 ) );

			stage.Add( entry );

			_featureTools[kind] = entry;
		}

		AddNoteTools( stages );

		return stages;
	}

	/// <summary>
	/// The grease pencil, on the end of the Sketch stage.
	///
	/// NOT IN THE CreateTools TABLE, even though it sits among tools that are. Every entry in that
	/// table is a FEATURE — it carries a ToolKind and its button calls AddFeature — and a note is
	/// deliberately not one of those (see PartStudio.Notes). Giving it a fake ToolKind to get it
	/// onto the bar would put annotation one careless switch statement away from the feature tree,
	/// which is precisely the coupling the whole design is avoiding. It is added here instead, to
	/// the stage the table already built.
	///
	/// The SKETCH stage because that is where the other thing you do with a pointer and a plane
	/// lives, and because it is the one stage that is never locked — a note on an empty studio
	/// ("start from the bracket drawing") is a reasonable first thing to want, and every other
	/// stage is dimmed until there is a body to act on.
	/// </summary>
	private void AddNoteTools( List<EffigyStage> stages )
	{
		if ( stages.FirstOrDefault( s => s.Name == StageSketch ) is not { } sketch )
			return;

		// One button rather than two. The pen and its colour used to be a checkable button sitting
		// next to a second, un-checkable one that existed only to hold the palette; VariantsAreSettings
		// is what lets the merged button keep both jobs — a plain click always arms or puts down the
		// pen, and the chevron behind it opens the same palette the second button used to be. Built
		// from the palette rather than written out again, for the same reason the Primitive menu is
		// built from PrimitiveFeature.Shape: a menu naming a colour the kernel has never heard of
		// would set an index meaning something else.
		_noteTool = new EffigyStageTool
		{
			Icon = EffigyIcon.NoteTool,
			Label = "Note",
			Tip = "Grease pencil — scribble notes over the part. Never exported.",
			Checkable = true,
			VariantsAreSettings = true,
			IconColor = SwatchColor( 0 ),
			Variants = Enumerable.Range( 0, NotePalette.Count ).Select( i => new EffigyStageVariant
			{
				Icon = EffigyIcon.NoteTool,
				Label = NotePalette.NameAt( i ),
				Tip = $"Draw notes in {NotePalette.NameAt( i ).ToLowerInvariant()}",
				Chosen = () => SetNoteColor( i ),
			} ).ToArray(),
		};

		_noteTool.Clicked = ToggleNotePen;

		_noteEraseTool = new EffigyStageTool
		{
			Icon = EffigyIcon.NoteEraseTool,
			Label = "Erase",
			Tip = "Eraser (E) — hold the left button and drag through the notes you want gone",
			Checkable = true,
		};

		_noteEraseTool.Clicked = ToggleNoteEraser;

		sketch.Add( _noteTool );
		sketch.Add( _noteEraseTool );
	}

	/// <summary>A palette swatch as the Color the toolbar paints with.</summary>
	private static Color SwatchColor( int index )
	{
		var swatch = NotePalette.At( index );

		return new Color( swatch.R, swatch.G, swatch.B );
	}

	/// <summary>
	/// A feature that comes in several shapes - Primitive - as one button with the shapes behind
	/// its chevron.
	///
	/// A CHANGE OF BEHAVIOUR, and a deliberate one. Clicking Primitive used to open the shape menu
	/// and nothing else, so adding a second cube was two clicks every time. It now works like every
	/// other tool that has variants: the button makes whatever is on its face, and the chevron
	/// picks a different shape and leaves it there.
	/// </summary>
	private EffigyStageVariant[] ChoiceVariants( CreateTool tool, ToolKind kind )
	{
		var variants = new EffigyStageVariant[tool.Choices.Length];

		for ( var i = 0; i < tool.Choices.Length; i++ )
		{
			var choice = i;

			variants[i] = new EffigyStageVariant
			{
				Icon = tool.Icon,
				Label = tool.Choices[choice],
				Tip = tool.Tip,
				Chosen = () => AddFeature( NewFeature( kind, choice ) ),
			};
		}

		return variants;
	}

	/// <summary>
	/// Put the part-studio stages on the bar, with the lock state recomputed.
	///
	/// Cheap enough to call whenever anything might have changed: it compares the lock it wants
	/// against the lock each stage already has and does nothing at all when they agree, which is
	/// the common case.
	/// </summary>
	private void ShowPartStages( bool force = false )
	{
		if ( _stageBar is null || _partStages is null )
			return;

		var reason = StarterLockReason();
		var changed = force || BarMode != EffigyBarMode.Part;

		// Was everything past the starter stage locked a moment ago? Asked before the loop
		// rewrites it, because the answer decides where to land.
		var wasLocked = _partStages.Any( s => s.Locked );

		foreach ( var stage in _partStages )
		{
			var locked = stage.Name == StageSketch ? null : reason;

			if ( stage.LockedReason == locked )
				continue;

			stage.LockedReason = locked;
			changed = true;
		}

		if ( !changed )
			return;

		BarMode = EffigyBarMode.Part;

		_stageBar.Mode = null;
		_stageBar.SetFinish( null, null );

		// JUST UNLOCKED means the first sketch was finished a moment ago, and the thing anybody
		// wants next is to pull it into a solid. Landing back on the starter stage would be
		// landing on the two tools that have just stopped being the only option.
		var land = wasLocked && reason is null
			? _partStages.FindIndex( s => s.Name == StageSolid )
			: _partStage;

		_stageBar.SetStages( _partStages, land );

		// Coming back from a sketch or a sculpt, the part tools are on screen again and their
		// selection marks were last set for whatever was selected before we left. Nothing else
		// will recompute them until the next selection change, which may never come if the user
		// finishes a sketch and goes straight for a tool.
		MarkToolsTakingSelection();
	}

	/// <summary>
	/// Why everything past the starter stage is unavailable, or null when it is not.
	///
	/// SOMETHING TO ACT ON, not specifically a sketch. Extrude, Fillet, Shell and the rest all need
	/// geometry, and adding one before there is any produces a feature that goes straight to red.
	/// The strip this replaced enforced that by HIDING seventeen of nineteen buttons until a sketch
	/// had curves in it - which also meant a studio begun with a Primitive, the other tool on the
	/// starter stage and a perfectly good way to start a part, sat there with a cube on screen and
	/// no fillet, no shell and no mirror to use on it. A body counts too, and the rule is now
	/// written on a dimmed tab instead of being enforced by disappearance.
	/// </summary>
	private string StarterLockReason() =>
		HasConfirmedSketch() || (_studio?.Bodies.Count ?? 0) > 0
			? null
			: "Draw a sketch or add a primitive first — these tools need something to act on";

	// --- sketch stages -------------------------------------------------------------------------

	/// <summary>
	/// The tools from Onshape's sketch row that this kernel can actually build.
	///
	/// Line, rectangle, circle, arc, polygon and point all map onto SketchLine / SketchArc /
	/// SketchCircle. The rest of Onshape's row — dimensions, constraints — has no kernel behind it,
	/// so those buttons are absent rather than present and dead.
	///
	/// SELECT IS ON EVERY STAGE. It is the neutral state every other tool falls back to - Escape
	/// lands on it, finishing a shape lands on it - so putting it behind a tab would make the most
	/// returned-to tool in sketch mode the only one that costs a stage change. It is one tool
	/// object appearing in four lists, not four tools, so its armed state cannot disagree with
	/// itself.
	/// </summary>
	private List<EffigyStage> BuildSketchStages()
	{
		var select = SketchTool( EffigyIcon.SelectTool, "Select",
			"Select - drag a point, or the grip at the middle of a curve; click points and curves to select them, and a selected point brings the rest of the selection with it",
			SketchToolKind.Select );

		var draw = new EffigyStage { Name = "Draw" };

		draw.Add( select );

		draw.Add( SketchGroup(
			new SketchToolVariant( EffigyIcon.LineTool, "Line",
				"Line - click start, click end; keeps chaining until Escape", SketchToolKind.Line ),
			new SketchToolVariant( EffigyIcon.LineMidpointTool, "Midpoint line",
				"Midpoint line - click the middle, then one end; it grows both ways", SketchToolKind.LineMidpoint ) ) );

		// The families that have more than one way to place them. Each is ONE button with the
		// alternatives behind its chevron, which is how Onshape's sketch row is arranged.
		draw.Add( SketchGroup(
			new SketchToolVariant( EffigyIcon.RectangleTool, "Rectangle",
				"Corner rectangle - click two opposite corners", SketchToolKind.Rectangle ),
			new SketchToolVariant( EffigyIcon.RectangleCentreTool, "Centre rectangle",
				"Centre rectangle - click the centre, then a corner", SketchToolKind.RectangleCentre ) ) );

		draw.Add( SketchGroup(
			new SketchToolVariant( EffigyIcon.CircleTool, "Circle",
				"Centre circle - click the centre, then a point on the rim", SketchToolKind.Circle ),
			new SketchToolVariant( EffigyIcon.CircleThreePointTool, "3 point circle",
				"3-point circle - click three points on the rim", SketchToolKind.CircleThreePoint ) ) );

		draw.Add( SketchGroup(
			new SketchToolVariant( EffigyIcon.ArcTool, "Arc",
				"Centre arc - click the centre, the start, then the end direction", SketchToolKind.Arc ),
			new SketchToolVariant( EffigyIcon.ArcThreePointTool, "3 point arc",
				"3-point arc - click start, end, then a point it passes through", SketchToolKind.ArcThreePoint ) ) );

		draw.Add( SketchTool( EffigyIcon.PointTool, "Point", "Point - click to place", SketchToolKind.Point ) );

		var shapes = new EffigyStage { Name = "Shapes" };

		shapes.Add( select );

		shapes.Add( SketchGroup(
			new SketchToolVariant( EffigyIcon.PolygonTool, "Polygon",
				"Inscribed polygon - click the centre, then a corner", SketchToolKind.Polygon ),
			new SketchToolVariant( EffigyIcon.PolygonCircumscribedTool, "Circumscribed polygon",
				"Circumscribed polygon - click the centre, then an edge midpoint", SketchToolKind.PolygonCircumscribed ) ) );

		shapes.Add( SketchTool( EffigyIcon.SlotTool, "Slot",
			"Slot - click both ends of the centre line, then the width", SketchToolKind.Slot ) );
		shapes.Add( SketchTool( EffigyIcon.EllipseTool, "Ellipse",
			"Ellipse - centre, the long axis, then the bulge", SketchToolKind.Ellipse ) );
		shapes.Add( SketchTool( EffigyIcon.SplineTool, "Spline",
			"Spline - click points, Enter finishes", SketchToolKind.Spline ) );

		// The four that EDIT what is already there get their own stage, because clicking one of
		// them on empty space does nothing and the grouping is what says why.
		var modify = new EffigyStage { Name = "Modify" };

		modify.Add( select );

		modify.Add( SketchTool( EffigyIcon.TrimTool, "Trim",
			"Trim - click the piece of a curve you want gone", SketchToolKind.Trim ) );
		modify.Add( SketchTool( EffigyIcon.ExtendTool, "Extend",
			"Extend - click the end of a curve to stretch it", SketchToolKind.Extend ) );
		modify.Add( SketchTool( EffigyIcon.SketchFilletTool, "Fillet",
			"Fillet - click a corner, then set the radius", SketchToolKind.Fillet ) );
		modify.Add( SketchTool( EffigyIcon.OffsetTool, "Offset",
			"Offset - click a curve, then which side and how far", SketchToolKind.Offset ) );

		// CUT sits with the four edits because that is what it does, but it is worked differently
		// from every other tool here: hold the button and drag, and the line you draw cuts what it
		// passes through. It is Trim swept rather than clicked - same call underneath - so crossing
		// an edge that ends at two corners takes that whole edge, and crossing a lone line takes
		// the line.
		modify.Add( SketchTool( EffigyIcon.CutTool, "Cut",
			"Cut - hold the left button and drag a line through the curves you want gone",
			SketchToolKind.Cut ) );

		// USE and its neighbours reach OUTSIDE the sketch. Everything on the stages above draws or
		// edits what the sketch already contains; these take the outline of the FACE the sketch is
		// sitting on and make it geometry the sketch owns. Until one of them is pressed that
		// outline is scenery - you can snap to it and you cannot build from it, which is the
		// distinction Onshape's Use tool exists to make.
		var reference = new EffigyStage { Name = "Reference" };

		reference.Add( select );

		var use = SketchTool( EffigyIcon.UseTool, "Use",
			"Use - click a green edge of the face this sketch is on to copy it into the sketch",
			SketchToolKind.Use );

		use.IconColor = EffigyToolChrome.ReferenceColor;
		reference.Add( use );

		// A press rather than a mode: there is nothing to aim at, so arming a tool for it would be
		// a step that does nothing but wait for a click anywhere.
		reference.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.UseAllTool,
			Label = "Use all",
			Tip = "Use all - copy the whole outline of the face this sketch is on into the sketch, so a "
				+ "line drawn across it has something to close against",
			IconColor = EffigyToolChrome.ReferenceColor,
			Clicked = () => _viewport.UseAllReferenceEdges(),
		} );

		// Construction geometry is a modifier on whatever tool is active, not a tool of its own -
		// same as Onshape's toggle. SketchCurve.Construction and ProfileFinder's handling of it
		// were already in the kernel with nothing in the UI able to set them.
		_constructionTool = new EffigyStageTool
		{
			Icon = EffigyIcon.ConstructionTool,
			Label = "Construction",
			Tip = "Construction geometry (Q) - reference lines that never become part of a profile",
			Checkable = true,
		};

		_constructionTool.Clicked = () => _viewport.ConstructionMode = _constructionTool.Checked;

		reference.Add( _constructionTool );

		_inspectorTool = new EffigyStageTool
		{
			Icon = EffigyIcon.ProfileInspectorTool,
			Label = "Inspector",
			Tip = "Profile Inspector - shade closed regions and highlight loose ends",
			Checkable = true,
			Checked = true,
		};

		_inspectorTool.Clicked = () => _viewport.ProfileInspector = _inspectorTool.Checked;

		reference.Add( _inspectorTool );

		return new List<EffigyStage> { draw, shapes, modify, reference };
	}

	/// <summary>A tool with only one way to place it: one variant, so no chevron appears.</summary>
	private EffigyStageTool SketchTool( EffigyIcon icon, string label, string tip, SketchToolKind kind ) =>
		SketchGroup( new SketchToolVariant( icon, label, tip, kind ) );

	/// <summary>
	/// One button for a family of tools. The first variant is what it shows to begin with; the
	/// rest sit behind its chevron and take its place once picked.
	/// </summary>
	private EffigyStageTool SketchGroup( params SketchToolVariant[] variants )
	{
		var tool = new EffigyStageTool
		{
			Icon = variants[0].Icon,
			Label = variants[0].Label,
			Tip = variants[0].Tip,
			Checkable = true,
			Checked = variants[0].Kind == SketchToolKind.Select,
			Variants = new EffigyStageVariant[variants.Length],
		};

		for ( var i = 0; i < variants.Length; i++ )
		{
			var variant = variants[i];

			tool.Variants[i] = new EffigyStageVariant
			{
				Icon = variant.Icon,
				Label = variant.Label,
				Tip = variant.Tip,
				Chosen = () =>
				{
					_viewport.SetSketchTool( variant.Kind );
					UpdateSketchToolChecks( variant.Kind );
				},
			};

			// Every variant is registered, not just the one on the face: arming a centre rectangle
			// from anywhere else has to be able to find the button that shows it.
			_sketchTools.Add( (tool, variant.Kind, i) );
		}

		return tool;
	}

	/// <summary>
	/// Only one sketch tool can be active, so the rest have to visibly let go.
	///
	/// Works on the tool DATA rather than on buttons, which is what lets it be right about a tool
	/// sitting on a stage that is not currently painted - arm Circle from the C shortcut while the
	/// Modify stage is showing and the tick is already correct by the time Reveal brings Draw to
	/// the front.
	/// </summary>
	private void UpdateSketchToolChecks( SketchToolKind active )
	{
		EffigyStageTool armed = null;
		var armedVariant = 0;

		foreach ( var (tool, kind, variant) in _sketchTools )
		{
			if ( kind != active )
				continue;

			armed = tool;
			armedVariant = variant;
		}

		foreach ( var (tool, _, _) in _sketchTools )
			tool.Checked = tool == armed;

		// A tool armed from somewhere else - a keyboard shortcut, or Escape dropping back to
		// Select - has to appear on the face of its button, or the bar would show one thing while
		// the viewport did another.
		if ( armed is not null )
			armed.Current = armedVariant;

		_stageBar?.Refresh();
	}

	/// <summary>Bring the stage holding the armed tool to the front. Separate from the check
	/// update because a click on a button that is already on screen must not make the bar jump,
	/// and Reveal is a no-op in exactly that case.</summary>
	private void RevealSketchTool( SketchToolKind active )
	{
		foreach ( var (tool, kind, _) in _sketchTools )
		{
			if ( kind != active )
				continue;

			_stageBar?.Reveal( tool );
			return;
		}
	}

	// --- sculpt mode ---------------------------------------------------------------------------

	private EffigySculptBar _sculptBar;

	/// <summary>The feature being sculpted, so finishing knows what to mark dirty.</summary>
	private SculptFeature _sculptFeature;

	// --- paint mode ---------------------------------------------------------------------------

	private EffigyPaintBar _paintBar;
	private EffigyMaterialBrushBar _materialBrushBar;

	/// <summary>The feature being painted, so finishing knows what to mark dirty.</summary>
	private PaintFeature _paintFeature;

	private readonly List<(EffigyStageTool Tool, BrushKind Kind)> _brushTools = new();
	private EffigyStageTool _maskTool;
	private EffigyStageTool _symmetryTool;

	/// <summary>
	/// The Sculpt workspace's landing bar — the tools you reach for BEFORE a sculpt is open.
	///
	/// The workspace is about sculpting, and sculpting begins with a cage and a Sculpt feature, so
	/// those are the two tools here. The brushes in <see cref="BuildSculptStages"/> only appear once
	/// a sculpt feature is actually entered; this is the bar the workspace shows the rest of the time.
	/// </summary>
	private List<EffigyStage> BuildSculptHomeStages()
	{
		var stage = new EffigyStage { Name = "Sculpt" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Subdivide,
			Label = "Subdivide",
			Tip = "Add a Subdivide — Catmull-Clark subdivision",
			Clicked = () => AddFeature( NewFeature( ToolKind.Subdivide, -1 ) ),
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Sculpt,
			Label = "Sculpt",
			Tip = "Add a Sculpt — brush detail onto the cage in levels",
			Clicked = () => AddFeature( NewFeature( ToolKind.Sculpt, -1 ) ),
		} );

		return new List<EffigyStage> { stage };
	}

	private List<EffigyStage> BuildSculptStages()
	{
		var brush = new EffigyStage { Name = "Brush" };

		brush.Add( BrushTool( EffigyIcon.SculptDraw, "Draw", "Draw — push the surface out along its normal", BrushKind.Draw ) );
		brush.Add( BrushTool( EffigyIcon.SculptSmooth, "Smooth", "Smooth — pull a region towards its own neighbours", BrushKind.Smooth ) );
		brush.Add( BrushTool( EffigyIcon.SculptInflate, "Inflate", "Inflate — push out in every direction at once", BrushKind.Inflate ) );
		brush.Add( BrushTool( EffigyIcon.SculptGrab, "Grab", "Grab — drag the surface sideways", BrushKind.Grab ) );
		brush.Add( BrushTool( EffigyIcon.SculptFlatten, "Flatten", "Flatten — cut a region back towards a plane", BrushKind.Flatten ) );
		brush.Add( BrushTool( EffigyIcon.SculptPinch, "Pinch", "Pinch — gather the surface towards the stroke", BrushKind.Pinch ) );

		// The two that change what a stroke DOES rather than which stroke it is. Their own stage
		// because they compose with all six brushes above - they are not a seventh brush.
		var stroke = new EffigyStage { Name = "Stroke" };

		_maskTool = new EffigyStageTool
		{
			Icon = EffigyIcon.SculptMask,
			Label = "Mask",
			Tip = "Mask (M) — paint the part you want held still",
			Checkable = true,
		};

		_maskTool.Clicked = ToggleSculptMasking;

		_symmetryTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Mirror,
			Label = "Symmetry",
			Tip = "Symmetry (X) — mirror every stroke across X",
			Checkable = true,
		};

		_symmetryTool.Clicked = ToggleSculptSymmetry;

		stroke.Add( _maskTool );
		stroke.Add( _symmetryTool );

		var levels = new EffigyStage { Name = "Levels" };

		levels.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.SculptLevelDown,
			Label = "Coarser",
			Tip = "Show one level coarser",
			Clicked = () => StepSculptLevel( -1 ),
		} );

		levels.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.SculptLevelUp,
			Label = "Finer",
			Tip = "Show — or add — one level finer",
			Clicked = () => StepSculptLevel( 1 ),
		} );

		levels.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.SculptBake,
			Label = "Bake",
			Tip = "Bake a normal map from this sculpt onto the cage",
			Clicked = BakeSculpt,
		} );

		return new List<EffigyStage> { brush, stroke, levels };
	}

	private EffigyStageTool BrushTool( EffigyIcon icon, string label, string tip, BrushKind kind )
	{
		var tool = new EffigyStageTool
		{
			Icon = icon,
			Label = label,
			Tip = tip,
			Checkable = true,
			Clicked = () => SetSculptBrush( kind ),
		};

		_brushTools.Add( (tool, kind) );

		return tool;
	}

	/// <summary>
	/// Open a sculpt feature for brushing.
	///
	/// Rolls the model back to just after this feature so the cage is what you see rather than
	/// whatever is stacked on top of it. EditFeature used to do that first, but Edit now calls
	/// this, so the rollback lives here — same as it always did, without the round trip.
	/// </summary>
	private void EnterSculpt( SculptFeature feature )
	{
		if ( feature is null || _viewport is null )
			return;

		// Already in this sculpt: a second Edit must not rebuild the session and drop a stroke.
		if ( BarMode == EffigyBarMode.Sculpt
			&& ReferenceEquals( _sculptFeature, feature )
			&& _viewport.IsSculpting )
			return;

		// Everything else that owns a left-click, shut down in one call — this used to be a
		// hand-kept list of the other modes here and a different partial list in EnterPaint. See
		// LeaveCurrentWorkspace in EffigyWindow.Workspaces.cs.
		LeaveCurrentWorkspace();

		// The cage does not exist until the features above this have run, and rolling to just
		// after this one is also what puts the thing being sculpted on screen. Skip the rebuild
		// when EditFeature already moved the bar here.
		var index = _studio.Features.IndexOf( feature );

		if ( index >= 0 && _studio.RollbackIndex != index + 1 )
		{
			_rollbackBeforeEdit ??= _studio.RollbackIndex;
			_studio.RollbackIndex = index + 1;
			RebuildStudio();
		}

		if ( feature.Sculpt is null )
		{
			// The feature errored, so there is nothing to sculpt on. Its own diagnostic says why far
			// better than anything this could invent. The dialog stays open so a missing body can
			// still be picked — that pick is what produces the cage.
			SetPrompt( feature.Error ?? "This sculpt has no cage yet — the feature below it did not build." );
			return;
		}

		_sculptFeature = feature;

		// What the workspace switcher comes back to. Set only once the door refusals above are
		// past, so a sculpt that would not open is not the one it remembers.
		_lastSculptFeature = feature;

		// The bar becomes the sculpt bar, and says so: brushes and levels behind the tabs, SCULPT
		// and the way out at the right. The dialog closes too — a sculpt is not edited through a
		// parameter list, so leaving one open would be two controls claiming the same feature.
		BarMode = EffigyBarMode.Sculpt;

		_stageBar.Mode = "SCULPT";
		_stageBar.SetFinish( "Finish", FinishSculpt );
		_stageBar.SetStages( _sculptStages );

		_dialog?.Close();

		var session = new SculptSession( feature.Sculpt );
		session.Radius = session.SuggestedRadius;

		_viewport.BeginSculpt( session );
		_sculptBar.Bind( session );
		_viewport.RefreshSculptPreview();

		UpdateSculptChecks();

		SetPrompt( "Sculpt: drag on the model. X mirrors, M masks, the level buttons add detail." );
	}

	private void FinishSculpt()
	{
		if ( _viewport is null || !_viewport.IsSculpting )
			return;

		_viewport.EndSculpt();
		_sculptBar.Bind( null );

		var feature = _sculptFeature;
		_sculptFeature = null;

		// Back to the Sculpt workspace's landing bar, not CAD — Subdivide and Sculpt are the tools
		// you reach for next. Leaving the workspace entirely is the CAD pill's job.
		ShowSculptHome();

		// THE ONLY FULL REBUILD IN SCULPT MODE, and that is the point. Every stroke marks the model
		// changed and refreshes the viewport straight from the session, because rebuilding the whole
		// feature tree per stroke would be both slow and wrong to look at - the tree builds the TOP
		// level while the viewport may be showing a coarser one. The tree catches up here.
		if ( feature is not null )
			_studio.MarkDirty( feature );

		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	private void SetSculptBrush( BrushKind kind )
	{
		if ( _viewport?.SculptSession is not { } session )
			return;

		// Picking a brush leaves masking, or the click would arm a tool that then does not run.
		session.Masking = false;
		session.Brush = kind;

		UpdateSculptChecks();
		_sculptBar?.Refresh();
	}

	// --- paint mode ---------------------------------------------------------------------------

	/// <summary>
	/// The Paint workspace's landing bar — the tools you reach for BEFORE a paint is open.
	///
	/// UV Project unwraps the mesh so colour has somewhere to live, and Paint is the brush itself.
	/// The brush in <see cref="BuildPaintStages"/> only appears once a paint feature is entered.
	/// </summary>
	private List<EffigyStage> BuildPaintHomeStages()
	{
		var stage = new EffigyStage { Name = "Paint" };

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.UVProject,
			Label = "UV Project",
			Tip = "Add a UV Project — re-project UVs (box or planar)",
			Clicked = () => AddFeature( NewFeature( ToolKind.UVProject, -1 ) ),
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.Paint,
			Label = "Paint",
			Tip = "Add a Paint — brush colour straight onto the model",
			Clicked = AddPaint,
		} );

		stage.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.FaceMaterial,
			Label = "Material",
			Tip = "Material brush — drag to lay the Materials browser's selection onto faces",
			Clicked = EnterMaterialBrush,
		} );

		return new List<EffigyStage> { stage };
	}

	/// <summary>
	/// The paint stage set. One stage with one always-armed brush, for now: the colour, radius and
	/// strength live on the floating paint bar, and an eraser or a fill is a later stroke tool, not
	/// a first-slice button that would have nothing behind it.
	/// </summary>
	private List<EffigyStage> BuildPaintStages()
	{
		var brush = new EffigyStage { Name = "Brush" };

		brush.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.PaintBrush,
			Label = "Brush",
			Tip = "Paint — drag on the model to lay colour down",
			Checkable = true,
			Checked = true,
		} );

		return new List<EffigyStage> { brush };
	}

	/// <summary>
	/// Add a Paint feature and step straight into painting it. Paint is not edited through a
	/// parameter dialog — its one input is "which body", which the viewport selection already names
	/// — so unlike every other tool there is no dialog to open on the way in.
	/// </summary>
	private void AddPaint()
	{
		if ( _viewport is null )
			return;

		LeaveCurrentWorkspace();

		var feature = new PaintFeature();

		RecordUndo();
		ApplyIdleGeometrySelection( feature );
		InsertAtRollback( feature );
		RebuildStudio();

		EnterPaint( feature );
	}

	/// <summary>
	/// Open a Paint feature for brushing.
	///
	/// Rolls the model back to just after this feature, the same guarantee EnterSculpt makes, then
	/// builds the session on the body the feature targets. A body whose UVs cannot carry paint is a
	/// refusal at the door rather than an enter-and-discover-later: the paint would scramble, and the
	/// fix (a UV Project in Unwrap mode above this feature) is a tree edit, not a brush setting.
	/// </summary>
	private void EnterPaint( PaintFeature feature )
	{
		if ( feature is null || _viewport is null )
			return;

		// Already painting this feature: a second Edit must not rebuild the session and drop a stroke.
		if ( BarMode == EffigyBarMode.Paint
			&& ReferenceEquals( _paintFeature, feature )
			&& _viewport.IsPainting )
			return;

		LeaveCurrentWorkspace();

		// The body does not exist until the features above this have run, and rolling to just after
		// this one is also what puts the thing being painted on screen.
		var index = _studio.Features.IndexOf( feature );

		if ( index >= 0 && _studio.RollbackIndex != index + 1 )
		{
			_rollbackBeforeEdit ??= _studio.RollbackIndex;
			_studio.RollbackIndex = index + 1;
			RebuildStudio();
		}

		// Paint paints ONE body at a time — one stroke list, one set of vertex colours. Anything else
		// is a door refusal. No unwrap gate: vertex colours need no UVs, which is half the point.
		var targets = _studio.Bodies.Where( b => feature.Bodies.Matches( b ) ).ToList();

		if ( targets.Count != 1 )
		{
			SetPrompt( targets.Count == 0
				? "Paint needs a body to paint on — add a primitive or extrude a sketch first."
				: "Paint paints one body at a time — pick one in the Parts list, then press Paint again." );
			return;
		}

		_paintFeature = feature;
		_lastPaintFeature = feature;

		BarMode = EffigyBarMode.Paint;

		_stageBar.Mode = "PAINT";
		_stageBar.SetFinish( "Finish", FinishPaint );
		_stageBar.SetStages( _paintStages );

		_dialog?.Close();

		// The session replays whatever strokes already exist, so re-entering a painted feature shows
		// the paint as it was left, not a blank surface.
		var session = new PaintSession( targets[0].Mesh, feature.Strokes );
		session.Radius = session.SuggestedRadius;

		_viewport.BeginPaint( session, slot => _studio.MaterialNames.TryGetValue( slot, out var name ) ? name : null );
		_paintBar.Bind( session, feature );
		_viewport.RefreshPaintPreview();

		// SAY IT AT THE DOOR ON A COARSE PART. Paint colours vertices, so a bare box has eight
		// places for colour to land and a stroke reads as a gradient across whole faces rather than
		// a mark where the cursor was. That is the tool working as designed and it looks exactly
		// like the tool being broken, which is what it looked like until somebody said so here.
		SetPrompt( session.IsCoarse
			? $"Paint: drag on the model. This body has only {targets[0].Mesh.Positions.Count} vertices "
				+ "and paint colours vertices — add a Subdivide above the Paint feature for a "
				+ "brush that follows the cursor instead of tinting whole faces."
			: "Paint: drag on the model. Colour, size and strength are on the bar below." );
	}

	/// <summary>
	/// Step into the material brush.
	///
	/// NO FEATURE IS CREATED, unlike Paint. A material dab is the same history edit dropping a
	/// material makes — FaceMaterialEdit assignments onto a slot — so the brush is a way of making
	/// those in bulk rather than a new kind of thing in the tree. Which is also why there is nothing
	/// to re-enter: leaving and coming back picks up wherever the document got to.
	/// </summary>
	private void EnterMaterialBrush()
	{
		if ( _viewport is null || _studio is null )
			return;

		LeaveCurrentWorkspace();

		// ONE BODY AT A TIME, the same door refusal Paint makes and for a plainer reason: the
		// session builds one BVH over one mesh, and "which body did the ray hit" is a question it
		// would have to answer before it could answer any other.
		if ( _studio.Bodies.Count != 1 )
		{
			SetPrompt( _studio.Bodies.Count == 0
				? "The material brush needs a body to paint on — add a primitive or extrude a sketch first."
				: "The material brush works on one body at a time — hide the others in the Parts list." );
			return;
		}

		var body = _studio.Bodies[0];

		BarMode = EffigyBarMode.Paint;

		_stageBar.Mode = "MATERIAL";
		_stageBar.SetFinish( "Finish", FinishMaterialBrush );
		_stageBar.SetStages( BuildMaterialBrushStages() );

		_dialog?.Close();

		var session = new MaterialBrushSession( body.Mesh );
		session.Radius = session.SuggestedRadius;

		_materialBrushBodyId = body.Id;

		_viewport.BeginMaterialBrush( session );
		_materialBrushBar.Bind( session );
		_materialBrushBar.SetMaterial( _materialsPanel?.SelectedMaterial );
		_viewport.MaterialBrushLoaded = !string.IsNullOrWhiteSpace( _materialsPanel?.SelectedMaterial );

		SetPrompt( string.IsNullOrWhiteSpace( _materialsPanel?.SelectedMaterial )
			? "Material brush: pick a material in the Materials browser, then drag on the model."
			: "Material brush: drag on the model. The material comes from the Materials browser." );
	}

	private void FinishMaterialBrush()
	{
		if ( _viewport is null || !_viewport.IsMaterialBrushing )
			return;

		LeaveMaterialBrush();
		ShowPaintHome();
	}

	/// <summary>
	/// Disarm without deciding where to go next.
	///
	/// Split from <see cref="FinishMaterialBrush"/> because the two callers want different things:
	/// pressing Finish means "back to the Paint home", while LeaveCurrentWorkspace is on its way
	/// somewhere else entirely and showing the paint stages on the way would overwrite the bar the
	/// workspace it is entering is about to set.
	/// </summary>
	/// <summary>The browser's selection moved. Only the bar cares — the dab reads the panel directly,
	/// so there is no copy of the material here to keep in step.</summary>
	private void OnBrushMaterialChanged()
	{
		if ( _viewport is not { IsMaterialBrushing: true } )
			return;

		_materialBrushBar?.SetMaterial( _materialsPanel?.SelectedMaterial );
		_viewport.MaterialBrushLoaded = !string.IsNullOrWhiteSpace( _materialsPanel?.SelectedMaterial );
	}

	/// <summary>
	/// A stroke began: record undo, and say so if the brush has nothing loaded.
	///
	/// The prompt is here rather than in the dab because a dab fires every frame - the same reason
	/// undo is recorded here - so warning there would rewrite the status bar continuously while
	/// the button is held. Once per gesture is what a person can read.
	/// </summary>
	private void OnMaterialStrokeStarted()
	{
		if ( string.IsNullOrWhiteSpace( _materialsPanel?.SelectedMaterial ) )
		{
			SetPrompt( "No material chosen — pick one in the Materials browser and the ring turns "
				+ "amber. Nothing was painted." );
			return;
		}

		RecordUndo();
	}

	private void LeaveMaterialBrush()
	{
		_viewport?.EndMaterialBrush();
		_materialBrushBar?.Bind( null );
		_materialBrushBodyId = null;
	}

	/// <summary>The material brush's stage set: one always-armed brush, the same shape the colour
	/// brush's own stage takes.</summary>
	private List<EffigyStage> BuildMaterialBrushStages()
	{
		var brush = new EffigyStage { Name = "Brush" };

		brush.Add( new EffigyStageTool
		{
			Icon = EffigyIcon.FaceMaterial,
			Label = "Material",
			Tip = "Material brush — drag on the model to lay the selected material down",
			Checkable = true,
			Checked = true,
		} );

		return new List<EffigyStage> { brush };
	}

	/// <summary>
	/// One dab: put the browser's material on every face the ring covered.
	///
	/// THE BODY IS RE-RESOLVED BY ID rather than captured, because RebuildStudio below remakes every
	/// body — the object the brush started with is stale by the second dab. The MESH the session
	/// raycasts is deliberately not re-pointed: a material assignment changes which slot a face
	/// carries and never its geometry, so the BVH built at the door stays true for the whole stroke,
	/// and rebuilding it per dab would cost the drag its frame rate for nothing.
	///
	/// Faces already on the slot report no change, so a held brush that is not moving does not put
	/// an undo step on the stack every frame — see MaterialDrop.Brush.
	/// </summary>
	private void OnMaterialDabbed( IReadOnlyList<int> faces )
	{
		var material = _materialsPanel?.SelectedMaterial;

		if ( string.IsNullOrWhiteSpace( material ) || faces is null || faces.Count == 0 )
			return;

		var body = _studio?.Bodies.FirstOrDefault( b => b.Id == _materialBrushBodyId );

		if ( body is null )
			return;

		var fresh = MaterialDrop.SlotCarrying( _studio, material ) < 0;

		if ( MaterialDrop.Brush( _studio, body, faces, material, out var slot, out var released ) <= 0 )
			return;

		// Same rule the drop follows: only a slot this gesture INVENTED gets a guessed world size,
		// so re-brushing a material never rewrites a number somebody typed.
		if ( fresh && slot > 0 )
			MaterialScale.SetScale( _studio, slot, EffigyMaterialSize.For( material ) );

		RebuildStudio();

		SetPrompt( released.Count > 0
			? $"{MaterialFileName( material )} → slot {slot}; slot{(released.Count == 1 ? "" : "s")} "
				+ $"{string.Join( ", ", released )} freed. Ctrl+Z puts it back."
			: $"{MaterialFileName( material )} → slot {slot}. Ctrl+Z puts it back." );
	}

	private string _materialBrushBodyId;

	private void FinishPaint()
	{
		if ( _viewport is null || !_viewport.IsPainting )
			return;

		_viewport.EndPaint();
		_paintBar.Bind( null );

		var feature = _paintFeature;
		_paintFeature = null;

		// Back to the Paint workspace's landing bar, not CAD — UV Project and Paint are the tools
		// you reach for next. Leaving the workspace entirely is the CAD pill's job.
		ShowPaintHome();

		// The one full rebuild in paint mode: strokes were appended per stroke, so this replays them
		// onto the feature's cached canvas and catches the tree up.
		if ( feature is not null )
			_studio.MarkDirty( feature );

		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	/// <summary>A stroke landed. The document is now unsaved and the paint bar's readouts may have
	/// moved, but the feature tree deliberately does NOT rebuild — see FinishPaint.</summary>
	private void OnPaintStrokeFinished( PaintStroke stroke )
	{
		// An undo point per stroke, taken BEFORE the stroke joins the list: the snapshot captures the
		// pre-stroke list, so Ctrl+Z pops back one stroke the way it pops one sketch line. The dab
		// colours never enter the document — they are the session's own array — so the feature's list
		// is the whole of what a paint undo has to restore.
		RecordUndo();

		if ( _paintFeature is not null )
			_paintFeature.AddStroke( stroke );

		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		_paintBar?.Refresh();
	}

	private void OnPaintSettingsChanged() => _paintBar?.Refresh();

	private void OnPaintBarChanged() => _viewport?.Update();

	/// <summary>
	/// Blend moved, which is a DOCUMENT edit rather than a brush setting.
	///
	/// It changes nothing on screen - the viewport already composites vertex colour over the
	/// material the same way both settings do - and everything about the compiled model, which is
	/// exactly the shape of edit that used to leave the title bar saying there was nothing to save.
	/// </summary>
	private void OnPaintBlendChanged()
	{
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}
	}

	// --- grease pencil -------------------------------------------------------------------------

	private EffigyStageTool _noteTool, _noteEraseTool;

	/// <summary>
	/// Arm or put down the pen.
	///
	/// NOT AN EffigyBarMode. Sketch and sculpt each take the whole bar over because they replace
	/// what every button on it means; the pen adds one thing you can do with the pointer and takes
	/// nothing away, so it stays on the Part bar as an armed tool. That also means notes can be
	/// scribbled between two feature edits without leaving and re-entering a mode, which is when
	/// people actually write them.
	/// </summary>
	private void ToggleNotePen()
	{
		if ( _viewport is null )
			return;

		if ( _viewport.IsNoting )
		{
			_viewport.EndNotes();
			UpdateNoteChecks();

			return;
		}

		// Sketching owns the click while it is open, so the two cannot both be armed. Sculpting
		// cannot be running here at all - it holds a different bar.
		if ( _viewport.IsSketching )
			FinishSketch();

		var session = new NoteSession( _studio.Notes )
		{
			Color = _noteTool?.Current ?? 0,
			Pivot = _studio.Origin,
		};

		session.SetBodies( _studio.Bodies );
		session.ScaleTo( NotePartSize() );

		// Point the viewport at the studio's list HERE rather than waiting for the next rebuild.
		// RefreshNotes is the other half of this and runs from RebuildStudio, which is fine for a
		// document swap and useless for the case that actually matters: arm the pen on a studio
		// nobody has touched since it opened, draw, and let go. Nothing rebuilds on a scribble - a
		// note is not in the history - so without this line the note the user just drew would not
		// be painted until they happened to edit a feature.
		_viewport.Notes = _studio.Notes;
		_viewport.PartSize = NotePartSize();

		_viewport.BeginNotes( session );
		UpdateNoteChecks();
	}

	private void ToggleNoteEraser()
	{
		if ( _viewport is null )
			return;

		// Reaching for the eraser with the pen down means you want to erase, not nothing. Arming
		// the pen first is the reading that leaves the button doing something.
		if ( !_viewport.IsNoting )
			ToggleNotePen();

		if ( _viewport.IsNoting )
			_viewport.NoteErasing = !_viewport.NoteErasing;

		UpdateNoteChecks();
	}

	private void SetNoteColor( int index )
	{
		// Picking a colour is also how you reach for the pen, the same way picking a primitive
		// shape makes one rather than only remembering the choice.
		if ( _viewport is not null && !_viewport.IsNoting )
			ToggleNotePen();

		if ( _viewport?.NoteSession is { } session )
		{
			session.Color = index;

			// A colour is a decision about the next stroke, never about the one that is running.
			_viewport.NoteErasing = false;
		}

		UpdateNoteChecks();
	}

	/// <summary>Put the bar's ticks back in step with the viewport, which the E and H shortcuts can
	/// change from under it. Same job UpdateSculptChecks does.</summary>
	private void UpdateNoteChecks()
	{
		var noting = _viewport?.IsNoting == true;

		if ( _noteTool is not null )
			_noteTool.Checked = noting;

		if ( _noteEraseTool is not null )
			_noteEraseTool.Checked = noting && _viewport.NoteErasing;

		if ( _noteTool is not null && _viewport?.NoteSession is { } session )
		{
			_noteTool.Current = session.Color;
			_noteTool.IconColor = SwatchColor( session.Color );
		}

		_stageBar?.Refresh();
		_viewport?.Update();
	}

	/// <summary>
	/// A stroke landed or a note was erased.
	///
	/// Dirties the document and NOTHING ELSE - no rebuild, no feature tree refresh. A note is not
	/// in the history, so there is nothing to re-evaluate, and rebuilding on every scribble would
	/// re-run the whole model to change a line the model has never heard of.
	/// </summary>
	private void OnNoteEdited()
	{
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		_viewport?.Update();
	}

	/// <summary>
	/// Ask for the words that go with a note, and hang them on it.
	///
	/// Through the session rather than by assigning Note.Text, so the caption lands on the same
	/// undo stack as the stroke it belongs to - a typo you cannot take back is worse than no
	/// caption.
	/// </summary>
	private void PromptNoteText( Note note )
	{
		if ( note is null || _viewport?.NoteSession is not { } session )
			return;

		// The same one-field popup every tree in this tool renames with - see
		// EffigyRigPanel.BeginRename. A note's caption is a rename in everything but name, and
		// inventing a second way to type one short string would be a second thing to get wrong.
		var menu = new Menu( _viewport );
		var edit = new LineEdit( note.Text ?? "", menu ) { FixedWidth = 260, PlaceholderText = "What does this one say?" };

		edit.ReturnPressed += () =>
		{
			var text = edit.Text?.Trim();

			menu.Close();

			// Trimmed to null rather than kept as an empty string: a caption of "" would still draw
			// its leader line up to a label with nothing in it, which reads as a bug rather than as
			// a note somebody cleared.
			if ( session.SetText( note, string.IsNullOrWhiteSpace( text ) ? null : text ) )
				OnNoteEdited();
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>
	/// Point the viewport and the live session at the studio's current notes and bodies.
	///
	/// Called from RebuildStudio, which is the one place every document change funnels through -
	/// an edit, an undo, a New, an Open. The session holds the studio's list BY REFERENCE, so most
	/// rebuilds need nothing done here; the case that does is a document swap, where the old
	/// session is still writing into the list belonging to a studio nobody can see any more.
	/// </summary>
	private void RefreshNotes()
	{
		if ( _viewport is null )
			return;

		_viewport.Notes = _studio.Notes;

		if ( _viewport.NoteSession is not { } session )
			return;

		if ( !ReferenceEquals( session.Notes, _studio.Notes ) )
		{
			// A new document. Re-arm rather than carrying the old session across, which would also
			// carry an undo stack that can paste the previous model's scribbles into this one.
			_viewport.EndNotes();
			ToggleNotePen();

			return;
		}

		// The bodies a stroke lands on have just been rebuilt. Without this a note drawn after an
		// extrude sinks to where the surface used to be.
		session.SetBodies( _studio.Bodies );
		session.Pivot = _studio.Origin;

		// And the part may be a different size than it was when the pen was armed - an extrude on an
		// empty studio takes it from nothing to something - so the distances that scale with it are
		// re-derived rather than left at what the first guess said.
		var size = NotePartSize();

		session.ScaleTo( size );
		_viewport.PartSize = size;
	}

	/// <summary>
	/// How big the part is, for everything about a note that has to scale with it.
	///
	/// ToMesh rather than ToVisibleMesh: hiding a body is about what you want to look at, and a note
	/// suddenly changing how finely it samples because a body was hidden would be a surprise with no
	/// cause the user could see. Zero on an empty studio, which ScaleTo reads as "assume the default
	/// primitive".
	/// </summary>
	private float NotePartSize()
	{
		if ( _studio is null || _studio.Bodies.Count == 0 )
			return 1f;

		var diagonal = _studio.ToMesh().BoundsDiagonal;

		return diagonal > 1e-6f ? diagonal : 1f;
	}

	private void ToggleSculptMasking()
	{
		if ( _viewport?.SculptSession is not { } session )
			return;

		session.Masking = !session.Masking;

		UpdateSculptChecks();
		_sculptBar?.Refresh();
	}

	private void ToggleSculptSymmetry()
	{
		if ( _viewport?.SculptSession is not { } session )
			return;

		session.MirrorX = !session.MirrorX;

		UpdateSculptChecks();
		_sculptBar?.Refresh();
	}

	/// <summary>Put the strip's ticks back in step with the session, which the X and M shortcuts can
	/// change from under it.</summary>
	private void UpdateSculptChecks()
	{
		var session = _viewport?.SculptSession;

		foreach ( var (tool, kind) in _brushTools )
			tool.Checked = session is not null && !session.Masking && session.Brush == kind;

		if ( _maskTool is not null )
			_maskTool.Checked = session?.Masking ?? false;

		if ( _symmetryTool is not null )
			_symmetryTool.Checked = session?.MirrorX ?? false;

		_stageBar?.Refresh();
	}

	/// <summary>
	/// Move the working level, adding one when asked for finer than exists.
	///
	/// ADDING RATHER THAN REFUSING at the top is the point of the button: somebody who has reached
	/// the finest level and presses "finer" wants the next one, not a message saying there is not
	/// one. Going below zero is different - level 0 is the cage itself and there is genuinely
	/// nothing under it.
	/// </summary>
	private void StepSculptLevel( int delta )
	{
		if ( _viewport?.SculptSession is not { } session )
			return;

		var sculpt = session.Sculpt;
		var target = session.Level + delta;

		if ( target < 0 )
		{
			SetPrompt( "Level 0 is the cage itself — there is nothing coarser than it." );
			return;
		}

		// Stepping below the top REMOVES the finest level when it is empty of detail, rather than
		// leaving a level nobody is using on the model for ever. Only when it is empty: throwing away
		// somebody's sculpt because they wanted a coarser view would be unforgivable, and the undo
		// stack is what makes even the empty case safe.
		if ( delta < 0 && session.Level == sculpt.TopLevel && !sculpt.HasDetail( sculpt.TopLevel ) )
		{
			session.RemoveTopLevel();
			SetPrompt( $"Dropped the empty level {sculpt.TopLevel + 1}. Ctrl+Z puts it back." );

			_viewport.RefreshSculptPreview();
			_sculptBar?.Refresh();
			NoteSculptEdited();

			return;
		}

		if ( target > sculpt.TopLevel )
		{
			var (vertices, faces) = sculpt.Cost( target );

			RecordUndo();
			sculpt.AddLevel();

			SetPrompt( $"Level {target}: {vertices:N0} vertices, {faces:N0} faces." );
		}

		session.Level = target;

		_viewport.RefreshSculptPreview();
		_sculptBar?.Refresh();
		NoteSculptEdited();
	}

	/// <summary>
	/// Bake the sculpt down onto the cage's UVs and write it out as a PNG.
	///
	/// The UVs are checked BEFORE anything is written. A bake over overlapping UVs does not fail: it
	/// produces a plausible map that is wrong wherever two faces shared a texel, and box projection -
	/// this tool's own default - overlaps by construction. Naming that is worth more than a file.
	/// </summary>
	/// <summary>
	/// The two normal-map conventions, and the size.
	///
	/// THESE EXIST AS CONTROLS BECAUSE NOBODY KNOWS THE ANSWER YET. Which way s&amp;box wants the green
	/// channel, and which end of the image v = 0 belongs at, are the two things the suite explicitly
	/// cannot judge and the sitting is meant to settle. A bake button that could only write one of
	/// the four combinations would make that sitting impossible to finish - you would find out the
	/// map was wrong and have no way to write the right one.
	///
	/// They live in Edit > Settings now, under "Normal map bake", rather than as three verbs on the
	/// Edit menu. A toggle whose whole state was a one-line prompt that had already scrolled away is
	/// a control you cannot read; the settings window shows the switch position and the size at once,
	/// and remembers them between sessions the way every other setting there does.
	///
	/// Defaults are OpenGL-style green and no vertical flip, which is what the sample in
	/// Effigy.Tests/out was written with, so the two can be compared directly.
	/// </summary>
	private bool _bakeFlipGreen;
	private bool _bakeFlipV;
	private int _bakeSize = 1024;

	private void BakeSculpt()
	{
		if ( _viewport?.SculptSession is not { } session )
			return;

		var sculpt = session.Sculpt;
		var cage = sculpt.Cage;
		var coverage = NormalBake.Measure( cage );

		if ( !coverage.CanBake )
		{
			SetPrompt( $"Cannot bake: {coverage.Problem}" );
			return;
		}

		var fd = new FileDialog( null )
		{
			Title = "Bake normal map to...",
			DefaultSuffix = ".png",
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SelectFile( $"{_sculptFeature?.Name ?? "sculpt"}_normal.png" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( "PNG image (*.png)" );

		if ( !fd.Execute() )
			return;

		try
		{
			var options = new BakeOptions { FlipGreen = _bakeFlipGreen };
			var map = NormalBake.Bake( cage, sculpt.Evaluate( sculpt.TopLevel ), _bakeSize, _bakeSize, options );

			PngWriter.WriteFile( fd.SelectedFile, map, _bakeFlipV );

			// The convention is named in the message on purpose. Two files that differ only in the
			// sign of one channel are indistinguishable once they are on disk, and the whole point of
			// the sitting is to work out which one is right.
			var convention = $"{(_bakeFlipGreen ? "DirectX" : "OpenGL")} green, v = 0 at the "
				+ $"{(_bakeFlipV ? "bottom" : "top")}";

			SetPrompt( $"Baked {map.Width}×{map.Height} to {fd.SelectedFile} — {map.FilledCount:N0} texels hit, "
				+ convention + "." );

			Log.Info( $"[Effigy] baked normal map to {fd.SelectedFile} ({convention})" );
		}
		catch ( Exception e )
		{
			// Writing a file is the one place failing quietly is unforgivable, same as Save.
			Log.Error( $"[Effigy] could not bake to {fd.SelectedFile}: {e.Message}" );
			SetPrompt( $"Bake failed: {e.Message}" );
		}
	}

	/// <summary>
	/// Step the sculpt's own undo stack and put the viewport back in step with it.
	///
	/// A stroke is one entry, which is what a user means by undo - see SculptSession.
	/// </summary>
	private void StepSculptHistory( bool redo )
	{
		if ( _viewport?.SculptSession is not { } session )
			return;

		if ( !(redo ? session.Redo() : session.Undo()) )
		{
			SetPrompt( redo ? "Nothing to redo in this sculpt." : "Nothing to undo in this sculpt." );
			return;
		}

		_viewport.RefreshSculptPreview();
		NoteSculptEdited();
	}

	/// <summary>
	/// The mask actions that are not a brush stroke: invert, clear, erase, and hide what is held.
	///
	/// IN AN EDIT &gt; SCULPT MASK SUBMENU RATHER THAN ON THE STRIP, deliberately. The strip is
	/// hand-painted glyphs and five more of them is real design work for actions nobody reaches for
	/// mid-stroke. The menu takes named Material icons, which this window already uses everywhere. A
	/// submenu rather than five flat entries because the whole group only does anything while a
	/// Sculpt feature is open, and a menu most sessions never need should not be the first thing Edit
	/// shows.
	///
	/// They are added unconditionally and refuse when there is no sculpt open, rather than the menu
	/// being rebuilt per state - a menu that changes shape depending on the mode is a menu whose
	/// items move under the cursor.
	/// </summary>
	private bool SculptingOrSaySo( out SculptSession session )
	{
		session = _viewport?.SculptSession;

		if ( session is null )
			SetPrompt( "That is a sculpting action — open a Sculpt feature first." );

		return session is not null;
	}

	private void InvertSculptMask()
	{
		if ( !SculptingOrSaySo( out var session ) )
			return;

		session.InvertMask();
		_viewport.RefreshSculptPreview();
		_sculptBar?.Refresh();

		SetPrompt( $"Mask inverted — {session.MaskFor( session.Level ).ProtectedFraction:P0} held." );
	}

	private void ProtectAllSculpt()
	{
		if ( !SculptingOrSaySo( out var session ) )
			return;

		// The other end of Clear, and the start of "mask everything but this": protect the lot, then
		// invert, then paint free the part you actually want to work on.
		session.ProtectAll();
		_viewport.RefreshSculptPreview();
		_sculptBar?.Refresh();

		SetPrompt( "Everything is masked - invert, or paint to release the part you want to work on." );
	}

	private void ClearSculptMask()
	{
		if ( !SculptingOrSaySo( out var session ) )
			return;

		session.ClearMask();
		_viewport.RefreshSculptPreview();
		_sculptBar?.Refresh();

		SetPrompt( "Mask cleared — nothing is held." );
	}

	private void ToggleSculptMaskErase()
	{
		if ( !SculptingOrSaySo( out var session ) )
			return;

		session.Erasing = !session.Erasing;
		session.Masking = true;

		UpdateSculptChecks();
		_sculptBar?.Refresh();

		SetPrompt( session.Erasing ? "Mask brush: erasing." : "Mask brush: painting." );
	}

	private void ToggleHideMasked()
	{
		if ( !SculptingOrSaySo( out var session ) )
			return;

		// A VIEW, like the level, and it reaches the model exactly as far as that one does: nowhere.
		// Hiding half a head to reach inside it must not export a head with half of it missing.
		session.HideMasked = !session.HideMasked;

		_viewport.RefreshSculptPreview();
		_sculptBar?.Refresh();

		SetPrompt( session.HideMasked
			? "Masked geometry hidden — the model still builds whole."
			: "Showing all geometry." );
	}

	/// <summary>The radius or strength box was typed in. The viewport only needs to know so the
	/// brush ring is drawn at the new size.</summary>
	private void OnSculptBarChanged() => _viewport?.Update();

	/// <summary>The viewport changed a brush setting itself - the X and M shortcuts - so the strip's
	/// ticks and the bar's readout have to catch up with it.</summary>
	private void OnSculptSettingsChanged()
	{
		UpdateSculptChecks();
		_sculptBar?.Refresh();
	}

	/// <summary>A stroke landed. The document is now unsaved and the bar's readouts have moved, but
	/// the feature tree deliberately does NOT rebuild - see FinishSculpt.</summary>
	/// <summary>
	/// The rig changed, so the document is unsaved.
	///
	/// The same two lines every other unsaved edit uses - there is no shared helper, as the pivot's
	/// own comment already notes. The viewport repaint is here too because a rig edit goes through
	/// nothing else that would ask for one: RebuildStudio is deliberately not called (no geometry
	/// moved) and that is the usual route to a redraw.
	/// </summary>
	private void NoteRigEdited()
	{
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		_viewport?.Update();
	}

	private void NoteSculptEdited()
	{
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		_sculptBar?.Refresh();
	}

	// --- sketch mode -------------------------------------------------------------------------

	/// <summary>
	/// Enter sketch mode on a Sketch feature: show the sketch toolbar, point the camera straight
	/// at the plane, and start on the Line tool.
	///
	/// The rebuild is needed for SketchFeature.Plane — the Sketch object's actual plane is only
	/// assigned when the feature executes — but the strip swap is UI and must happen first so the
	/// toolbar change is instant.  BeginSketch uses the rebuilt plane, so it comes after.
	/// </summary>
	private void EnterSketch( SketchFeature feature )
	{
		if ( feature is null || _viewport is null )
			return;

		// Already in this sketch: a second Edit (tree click, dialog Open, the Sketch tool) must
		// not reset the tool or drop a half-drawn curve.
		if ( BarMode == EffigyBarMode.Sketch
			&& ReferenceEquals( _viewport.ActiveSketch, feature.Sketch ) )
			return;

		if ( _viewport.IsSculpting )
			FinishSculpt();

		// The bar's stages become the SKETCH stages, and the mode goes on the bar next to the
		// control that leaves it. Do this BEFORE the rebuild so the swap is instant instead of
		// waiting for the (potentially slow) PartStudio rebuild to finish.
		//
		// The feature's own name is the mode label rather than a bare "SKETCH": a document with
		// four sketches in it makes "which one am I in" a real question, and the feature tree is
		// the only other place that answers it.
		BarMode = EffigyBarMode.Sketch;

		_stageBar.Mode = feature?.Name?.ToUpperInvariant() ?? "SKETCH";
		_stageBar.SetFinish( "Finish", FinishSketch );
		_stageBar.SetStages( _sketchStages );

		// Sketching and the bone tool both drive left-clicks in the viewport; only one may own
		// them. The bone tool refuses to arm on top of an open sketch (see SetBoneToolActive), so
		// the only direction this needs covering is the other one - entering a sketch while the
		// bone tool happened to be armed.
		_rigPanel?.CancelBoneTool();

		// THE SKETCH BEING EDITED HAS TO BE ONE THAT RUNS. Sketch.Plane is only assigned when
		// SketchFeature.Execute does it, so a sketch sitting at or below the bar is still on the
		// default XY plane however carefully its face was picked - and everything downstream of the
		// plane is then wrong in a way that looks like a drawing bug rather than a rollback one.
		//
		// EnterSculpt has made exactly this guarantee for sculpts since it was written, for the
		// same reason: the cage does not exist until the features above it have run. This is that
		// line, for the other mode that edits something a feature has to build first.
		var index = _studio.Features.IndexOf( feature );

		if ( index >= 0 && _studio.EffectiveCount <= index )
		{
			_rollbackBeforeEdit ??= _studio.RollbackIndex;
			_studio.RollbackIndex = index + 1;
		}

		RebuildStudio();

		_viewport.BeginSketch( feature.Sketch );

		// AFTER BeginSketch, which clears whatever the last sketch was sitting on. The plane the
		// outline is expressed in is the one the rebuild just assigned, so this cannot move above
		// RebuildStudio either.
		RefreshSketchReference( feature );

		_viewport.ConstructionMode = false;

		if ( _constructionTool is not null )
			_constructionTool.Checked = false;

		UpdateSketchToolChecks( _viewport.SketchTool );
		RevealSketchTool( _viewport.SketchTool );
	}

	private void FinishSketch()
	{
		if ( !_viewport.IsSketching )
			return;

		_viewport.EndSketch();

		// The part stages come back with their locks recomputed, so the stages this sketch just
		// unlocked are already open rather than opening a beat later.
		ShowPartStages( force: true );

		UpdateSketchToolChecks( SketchToolKind.Select );

		SetPrompt( "" );
		RebuildStudio();
	}

	/// <summary>A curve was drawn. Rebuilding here is what makes an extrude above the sketch update
	/// as you draw its profile.</summary>
	private void OnSketchEdited()
	{
		// The curve just drawn lives inside a SketchFeature's Sketch object, and PartStudio caches
		// a CLONE of that sketch after the feature runs (Snapshot.Of). Without marking it dirty the
		// clone is what every downstream feature keeps reading, so an extrude above the sketch never
		// sees the profile just closed.
		if ( ActiveSketchFeature() is { } sketchFeature )
			_studio.MarkDirty( sketchFeature );

		RebuildStudio();
		_dialog?.Rebuild();
	}

	/// <summary>
	/// Give the sketcher the outline of the face the open sketch sits on, so it can be seen and
	/// snapped to. Null for a sketch on Top/Front/Right, which has nothing underneath it.
	///
	/// REBUILT FROM THE MODEL EVERY TIME rather than cached on the feature. The face moves - that
	/// is the whole point of attaching a sketch to one - and an outline held over from before the
	/// move is not a stale drawing, it is a set of snap targets sitting where the face used to be.
	/// Wrong in the one way that looks exactly like right.
	/// </summary>
	private void RefreshSketchReference( SketchFeature feature )
	{
		if ( _viewport is null )
			return;

		var reference = feature?.Face is { } face
			? SketchReference.FromFace( _studio.Bodies, face, feature.Sketch.Plane )
			: null;

		// The other half of the BeginSketch probe line. That one says where the sketch plane ended
		// up; this says whether the face it was supposed to come from was found at all, and where
		// that face is - so "grid in the wrong place" resolves to either "the reference is gone" or
		// "the reference is fine and the plane still went elsewhere" in one repro.
		if ( EffigyViewport.ProbeSketch && feature?.Face is { } probed )
		{
			var found = FacePlane.TryResolveFace( _studio.Bodies, probed, out var body, out var index );

			Log.Info( $"[effigy-probe] SketchReference body={probed.BodyId} resolved={found} "
				+ $"to={body?.Id}#{index} refPoint={probed.Point} refNormal={probed.Normal} "
				+ $"faceCentroid={(found ? body.Mesh.FaceCentroid( body.Mesh.Faces[index] ).ToString() : "-")} "
				+ $"outlinePoints={reference?.Points.Count ?? 0} outlineEdges={reference?.Edges.Count ?? 0} "
				+ $"error={feature.Diagnostic?.Problem}" );

			// WHOSE SketchFeature THIS IS. The plane above came back as the global XY, which is
			// what Sketch.Plane holds until SketchFeature.Execute overwrites it - so either the
			// feature never executed, or the object being read is not the one that did. Those want
			// opposite fixes, and only the tree can tell them apart.
			var inTree = _studio.Features.IndexOf( feature );
			var live = inTree >= 0 ? _studio.Features[inTree] as SketchFeature : null;

			Log.Info( $"[effigy-probe]   feature index={inTree} of {_studio.Features.Count} "
				+ $"rollback={_studio.RollbackIndex} effective={_studio.EffectiveCount} "
				+ $"suppressed={feature.Suppressed} sameObject={ReferenceEquals( live, feature )} "
				+ $"sameSketch={ReferenceEquals( live?.Sketch, feature.Sketch )} "
				+ $"livePlaneOrigin={live?.Sketch?.Plane?.Origin.ToString() ?? "-"} "
				+ $"faceSet={feature.Face.HasValue}" );

			// WHERE THE OUTLINE CAME FROM, when the counts above are not what the geometry says
			// they should be. The same body and face fed to the same kernel outside the editor
			// gives four points and four edges; in here it gives two, so the disagreement is in
			// the mesh rather than in the rule, and this prints the mesh.
			if ( found )
			{
				var mesh = body.Mesh;
				var faceCorners = mesh.Faces[index].Count;
				var surface = FaceSurface.FromFace( mesh, index );
				var corners = string.Join( " ", mesh.Faces[index].Indices
					.Select( i => $"{i}:{mesh.Positions[i]}" ) );

				Log.Info( $"[effigy-probe]   mesh faces={mesh.Faces.Count} verts={mesh.Positions.Count} "
					+ $"diag={mesh.BoundsDiagonal:F4} faceCorners={faceCorners} "
					+ $"surfaceFaces={surface.Faces.Count} surfaceBoundary={surface.Boundary.Count}" );

				Log.Info( $"[effigy-probe]   corners {corners}" );

				if ( feature.Sketch?.Plane is { } sp )
				{
					var flat = string.Join( " ", mesh.Faces[index].Indices
						.Select( i => sp.ToPlane( mesh.Positions[i] ).ToString() ) );

					Log.Info( $"[effigy-probe]   planeOrigin={sp.Origin} x={sp.XAxis} y={sp.YAxis} "
						+ $"inPlane {flat}" );
				}
			}
		}

		_viewport.SetSketchReference( reference );
	}

	/// <summary>The feature that owns the sketch currently being drawn on, by identity.</summary>
	private SketchFeature ActiveSketchFeature()
	{
		if ( _viewport?.ActiveSketch is not { } active )
			return null;

		return _studio.Features
			.OfType<SketchFeature>()
			.FirstOrDefault( f => ReferenceEquals( f.Sketch, active ) );
	}

	/// <summary>
	/// A parameter on the open feature changed.
	///
	/// MARKING IT DIRTY IS THE ENTIRE POINT OF THIS METHOD. PartStudio caches the body list after
	/// each feature and only re-runs from the first dirty one — and Rebuild() ends by setting
	/// _dirtyFrom to the feature count, so a rebuild with nothing marked reuses the whole cache and
	/// re-executes NOTHING.
	///
	/// This was wired straight to RebuildStudio, so every edit made through a feature dialog was
	/// silently thrown away: the sketch plane dropdown (which is why a sketch stayed on XY however
	/// many times you picked Front or Right), an extrude distance, subdivide levels, every checkbox.
	/// Picking highlighted beautifully and then changed nothing.
	/// </summary>
	private void OnFeatureEdited()
	{
		if ( _dialog?.Feature is { } feature )
			_studio.MarkDirty( feature );

		// The dropdown and the strip are two views of one ChoiceParam, so an edit through either
		// has to refresh the other or they disagree about what is armed - which is the exact
		// confusion the strip exists to end.
		_resultStrip?.Bind( _dialog?.Feature, SketchHostBodyId );

		RebuildStudio();
	}

	/// <summary>
	/// A click on the ADD/REMOVE strip. The parameter is already set by the time this runs; what
	/// is left is everything a dropdown change would have done.
	///
	/// The dialog rebuild is not optional even though the dropdown is gone: Result decides which
	/// parameters a feature declares in some cases, and a dialog still showing rows for the mode it
	/// was in before is the same disagreement in a different place.
	/// </summary>
	private void OnResultStripChanged()
	{
		OnFeatureEdited();

		_dialog?.Rebuild();
	}

	/// <summary>
	/// Which body a sketch was drawn on, or null for one on a global plane. This is what Auto
	/// reads, so it is what the strip's Auto hint has to read too.
	///
	/// Straight off SketchFeature.Face rather than through the kernel's own resolution, because
	/// that needs a FeatureContext which only exists mid-rebuild - see EffigyResultStrip.ResolveAuto.
	/// </summary>
	private string SketchHostBodyId( string sketchId ) =>
		_studio.Features.OfType<SketchFeature>().FirstOrDefault( f => f.Id == sketchId )?.Face?.BodyId;

	/// <summary>The left half of the status bar — what the active tool wants next.</summary>
	private void SetPrompt( string prompt )
	{
		if ( _promptLabel.IsValid() )
			_promptLabel.Text = prompt;
	}

	// --- which creation tools are on the strip -------------------------------------------------

	/// <summary>
	/// Which feature a strip button makes. An ENUM RATHER THAN A Func&lt;Feature&gt;.
	///
	/// The table below is static, and static state survives a hotload while the assembly under it
	/// does not. A stored lambda therefore comes back pointing into the old assembly, which the
	/// hotloader cannot substitute — clicking a button threw "Unable to find matching substitution
	/// for a lambda method" and every tool was dead until the editor restarted. An enum value is an
	/// int and migrates without any of that; the switch that turns it into a feature is ordinary
	/// code, recompiled with everything else. Same reason no System.Type is held here either.
	/// </summary>
	private enum ToolKind
	{
		Sketch, Primitive, Extrude, Revolve, Sweep, Loft, Chamfer, Fillet, Shell, Subdivide,
		Draft, Hole, Sculpt, Mirror, LinearPattern, CircularPattern, Transform, UVProject, FaceMaterial,
		MoveFace, Paint, Boolean,
	}

	/// <summary>Build one, and apply the variant chosen from its dropdown where it has one.</summary>
	private static Feature NewFeature( ToolKind kind, int choice ) => kind switch
	{
		ToolKind.Sketch => new SketchFeature(),
		ToolKind.Primitive => NewPrimitive( choice ),
		ToolKind.Extrude => AwaitingPick( new ExtrudeFeature() ),
		ToolKind.Revolve => AwaitingPick( NewRevolve() ),
		ToolKind.Sweep => new SweepFeature(),
		ToolKind.Loft => new LoftFeature(),
		ToolKind.Chamfer => new ChamferFeature(),
		ToolKind.Fillet => new FilletFeature(),
		ToolKind.Shell => new ShellFeature(),
		ToolKind.Subdivide => new SubdivideFeature(),
		ToolKind.Draft => new DraftFeature(),
		ToolKind.Hole => new HoleFeature(),
		ToolKind.Sculpt => new SculptFeature(),
		ToolKind.Mirror => new MirrorFeature(),
		ToolKind.LinearPattern => new LinearPatternFeature(),
		ToolKind.CircularPattern => new CircularPatternFeature(),
		ToolKind.Transform => new TransformFeature(),
		ToolKind.UVProject => new UVProjectFeature(),
		ToolKind.FaceMaterial => new FaceMaterialFeature(),
		ToolKind.MoveFace => new MoveFaceFeature(),
		ToolKind.Paint => new PaintFeature(),
		ToolKind.Boolean => NewBoolean( choice ),
		_ => throw new ArgumentOutOfRangeException( nameof( kind ), kind, "no feature for this tool" )
	};

	/// <summary>
	/// A feature the toolbar just made waits to be pointed at a sketch instead of helping itself to
	/// the most recent one.
	///
	/// Clicking Extrude used to put a solid on screen before you had said anything: the kernel reads
	/// an unset reference as "the last sketch", so the default distance was applied to whatever was
	/// nearest and the part jumped up a unit under the cursor. Handy once, startling every other
	/// time, and it hid the question the dialog was asking.
	///
	/// ONLY EXTRUDE AND REVOLVE. A Sweep's path and a Loft's sections are DESIGNED around unset
	/// references - drawing the profile and the path in either order is the point, and their
	/// tooltips promise it - so making those ask first would take away the thing the defaults are
	/// for.
	/// </summary>
	private static T AwaitingPick<T>( T feature ) where T : SketchConsumingFeature
	{
		feature.SketchFeatureId = SketchConsumingFeature.AwaitingPick;
		return feature;
	}

	/// <summary>
	/// A revolve that works on the first press.
	///
	/// The kernel's default axis is the typed one, and it has to stay that way so documents saved
	/// before the Axis dropdown existed rebuild exactly as they were - see RevolveFeature.AxisMode.
	/// A revolve created HERE has no such history, so it gets the mode a person actually wants:
	/// spun about its own left edge, like a lathe profile.
	/// </summary>
	private static RevolveFeature NewRevolve()
	{
		var feature = new RevolveFeature();
		feature.AxisMode.Index = RevolveFeature.AxisProfileLeftEdge;

		return feature;
	}

	/// <summary>Build a Boolean already set to the operation picked from the button's dropdown.
	/// Same shape as NewPrimitive: the variant is a starting value on the feature, not a separate
	/// kind of feature, so it stays editable in the dialog afterwards.</summary>
	private static BooleanFeature NewBoolean( int operation )
	{
		var feature = new BooleanFeature();

		if ( operation >= 0 )
			feature.Operation.Index = operation;

		return feature;
	}

	private static PrimitiveFeature NewPrimitive( int shape )
	{
		var feature = new PrimitiveFeature();

		if ( shape >= 0 )
			feature.Shape.Index = shape;

		return feature;
	}

	/// <summary>One button on the feature strip. Held as data rather than written straight into the
	/// layout so the strip can be rebuilt with a subset of them.</summary>
	private sealed class CreateTool
	{
		public EffigyIcon Icon;
		public string Tip;
		public ToolKind Kind;

		/// <summary>
		/// A material-symbol name for this tool where it appears in a Qt MENU rather than on the
		/// strip — the right-click face menu, which cannot draw an EffigyIcon because those are
		/// hand-painted glyphs and a Menu takes a name.
		///
		/// Beside the label rather than in a switch somewhere, so a tool that starts accepting a
		/// face brings its icon with it. Null is fine: the menu falls back rather than breaking.
		/// </summary>
		public string MenuIcon;

		/// <summary>
		/// Which stage tab this tool sits behind.
		///
		/// THE COLUMN THAT REPLACED GapBefore AND Starter. A bool saying "put a wider gap before
		/// this one" grouped the tools without naming the groups, and a bool saying "show this one
		/// from the start" hid the rest rather than explaining them. A stage name does both jobs
		/// out loud: it is the group's label on the tab, and it is what the lock rule tests.
		/// </summary>
		public string Stage;

		/// <summary>Text beside the glyph. On every tool now — showing one stage at a time is what
		/// bought the room, and the names are the whole reason to do it.</summary>
		public string Label;

		/// <summary>
		/// Variants behind this button, or null for one that just does its thing.
		///
		/// Where they exist the button opens a menu instead of adding anything, and the index
		/// chosen goes to <see cref="NewFeature"/>. Primitive is the case this was built for: six
		/// shapes that are one feature with one parameter set differently, which is a list rather
		/// than six buttons.
		/// </summary>
		public string[] Choices;
	}

	/// <summary>The shapes behind the Primitive button. Taken from PrimitiveFeature.Shape rather
	/// than written out again, so the menu cannot drift from the parameter it sets — a menu naming
	/// a shape the feature has never heard of would set an index that means something else.
	/// </summary>
	private static string[] PrimitiveShapes => new PrimitiveFeature().Shape.Options;

	/// <summary>Read off the feature rather than typed again here, for the reason the Primitive
	/// menu is: a dropdown naming an operation the kernel has never heard of is a bug nothing
	/// catches until someone picks it.</summary>
	private static string[] BooleanOps => new BooleanFeature().Operation.Options;

	/// <summary>
	/// The strip's tools, BUILT FRESH ON EVERY READ rather than held in a static field.
	///
	/// Nothing here is expensive — it runs once per toolbar refresh, which happens when a sketch is
	/// finished — and a property cannot carry objects from a dead assembly across a hotload the way
	/// a static field does. Between this and ToolKind replacing the factory delegates, there is no
	/// state left here for a reload to invalidate.
	/// </summary>
	private static CreateTool[] CreateTools => new CreateTool[]
	{
		// --- Sketch: the two tools that can start a part from nothing ---------------------------
		new() { Icon = EffigyIcon.Sketch, Label = "Sketch", Stage = StageSketch, MenuIcon = "edit",
			Tip = "Add a Sketch feature — draw lines/arcs on a plane",
			Kind = ToolKind.Sketch },

		new() { Icon = EffigyIcon.Primitive, Label = "Primitive", Stage = StageSketch,
			Tip = "Add a Primitive — pick a shape",
			Kind = ToolKind.Primitive, Choices = PrimitiveShapes },

		// --- Solid: profiles become bodies ------------------------------------------------------
		new() { Icon = EffigyIcon.Extrude, Label = "Extrude", Stage = StageSolid, MenuIcon = "arrow_upward",
			Tip = "Add an Extrude — pull a sketch profile, or a face of a part, into a solid",
			Kind = ToolKind.Extrude },
		new() { Icon = EffigyIcon.Revolve, Label = "Revolve", Stage = StageSolid,
			Tip = "Add a Revolve — sweep a sketch profile around an axis",
			Kind = ToolKind.Revolve },

		// Neither of these needs its selector filled in to do something: an empty
		// SweepFeature.PathSketchId means "the sketch before the profile's", and a LoftFeature with
		// fewer than two Sections lofts every sketch there is. Both are the order a person draws
		// them in, so the tooltips say so rather than sending them to a dialog first.
		new() { Icon = EffigyIcon.Sweep, Label = "Sweep", Stage = StageSolid,
			Tip = "Add a Sweep — run a sketch profile along a path sketch",
			Kind = ToolKind.Sweep },
		new() { Icon = EffigyIcon.Loft, Label = "Loft", Stage = StageSolid,
			Tip = "Add a Loft — skin a surface between two or more sketches",
			Kind = ToolKind.Loft },

		// LAST ON SOLID, because it is the tool that turns several solids back into one and there
		// have to be several first. Not on Repeat with Mirror and the patterns: those COPY bodies
		// and this CONSUMES them, which is the opposite direction and a bad neighbour.
		new() { Icon = EffigyIcon.Boolean, Label = "Boolean", Stage = StageSolid, MenuIcon = "join_full",
			Tip = "Add a Boolean — union, subtract or intersect bodies",
			Kind = ToolKind.Boolean, Choices = BooleanOps },

		// --- Detail: refine a body that already exists ------------------------------------------
		// Fillet before Chamfer, which is the order Onshape puts them in and the order people reach
		// for them: rounding an edge is the common case and chamfering it is the deliberate one.
		new() { Icon = EffigyIcon.Fillet, Label = "Fillet", Stage = StageDetail, MenuIcon = "rounded_corner",
			Tip = "Add a Fillet — round sharp edges to a radius",
			Kind = ToolKind.Fillet },
		new() { Icon = EffigyIcon.Chamfer, Label = "Chamfer", Stage = StageDetail, MenuIcon = "details",
			Tip = "Add a Chamfer — cut sharp edges back by a distance",
			Kind = ToolKind.Chamfer },
		new() { Icon = EffigyIcon.Shell, Label = "Shell", Stage = StageDetail, MenuIcon = "crop_free",
			Tip = "Add a Shell — hollow to a wall thickness",
			Kind = ToolKind.Shell },

		// WITH THE DETAIL TOOLS because it acts on a solid that already exists, which is what that
		// stage means. It is not next to Extrude even though the two overlap: Extrude BUILDS from a
		// face and this MOVES one, and a wall that is in the wrong place is a thing you fix late.
		new() { Icon = EffigyIcon.MoveFace, Label = "Move Face", Stage = StageDetail, MenuIcon = "open_with",
			Tip = "Add a Move Face — push, pull or slide picked faces of a part",
			Kind = ToolKind.MoveFace },

		// Both act on picked faces of a solid that already exists, which is what puts them with
		// Shell rather than with Extrude.
		new() { Icon = EffigyIcon.Draft, Label = "Draft", Stage = StageDetail, MenuIcon = "signal_cellular_null",
			Tip = "Add a Draft — taper picked faces so the part leaves a mould",
			Kind = ToolKind.Draft },
		new() { Icon = EffigyIcon.Hole, Label = "Hole", Stage = StageDetail, MenuIcon = "radio_button_unchecked",
			Tip = "Add a Hole — drill, counterbore or countersink into a face",
			Kind = ToolKind.Hole },

		// --- Repeat: copy and move whole bodies -------------------------------------------------
		new() { Icon = EffigyIcon.Mirror, Label = "Mirror", Stage = StageRepeat,
			Tip = "Add a Mirror — reflect bodies across a plane",
			Kind = ToolKind.Mirror },
		new() { Icon = EffigyIcon.LinearPattern, Label = "Linear", Stage = StageRepeat,
			Tip = "Add a Linear Pattern — copy bodies along a direction",
			Kind = ToolKind.LinearPattern },
		new() { Icon = EffigyIcon.CircularPattern, Label = "Circular", Stage = StageRepeat,
			Tip = "Add a Circular Pattern — copy bodies around an axis",
			Kind = ToolKind.CircularPattern },
		new() { Icon = EffigyIcon.Transform, Label = "Transform", Stage = StageRepeat,
			Tip = "Add a Transform — move, rotate or scale bodies",
			Kind = ToolKind.Transform },

		// --- Finish: what is left on the CAD bar after the other workspaces took theirs ---------
		// Subdivide and Sculpt live in the Sculpt workspace, and UV Project and Paint in the Paint
		// workspace. Face Material is the one finish-line tool that stays here: it is a material-slot
		// assignment, a thing CAD owns, not a sculpt or a paint.
		new() { Icon = EffigyIcon.FaceMaterial, Label = "Face Material", Stage = StageFinish, MenuIcon = "palette",
			Tip = "Add a Face Material — put picked faces on a material slot",
			Kind = ToolKind.FaceMaterial },
	};

	// --- what a tool will take from the selection ----------------------------------------------

	/// <summary>
	/// What the tool behind a strip button will take from the geometry already picked.
	///
	/// ASKED OF THE FEATURE, never written down here. "Which tools consume a face" used to live in
	/// three places that could not see each other — the switch in Feature.ApplyGeometrySelection,
	/// the sentence under the viewport, and the pick-mode flags each dialog arms — so the hint could
	/// name a tool that ignored the face, and a tool that wanted one could be missing from the list
	/// with nothing to notice. Feature.Accepts is the one declaration now, and everything below
	/// reads it rather than repeating it.
	///
	/// A FRESH FEATURE PER CALL, no cache. Accepts is a constant expression on every feature, and
	/// the alternative — a static map from ToolKind — is exactly the shape of state that comes back
	/// from a hotload pointing into a dead assembly (see ToolKind's own note). This is called when
	/// the selection changes and when a menu opens, never from a paint pass.
	/// </summary>
	private static GeometryKind AcceptedBy( ToolKind kind ) => NewFeature( kind, -1 ).Accepts;

	/// <summary>The strip's tools that will use a pick of this kind, in the order they sit on the
	/// bar so the sentence reads left to right the way the buttons do.</summary>
	private static List<CreateTool> ToolsAccepting( GeometryKind kind ) =>
		CreateTools.Where( t => AcceptedBy( t.Kind ).HasFlag( kind ) ).ToList();

	/// <summary>Those tools, named. Empty when nothing consumes this kind yet.</summary>
	private static string ToolsNamed( GeometryKind kind )
	{
		var names = ToolsAccepting( kind ).Select( t => t.Label ).ToList();

		return names.Count switch
		{
			0 => "",
			1 => names[0],
			_ => $"{string.Join( ", ", names.Take( names.Count - 1 ) )} and {names[^1]}",
		};
	}

	/// <summary>
	/// A sketch with something drawn in it exists, so the rest of the tools have something to bite
	/// on.
	///
	/// Curves rather than merely the feature: clicking Sketch adds the feature to the tree straight
	/// away, before a plane is even chosen, so its presence alone would unlock the bar while there
	/// was still nothing to extrude.
	/// </summary>
	private bool HasConfirmedSketch() =>
		_studio is not null
		&& _studio.Features.OfType<SketchFeature>().Any( f => f.Sketch is { Curves.Count: > 0 } );

	/// <summary>
	/// The feature the toolbar made a moment ago that this click would only make a second copy of,
	/// or null.
	///
	/// PENDING is the dialog still being open on it as a NEW feature: it has not been ticked, and
	/// its cross would delete it again. UNTOUCHED is nobody having answered anything on it yet -
	/// nothing drawn in a sketch, no number typed into an extrude. Both halves matter: without the
	/// first, clicking Extrude after committing one would reopen the committed extrude instead of
	/// starting the next; without the second, there would be no way to add two of anything in a row
	/// without ticking in between.
	/// </summary>
	private Feature PendingDuplicate( Feature candidate )
	{
		if ( _dialog is not { IsOpen: true, IsNew: true, IsUntouched: true } )
			return null;

		var pending = _dialog.Feature;

		if ( pending is null || pending.GetType() != candidate.GetType() )
			return null;

		// A variant picked out of a menu is a DIFFERENT thing to make even though it is the same
		// feature class, and reusing the pending cube would silently swallow the sphere just chosen.
		// Sketch is exempt: its plane is answered inside the dialog, so a candidate built fresh with
		// the default plane says nothing about what the pending one is set to.
		if ( pending is not SketchFeature && !SameParameters( pending, candidate ) )
			return null;

		return pending;
	}

	/// <summary>
	/// Whether two features of the same type are set up identically.
	///
	/// Written out by parameter type rather than through some general value accessor because IParam
	/// deliberately has none - the parameters ARE the storage in this kernel (see Feature.cs), which
	/// is the same reason the dialog's snapshot is a switch like this one. Exact float comparison is
	/// correct here: both sides are constructor defaults, not the result of arithmetic.
	/// </summary>
	private static bool SameParameters( Feature a, Feature b )
	{
		var left = a.Parameters;
		var right = b.Parameters;

		if ( left.Count != right.Count )
			return false;

		for ( var i = 0; i < left.Count; i++ )
		{
			var same = (left[i], right[i]) switch
			{
				(FloatParam x, FloatParam y) => x.Value == y.Value,
				(IntParam x, IntParam y) => x.Value == y.Value,
				(BoolParam x, BoolParam y) => x.Value == y.Value,
				(Vec3Param x, Vec3Param y) => x.Value.Equals( y.Value ),
				(ChoiceParam x, ChoiceParam y) => x.Index == y.Index,

				// A parameter kind nobody here knows about: treat it as a difference, so an unknown
				// setting can never be quietly thrown away by reusing a feature that does not match.
				_ => false,
			};

			if ( !same )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Append a feature and leave it selected with its dialog open — Onshape's behaviour, and the
	/// reason the buttons feel like they did something. A freshly added Extrude with no sketch
	/// above it WILL show an error; that is correct, and the parameter panel is where you fix it.
	/// </summary>
	private void AddFeature( Feature feature )
	{
		// Pressing the same button again while the last one is still sitting there unanswered and
		// unconfirmed goes BACK TO THAT ONE rather than stacking another copy into the tree.
		// Impatience with a picker - clicking once more because nothing appeared to happen - produced
		// a row of identical dead features that all had to be deleted by hand. Nothing is added here,
		// so there is no undo step to record either.
		if ( PendingDuplicate( feature ) is { } pending )
		{
			_featureTree?.Select( pending );

			// Some features open with nothing left to ask for - a Fillet arrives with its radius
			// already typed in - and a click that neither adds anything nor lights anything up reads as
			// a broken button. Say what happened instead.
			if ( !_dialog.ReassertPending() )
				SetPrompt( $"{pending.Name ?? pending.TypeName} is already open above - finish it with the "
					+ "tick, or cancel it, before adding another." );

			return;
		}

		// SUBDIVIDE ASKS WHICH PART FIRST. An empty Bodies list means "the whole studio" to the
		// feature, so a click with nothing selected quietly quadrupled the triangle count of every
		// part in the document - including the ones you were not looking at, and on a cage you were
		// about to sculpt that is the difference between a usable model and a dense one.
		//
		// The whole-body form is not removed, because it is the one that actually smooths and is
		// usually what you want: this only refuses to GUESS which body. Click a part in the Parts
		// list and you get that part entire; pick faces in the viewport and you get those, since a
		// face selection already names its own body.
		if ( feature is SubdivideFeature
			&& _viewport is not null
			&& _viewport.IdleFaces.Count == 0
			&& _viewport.IdleBodyIds.Count == 0 )
		{
			SetPrompt( "Subdivide needs to know which part - click one in the Parts list on the "
				+ "left, or pick faces in the viewport, then press Subdivide again." );
			return;
		}

		RecordUndo();

		// A face or part already selected is the input, the way Onshape consumes the current
		// selection instead of making you pick it again inside the dialog that just opened.
		ApplyIdleGeometrySelection( feature );

		// A new feature goes AT THE ROLLBACK BAR, not at the end of the tree - same as Onshape.
		// Appending would drop it below the bar, where it does not get evaluated: you would add an
		// Extrude while rolled back, watch nothing happen, and have no way to tell why. The bar
		// moves down past it so the thing you just added is the last one running.
		//
		// THE BAR AT EXACTLY Features.Count IS THE CASE THAT WAS MISSING, and it is the ordinary
		// one rather than an edge case: finishing an edit on the LAST feature leaves the bar at
		// index+1, which is Features.Count - a finite number that means "everything runs" today and
		// "everything except the next thing you add" a moment later. The old test was `<`, so that
		// bar took the append branch and never moved, the new feature landed exactly ON the bar,
		// and EffectiveCount excluded it. It was added to the tree, drawn in the tree, and never
		// executed.
		//
		// For a sketch on a face that is invisible until you look closely: SketchFeature.Execute is
		// what derives the plane from the face, so a sketch that never ran keeps the default XY
		// plane. The face outline then gets projected onto XY, which throws away Z, folds the
		// face's four corners onto two, and draws one green line lying flat through the middle of
		// the model. Every part of that is downstream of this comparison.
		InsertAtRollback( feature );

		RebuildStudio();

		_featureTree?.Select( feature );

		// Select() above already opened the dialog through the tree's selection callback, but as
		// an edit. Reopening marks it as new, which is what makes Cancel delete it rather than
		// leaving a half-configured feature behind.
		_dialog?.Open( feature, isNew: true );
	}

	// --- docks (viewport, feature tree, parameter panel) -----------------------------------

	private void BuildDocks()
	{
		_viewport = new EffigyViewport( this );

		_featureTree = new EffigyFeatureTreePanel( this, _studio )
		{
			FeatureSelected = OnFeatureSelected,
			StudioChanged = OnStudioChanged,
			VisibilityToggled = OnTreeVisibilityToggled,
			CommandRequested = OnFeatureCommand,
			RenameCommitted = OnFeatureRenamed,
		};

		_dialog = new EffigyFeatureDialog( this, _viewport )
		{
			Edited = OnFeatureEdited,
			Renamed = () => _featureTree?.Rebuild(),
			Accepted = OnDialogAccepted,
			Cancelled = OnDialogCancelled,
			SketchRequested = EnterSketch,
			SculptRequested = EnterSculpt,
			SketchNameLookup = id => _studio.Features.OfType<SketchFeature>().FirstOrDefault( f => f.Id == id )?.Name,
			PickableBodiesLookup = () => _studio.Bodies,
			BodyNameLookup = id => _studio.Bodies.FirstOrDefault( b => b.Id == id )?.Name,
			OpenedForFeature = f =>
			{
				UpdatePickTargets( f );
				_resultStrip?.Bind( f, SketchHostBodyId );
			},
			MaterialLookup = SlotMaterial,
			MaterialChanged = SetSlotMaterial,
		};

		_partsPanel = new EffigyPartsPanel( this, _studio )
		{
			VisibilityToggled = OnPartVisibilityToggled,
			CommandRequested = OnPartCommand,
			RenameCommitted = OnPartRenamed,
			SelectionChanged = OnPartTreeSelectionChanged,
		};

		// The Materials dock is the material BROWSER - a grid of the project's materials you drag
		// onto faces - not the column of slot rows it used to be. It edits nothing itself: a drag is
		// reported by the VIEWPORT, which is where it lands, and the two clicks come back here.
		_materialsPanel = new EffigyMaterialsPanel( this, _studio )
		{
			MaterialChanged = SetSlotMaterial,
			MaterialActivated = SetBaseMaterial,
			ScaleChanged = SetMaterialScale,

			// So an armed brush picks up a new material the moment it is clicked in the browser,
			// rather than needing to be left and re-entered to notice.
			SelectedMaterialChanged = OnBrushMaterialChanged,
		};

		_rigPanel = new EffigyRigPanel( this, _studio, _viewport );

		// Built here with the rest so the dock's CreateAction has something to hand back. The
		// engine's console widget goes inside it - see EffigyConsolePanel for why that takes any
		// code at all.
		_consolePanel = new EffigyConsolePanel( this );

		_tutorial = new EffigyTutorial();

		_tutorialPanel = new EffigyTutorialPanel( this )
		{
			Tutorial = _tutorial,
			RevealPanel = RevealDock,
			HighlightTool = HighlightTool,

			// Restart and Dismiss both change what the strip should be showing, and the panel
			// itself has no idea a toolbar exists. Re-evaluating here also means a Restart drops
			// straight back to whichever step the document already satisfies, rather than
			// insisting on step one of work that is already done.
			Changed = RefreshTutorial,
		};

		// Dialog ABOVE the tree in one column, which is where Onshape puts it. It was a separate
		// right-hand dock at first and that was the single biggest reason the tool did not read as
		// Onshape: the thing you are editing and the history you are editing it in belong in the
		// same column, and the viewport gets everything else.
		_leftPanel = new Widget( this ) { Layout = Layout.Column() };
		_leftPanel.Name = "Features";
		_leftPanel.WindowTitle = "Features";
		_leftPanel.SetWindowIcon( "account_tree" );
		_leftPanel.Layout.Add( _dialog );
		_leftPanel.Layout.Add( _featureTree, 1 );

		// Parts BELOW the feature tree, the way Onshape stacks them: the recipe on top, what it
		// actually built underneath.
		_leftPanel.Layout.Add( _partsPanel );

		_viewport.SketchEdited = OnSketchEdited;

		// The origin is the model's pivot, so moving it changes the exported result and has to be
		// recorded. Dead until now for want of anything downstream that cared.
		_viewport.OriginMoved = OnOriginMoved;
		_viewport.LightingChanged = OnLightingChanged;

		// APPLYING OR DELETING A CONSTRAINT MOVES THE SKETCH, so it is a sketch edit and has to
		// reach the same place every other one does. This event had no subscriber at all: the solver
		// ran, the points moved on screen, and nothing was ever marked dirty - so an extrude above
		// the sketch went on standing on the profile from before the constraint. Exactly the fault
		// the point drag had, one event over.
		_viewport.SketchConstraintApplied = OnSketchEdited;

		_viewport.IdleSelectionChanged = OnIdleSelectionChanged;
		_viewport.FaceContextMenuRequested = OpenFaceMenu;
		_viewport.MaterialDropped = OnMaterialDropped;
		_viewport.SketchConstraintMenuRequested = OpenSketchConstraintMenu;

		// Fired BEFORE the viewport changes a sketch, which is the only moment a useful "before"
		// exists to snapshot.
		_viewport.SketchEditing = RecordUndo;
		_viewport.SketchPromptChanged = SetPrompt;

		// Same "before" moment, for the rig: a bone placed, deleted, renamed, or mirrored.
		_rigPanel.RigChanging = RecordUndo;

		// AND THE "AFTER", which nothing had ever subscribed to. RigChanged has been declared and
		// raised from five places since the panel was written, with no listener on the other end,
		// so a rig edit never marked the document unsaved. That was invisible while the rig lived
		// only in this window - there was nothing to save it INTO, so nothing to lose - and became
		// a way to lose work the moment the rig went into the .effigy file: place bones, close the
		// window, and it closes clean without asking, because as far as the title bar was concerned
		// nothing had happened.
		_rigPanel.RigChanged = NoteRigEdited;

		_centralDock = DockManager.SetCentralWidget( _viewport );

		DockManager.RegisterDock( new() { Title = "Features", Icon = "account_tree", Area = DockArea.Left, CreateAction = () => _leftPanel } );
		DockManager.RegisterDock( new() { Title = "Rig", Icon = "account_tree", Area = DockArea.Right, CreateAction = () => _rigPanel } );

		// Right, tabbed behind the Rig, because both are things you do to a part that is already
		// modelled and neither is worth permanent screen room while you are still modelling it.
		DockManager.RegisterDock( new() { Title = "Materials", Icon = "palette", Area = DockArea.Right, CreateAction = () => _materialsPanel } );

		// Bottom, full width, and NOT tabbed behind anything. A tutorial that shares a tab strip
		// is a tutorial you lose the moment you look at the thing it told you to look at — which
		// is every step. Along the bottom it stays readable while both side docks are in use.
		DockManager.RegisterDock( new() { Title = "Tutorial", Icon = "school", Area = DockArea.Bottom, CreateAction = () => _tutorialPanel } );

		// Bottom as well, and tabbing with the Tutorial is fine here in a way it is not for the
		// Rig: the tutorial's whole objection to sharing a tab strip is that you lose it the moment
		// you look at what it told you to look at, and nobody follows the tutorial and reads the
		// console at the same time. Along the bottom is also where every editor puts a console, and
		// full width is what a stack trace needs.
		DockManager.RegisterDock( new() { Title = "Console", Icon = "terminal", Area = DockArea.Bottom, CreateAction = () => _consolePanel } );

		// Bumped from Effigy1: the Parameters dock is gone and the tree moved into a shared column
		// with the dialog. A restored Effigy1 layout would reinstate the old two-dock arrangement
		// and BuildDefaultLayout would never run again.
		// Bumped from Effigy2: restored Effigy2 layouts came back degenerate - the Features dock a
		// sliver and stray chrome floating over the viewport - so anyone with one saved never got
		// a usable window. A fresh cookie forces the known-good default layout.
		// Bumped from Effigy3: the Materials dock is new, and a restored Effigy3 layout knows
		// nothing about it - the panel would exist, be wired up, and never appear on screen.
		// Bumped from Effigy4: the Tutorial dock is new, and the same applies — worse here,
		// because the one person it is for is the one person guaranteed to have no saved layout
		// only if they have never opened Effigy before, which is not who upgrades.
		// Bumped from Effigy5: the Materials dock is now the material browser rather than a column
		// of slot rows, and it wants room to show a grid. A restored Effigy5 layout would give the
		// new panel the width the old list was sized for and it would come back one cell wide.
		// Bumped from Effigy6: the default layout opens the feature tree and nothing else. Which
		// docks are open lives in the saved layout, so without a new cookie everyone who has
		// already opened Effigy keeps starting with the Rig and Materials columns forever.
		// Bumped from Effigy7: the Console dock is new. A restored Effigy7 layout knows nothing
		// about it, so the panel would exist, be registered, and have nowhere on screen to go -
		// the same failure the Materials and Tutorial docks each hit when they arrived.
		StateCookie = "Effigy8";
	}

	/// <summary>The Parts list clicked a row. That is a whole-part selection — faces drop, the
	/// viewport lights the solid, and the next tool that acts on bodies uses this part.</summary>
	private bool _syncingSelection;

	private void OnPartTreeSelectionChanged( IReadOnlyList<string> bodyIds )
	{
		if ( _syncingSelection || _viewport is null )
			return;

		_syncingSelection = true;
		_viewport.SelectBodies( bodyIds );
		_syncingSelection = false;
		DescribeGeometrySelection();
	}

	/// <summary>A face (or empty space) was clicked in the viewport. Keep the Parts list on the
	/// same bodies so the row highlight and the 3D highlight cannot disagree.</summary>
	private void OnIdleSelectionChanged()
	{
		if ( _syncingSelection || _partsPanel is null )
			return;

		_syncingSelection = true;
		_partsPanel.Select( _viewport.IdleBodyIds );
		_syncingSelection = false;
		DescribeGeometrySelection();
	}

	/// <summary>
	/// The sentence under the viewport that says what the thing you just clicked is good for.
	///
	/// GENERATED FROM Feature.Accepts, not typed. It used to read "Face of {name} selected — Sketch,
	/// Draft, Hole, Face Material and Fillet will use it", which was true on the day it was written
	/// and became a lie the moment anything else learned to take a face — Subdivide did, and the
	/// line was edited by hand to keep up. A hint that has to be maintained alongside the tools it
	/// describes is a hint that will eventually be wrong, quietly, in the one place a person looks
	/// to find out what they can do next.
	/// </summary>
	private void DescribeGeometrySelection()
	{
		if ( _viewport is null )
			return;

		MarkToolsTakingSelection();

		if ( _viewport.FacePickMode || _viewport.BodyPickMode || _viewport.PlanePickMode || _viewport.SketchPickMode )
			return;

		if ( _viewport.IdleSketchFeatureId is not null )
		{
			var name = _studio.Features.OfType<SketchFeature>()
				.FirstOrDefault( f => f.Id == _viewport.IdleSketchFeatureId )?.Name ?? "Sketch";
			var n = _viewport.IdleRegionSeeds.Count;
			SetPrompt( n <= 1
				? $"{name} selected{Users( GeometryKind.SketchRegion, one: true )}"
				: $"{name}: {n} faces selected{Users( GeometryKind.SketchRegion, one: false )}" );
			return;
		}

		var faces = _viewport.IdleFaces.Count;
		var edgeCount = _viewport.IdleEdges.Count;
		var bodies = _viewport.IdleBodyIds.Count;

		if ( edgeCount == 1 )
		{
			SetPrompt( $"1 edge selected{Users( GeometryKind.Edge, one: true )}" );
			return;
		}

		if ( edgeCount > 1 )
		{
			SetPrompt( $"{edgeCount} edges selected{Users( GeometryKind.Edge, one: false )}" );
			return;
		}

		if ( faces == 1 )
		{
			var id = _viewport.IdleFaces[0].BodyId;
			var name = _studio.Bodies.FirstOrDefault( b => b.Id == id )?.Name ?? "part";
			SetPrompt( $"Face of {name} selected{Users( GeometryKind.Face, one: true )}" );
			return;
		}

		if ( faces > 1 )
		{
			SetPrompt( $"{faces} faces selected{Users( GeometryKind.Face, one: false )}" );
			return;
		}

		if ( bodies == 1 )
		{
			var name = _studio.Bodies.FirstOrDefault( b => b.Id == _viewport.IdleBodyIds[0] )?.Name ?? "part";
			SetPrompt( $"{name} selected" );
			return;
		}

		if ( bodies > 1 )
		{
			SetPrompt( $"{bodies} parts selected" );
			return;
		}

		SetPrompt( "" );
	}

	/// <summary>
	/// The " — Draft, Hole and Fillet will use it." half of the hint, or nothing at all when no tool
	/// takes this kind of pick yet.
	///
	/// SAYING NOTHING IS A REAL ANSWER. A selection nothing consumes should report itself and stop,
	/// rather than promising a list that turns out to be empty.
	/// </summary>
	private static string Users( GeometryKind kind, bool one )
	{
		var names = ToolsNamed( kind );

		return names.Length == 0 ? "" : $" — {names} will use {(one ? "it" : "them")}.";
	}

	/// <summary>
	/// What kind of geometry is selected right now, in the vocabulary Accepts speaks.
	///
	/// THE PRIORITY MIRRORS DescribeGeometrySelection'S, deliberately, because the sentence under
	/// the viewport and the marks on the bar are two readings of one fact and they must not
	/// disagree about which one it is. Sketch region beats edges beats faces beats bodies, and the
	/// first branch that matches wins in both places.
	///
	/// A FACE SELECTION ALSO NAMES ITS BODY, so it reports Face | Body. This is not a convenience:
	/// ApplyGeometrySelection really does hand a body-only tool the part a picked face belongs to,
	/// and AcceptsTests asserts that asymmetry rather than working around it. Reporting Face alone
	/// here would leave Shell and Subdivide unmarked on a click that they would in fact consume.
	///
	/// NOTHING WHILE A DIALOG IS PICKING. A tool with a pick mode armed owns the clicks, and the
	/// marks would be describing a selection that is on its way into a box rather than one sitting
	/// there waiting for a tool.
	/// </summary>
	private GeometryKind SelectedGeometry()
	{
		if ( _viewport is null )
			return GeometryKind.None;

		if ( _viewport.FacePickMode || _viewport.BodyPickMode || _viewport.PlanePickMode || _viewport.SketchPickMode )
			return GeometryKind.None;

		if ( _viewport.IdleSketchFeatureId is not null )
			return GeometryKind.SketchRegion;

		if ( _viewport.IdleEdges.Count > 0 )
			return GeometryKind.Edge;

		if ( _viewport.IdleFaces.Count > 0 )
			return GeometryKind.Face | GeometryKind.Body;

		if ( _viewport.IdleBodyIds.Count > 0 )
			return GeometryKind.Body;

		return GeometryKind.None;
	}

	/// <summary>
	/// Light the bar's tools that will use what is selected.
	///
	/// The third consumer of Accepts, and the last one to be built: the type switch that DOES the
	/// consuming, the sentence under the viewport that names the tools, and now the buttons
	/// themselves. All three ask the same declaration, so the bar cannot mark a tool the sentence
	/// leaves out.
	///
	/// ASKED ONCE PER SELECTION CHANGE, NEVER PER PAINT. AcceptedBy builds a fresh feature to read
	/// a constant off it — cheap, but not free, and not something to do sixty times a second for
	/// every button on screen. The answer is stored on the tool and the bar just draws it.
	///
	/// PART MODE ONLY. The sketch stages have their own vocabulary — points and curves, not faces
	/// and bodies — and none of their tools declares Accepts, so marking there would mean marking
	/// nothing, every time, forever.
	/// </summary>
	private void MarkToolsTakingSelection()
	{
		if ( _featureTools.Count == 0 )
			return;

		var selected = BarMode == EffigyBarMode.Part ? SelectedGeometry() : GeometryKind.None;
		var changed = false;

		foreach ( var (kind, tool) in _featureTools )
		{
			// Any overlap at all, not an exact match: a tool that takes faces OR bodies is worth
			// marking when either is what you have.
			var takes = selected != GeometryKind.None && (AcceptedBy( kind ) & selected) != GeometryKind.None;

			if ( tool.Takes == takes )
				continue;

			tool.Takes = takes;
			changed = true;
		}

		// Only when something actually moved. This runs on every selection change, and a selection
		// change is also a viewport repaint — refreshing the bar unconditionally would put a widget
		// update behind every click that lands on nothing.
		if ( changed )
			_stageBar?.Refresh();
	}

	/// <summary>
	/// Put a new feature at the rollback bar. Split out of AddFeature so the pull gizmo places its
	/// Move Face the same way the toolbar places everything else — see AddFeature for why the
	/// comparison below is what it is.
	/// </summary>
	private void InsertAtRollback( Feature feature )
	{
		var at = Math.Min( _studio.RollbackIndex, _studio.Features.Count );

		if ( at < _studio.Features.Count )
			_studio.Insert( at, feature );
		else
			_studio.Add( feature );

		// Only when the bar is a real position. int.MaxValue already means "evaluate everything"
		// and must stay that way, or every add would pin it to a number that stops meaning "the
		// end" the next time something is appended.
		if ( _studio.RollbackIndex < _studio.Features.Count )
			_studio.RollbackIndex = at + 1;
	}

	/// <summary>Copy the current viewport selection onto a feature the toolbar just made.</summary>
	private void ApplyIdleGeometrySelection( Feature feature )
	{
		if ( _viewport is null || feature is null )
			return;

		if ( !_viewport.HasIdleSelection )
			return;

		feature.ApplyGeometrySelection( _viewport.IdleFaces, _viewport.IdleBodyIds, _studio.Bodies,
			_viewport.IdleEdges, _viewport.IdleSketchFeatureId, _viewport.IdleRegionSeeds );
	}

	/// <summary>Hide or show one body, from the Parts list's eye or its Hide menu item.
	///
	/// Per body, not per feature: hiding one copy of a pattern must not hide the rest. No
	/// MarkDirty — this is drawing, not geometry, and PartStudio reapplies HiddenBodyIds at the
	/// end of every rebuild including a cached one.</summary>
	private void OnPartVisibilityToggled( string bodyId )
	{
		if ( string.IsNullOrEmpty( bodyId ) )
			return;

		RecordUndo();

		if ( !_studio.HiddenBodyIds.Remove( bodyId ) )
			_studio.HiddenBodyIds.Add( bodyId );

		RebuildStudio();
	}

	private void OnPartCommand( string bodyId, EffigyPartCommand command )
	{
		if ( string.IsNullOrEmpty( bodyId ) )
			return;

		switch ( command )
		{
			case EffigyPartCommand.Rename:
				_partsPanel?.BeginRename( bodyId );
				break;

			case EffigyPartCommand.ToggleVisibility:
				OnPartVisibilityToggled( bodyId );
				break;

			case EffigyPartCommand.Edit:
				if ( FeatureForBody( bodyId ) is { } feature )
					EditFeature( feature );
				break;

			case EffigyPartCommand.Delete:
				if ( FeatureForBody( bodyId ) is { } toDelete )
					OnFeatureCommand( toDelete, EffigyFeatureCommand.Delete );
				break;

			case EffigyPartCommand.Isolate:
				RecordUndo();

				_studio.HiddenBodyIds.Clear();

				foreach ( var body in _studio.Bodies )
				{
					if ( body.Id != bodyId )
						_studio.HiddenBodyIds.Add( body.Id );
				}

				RebuildStudio();
				break;

			case EffigyPartCommand.ShowAll:
				RecordUndo();
				_studio.HiddenBodyIds.Clear();
				RebuildStudio();
				break;
		}
	}

	private void OnPartRenamed( string bodyId, string name )
	{
		if ( string.IsNullOrEmpty( bodyId ) )
			return;

		RecordUndo();

		var trimmed = string.IsNullOrWhiteSpace( name ) ? null : name.Trim();

		if ( trimmed is null )
			_studio.BodyNames.Remove( bodyId );
		else
			_studio.BodyNames[bodyId] = trimmed;

		RebuildStudio();
	}

	private Feature FeatureForBody( string bodyId )
	{
		var featureId = _studio.Bodies.FirstOrDefault( b => b.Id == bodyId )?.FeatureId;

		return featureId is null ? null : _studio.Features.FirstOrDefault( f => f.Id == featureId );
	}

	private void OnTreeVisibilityToggled( string key, bool visible )
	{
		if ( _viewport is null )
			return;

		switch ( key )
		{
			case "origin": _viewport.OriginVisible = visible; break;
			case "top": _viewport.TopPlaneVisible = visible; break;
			case "front": _viewport.FrontPlaneVisible = visible; break;
			case "right": _viewport.RightPlaneVisible = visible; break;
			default:
				var sketch = _studio.Features.OfType<SketchFeature>()
					.FirstOrDefault( x => $"sketch:{x.Id}" == key );
				_viewport.SetSketchVisibility( sketch?.Sketch, visible );
				break;
		}
	}

	/// <summary>The window a fresh Effigy opens as: the feature tree on the left, the viewport
	/// taking everything else, and nothing else on screen.
	///
	/// THE RIG AND MATERIALS DOCKS ARE REGISTERED BUT DELIBERATELY NOT OPENED. Both are for work
	/// that comes after there is a shape to do it to, and a window that starts with three panels
	/// open spends its first minute being closed rather than used. Each is one click away in View,
	/// which is the whole reason every dock now has a line in that menu.</summary>
	protected override void BuildDefaultLayout()
	{
		var featuresDock = DockManager.OpenDock( "Features", DockArea.Left, _centralDock );
		DockManager.SetSplitterProportions( featuresDock, 0.26f, 0.74f );

		DockManager.RaiseDock( "Features" );
	}

	// --- status bar -------------------------------------------------------------------------

	private void BuildStatusBar()
	{
		_statusWidget = new StatusBar( this );
		_statusWidget.AddWidgetLeft( new Editor.Label( "Effigy" ) { FixedWidth = 52 }, 0 );

		_promptLabel = new Editor.Label( "" );
		_statusWidget.AddWidgetLeft( _promptLabel, 1 );

		_statusInfoLabel = new Editor.Label( "" );
		_statusWidget.AddWidgetRight( _statusInfoLabel, 0 );

		_viewport.ModelInfoChanged = info =>
		{
			if ( _statusInfoLabel.IsValid() )
				_statusInfoLabel.Text = info;
		};

		StatusBar = _statusWidget;
	}

	// --- feature actions --------------------------------------------------------------------

	/// <summary>
	/// Selection in the tree opens that feature's dialog. A sketch that already has a plane
	/// goes straight into sketch mode from that Open — selecting it is editing it. A sculpt
	/// that already has a cage does the same.
	///
	/// A null selection deliberately does nothing. Every rebuild clears and refills the tree,
	/// which momentarily reports "nothing selected" - closing the dialog on that would slam it
	/// shut on the first tick of every slider drag, since dragging rebuilds.
	/// </summary>
	private void OnFeatureSelected( Feature feature )
	{
		if ( feature is null )
			return;

		if ( _viewport.IsSketching && feature != _dialog?.Feature )
			FinishSketch();

		// Sculpt closes its dialog on entry, so "still this feature" is the live sculpt rather
		// than the dialog. Clicking the same row must not Finish and re-Open.
		if ( _viewport.IsSculpting && feature != _sculptFeature )
			FinishSculpt();

		if ( _viewport.IsSculpting && feature == _sculptFeature )
			return;

		// Paint is the same shape: no dialog, so "still this feature" is the live paint session.
		if ( _viewport.IsPainting && feature != _paintFeature )
			FinishPaint();

		if ( _viewport.IsPainting && feature == _paintFeature )
			return;

		// Selecting a paint feature IS painting it — there is no parameter dialog to stop at.
		if ( feature is PaintFeature paint )
		{
			HighlightFeatureInViewport( feature );
			EnterPaint( paint );
			return;
		}

		// Same reasoning as FinishSketch above, for the bone tool: opening a dialog that may set
		// SketchPickMode (Extrude/Revolve) or arm a body/plane picker of its own would otherwise
		// collide with it exactly the way an open sketch would. Cheap to cancel outright — all
		// that's lost is an empty pending-chain state, not a feature mid-edit.
		if ( _viewport.BoneToolActive && feature != _dialog?.Feature )
			_rigPanel?.CancelBoneTool();

		if ( _dialog is null || (_dialog.IsOpen && _dialog.Feature == feature) )
			return;

		HighlightFeatureInViewport( feature );
		_dialog.Open( feature, isNew: false );
	}

	/// <summary>Clicking a feature in the tree lights what it made: a sketch's shaded face, or
	/// the parts a Primitive/Extrude/Fillet produced. Same idea as clicking a row in Parts.
	/// </summary>
	private void HighlightFeatureInViewport( Feature feature )
	{
		if ( _viewport is null || feature is null )
			return;

		_syncingSelection = true;

		if ( feature is SketchFeature )
		{
			_viewport.SelectIdleSketch( feature.Id, null );
		}
		else
		{
			var ids = new List<string>();

			foreach ( var body in _studio.Bodies )
			{
				if ( body.FeatureId == feature.Id )
					ids.Add( body.Id );
			}

			if ( ids.Count > 0 )
				_viewport.SelectBodies( ids );
		}

		_syncingSelection = false;
		_partsPanel?.Select( _viewport.IdleBodyIds );
		DescribeGeometrySelection();
	}

	private void OnDialogAccepted( Feature feature )
	{
		_resultStrip?.Bind( null, SketchHostBodyId );

		// The "already open above" line belongs to a feature that is no longer pending - see
		// AddFeature. Leaving it up would have it telling you to finish something you just did.
		SetPrompt( "" );

		if ( _viewport.IsSketching )
			FinishSketch();

		if ( _viewport.IsSculpting )
			FinishSculpt();

		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	/// <summary>Cancel on a feature that the toolbar had just created removes it outright - the
	/// feature only ever existed to be configured, so an abandoned dialog should leave the tree as
	/// it was. Cancelling an edit has already had its parameters restored by the dialog.</summary>
	private void OnDialogCancelled( Feature feature, bool wasNew )
	{
		_resultStrip?.Bind( null, SketchHostBodyId );
		SetPrompt( "" );

		if ( wasNew )
			_studio.Remove( feature );

		if ( _viewport.IsSketching )
			FinishSketch();

		if ( _viewport.IsSculpting )
			FinishSculpt();

		RestoreRollbackAfterEdit();
		RebuildStudio();
	}

	private void OnStudioChanged()
	{
		RebuildStudio();
	}

	/// <summary>
	/// Where the studio lives on disk, and whether it has been changed since it got there.
	///
	/// EVERY EDIT GOES THROUGH RebuildStudio, which is why the dirty flag is set there rather than
	/// at each of the thirty-odd call sites. Marking at the funnel cannot be forgotten by whoever
	/// adds the thirty-first; marking at the sites is a promise nobody keeps for long. Load, save
	/// and new all rebuild too, so each of those clears the flag afterwards.
	/// </summary>
	private string _documentPath;

	private bool _dirty;

	private void MarkClean()
	{
		_dirty = false;
		UpdateTitle();
	}

	private void UpdateTitle() =>
		Title = $"Effigy - {(_documentPath is null ? "untitled" : Path.GetFileName( _documentPath ))}{(_dirty ? "*" : "")}";

	private void RebuildStudio()
	{
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}

		var report = _studio.Rebuild();
		_featureTree?.Rebuild();
		_partsPanel?.Refresh();
		_materialsPanel?.Refresh();
		_rigPanel?.RefreshBodyNames();

		// Covers every other way the lock can change — undo back past the first sketch, deleting
		// it, opening a saved studio. Cheap: it returns immediately unless a stage's lock is
		// actually wrong.
		if ( BarMode == EffigyBarMode.Part )
			ShowPartStages();

		// Show whatever DID build, errors or not. A broken feature halfway down the tree should
		// leave the part above it on screen — going blank hides the very geometry you need to
		// look at to work out what the failing feature is missing.
		// Preview shows only what is visible; export below deliberately still takes everything.
		// Each face's slot resolves to the material dropped on it, so the preview wears the real
		// vmats rather than one flat placeholder. Unbound slots come back null and fall back.
		// Vertex colours ride on the mesh and composite over the material, so a painted body needs
		// no special material here — the ordinary build already carries them.
		var preview = EffigyPreview.Build( _studio.ToVisibleMesh(),
			slot => _studio.MaterialNames.TryGetValue( slot, out var name ) ? name : null );

		// Frame only when geometry first appears. Every later rebuild leaves the camera alone,
		// because rebuilds also happen on every parameter tick and the view must hold still
		// while you drag.
		_viewport?.SetModel( preview, frameCamera: preview is not null && !_hasPreview );
		_hasPreview = preview is not null;

		// The preview model is one flat grey, so a material slot is invisible in it. The viewport
		// tints the faces that carry one instead, and needs the bodies to do it - the mesh handed to
		// EffigyPreview above has already been flattened into one and lost which body it came from.
		_viewport?.SetDisplayBodies( _studio.Bodies );

		// Notes are not in the history and nothing above rebuilt them, but the surfaces they land
		// on have just moved and a New or an Open has swapped the list out from under the pen.
		RefreshNotes();

		// Rebuild() above discarded every tree node, taking the highlight with it. The feature
		// being edited has to stay visibly selected or the tree and the dialog disagree about
		// what you are working on.
		if ( _dialog?.Feature is { } editing )
			_featureTree?.Select( editing );

		UpdateDisplaySketches();

		// The face an open sketch is drawn on has just been rebuilt, so its outline is a rebuild
		// out of date. This is what makes editing the block underneath while its sketch is open
		// work rather than quietly snap to where that block used to be — and it is why the outline
		// lives here rather than being handed over once on entry.
		if ( _viewport?.IsSketching == true && ActiveSketchFeature() is { } openSketch )
			RefreshSketchReference( openSketch );

		// Feature.Error and Feature.Warning are only meaningful once the studio has tried to run
		// the feature, so the dialog's state is refreshed here rather than when it was opened.
		_dialog?.RefreshState();

		// AFTER the rebuild, never before. Every check the tutorial makes is about what the tree
		// produced — a body with volume, a fillet that did not error — and all of those are stale
		// or absent until Rebuild has run. Asking first would tick step one off on the rebuild
		// after the reader finished it, which reads as the tutorial lagging a move behind.
		RefreshTutorial();

		if ( report.HasErrors )
			Log.Warning( $"[Effigy] rebuild: {string.Join( "; ", report.Errors.Select( e => e.Message ) )}" );
	}

	// --- tutorial ------------------------------------------------------------------------------

	/// <summary>Open the tutorial dock and put it back at step one. The Help menu's whole
	/// contents, and the reason that menu exists.</summary>
	private void StartTutorial()
	{
		DockManager.SetDockState( "Tutorial", true );
		DockManager.RaiseDock( "Tutorial" );
		SyncDockChecks();

		_tutorial?.Restart();
		RefreshTutorial();
	}

	/// <summary>Open a dock and bring it to the front, for the panel's "show me the X panel"
	/// button. Opening without raising is not enough — a dock tabbed behind another comes back
	/// visible and still hidden, which looks exactly like the button doing nothing.</summary>
	private void RevealDock( string title )
	{
		if ( string.IsNullOrEmpty( title ) )
			return;

		DockManager.SetDockState( title, true );
		DockManager.RaiseDock( title );
		SyncDockChecks();
	}

	/// <summary>Re-read the document, advance the tutorial past anything already done, and repaint
	/// the panel. Called from RebuildStudio, so a step ticks off on the same rebuild that
	/// satisfied it rather than on whatever the reader happens to do next.</summary>
	private void RefreshTutorial()
	{
		if ( _tutorial is null )
			return;

		_tutorial.Evaluate( new EffigyTutorialState( _studio ) );

		// Rebuild unconditionally rather than only when Evaluate moved. The panel also renders
		// Active, the step's own pointer affordance and the highlight, and those change on a
		// Restart or a Dismiss that moved nothing at all.
		_tutorialPanel?.Rebuild();
	}

	/// <summary>Told by the panel which tool the current step wants, or null for none. Stores the
	/// target and re-applies; never stores a button.</summary>
	private void HighlightTool( EffigyToolTarget? target )
	{
		_highlightedTool = target;
		ApplyToolHighlight();
	}

	/// <summary>
	/// Push the current highlight onto the tools, and bring the one it wants into view.
	///
	/// Clears every tool before setting one, rather than tracking which was lit last: the cost of
	/// being certain is one pass over nineteen objects, and a stale ring on a button nobody can
	/// turn off is the failure it buys out of.
	///
	/// REVEALING IS THE HALF THE STRIP COULD NOT DO. A tutorial that says "press Extrude" used to
	/// be pointing at a button that was either on screen or hidden by the starter set, with
	/// nothing in between; now the tool always exists, and lighting it also opens the stage it
	/// lives on so the reader is looking at it.
	/// </summary>
	private void ApplyToolHighlight()
	{
		if ( _featureTools.Count == 0 || BarMode != EffigyBarMode.Part )
			return;

		var wanted = _highlightedTool is { } target ? ToolKindFor( target ) : null;

		EffigyStageTool lit = null;

		foreach ( var (kind, tool) in _featureTools )
		{
			tool.Attention = wanted == kind;

			if ( tool.Attention )
				lit = tool;
		}

		// Only when it is not already in front of the reader — Reveal is a no-op on the current
		// stage, so this cannot fight somebody who has just navigated somewhere themselves.
		if ( lit is not null )
			_stageBar?.Reveal( lit );

		_stageBar?.Refresh();
	}

	/// <summary>
	/// The tutorial's vocabulary of tools, mapped onto the strip's.
	///
	/// The one place the two enums meet, and the reason they are two enums: the strip's list is
	/// free to grow without the tutorial silently claiming to teach whatever was added, and the
	/// tutorial's list is free to name a tool the strip has not got yet — which returns null here
	/// and lights nothing, rather than throwing in a paint path.
	/// </summary>
	private static ToolKind? ToolKindFor( EffigyToolTarget target ) => target switch
	{
		EffigyToolTarget.Primitive => ToolKind.Primitive,
		EffigyToolTarget.Hole => ToolKind.Hole,
		_ => null,
	};

	/// <summary>Push all committed sketches from the feature tree into the viewport so they
	/// remain visible after leaving sketch mode, and push the subset a feature being edited is
	/// allowed to pick — only sketches standing before it in the history, since a feature cannot
	/// consume a sketch that has not run yet.</summary>
	private void UpdateDisplaySketches() => UpdatePickTargets( _dialog?.Feature );

	/// <summary>Turn the material-slot tint on and off. OFF by default, because the preview now
	/// renders the real vmat bound to each slot and a tint over the top of that is a lie about what
	/// the part is made of. Turn it on to ask the other question - which SLOT a face is on, which two
	/// slots sharing one material or an unbound slot cannot be read off the rendered colour.</summary>
	private void ToggleMaterialShading()
	{
		if ( _viewport is null )
			return;

		_viewport.ShadeMaterialSlots = !_viewport.ShadeMaterialSlots;
	}

	/// <summary>Whether the rules holding a sketch together are drawn on it. On by default — a
	/// constraint you cannot see is a constraint you fight, and until now there were none to see
	/// because there was no way to add one.</summary>
	private void ToggleConstraintMarks()
	{
		if ( _viewport is null )
			return;

		_viewport.ShowConstraintMarks = !_viewport.ShowConstraintMarks;
	}

	/// <summary>Rebuild both sketch lists against the feature a dialog is open on. Called by the
	/// dialog the moment it opens, because the pick list and the auto-arm decision are only
	/// correct relative to THAT feature.</summary>
	private void UpdatePickTargets( Feature editing )
	{
		if ( _viewport is null )
			return;

		var sketchFeatures = _studio.Features.OfType<SketchFeature>().ToList();

		_viewport.SetDisplaySketches( sketchFeatures.Select( f => f.Sketch ) );
		UpdateSketchVisibility( sketchFeatures, editing );

		var cutoff = editing is null ? int.MaxValue : _studio.Features.IndexOf( editing );

		if ( cutoff < 0 )
			cutoff = int.MaxValue;

		_viewport.SetPickableSketches( _studio.Features.Take( cutoff )
			.OfType<SketchFeature>()
			.Select( f => new EffigyViewport.PickableSketch( f.Id, f.Name ?? f.TypeName, f.Sketch ) ) );
	}

	/// <summary>
	/// Hide the sketches that have already been turned into geometry, keeping the eye in the
	/// feature tree authoritative wherever it has been clicked.
	///
	/// The one sketch that is always shown regardless is the one the open dialog is building
	/// from: you cannot pick a region of a sketch that is not on screen, and while a feature is
	/// being edited its input is the thing you are looking at.
	/// </summary>
	private void UpdateSketchVisibility( List<SketchFeature> sketchFeatures, Feature editing )
	{
		var editingId = editing is SketchConsumingFeature consumer
			? _studio.ResolveSketchFeatureId( consumer )
			: null;

		foreach ( var feature in sketchFeatures )
		{
			var visible = _featureTree?.IsVisible( $"sketch:{feature.Id}" ) ?? true;

			_viewport.SetSketchVisibility( feature.Sketch, visible || feature.Id == editingId );
		}
	}

	private void NewStudio() => ConfirmDiscard( () =>
	{
		RecordUndo();
		_studio = new PartStudio();
		_featureTree?.SetStudio( _studio );
		_partsPanel?.SetStudio( _studio );
		_materialsPanel?.SetStudio( _studio );
		_rigPanel?.SetStudio( _studio );
		_dialog?.Close();

		// The handle has to show the pivot the document carries, or opening a file would leave the
		// marker at zero while the export used the saved value. SetOrigin raises OriginMoved and so
		// dirties the document; both callers MarkClean() below, after this.
		SyncOriginFromStudio();
		RebuildStudio();

		_documentPath = null;
		MarkClean();
	} );

	private void DeleteSelectedFeature()
	{
		if ( _featureTree?.SelectedFeature is { } feature )
		{
			RecordUndo();
			_studio.Remove( feature );
			_dialog?.Close();
			RebuildStudio();
		}
	}

	private void MoveFeatureUp()
	{
		if ( _featureTree?.SelectedFeature is not { } feature )
			return;

		var idx = _studio.Features.IndexOf( feature );
		if ( idx > 0 )
		{
			RecordUndo();
			_studio.Move( idx, idx - 1 );
			RebuildStudio();
		}
	}

	private void MoveFeatureDown()
	{
		if ( _featureTree?.SelectedFeature is not { } feature )
			return;

		var idx = _studio.Features.IndexOf( feature );
		if ( idx < _studio.Features.Count - 1 )
		{
			RecordUndo();
			_studio.Move( idx, idx + 1 );
			RebuildStudio();
		}
	}

	/// <summary>
	/// Where the feature tree's context menu ends up. The panel raises intent; everything that
	/// needs the studio, the dialog or the undo stack happens here.
	/// </summary>
	private void OnFeatureCommand( Feature feature, EffigyFeatureCommand command )
	{
		if ( feature is null )
			return;

		var index = _studio.Features.IndexOf( feature );

		switch ( command )
		{
			case EffigyFeatureCommand.Edit:
				EditFeature( feature );
				break;

			case EffigyFeatureCommand.Sculpt:
				if ( feature is SculptFeature )
					EditFeature( feature );
				break;

			case EffigyFeatureCommand.Rename:
				_featureTree?.BeginRename( feature );
				break;

			case EffigyFeatureCommand.ToggleSuppress:
				RecordUndo();
				feature.Suppressed = !feature.Suppressed;
				_studio.MarkDirty( feature );
				RebuildStudio();
				break;

			case EffigyFeatureCommand.Delete:
				RecordUndo();

				if ( _dialog?.Feature == feature )
				{
					_dialog.Close();
					RestoreRollbackAfterEdit();
				}

				_studio.Remove( feature );
				RebuildStudio();
				break;

			case EffigyFeatureCommand.MoveUp when index > 0:
				RecordUndo();
				_studio.Move( index, index - 1 );
				RebuildStudio();
				break;

			case EffigyFeatureCommand.MoveDown when index >= 0 && index < _studio.Features.Count - 1:
				RecordUndo();
				_studio.Move( index, index + 1 );
				RebuildStudio();
				break;

			// An explicit move of the bar STICKS. Forgetting the pre-edit position is the point:
			// otherwise closing a dialog that happened to be open would put the bar back and undo
			// the move the user just made by hand.
			case EffigyFeatureCommand.RollbackTo when index >= 0:
				RecordUndo();
				_rollbackBeforeEdit = null;
				SetRollback( index );
				break;

			case EffigyFeatureCommand.RollForward:
				RecordUndo();
				_rollbackBeforeEdit = null;
				SetRollback( int.MaxValue );
				break;
		}
	}

	private void OnFeatureRenamed( Feature feature, string name )
	{
		if ( feature is null )
			return;

		RecordUndo();

		// Blank means "no name of your own", which is what a feature starts with - the tree falls
		// back to the type name. Storing "" instead would print an empty row.
		feature.Name = string.IsNullOrWhiteSpace( name ) ? null : name.Trim();

		_featureTree?.Rebuild();
		_partsPanel?.Refresh();

		if ( _dialog?.Feature == feature )
			_dialog.Open( feature, isNew: false );
	}

	/// <summary>Move the rollback bar and rebuild. RollbackIndex is the index of the first feature
	/// NOT evaluated, so int.MaxValue means "everything runs".</summary>
	private void SetRollback( int index )
	{
		_studio.RollbackIndex = index;
		RebuildStudio();
	}

	/// <summary>
	/// Onshape's edit: roll the model back to how it looked WHEN THIS FEATURE RAN, and open its
	/// parameters. Editing an extrude with six features stacked on top of it is otherwise done
	/// blind - you cannot see the thing you are changing.
	///
	/// The previous bar position is remembered and put back when the dialog closes, so an edit
	/// does not silently leave half the model switched off. An explicit "Roll back to before
	/// this" from the menu is the one that sticks.
	/// </summary>
	private void EditFeature( Feature feature )
	{
		var index = _studio.Features.IndexOf( feature );

		if ( index < 0 )
			return;

		_rollbackBeforeEdit ??= _studio.RollbackIndex;
		_studio.RollbackIndex = index + 1;

		RebuildStudio();

		_featureTree?.Select( feature );

		// Sculpt closes its dialog on entry. Opening it here would put the body picker back
		// up after EnterSculpt had just dismissed it. Select() may already have Opened (and
		// entered) via OnFeatureSelected; EnterSculpt is a no-op when that sculpt is active.
		if ( feature is SculptFeature sculpt )
		{
			EnterSculpt( sculpt );
			return;
		}

		// Paint is the same shape as sculpt: editing it means painting it, not parking on a dialog.
		if ( feature is PaintFeature paint )
		{
			EnterPaint( paint );
			return;
		}

		_dialog?.Open( feature, isNew: false );

		// A sketch's Edit is entering the sketch, not parking on a dialog that asks you to
		// confirm you meant it. Open() also requests this when the plane is already chosen;
		// EnterSketch is a no-op when that sketch is already active.
		if ( feature is SketchFeature sketch )
			EnterSketch( sketch );
	}

	/// <summary>Where the rollback bar was before an Edit temporarily moved it. Null when no edit
	/// has moved it.</summary>
	private int? _rollbackBeforeEdit;

	/// <summary>Put the bar back after an edit finishes, whichever way it finished.</summary>
	private void RestoreRollbackAfterEdit()
	{
		if ( _rollbackBeforeEdit is not { } previous )
			return;

		_rollbackBeforeEdit = null;
		_studio.RollbackIndex = previous;
	}

	private void ToggleSuppressFeature()
	{
		if ( _featureTree?.SelectedFeature is { } feature )
		{
			RecordUndo();
			feature.Suppressed = !feature.Suppressed;

			// Without this the rebuild restores everything above the first dirty feature from the
			// cache, so the feature you just suppressed is re-used exactly as it was and nothing
			// on screen changes.
			_studio.MarkDirty( feature );
			RebuildStudio();
		}
	}

	// --- export / compile (reusing EffigyTool's proven logic) -------------------------------

	[Shortcut( "editor.save", "CTRL+S", ShortcutType.Window )]
	private void Save()
	{
		// A studio that has never been saved has nowhere to go, so Save becomes Save As the first
		// time. Silently doing nothing here is the shape of the bug the rig tool had.
		if ( _documentPath is null )
		{
			SaveAs();
			return;
		}

		WriteDocument( _documentPath );
	}

	private void SaveAs()
	{
		var fd = new FileDialog( null )
		{
			Title = "Save Part Studio As...",
			DefaultSuffix = StudioDocument.Extension,
			Directory = Project.Current?.GetAssetsPath() ?? "",
		};

		fd.SelectFile( _documentPath ?? $"untitled{StudioDocument.Extension}" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( $"Effigy Part Studio (*{StudioDocument.Extension})" );

		if ( !fd.Execute() )
			return;

		WriteDocument( fd.SelectedFile );
	}

	private void WriteDocument( string path )
	{
		try
		{
			StudioDocument.WriteFile( _studio, path );
		}
		catch ( Exception e )
		{
			// Saving is the one operation where failing quietly is unforgivable: the whole point of
			// pressing it is to be able to close the window.
			Log.Error( $"[Effigy] could not save to {path}: {e.Message}" );
			return;
		}

		// THE DELTAS ARE NOT IN THE DOCUMENT. StudioDocument saves a feature's public fields, and a
		// sculpt's state is megabytes of per-vertex deltas that deliberately do not go into a text
		// format - see SculptFeature. Without this the .effigy file saves perfectly and the sculpt is
		// gone, which is the worst shape a save bug can have: it looks like it worked.
		try
		{
			var blobs = SculptSidecar.Save( _studio, path );

			if ( blobs > 0 )
				Log.Info( $"[Effigy] wrote {blobs} sculpt blob(s) beside {path}" );
		}
		catch ( Exception e )
		{
			// The document itself is already on disk, so this is not fatal - but it must be loud. A
			// sculpt that quietly did not save is the thing this whole side-car exists to avoid.
			Log.Error( $"[Effigy] saved {path} but could NOT write its sculpt data: {e.Message}" );
		}

		_documentPath = path;
		MarkClean();

		Log.Info( $"[Effigy] saved {path}" );
	}

	private void Open()
	{
		// The unsaved work belongs to the studio being replaced, so the question comes first.
		ConfirmDiscard( () =>
		{
			var fd = new FileDialog( null )
			{
				Title = "Open Part Studio",
				DefaultSuffix = StudioDocument.Extension,
				Directory = Project.Current?.GetAssetsPath() ?? "",
			};

			fd.SetFindFile();

			// No SetModeOpen call: SetModeSave is the only one of the pair with proven usage in this
			// repo, and an unproven method name is a COMPILE error that takes the whole editor
			// assembly down rather than failing at the one dialog. Not calling it leaves the dialog
			// in its default mode, which at worst is a cosmetic wrinkle on an open dialog.
			fd.SetNameFilter( $"Effigy Part Studio (*{StudioDocument.Extension})" );

			if ( fd.Execute() )
				LoadDocument( fd.SelectedFile );
		} );
	}

	private void LoadDocument( string path )
	{
		PartStudio loaded;

		try
		{
			loaded = StudioDocument.ReadFile( path );
		}
		catch ( Exception e )
		{
			// StudioDocument's errors name the line and what was wrong with it, so they are worth
			// passing through rather than replacing with "could not open".
			Log.Error( $"[Effigy] could not open {path}: {e.Message}" );
			return;
		}

		// BEFORE the rebuild, because that is when the deltas are consumed: SculptSidecar hands each
		// feature its bytes, and the feature turns them into a sculpt on the first rebuild, once the
		// cage it belongs to has been built by the features above it.
		try
		{
			SculptSidecar.Load( loaded, path );
		}
		catch ( Exception e )
		{
			Log.Error( $"[Effigy] opened {path} but could not read its sculpt data: {e.Message}" );
		}

		_studio = loaded;
		_featureTree?.SetStudio( _studio );
		_partsPanel?.SetStudio( _studio );
		_materialsPanel?.SetStudio( _studio );
		_rigPanel?.SetStudio( _studio );
		_dialog?.Close();

		// The handle has to show the pivot the document carries, or opening a file would leave the
		// marker at zero while the export used the saved value. SetOrigin raises OriginMoved and so
		// dirties the document; both callers MarkClean() below, after this.
		SyncOriginFromStudio();

		// History belongs to the document that was open. Carrying it across a load would let Ctrl+Z
		// paste the previous model's features into this one.
		_undoStack.Clear();
		_redoStack.Clear();

		RebuildStudio();

		_documentPath = path;
		MarkClean();

		// Deliberately AFTER the rebuild: a file that opens with a broken feature is exactly the
		// file you opened it to fix, and it should be on screen rather than refused.
		Log.Info( $"[Effigy] opened {path}" );
	}

	/// <summary>
	/// Ask before throwing away unsaved work, then run <paramref name="proceed"/>.
	///
	/// Cancel does nothing at all, which is the point of it: the studio is left exactly as it was.
	/// Modelled on the rig tool's, down to the button order — the same question should not be asked
	/// two different ways in one editor.
	/// </summary>
	private void ConfirmDiscard( Action proceed )
	{
		if ( !_dirty )
		{
			proceed();
			return;
		}

		var name = _documentPath is null ? "untitled" : Path.GetFileName( _documentPath );

		var confirm = new PopupWindow( "Unsaved Changes",
			$"\"{name}\" has unsaved changes. Would you like to save now?", "Cancel",
			new Dictionary<string, Action>
			{
				{ "Don\'t Save", proceed },
				{ "Save", () => { Save(); proceed(); } }
			} );

		confirm.Show();
	}

	/// <summary>
	/// Closing with unsaved work asks first.
	///
	/// Returning false CANCELS the close, and the window is closed again from inside the popup once
	/// the question is answered — Don't Save clears the flag first so the second Close sails past
	/// this check rather than asking again forever.
	/// </summary>
	protected override bool OnClose()
	{
		if ( !_dirty )
			return true;

		var name = _documentPath is null ? "untitled" : Path.GetFileName( _documentPath );

		var confirm = new PopupWindow( "Unsaved Changes",
			$"\"{name}\" has unsaved changes. Would you like to save now?", "Cancel",
			new Dictionary<string, Action>
			{
				{ "Don\'t Save", () => { _dirty = false; Close(); } },
				{ "Save", () => { Save(); Close(); } }
			} );

		confirm.Show();
		return false;
	}

	/// <summary>
	/// The PhysicsShapeList the export should carry, or an empty string for none.
	///
	/// THE SHAPES USED TO GO NOWHERE. They were computed, correct and tested, and the .vmdl carried
	/// no collision at all, because writing one meant guessing at ModelDoc's KV3 and a guessed node
	/// fails as a model that will not load rather than as a model without physics. That is settled
	/// now: every key VmdlPhysics writes was put into a probe .vmdl, compiled, and read back off the
	/// compiled model's own physics bounds. See that file for what each probe answered.
	///
	/// A RIGGED PART FALLS BACK TO THE RENDER MESH, and that is the one judgement call here. Every
	/// shape CollisionBuilder produces is in MODEL space, with no bone to hang off - a shape list on
	/// a skinned model wants parent_bone set per shape, and the mapping from a body to the bone that
	/// drives it is exactly the thing the rig panel exists to let somebody decide. Writing them all
	/// against the root would put a static collision hull on an animating character, which is the
	/// wrong kind of wrong: it looks right until something moves. PhysicsMeshFromRender is honest,
	/// costs nothing, and is what every hand-authored model in this project already uses.
	/// </summary>
	private string BuildPhysics( bool rigged )
	{
		if ( _studio is null )
			return "";

		if ( rigged )
			return VmdlPhysics.MeshFromRender();

		try
		{
			var report = CollisionBuilder.Build( _studio );

			ApplyPivot( report.Shapes );

			var node = VmdlPhysics.ShapeList( report.Shapes );

			if ( node.Length == 0 )
				return VmdlPhysics.MeshFromRender();

			Log.Info( $"[Effigy] collision into the .vmdl: {report}" );
			return node;
		}
		catch ( Exception e )
		{
			// A collision build failing must not take the export with it. The model without physics
			// is still a model; the exception on the way to one is not worth losing it over.
			Log.Warning( $"[Effigy] collision could not be built ({e.Message}) - falling back to the render mesh" );
			return VmdlPhysics.MeshFromRender();
		}
	}

	/// <summary>
	/// What this part's physics representation is, listed where a person can read it.
	///
	/// Still worth having now that the shapes reach the .vmdl: this is where you find out WHY a part
	/// came out as one hull per body instead of as the boxes it was drawn from - CollisionReport
	/// names the feature that spoiled the decomposition, and nothing in the compiled model does.
	/// </summary>
	private void ReportCollision()
	{
		if ( _studio is null )
			return;

		var report = CollisionBuilder.Build( _studio );

		Log.Info( $"[Effigy] collision: {report}" );

		foreach ( var shape in report.Shapes )
			Log.Info( $"[Effigy]   {shape} at ({shape.Position.x:0.##}, {shape.Position.y:0.##}, {shape.Position.z:0.##})" );

		SetPrompt( report.FromHistory
			? $"Collision: {report.Shapes.Count} shape(s) read straight from the history — see the console."
			: $"Collision: {report.Shapes.Count} hull(s) — {report.Reason}. See the console." );
	}

	// --- the pivot -----------------------------------------------------------------------------

	/// <summary>
	/// The offset that moves the model's origin to (0,0,0), which is what every writer applies on
	/// the way out. See PartStudio.Origin for what the pivot IS; this is only the arithmetic.
	///
	/// EffigyViewport.ToWorldDir is the identity, so the viewport's Vector3 and the kernel's Vec3
	/// are the same three numbers and no axis mapping belongs here. If that ever stops being true,
	/// this is the conversion that has to learn about it.
	/// </summary>
	private Vec3 PivotOffset => _studio is null ? default : -_studio.Origin;

	/// <summary>Whether the pivot has been moved off zero at all. The untouched case — which is
	/// most documents — then does no work and cannot walk vertices through a float add that was
	/// only ever going to add nothing.</summary>
	private bool HasPivot => PivotOffset.Length > 1e-6f;

	/// <summary>
	/// Shift a mesh onto the pivot.
	///
	/// Safe to mutate in place: ToMesh and ToMeshWithBodies merge the bodies into a FRESH PolyMesh
	/// every call, so this never touches geometry the studio is still holding. Handing it a body's
	/// own mesh would move the model itself, one export at a time.
	/// </summary>
	private void ApplyPivot( PolyMesh mesh )
	{
		if ( mesh is not null && HasPivot )
			MeshTransform.Apply( mesh, Xform.Translate( PivotOffset ) );
	}

	/// <summary>
	/// The skeleton shifted onto the pivot, as a COPY — the rig panel is still holding the original
	/// and exporting a model must not move the user's bones.
	///
	/// ONLY THE ROOTS MOVE. Every other bone's Local is relative to its parent, so shifting a root
	/// carries its whole chain; shifting the children too would move them once per level of depth.
	/// </summary>
	private Skeleton PivotedSkeleton( Skeleton skeleton )
	{
		if ( skeleton is null || !HasPivot )
			return skeleton;

		var copy = skeleton.Clone();
		var shift = Xform.Translate( PivotOffset );

		foreach ( var bone in copy.Bones )
		{
			if ( bone.Parent < 0 )
				bone.Local = shift * bone.Local;
		}

		return copy;
	}

	/// <summary>Shift built collision onto the pivot, so the hulls stay where the mesh went. Without
	/// this the render mesh moves and the physics stays behind, which reads in game as a model you
	/// walk through and a wall where nothing is.</summary>
	private void ApplyPivot( List<CollisionShape> shapes )
	{
		if ( shapes is null || !HasPivot )
			return;

		var offset = PivotOffset;

		foreach ( var shape in shapes )
		{
			shape.Position += offset;

			if ( shape.Points is null )
				continue;

			for ( var i = 0; i < shape.Points.Count; i++ )
				shape.Points[i] += offset;
		}
	}

	/// <summary>
	/// The origin handle was dragged, or set from a number field.
	///
	/// This event had no subscriber at all until the origin became the pivot, which is why the
	/// handle used to move a marker and nothing else. It does NOT rebuild: the kernel builds in its
	/// own coordinates and the viewport draws in the same ones, so the pivot changes only what the
	/// writers subtract. It does dirty the DOCUMENT, because a pivot that is not saved is not a
	/// pivot.
	/// </summary>
	/// <summary>Push the document's pivot onto the origin handle. The other direction of
	/// <see cref="OnOriginMoved"/>, for load and for New.</summary>
	private void SyncOriginFromStudio()
	{
		if ( _studio is null || _viewport is null )
			return;

		var o = _studio.Origin;

		_viewport.SetOrigin( new Vector3( o.x, o.y, o.z ) );
	}

	private void OnOriginMoved()
	{
		if ( _studio is null || _viewport is null )
			return;

		var o = _viewport.OriginPosition;

		_studio.Origin = new Vec3( o.x, o.y, o.z );
		// Same two lines every other unsaved edit uses; there is no shared helper to call.
		if ( !_dirty )
		{
			_dirty = true;
			UpdateTitle();
		}
	}

	/// <summary>
	/// Say what is about to happen before the blocking part of it.
	///
	/// Export opens with a full synchronous rebuild, and a rebuild of a dense subdivide or a sculpt
	/// can take long enough for the tools stall monitor to fire. When that happened there was
	/// nothing in the log between "opened" and the stall, so the hang was indistinguishable from a
	/// hang anywhere else in the editor — and if the process goes away before the rebuild returns,
	/// the completion line that would have named the culprit is never written. One line before the
	/// call is the difference between guessing and knowing.
	/// </summary>
	private RebuildReport RebuildForExport( string what )
	{
		Log.Info( $"[Effigy] rebuilding before {what} — {_studio.Features.Count} features" );
		return _studio.Rebuild();
	}

	/// <summary>
	/// The clips queued for the next compile.
	///
	/// Held on the window rather than in the document, because they are an EXPORT setting and the
	/// .effigy document is the parametric model. That is a real trade and worth naming: the list
	/// does not survive closing the tool, and a clip added yesterday has to be added again. Putting
	/// it in the document means a saved model referencing a .riganim that references a model, which
	/// is a loop the loader would have to break; that is a bigger decision than this one and is not
	/// made here.
	/// </summary>
	private readonly List<EffigyAnimExport.ClipSource> _animClips = new();

	private EffigyAnimClipsWindow _animClipsWindow;

	private void OpenAnimClips()
	{
		// One dialog. Reopening brings the existing one forward rather than stacking a second list
		// over the first, both editing the same clips.
		if ( _animClipsWindow.IsValid() )
		{
			_animClipsWindow.Focus();
			return;
		}

		// No dirty mark. The clip list is not saved into the .effigy (see _animClips), so putting a
		// * in the title would promise a save that does not carry it.
		_animClipsWindow = new EffigyAnimClipsWindow( this, _animClips );
		_animClipsWindow.Show();
	}

	/// <summary>
	/// The stem every exported file takes: the document's own name.
	///
	/// EVERYTHING USED TO BE CALLED "export". models/effigy/export.vmdl, export.obj, export.dmx,
	/// export.smd - one set of names for every part studio in the project, so compiling the
	/// spatula overwrote the grill, and whatever had already been placed in a scene changed shape
	/// under you without a word. The document has a name, the user chose it, and it is the obvious
	/// thing to name its model after.
	///
	/// AN UNSAVED DOCUMENT HAS NO NAME TO TAKE, so this asks for one through the same save dialog
	/// Save As uses. Inventing "untitled" instead would only move the collision to a different
	/// spelling and hide it again. Cancelling returns null and the caller exports nothing, which
	/// is what cancelling a dialog should do.
	/// </summary>
	private string ExportBaseName()
	{
		if ( _documentPath is not null )
			return SanitiseAssetName( Path.GetFileNameWithoutExtension( _documentPath ) );

		var fd = new FileDialog( null )
		{
			Title = "Name the exported model",
			DefaultSuffix = ".vmdl",
			Directory = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" ),
		};

		fd.SelectFile( "untitled.vmdl" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( "Model (*.vmdl)" );

		if ( !fd.Execute() )
			return null;

		return SanitiseAssetName( Path.GetFileNameWithoutExtension( fd.SelectedFile ) );
	}

	/// <summary>
	/// Fold a document name into something safe to sit in an asset path: lowercase, and nothing
	/// outside a-z, 0-9, underscore and hyphen. A studio saved as "Flat Top v2" therefore compiles
	/// to flat_top_v2.vmdl rather than to a path the asset system has to guess at.
	/// </summary>
	private static string SanitiseAssetName( string raw )
	{
		if ( string.IsNullOrWhiteSpace( raw ) )
			return "export";

		var sb = new System.Text.StringBuilder( raw.Length );

		foreach ( var c in raw.ToLowerInvariant() )
			sb.Append( (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'
				? c : '_' );

		var name = sb.ToString().Trim( '_' );

		// Everything stripped - a name made entirely of punctuation. "export" is a poor name but a
		// working one, and better than an empty filename.
		return name.Length == 0 ? "export" : name;
	}

	private void ExportObj()
	{
		var report = RebuildForExport( "OBJ export" );
		if ( report.HasErrors || _studio.Bodies.Count == 0 )
		{
			Log.Warning( "[Effigy] cannot export — studio has errors or no bodies" );
			return;
		}

		var name = ExportBaseName();
		if ( name is null )
			return;

		var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		var objPath = Path.Combine( folder, $"{name}.obj" );

		// Slot names go through so the file names its materials the way the user did, rather than
		// material_0..63. NameForSlot falls back to the numbers for anything unnamed. Vertex colours
		// ride on the mesh and are written by the exporter alongside the positions.
		var mesh = _studio.ToMesh();
		ApplyPivot( mesh );

		ObjWriter.WriteFile( mesh, objPath, name,
			materialName: _studio.NameForSlot );
		Log.Info( $"[Effigy] exported {objPath}" );
	}

	private void CompileVmdl()
	{
		var report = RebuildForExport( "vmdl compile" );
		if ( report.HasErrors || _studio.Bodies.Count == 0 )
		{
			Log.Warning( "[Effigy] cannot compile — studio has errors or no bodies" );
			return;
		}

		var name = ExportBaseName();
		if ( name is null )
			return;

		var folder = EffigyAssetFolder.ResolveAssetFolder( "models/effigy" );
		Directory.CreateDirectory( folder );

		// RIGGED PATH: bones exist in the rig panel, so export DMX (which carries the skeleton
		// and per-vertex weights) instead of a weightless OBJ.
		if ( _rigPanel is { HasBones: true } rig )
		{
			var (mesh, ranges) = _studio.ToMeshWithBodies();
			var skeleton = rig.Skeleton;

			// BindBodies assigns each body's vertices to the bone it was assigned to in the rig
			// panel. Unassigned bodies fall back to nearest-bone rigid weighting. SmoothWeights
			// then diffuses across mesh adjacency so joints bend rather than crease.
			var weights = SkinBinder.BindBodies( mesh, ranges, rig.BodyBoneMap, skeleton );
			weights = SkinBinder.SmoothWeights( mesh, weights );
			mesh.Skin = weights;

			// AFTER binding, and both together. The weights come from distances between vertices and
			// bones, so shifting either side before the bind would rig the model to where the bones
			// used to be. Shifting both afterwards moves the bind pose and leaves the weights - which
			// are indices and scalars, not positions - saying exactly what they said.
			ApplyPivot( mesh );
			skeleton = PivotedSkeleton( skeleton );

			// DMX, not SMD. ModelDoc's loader takes FBX, DMX, OBJ and VOX and nothing else (see
			// DmxWriter for the exact string it prints), so DMX is the only supported format that
			// carries a skeleton and per-vertex weights. The .smd is still written alongside it
			// because every DCC reads one and it costs nothing to keep.
			var smdPath = Path.Combine( folder, $"{name}.smd" );
			SmdWriter.WriteFile( mesh, smdPath, skeleton, materialName: _studio.NameForSlot );

			var dmxPath = Path.Combine( folder, $"{name}.dmx" );
			DmxWriter.WriteFile( mesh, dmxPath, skeleton, materialName: _studio.NameForSlot,
				modelName: name );

			Log.Info( $"[Effigy] wrote {dmxPath} - {skeleton.Count} bones, {mesh.VertexCount} vertices" );

			// The clips, written beside the mesh and BEFORE the folder is registered, so the asset
			// system sees the .dmx files at the same moment it sees the .vmdl that names them.
			//
			// The skeleton passed here is the PIVOTED one, the same one the mesh was written
			// against. A clip sampled against the unpivoted rig would pose the model correctly
			// relative to bones that had since moved, which is a whole-model offset that only
			// appears once the clip plays.
			var clips = EffigyAnimExport.WriteClips( _animClips, folder, "models/effigy", skeleton );

			var vmdlPath = Path.Combine( folder, $"{name}.vmdl" );
			File.WriteAllText( vmdlPath, BuildSkinnedVmdl( $"models/effigy/{name}.dmx", skeleton,
				BuildPhysics( rigged: true ), VmdlMaterials.GroupList( _studio, mesh ),
				VmdlAnimation.AnimationList( clips.ToArray() ) ) );

			var result = EffigyAssetFolder.Register( folder );
			Log.Info( $"[Effigy] wrote {vmdlPath} - {result.Registered} registered" );

			var asset = AssetSystem.FindByPath( $"models/effigy/{name}.vmdl" );

			if ( asset is null )
			{
				Log.Warning( $"[Effigy] {name}.vmdl was written but the asset system couldn't find it" );
				return;
			}

			asset.Compile( true );

			if ( asset.IsCompileFailed )
			{
				Log.Warning( $"[Effigy] {name}.vmdl compile FAILED - the compiler's own output above "
					+ "says why. The .dmx and .smd are both on disk either way." );
				return;
			}

			Log.Info( $"[Effigy] {name}.vmdl compiled - {skeleton.Count} bone(s), loading into viewport" );
			_viewport?.SetModel( Model.Load( $"models/effigy/{name}.vmdl" ) );
			return;
		}

		// STATIC PATH: no bones — export a weightless OBJ.
		var staticObjPath = Path.Combine( folder, $"{name}.obj" );
		var staticMesh = _studio.ToMesh();
		ApplyPivot( staticMesh );

		ObjWriter.WriteFile( staticMesh, staticObjPath, name,
			materialName: _studio.NameForSlot );

		var staticVmdlPath = Path.Combine( folder, $"{name}.vmdl" );
		File.WriteAllText( staticVmdlPath, BuildVmdl( $"models/effigy/{name}.obj",
			BuildPhysics( rigged: false ), VmdlMaterials.GroupList( _studio, staticMesh ) ) );

		var staticResult = EffigyAssetFolder.Register( folder );
		Log.Info( $"[Effigy] wrote {staticObjPath} and {staticVmdlPath} — {staticResult.Registered} registered" );

		var staticAsset = AssetSystem.FindByPath( $"models/effigy/{name}.vmdl" );
		if ( staticAsset is null )
		{
			Log.Warning( $"[Effigy] {name}.vmdl was written but asset system couldn't find it" );
			return;
		}

		staticAsset.Compile( true );
		Log.Info( staticAsset.IsCompileFailed
			? $"[Effigy] {name}.vmdl compile FAILED"
			: $"[Effigy] {name}.vmdl compiled — loading into viewport" );

		if ( !staticAsset.IsCompileFailed )
		{
			var model = Model.Load( $"models/effigy/{name}.vmdl" );
			_viewport?.SetModel( model );
		}
	}

	/// <summary>
	/// Same one-node RenderMeshFile shape as EffigyTool.BuildVmdl, plus whatever PhysicsShapeList
	/// VmdlPhysics built and the MaterialGroupList VmdlMaterials built.
	///
	/// MATERIALS USED TO GO NOWHERE, the same way collision did. The mesh writers named each slot
	/// and the .vmdl had no MaterialGroupList, so ModelDoc filled one in with
	/// use_global_default = true and materials/default.vmat — a part that rendered in the viewport
	/// with the materials that were dropped on it compiled as a blank grey prop. The node is always
	/// present: an omitted list is what gets replaced, an empty one with the global default off
	/// leaves the mesh names in place.
	///
	/// THE -90 PITCH AND -90 YAW ARE NOT DECORATION. ModelDoc's OBJ importer does not land the mesh
	/// in the coordinates the file gives it. It reads the file as Y-up (the OBJ convention) and then
	/// turns it another quarter turn, so the whole thing arrives cyclically permuted:
	///
	///     engine.x = obj.z    engine.y = obj.x    engine.z = obj.y
	///
	/// The kernel is Z-up - its sketch planes are named "Top (XY)", "Front (XZ)", "Right (YZ)" - so
	/// this is TWO errors stacked, and only one of them used to be corrected here. A bare -90 yaw
	/// undoes the extra turn and leaves the Y-up reading in place, landing the mesh at
	/// (obj.x, -obj.z, obj.y): a part drawn lying flat comes out standing on its side. [-90, -90, 0]
	/// is the full inverse of the permutation above and puts the mesh back in the coordinates the
	/// file was written in.
	///
	/// MEASURED. A two-box part whose OBJ bounds are 155 x 159 x 84 compiled to 84 x 155 x 159 at
	/// rotation zero - the permutation, read straight off the numbers - and to 155 x 159 x 84 at
	/// [-90, -90, 0], with the bar still pointing along +x and the raised lip still on top, so this
	/// is the identity and not some other transform that happens to share its bounds.
	///
	/// The old measurement was not wrong, it was too narrow: it unioned a bar along x = 0..10 with a
	/// matching PhysicsShapeBox and checked only that ONE axis came back 10 wide. A -90 yaw does
	/// hold x still, which is why it passed while y and z stayed swapped.
	///
	/// This matters most for collision. The shapes BuildPhysics emits come from CollisionBuilder
	/// over the studio, i.e. in kernel coordinates, and import_rotation does not touch them - so the
	/// mesh has to arrive in kernel coordinates too, or the collision sits at an angle to the model
	/// it belongs to.
	///
	/// The DMX path does not get this and must not: it is only the OBJ importer that turns the mesh,
	/// and the rigged export uses PhysicsMeshFromRender anyway, so its physics follows its mesh
	/// wherever the importer puts it.
	/// </summary>
	static string BuildVmdl( string meshFilename, string physics = "", string materials = "" ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n" +
		"{\n" +
		"\trootNode = \n" +
		"\t{\n" +
		"\t\t_class = \"RootNode\"\n" +
		"\t\tchildren = \n" +
		"\t\t[\n" +
		materials +
		"\t\t\t{\n" +
		"\t\t\t\t_class = \"RenderMeshList\"\n" +
		"\t\t\t\tchildren = \n" +
		"\t\t\t\t[\n" +
		"\t\t\t\t\t{\n" +
		"\t\t\t\t\t\t_class = \"RenderMeshFile\"\n" +
		"\t\t\t\t\t\tname = \"Body_LOD0\"\n" +
		"\t\t\t\t\t\tchildren = \n" +
		"\t\t\t\t\t\t[\n" +
		"\t\t\t\t\t\t]\n" +
		$"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n" +
		"\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_rotation = [ -90.0, -90.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_scale = 1.0\n" +
		"\t\t\t\t\t\talign_origin_x_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_y_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_z_type = \"None\"\n" +
		"\t\t\t\t\t\tparent_bone = \"\"\n" +
		"\t\t\t\t\t},\n" +
		"\t\t\t\t]\n" +
		"\t\t\t},\n" +
		physics +
		"\t\t]\n" +
		"\t\tmodel_archetype = \"\"\n" +
		"\t\tprimary_associated_entity = \"\"\n" +
		"\t\tanim_graph_name = \"\"\n" +
		"\t\tbase_model_name = \"\"\n" +
		"\t}\n" +
		"}\n";

	/// <summary>
	/// A skinned .vmdl: the RenderMeshFile points at an SMD (which carries the bone hierarchy,
	/// bind pose, and per-vertex weights). ModelDoc imports the skeleton from the SMD and bakes
	/// everything into the compiled model.
	/// </summary>
	/// <summary>
	/// <paramref name="animations"/> is the AnimationList node. Empty means the bind pose alone,
	/// which is what this always wrote — `VmdlAnimation.AnimationList()` with no clips is
	/// byte-identical to `BindPoseList()`, and a test holds that, so the no-clips path is
	/// unchanged rather than merely equivalent.
	/// </summary>
	static string BuildSkinnedVmdl( string meshFilename, Skeleton skeleton, string physics = "",
		string materials = "", string animations = null ) =>
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n" +
		"{\n" +
		"\trootNode = \n" +
		"\t{\n" +
		"\t\t_class = \"RootNode\"\n" +
		"\t\tchildren = \n" +
		"\t\t[\n" +
		materials +
		"\t\t\t{\n" +
		"\t\t\t\t_class = \"RenderMeshList\"\n" +
		"\t\t\t\tchildren = \n" +
		"\t\t\t\t[\n" +
		"\t\t\t\t\t{\n" +
		"\t\t\t\t\t\t_class = \"RenderMeshFile\"\n" +
		"\t\t\t\t\t\tname = \"Body_LOD0\"\n" +
		"\t\t\t\t\t\tchildren = \n" +
		"\t\t\t\t\t\t[\n" +
		"\t\t\t\t\t\t]\n" +
		$"\t\t\t\t\t\tfilename = \"{meshFilename}\"\n" +
		"\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n" +
		"\t\t\t\t\t\timport_scale = 1.0\n" +
		"\t\t\t\t\t\talign_origin_x_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_y_type = \"None\"\n" +
		"\t\t\t\t\t\talign_origin_z_type = \"None\"\n" +
		"\t\t\t\t\t\tparent_bone = \"\"\n" +
		"\t\t\t\t\t},\n" +
		"\t\t\t\t]\n" +
		"\t\t\t},\n" +
		VmdlAnimation.BoneMarkupList( skeleton ) +
		// THE BIND POSE, which a non-static model is documented as needing or morph targets and IK
		// data break quietly. It was absent until the node's real shape could be read off a shipping
		// file rather than guessed - see VmdlAnimation. Clips join it in the same node rather than
		// replacing it.
		(string.IsNullOrEmpty( animations ) ? VmdlAnimation.BindPoseList() : animations) +
		physics +
		"\t\t]\n" +
		"\t\tmodel_archetype = \"\"\n" +
		"\t\tprimary_associated_entity = \"\"\n" +
		"\t\tanim_graph_name = \"\"\n" +
		"\t\tbase_model_name = \"\"\n" +
		"\t}\n" +
		"}\n";

	// --- undo / redo -------------------------------------------------------------------------

	/// <summary>
	/// A point in the studio's history: which features exist, in what order, with what values,
	/// and where the rollback bar was.
	///
	/// The values are the part that was missing. The previous version snapshotted
	/// `_studio.Features.Select( f => f ).ToList()` - a shallow copy of the LIST, holding the same
	/// Feature objects. Parameters are the storage in this kernel (see Feature.cs: "The parameter
	/// object IS the storage"), so undo restored membership and order while silently keeping every
	/// number the user had changed since. Ctrl+Z after a parameter edit did nothing at all.
	///
	/// Values are keyed by parameter object rather than by index, because PrimitiveFeature returns
	/// a different Parameters list per shape - indices are not stable across a shape change, and
	/// the parameter objects are (they are readonly fields on the feature).
	/// </summary>
	private sealed class StudioSnapshot
	{
		public List<Feature> Features;
		public Dictionary<IParam, object> Values;

		/// <summary>
		/// A copy of every sketch's geometry, which is NOT a parameter and so was invisible to
		/// undo entirely.
		///
		/// This is what made Ctrl+Z during sketching so strange: the curves you had drawn were not
		/// in the snapshot, so undo could neither remove nor restore them. It went back to the
		/// last thing that WAS recorded - usually the moment the Sketch feature was added - took
		/// the feature out of the tree, and left the lines it owned still drawn on screen.
		/// </summary>
		public Dictionary<SketchFeature, Sketch> Sketches;

		/// <summary>
		/// The faces each material assignment holds, which are not parameters either and so were
		/// invisible to undo for the same reason sketch geometry was.
		///
		/// This mattered little while the only way to pick faces was a dialog you could cancel. It
		/// matters now that right-clicking a face assigns one: without it, Ctrl+Z after a right-click
		/// took away a feature it had just added and left a face added to an existing one exactly
		/// where it was.
		/// </summary>
		public Dictionary<FaceMaterialFeature, List<FaceRef>> FaceSets;

		/// <summary>
		/// Every paint feature's stroke list, for the same reason Sketches and FaceSets are captured:
		/// strokes are a public field, not a parameter, so the parameter sweep would miss them and
		/// Ctrl+Z after a brush stroke would do nothing. The list is copied by reference — strokes are
		/// immutable once painted — so the snapshot shares the stroke objects and only the list is new.
		/// </summary>
		public Dictionary<PaintFeature, List<PaintStroke>> PaintStrokes;

		/// <summary>Slot names, renamed from the same menu.</summary>
		public Dictionary<int, string> MaterialNames;

		/// <summary>Parts-list names, keyed by body id. Not a feature field, so they have to be
		/// captured the same way material names are or Ctrl+Z after a rename would keep the new
		/// name on the same Feature objects.</summary>
		public Dictionary<string, string> BodyNames;

		public HashSet<string> HiddenBodyIds;

		/// <summary>Feature.Name at this step. The Feature objects themselves are shared across
		/// snapshots, so a rename mutated in place would survive undo without this.</summary>
		public Dictionary<Feature, string> FeatureNames;

		public int RollbackIndex;

		/// <summary>A full clone (Skeleton.Clone) rather than a reference — the rig panel mutates
		/// its own Skeleton in place, so holding the same instance would make every snapshot equal
		/// the current state by the time anyone looked at it again.</summary>
		public Skeleton RigSkeleton;

		public Dictionary<string, string> BodyBoneMap;
	}

	private readonly List<StudioSnapshot> _undoStack = new();
	private readonly List<StudioSnapshot> _redoStack = new();

	private StudioSnapshot Capture()
	{
		var values = new Dictionary<IParam, object>();

		foreach ( var feature in _studio.Features )
		{
			foreach ( var param in feature.Parameters )
			{
				if ( ParamValue( param ) is { } value )
					values[param] = value;
			}
		}

		var sketches = new Dictionary<SketchFeature, Sketch>();

		foreach ( var feature in _studio.Features.OfType<SketchFeature>() )
			sketches[feature] = feature.Sketch.Clone();

		var faceSets = new Dictionary<FaceMaterialFeature, List<FaceRef>>();

		foreach ( var feature in _studio.Features.OfType<FaceMaterialFeature>() )
			faceSets[feature] = new List<FaceRef>( feature.Faces );

		var paintStrokes = new Dictionary<PaintFeature, List<PaintStroke>>();

		foreach ( var feature in _studio.Features.OfType<PaintFeature>() )
			paintStrokes[feature] = feature.Strokes is null ? null : new List<PaintStroke>( feature.Strokes );

		return new StudioSnapshot
		{
			Features = _studio.Features.ToList(),
			Values = values,
			Sketches = sketches,
			FaceSets = faceSets,
			PaintStrokes = paintStrokes,
			MaterialNames = new Dictionary<int, string>( _studio.MaterialNames ),
			BodyNames = new Dictionary<string, string>( _studio.BodyNames ),
			HiddenBodyIds = new HashSet<string>( _studio.HiddenBodyIds ),
			FeatureNames = _studio.Features.ToDictionary( f => f, f => f.Name ),
			RollbackIndex = _studio.RollbackIndex,
			RigSkeleton = _rigPanel?.Skeleton.Clone() ?? new Skeleton(),
			BodyBoneMap = _rigPanel is null
				? new Dictionary<string, string>()
				: new Dictionary<string, string>( _rigPanel.BodyBoneMap ),
		};
	}

	private static object ParamValue( IParam param ) => param switch
	{
		FloatParam f => f.Value,
		IntParam i => i.Value,
		BoolParam b => b.Value,
		Vec3Param v => v.Value,
		ChoiceParam c => c.Index,
		_ => null,
	};

	private void Restore( StudioSnapshot snapshot )
	{
		_studio.Features = snapshot.Features.ToList();
		_studio.RollbackIndex = snapshot.RollbackIndex;

		foreach ( var (param, value) in snapshot.Values )
		{
			switch ( param )
			{
				case FloatParam f when value is float v: f.Value = v; break;
				case IntParam i when value is int v: i.Value = v; break;
				case BoolParam b when value is bool v: b.Value = v; break;
				case Vec3Param p when value is Vec3 v: p.Value = v; break;
				case ChoiceParam c when value is int v: c.Index = v; break;
			}
		}

		// Sketch geometry is put back INTO THE EXISTING Sketch objects rather than swapped for the
		// clones. The viewport holds a direct reference to whichever sketch is open, so replacing
		// the object would leave it drawing an orphan - which is the other half of the bug this
		// fixes.
		foreach ( var (feature, sketch) in snapshot.Sketches )
		{
			feature.Sketch.Points = new List<Vec2>( sketch.Points );
			feature.Sketch.Curves = sketch.Curves.Select( c => c.Clone() ).ToList();
			feature.Sketch.Constraints = sketch.Constraints
				.Select( c => new SketchConstraint( c.Kind, c.CurveId ) ).ToList();
		}

		// Put back INTO the existing lists, for the same reason sketch geometry is: the dialog's
		// selection box holds a direct reference to the feature it is editing.
		foreach ( var (feature, faces) in snapshot.FaceSets )
		{
			feature.Faces.Clear();
			feature.Faces.AddRange( faces );
		}

		foreach ( var (feature, strokes) in snapshot.PaintStrokes )
			feature.ReplaceStrokes( strokes );

		_studio.MaterialNames.Clear();

		foreach ( var (slot, name) in snapshot.MaterialNames )
			_studio.MaterialNames[slot] = name;

		_studio.BodyNames.Clear();

		foreach ( var (id, name) in snapshot.BodyNames )
			_studio.BodyNames[id] = name;

		_studio.HiddenBodyIds.Clear();

		foreach ( var id in snapshot.HiddenBodyIds )
			_studio.HiddenBodyIds.Add( id );

		foreach ( var (feature, name) in snapshot.FeatureNames )
			feature.Name = name;

		_rigPanel?.RestoreRig( snapshot.RigSkeleton, snapshot.BodyBoneMap );

		_studio.MarkAllDirty();

		// The dialog may be open on a feature the restore just removed, and its snapshot of
		// "before" is now meaningless either way.
		_dialog?.Close();

		// If the sketch being drawn on no longer exists, sketch mode has to end with it. Leaving
		// it open is what left curves on screen belonging to a feature that had just been undone
		// out of the tree.
		if ( _viewport?.IsSketching == true && ActiveSketchFeature() is null )
			FinishSketch();

		if ( _viewport?.IsSculpting == true
			&& (_sculptFeature is null || !_studio.Features.Contains( _sculptFeature )) )
			FinishSculpt();

		// Paint's undo IS the document's — there is no session-internal stack. The restore rewrote the
		// feature's stroke list, but the live session holds its own colour array, so it is either ended
		// (the feature is gone) or rebuilt from the restored strokes (the feature is still there).
		if ( _viewport?.IsPainting == true )
		{
			if ( _paintFeature is null || !_studio.Features.Contains( _paintFeature ) )
				FinishPaint();
			else
				_viewport.PaintSession.Reload( _paintFeature.Strokes );
		}

		RebuildStudio();
	}

	/// <summary>
	/// Mark an undo point.
	///
	/// Granularity is one dialog session, not one keystroke: this is called when a feature is
	/// added, when its dialog is opened to edit it, and on the structural commands. Recording per
	/// parameter tick would put a hundred steps on the stack for one slider drag.
	///
	/// SKETCHING IS THE EXCEPTION, and deliberately so: there each committed entity is its own
	/// step, because "undo the line I just drew" is what the key means while a sketch is open, and
	/// a dialog session there could be fifty lines long.
	/// </summary>
	private void RecordUndo()
	{
		var snapshot = Capture();

		// A step that changes nothing is a Ctrl+Z that appears broken. Clicks that only advance a
		// tool - the first corner of a rectangle, a grabbed point let go where it was - go through
		// the same path as clicks that do commit something, so the cheapest place to tell them
		// apart is here, by comparing against what is already on top.
		if ( _undoStack.Count > 0 && Same( _undoStack[^1], snapshot ) )
			return;

		_undoStack.Add( snapshot );
		_redoStack.Clear();

		if ( _undoStack.Count > 100 )
			_undoStack.RemoveAt( 0 );
	}

	/// <summary>Whether two snapshots describe the same model - same features in the same order,
	/// same parameter values, same sketch geometry.</summary>
	private static bool Same( StudioSnapshot a, StudioSnapshot b )
	{
		if ( a.RollbackIndex != b.RollbackIndex || a.Features.Count != b.Features.Count )
			return false;

		for ( var i = 0; i < a.Features.Count; i++ )
		{
			if ( !ReferenceEquals( a.Features[i], b.Features[i] ) )
				return false;
		}

		if ( a.Values.Count != b.Values.Count )
			return false;

		foreach ( var (param, value) in a.Values )
		{
			if ( !b.Values.TryGetValue( param, out var other ) || !Equals( value, other ) )
				return false;
		}

		if ( a.Sketches.Count != b.Sketches.Count )
			return false;

		foreach ( var (feature, sketch) in a.Sketches )
		{
			if ( !b.Sketches.TryGetValue( feature, out var other ) || !SameSketch( sketch, other ) )
				return false;
		}

		if ( a.FaceSets.Count != b.FaceSets.Count )
			return false;

		foreach ( var (feature, faces) in a.FaceSets )
		{
			// By COUNT, not by comparing references. Two captures of the same face are not equal, so
			// a per-element comparison would call every snapshot different and put a step on the undo
			// stack for clicks that changed nothing. A count is enough for what this decides: whether
			// a face went in or came out.
			if ( !b.FaceSets.TryGetValue( feature, out var others ) || faces.Count != others.Count )
				return false;
		}

		if ( a.PaintStrokes.Count != b.PaintStrokes.Count )
			return false;

		foreach ( var (feature, strokes) in a.PaintStrokes )
		{
			if ( !b.PaintStrokes.TryGetValue( feature, out var others ) || !SameStrokes( strokes, others ) )
				return false;
		}

		if ( a.MaterialNames.Count != b.MaterialNames.Count )
			return false;

		foreach ( var (slot, name) in a.MaterialNames )
		{
			if ( !b.MaterialNames.TryGetValue( slot, out var other ) || name != other )
				return false;
		}

		if ( a.BodyNames.Count != b.BodyNames.Count )
			return false;

		foreach ( var (id, name) in a.BodyNames )
		{
			if ( !b.BodyNames.TryGetValue( id, out var other ) || name != other )
				return false;
		}

		if ( a.HiddenBodyIds.Count != b.HiddenBodyIds.Count )
			return false;

		foreach ( var id in a.HiddenBodyIds )
		{
			if ( !b.HiddenBodyIds.Contains( id ) )
				return false;
		}

		if ( a.FeatureNames.Count != b.FeatureNames.Count )
			return false;

		foreach ( var (feature, name) in a.FeatureNames )
		{
			if ( !b.FeatureNames.TryGetValue( feature, out var other ) || name != other )
				return false;
		}

		if ( !SameSkeleton( a.RigSkeleton, b.RigSkeleton ) )
			return false;

		if ( a.BodyBoneMap.Count != b.BodyBoneMap.Count )
			return false;

		foreach ( var (body, bone) in a.BodyBoneMap )
		{
			if ( !b.BodyBoneMap.TryGetValue( body, out var other ) || bone != other )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Whether two bones are equally soft.
	///
	/// NULL IS A VALUE HERE, not a missing one: a bone with no SoftBone is RIGID, and rigid versus
	/// soft-with-default-numbers is the single biggest difference a bone can have. Comparing the
	/// four floats without first comparing the nulls would call those two the same thing.
	/// </summary>
	private static bool SameSoft( SoftBone a, SoftBone b )
	{
		if ( a is null || b is null )
			return a is null && b is null;

		// Exact, for the reason SameSkeleton gives below: a stiffness nudged from 60 to 60.5 through
		// the inspector was still a deliberate edit, and tuning a wobble is a run of exactly those.
		return a.Stiffness.Equals( b.Stiffness )
			&& a.Damping.Equals( b.Damping )
			&& a.Weight.Equals( b.Weight )
			&& a.MaxAngle.Equals( b.MaxAngle );
	}

	/// <summary>Exact comparison, same reasoning as SameSketch's point-by-point check: a bone
	/// nudged by a millionth of a unit through the numeric inspector was still moved on purpose,
	/// and a tolerance here would silently swallow a fine adjustment instead of recording it.</summary>
	private static bool SameSkeleton( Skeleton a, Skeleton b )
	{
		if ( a.Count != b.Count )
			return false;

		for ( var i = 0; i < a.Count; i++ )
		{
			var ba = a.Bones[i];
			var bb = b.Bones[i];

			if ( ba.Name != bb.Name || ba.Parent != bb.Parent || ba.Length != bb.Length )
				return false;

			// SOFTNESS COUNTS AS A DIFFERENCE, and leaving it out cost an undo step per edit.
			// RecordUndo drops a snapshot that compares equal to the one already on top - the right
			// call for a click that only advanced a tool - so a comparison blind to Soft made every
			// softness change look like nothing happened. Ticking Soft and then Ctrl+Z left the bone
			// soft, and re-tuning stiffness twice in a row lost the first value entirely.
			if ( !SameSoft( ba.Soft, bb.Soft ) )
				return false;

			if ( !ba.Local.X.Equals( bb.Local.X ) || !ba.Local.Y.Equals( bb.Local.Y )
				|| !ba.Local.Z.Equals( bb.Local.Z ) || !ba.Local.Origin.Equals( bb.Local.Origin ) )
				return false;
		}

		return true;
	}

	private static bool SameSketch( Sketch a, Sketch b )
	{
		if ( a.Points.Count != b.Points.Count || a.Curves.Count != b.Curves.Count )
			return false;

		for ( var i = 0; i < a.Points.Count; i++ )
		{
			// Exact comparison on purpose: a point that moved by a millionth of a unit was still
			// moved by the user, and a tolerance here would silently swallow fine adjustments.
			if ( a.Points[i].x != b.Points[i].x || a.Points[i].y != b.Points[i].y )
				return false;
		}

		for ( var i = 0; i < a.Curves.Count; i++ )
		{
			if ( a.Curves[i].Id != b.Curves[i].Id || a.Curves[i].Construction != b.Curves[i].Construction )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Whether two paint-stroke lists describe the same paint, for the "did this change anything"
	/// dedupe in <see cref="RecordUndo"/>. Compared by the stroke objects' IDENTITY, not their fields:
	/// strokes are immutable once painted, so a capture shares the very objects the feature holds and
	/// two captures agree iff they point at the same strokes. Null means "never painted" and is a value
	/// here, not a missing one.
	/// </summary>
	private static bool SameStrokes( List<PaintStroke> a, List<PaintStroke> b )
	{
		if ( a is null || b is null )
			return a is null && b is null;

		if ( a.Count != b.Count )
			return false;

		for ( var i = 0; i < a.Count; i++ )
		{
			if ( !ReferenceEquals( a[i], b[i] ) )
				return false;
		}

		return true;
	}

	// ShortcutType.Window, matching RigControlWindow and ShaderGraph's MainWindow. Without the
	// attribute the Edit menu's "editor.undo" name resolves to nothing and Ctrl+Z never reaches
	// this window - the menu item worked and the key did not.
	[Shortcut( "editor.undo", "CTRL+Z", ShortcutType.Window )]
	private void Undo()
	{
		// SCULPT MODE OWNS UNDO OUTRIGHT while it is open, and does not fall through when its own
		// stack is empty. The studio's undo restores a feature list, and a snapshot taken before this
		// sculpt feature existed would leave the live session holding a feature the studio no longer
		// has. Doing nothing is the honest answer to "there is nothing left to undo in here".
		if ( _viewport?.SculptSession is not null )
		{
			StepSculptHistory( redo: false );
			return;
		}

		// THE PEN FALLS THROUGH WHERE SCULPTING DOES NOT, and the difference is that sculpt mode is
		// exclusive and the pen is not. Sculpting owns undo outright because the studio's undo
		// restores a feature list its live session may not survive; a note is not in that list at
		// all, so once the pen's own stack is empty the next Ctrl+Z is unambiguously about the
		// model. Owning it anyway would mean arming the pen quietly disabled undo for everything
		// else on the bar.
		if ( _viewport?.NoteSession is { CanUndo: true } noting )
		{
			noting.Undo();
			OnNoteEdited();

			return;
		}

		if ( _undoStack.Count == 0 )
			return;

		_redoStack.Add( Capture() );

		var previous = _undoStack[^1];
		_undoStack.RemoveAt( _undoStack.Count - 1 );

		Restore( previous );
	}

	// CTRL+Y, which is what this editor's own asset editors bind redo to.
	[Shortcut( "editor.redo", "CTRL+Y", ShortcutType.Window )]
	private void Redo()
	{
		if ( _viewport?.SculptSession is not null )
		{
			StepSculptHistory( redo: true );
			return;
		}

		// Falls through once the pen's stack is empty - see Undo above for why the pen does not own
		// the shortcut the way sculpting does.
		if ( _viewport?.NoteSession is { CanRedo: true } noting )
		{
			noting.Redo();
			OnNoteEdited();

			return;
		}

		if ( _redoStack.Count == 0 )
			return;

		_undoStack.Add( Capture() );

		var next = _redoStack[^1];
		_redoStack.RemoveAt( _redoStack.Count - 1 );

		Restore( next );
	}

	// --- sketch shortcuts --------------------------------------------------------------------

	// Onshape's own sketch keys: N looks square at the sketch plane, L is line, C is circle,
	// Q toggles construction geometry. They are documented shortcuts, not invented ones.

	[Shortcut( "effigy.view.normal", "N", ShortcutType.Window )]
	private void ShortcutViewNormal() => _viewport?.ViewNormalToSketchPlane();

	[Shortcut( "effigy.sketch.line", "L", ShortcutType.Window )]
	private void ShortcutLineTool() => ArmSketchTool( SketchToolKind.Line );

	[Shortcut( "effigy.sketch.circle", "C", ShortcutType.Window )]
	private void ShortcutCircleTool() => ArmSketchTool( SketchToolKind.Circle );

	[Shortcut( "effigy.sketch.construction", "Q", ShortcutType.Window )]
	private void ShortcutConstruction()
	{
		if ( _viewport?.IsSketching != true || _constructionTool is null )
			return;

		_constructionTool.Checked = !_constructionTool.Checked;
		_viewport.ConstructionMode = _constructionTool.Checked;

		// The toggle lives on the Reference stage, which is probably not the one showing — a
		// modifier flipped by a shortcut that leaves no mark anywhere on screen is a mode you
		// forget you are in.
		_stageBar?.Reveal( _constructionTool );
		_stageBar?.Refresh();
	}

	/// <summary>A sketch tool key outside sketch mode has nothing to arm, and silently switching a
	/// hidden tool would leave the strip disagreeing with the viewport next time it opened.</summary>
	private void ArmSketchTool( SketchToolKind kind )
	{
		if ( _viewport?.IsSketching != true )
			return;

		_viewport.SetSketchTool( kind );

		UpdateSketchToolChecks( kind );
		RevealSketchTool( kind );
	}

	// --- palette / theming ------------------------------------------------------------------

	private void SetPalette( int index )
	{
		_paletteIndex = Math.Clamp( index, 0, EffigyPalette.All.Length - 1 );
		_palette = EffigyPalette.All[_paletteIndex];

		ApplyPalette();

		// No BuildMenuBar() any more. It was here to redraw the View menu's checkmarks, and the
		// palette list is a dropdown in Edit > Settings now — the combo already shows what is
		// selected, and rebuilding the whole menu bar to update a tick that no longer exists was
		// throwing away the Edit and View menus on every palette change.
		EditorCookie.Set( PaletteCookie, _paletteIndex );
	}

	// --- settings ------------------------------------------------------------------------------

	/// <summary>Where the two settings persist between sessions. EditorCookie is the engine's own
	/// per-editor store — the same one the Boolean tool keeps its mode in.</summary>
	private const string PaletteCookie = "Effigy.Palette";

	/// <summary>A NEW KEY, not the old Effigy.ShowSketchGrid. That one meant "grid on the sketch
	/// plane" and defaulted to on; this one means "grid on every plane" and defaults to off. Reusing
	/// the key would have read a value stored against the old meaning and turned every plane's grid
	/// on for anyone who had ever opened the settings window.</summary>
	private const string PlaneGridCookie = "Effigy.ShowPlaneGrid";
	private const string GridSpacingCookie = "Effigy.GridSpacing";
	private const string SnapGridCookie = "Effigy.SnapToGrid";
	private const string SnapPointsCookie = "Effigy.SnapToPoints";
	private const string SnapFaceEdgesCookie = "Effigy.SnapToFaceEdges";

	/// <summary>Defaults to off. The stand-in is a whole character in the viewport and most parts
	/// are not built at body scale, so it is something you ask for rather than something you have
	/// to turn off before you can see what you are making.</summary>
	private const string SizeReferenceCookie = "Effigy.ShowSizeReference";

	/// <summary>Defaults to on. Modelling wants every face readable; the studio sun is the setting
	/// you turn on when you want to judge a material, not the light you sketch under.</summary>
	private const string FullBrightCookie = "Effigy.FullBright";

	/// <summary>The normal-map bake conventions and size. Defaults match the reference sample in
	/// Effigy.Tests/out: OpenGL green, no vertical flip, 1024.</summary>
	private const string BakeGreenCookie = "Effigy.BakeDirectXGreen";
	private const string BakeFlipVCookie = "Effigy.BakeFlipV";
	private const string BakeSizeCookie = "Effigy.BakeSize";

	/// <summary>The open settings window, or null. Held so a second Edit > Settings raises the one
	/// already open rather than stacking another on top of it.</summary>
	private EffigySettingsWindow _settingsWindow;

	/// <summary>The grid switch floating over the sketch, or null before the viewport is built.
	/// </summary>
	private EffigySketchGridBar _gridBar;

	/// <summary>
	/// The overlay changed the grid. It has already written the viewport; this is the rest of what
	/// the settings window's own callback does — remember it, and put the open settings window
	/// straight if it happens to be showing the value that just changed.
	///
	/// Both controls edit ONE value rather than each keeping their own, so the only thing that can
	/// go wrong is a stale view, and that is what the refresh below is for.
	/// </summary>
	private void OnGridBarChanged()
	{
		if ( !_viewport.IsValid() )
			return;

		EditorCookie.Set( PlaneGridCookie, _viewport.ShowPlaneGrid );
		EditorCookie.Set( GridSpacingCookie, _viewport.GridSpacing );

		_settingsWindow?.Sync( CurrentSettings() );
	}

	private void OpenSettings()
	{
		if ( _settingsWindow.IsValid() )
		{
			_settingsWindow.Focus();
			return;
		}

		_settingsWindow = new EffigySettingsWindow( this, CurrentSettings(), ApplySettings,
			addPointLight: AddViewportLight,
			clearLights: ClearViewportLights );
		_settingsWindow.Show();
	}

	private EffigySettingsWindow.Values CurrentSettings() => new()
	{
		ShowGrid = _viewport?.ShowPlaneGrid ?? false,
		GridSpacing = _viewport?.GridSpacing ?? 0f,
		SnapToGrid = _viewport?.SnapToGrid ?? true,
		SnapToPoints = _viewport?.SnapToPoints ?? true,
		SnapToFaceEdges = _viewport?.SnapToFaceEdges ?? true,
		PaletteIndex = _paletteIndex,
		ShowSizeReference = _viewport?.ShowSizeReference ?? false,
		SizeReferenceHeight = _viewport?.SizeReferenceHeight ?? 0f,
		FullBright = _viewport?.FullBright ?? true,
		PlacedLightCount = _viewport?.PlacedLightCount ?? 0,
		BakeDirectXGreen = _bakeFlipGreen,
		BakeFlipV = _bakeFlipV,
		BakeSize = _bakeSize,
	};

	/// <summary>Take everything the settings window is showing and make it true, then remember it.
	/// Called on every control change rather than behind an OK button — a viewport setting you
	/// cannot see take effect is one you have to guess at.</summary>
	private EffigySettingsWindow.Values ApplySettings( EffigySettingsWindow.Values values )
	{
		if ( _viewport.IsValid() )
		{
			_viewport.ShowPlaneGrid = values.ShowGrid;
			_viewport.GridSpacing = values.GridSpacing;
			_viewport.SnapToGrid = values.SnapToGrid;
			_viewport.SnapToPoints = values.SnapToPoints;
			_viewport.SnapToFaceEdges = values.SnapToFaceEdges;
			_viewport.ShowSizeReference = values.ShowSizeReference;
			_viewport.FullBright = values.FullBright;

			// READ BACK, not echoed. The viewport turns the switch off again if the citizen will
			// not load, and it is the only thing that knows how tall the one that did load is - so
			// what goes back to the settings window is what actually happened, not what was asked
			// for. Full bright is the same shape: adding a light turns it off so the lamp is
			// visible, and the switch has to follow.
			values.ShowSizeReference = _viewport.ShowSizeReference;
			values.SizeReferenceHeight = _viewport.SizeReferenceHeight;
			values.FullBright = _viewport.FullBright;
			values.PlacedLightCount = _viewport.PlacedLightCount;
		}

		if ( values.PaletteIndex != _paletteIndex )
			SetPalette( values.PaletteIndex );

		// Not viewport state - the bake reads these fields directly when the Bake button is pressed,
		// so applying them is just storing them.
		_bakeFlipGreen = values.BakeDirectXGreen;
		_bakeFlipV = values.BakeFlipV;
		_bakeSize = values.BakeSize;

		EditorCookie.Set( BakeGreenCookie, values.BakeDirectXGreen );
		EditorCookie.Set( BakeFlipVCookie, values.BakeFlipV );
		EditorCookie.Set( BakeSizeCookie, values.BakeSize );

		EditorCookie.Set( PlaneGridCookie, values.ShowGrid );
		EditorCookie.Set( GridSpacingCookie, values.GridSpacing );
		EditorCookie.Set( SnapGridCookie, values.SnapToGrid );
		EditorCookie.Set( SnapPointsCookie, values.SnapToPoints );
		EditorCookie.Set( SnapFaceEdgesCookie, values.SnapToFaceEdges );
		EditorCookie.Set( SizeReferenceCookie, values.ShowSizeReference );
		EditorCookie.Set( FullBrightCookie, values.FullBright );

		return values;
	}

	/// <summary>Put last session's settings back, before anything is drawn with them.</summary>
	private void RestoreSettings()
	{
		SetPalette( EditorCookie.Get( PaletteCookie, _paletteIndex ) );

		_bakeFlipGreen = EditorCookie.Get( BakeGreenCookie, false );
		_bakeFlipV = EditorCookie.Get( BakeFlipVCookie, false );
		_bakeSize = EditorCookie.Get( BakeSizeCookie, 1024 );

		if ( !_viewport.IsValid() )
			return;

		_viewport.ShowPlaneGrid = EditorCookie.Get( PlaneGridCookie, false );
		_viewport.GridSpacing = EditorCookie.Get( GridSpacingCookie, 0f );
		_viewport.SnapToGrid = EditorCookie.Get( SnapGridCookie, true );
		_viewport.SnapToPoints = EditorCookie.Get( SnapPointsCookie, true );
		_viewport.SnapToFaceEdges = EditorCookie.Get( SnapFaceEdgesCookie, true );
		_viewport.ShowSizeReference = EditorCookie.Get( SizeReferenceCookie, false );
		_viewport.FullBright = EditorCookie.Get( FullBrightCookie, true );
	}

	/// <summary>Drop a point light into the viewport. Full bright turns off so the lamp is
	/// visible — a light you cannot see is how this would look like it did nothing.</summary>
	private void AddViewportLight()
	{
		if ( !_viewport.IsValid() )
			return;

		_viewport.AddPointLight();
		SetPrompt( "Drag the bulb to move it. Delete removes it. Full bright is in Settings if you want even light back." );
	}

	private void ClearViewportLights()
	{
		if ( !_viewport.IsValid() )
			return;

		_viewport.ClearLights();
		SetPrompt( _viewport.FullBright
			? "Lamps cleared."
			: "Lamps cleared. The studio sun is still on." );
	}

	/// <summary>A light was added, removed, or full bright flipped — persist the mode and keep
	/// the settings window's switch in agreement if it is open.</summary>
	private void OnLightingChanged()
	{
		if ( !_viewport.IsValid() )
			return;

		EditorCookie.Set( FullBrightCookie, _viewport.FullBright );

		if ( _settingsWindow.IsValid() )
			_settingsWindow.Sync( CurrentSettings() );
	}

	/// <summary>
	/// Push the active palette at everything that reads one.
	///
	/// This set a single property that the camera had already read once in the viewport's
	/// constructor, before any palette was applied - so all four palettes rendered identically.
	/// See EffigyViewport.BackgroundColor for the other half of that fix.
	/// </summary>
	private void ApplyPalette()
	{
		if ( !_viewport.IsValid() )
			return;

		_viewport.BackgroundColor = _palette.ViewportBg;

		// The bar is CHROME and takes the chrome colour. The strips it replaced took the viewport's
		// background instead, because they sat on the 3D view and had to disappear into it; the bar
		// sits above the view and is meant to be seen.
		if ( _stageBar is not null )
			_stageBar.ChromeColor = _palette.Chrome;

		// Chrome2 rather than Chrome, so the two docked rows are distinguishable without a divider
		// doing all the work. The workspace row is the outer one and takes the darker of the pair —
		// chrome that contains other chrome should recede behind it, not sit in front.
		if ( _workspaceBar is not null )
			_workspaceBar.ChromeColor = _palette.Chrome2;

		if ( _sculptBar is not null )
			_sculptBar.GapColor = _palette.ViewportBg;

		// The chrome colour, not the viewport's: this one sits ON the bar, so its gaps have to
		// disappear into the bar the way the tool buttons beside it do.
		if ( _gridBar is not null )
			_gridBar.GapColor = _palette.Chrome;

		// Grid lines want the palette's dim text colour: it is picked to sit just above the
		// background in every one of these palettes, which is exactly the job.
		_viewport.PlaneColor = _palette.TextDim.WithAlpha( 0.55f );
	}

	// --- constraining a sketch selection --------------------------------------------------------

	/// <summary>
	/// The constraint menu, on a right-click inside a sketch.
	///
	/// A MENU RATHER THAN A TOOLBAR, which is not what Onshape does. The reason is what the offers
	/// are: they change with every click, so a strip of buttons would have to relabel, enable and
	/// disable itself per frame, and every bit of that is widget code this repo cannot compile to
	/// check. A menu is built fresh each time it opens, from machinery already proven in the feature
	/// tree and the face menu, and it puts the choices where the cursor already is.
	///
	/// What may be applied is ConstraintTools' answer, not this method's — it knows a point and a
	/// line make a point-on-line and two lines do not, and it knows what the sketch already says.
	/// </summary>
	private void OpenSketchConstraintMenu()
	{
		if ( _viewport?.ActiveSketch is not { } sketch )
			return;

		var offers = ConstraintTools.Offers( sketch, _viewport.SketchSelection );

		var menu = new Menu( _viewport );

		if ( offers.Count == 0 )
		{
			// SAYING SO IS THE POINT. An empty menu, or no menu at all, reads as a broken right
			// button — the user has selected something and is entitled to know why it buys them
			// nothing.
			menu.AddHeading( "Nothing to constrain from this selection" );

			menu.AddOption( "Clear selection", "backspace", () => _viewport.ClearSketchSelection() );

			menu.OpenAtCursor();
			return;
		}

		menu.AddHeading( Describe( _viewport.SketchSelection ) );

		foreach ( var offer in offers )
		{
			var it = offer;

			var option = menu.AddOption( it.NeedsValue ? $"{it.Label}…" : it.Label, IconFor( it.Kind ),
				() =>
				{
					if ( it.NeedsValue )
						AskForDimension( it );
					else
						ApplyConstraint( it );
				} );

			option.StatusTip = it.Hint;
		}

		menu.AddSeparator();

		menu.AddOption( "Clear selection", "backspace", () => _viewport.ClearSketchSelection() );

		menu.OpenAtCursor();
	}

	/// <summary>
	/// A dimension asks for its number before it is applied, in the one-field popup the feature tree
	/// renames with — pre-filled with what the sketch currently measures.
	///
	/// Pre-filled matters more than it looks. Most dimensions are added to LOCK something where it
	/// already is, and an empty box turns that into measuring by hand and typing a rounded version,
	/// which moves the geometry by however much the rounding was.
	/// </summary>
	private void AskForDimension( ConstraintOffer offer )
	{
		var menu = new Menu( _viewport );

		var edit = new LineEdit( Expression.Format( offer.Value ), menu ) { FixedWidth = 140 };

		edit.ReturnPressed += () =>
		{
			menu.Close();

			// Through the expression evaluator, the same as every numeric field in the dialog, so a
			// dimension can be typed as "25/2" or "3*8" like any other number in this editor.
			// The offer's own unit, so an angle typed as "45" reads as degrees and a length as units —
			// the same evaluator every numeric field in the dialog goes through.
			if ( !Expression.TryEvaluate( edit.Text, string.IsNullOrEmpty( offer.Unit ) ? null : offer.Unit, out var value ) )
			{
				SetPrompt( $"'{edit.Text}' is not a number" );
				return;
			}

			offer.Value = value;
			ApplyConstraint( offer );
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>Apply, and treat it as an edit of the sketch — an undo step, and a rebuild, because
	/// the solve has moved geometry that features downstream are standing on.</summary>
	private void ApplyConstraint( ConstraintOffer offer )
	{
		RecordUndo();

		if ( !_viewport.ApplySketchConstraint( offer ) )
			return;

		OnSketchEdited();
	}

	static string Describe( SketchSelection selection )
	{
		var parts = new List<string>();

		if ( selection.Points.Count > 0 )
			parts.Add( $"{selection.Points.Count} point{(selection.Points.Count == 1 ? "" : "s")}" );

		if ( selection.Curves.Count > 0 )
			parts.Add( $"{selection.Curves.Count} curve{(selection.Curves.Count == 1 ? "" : "s")}" );

		return string.Join( " and ", parts );
	}

	/// <summary>Classic Material Icons only — the set this editor's other menus draw from.</summary>
	static string IconFor( SketchConstraintKind kind ) => kind switch
	{
		SketchConstraintKind.Horizontal => "horizontal_rule",
		SketchConstraintKind.Vertical => "straighten",
		SketchConstraintKind.Coincident => "adjust",
		SketchConstraintKind.Distance => "straighten",
		SketchConstraintKind.EqualLength => "drag_handle",
		SketchConstraintKind.Parallel => "menu",
		SketchConstraintKind.Perpendicular => "square_foot",
		SketchConstraintKind.Angle => "square_foot",
		SketchConstraintKind.PointOnLine => "linear_scale",
		SketchConstraintKind.Symmetric => "flip",
		SketchConstraintKind.Radius => "radio_button_unchecked",
		SketchConstraintKind.Diameter => "circle",
		SketchConstraintKind.Midpoint => "vertical_align_center",
		SketchConstraintKind.Concentric => "adjust",
		SketchConstraintKind.Fixed => "lock",
		SketchConstraintKind.Tangent => "trip_origin",
		SketchConstraintKind.TangentArcs => "trip_origin",
		_ => "rule",
	};

	// --- right-click a face -------------------------------------------------------------------

	/// <summary>
	/// The right-click menu on a face of the model: what you can DO to this face, then what it is
	/// made of.
	///
	/// THE TOOL LIST IS GENERATED FROM Feature.Accepts, which is the whole point of it. The complaint
	/// that started this was pointing at a face, wanting to extrude it, and finding the tool nowhere
	/// on offer — so a hand-kept list here would have been the same failure with an extra place to
	/// forget. Every tool that says it takes a face appears, seeded with the face under the cursor,
	/// and one that learns to take a face later appears without anybody editing this.
	///
	/// The face is SELECTED first and then the tool is added, rather than the feature being poked
	/// directly: AddFeature already copies the idle selection onto whatever it makes, and going
	/// through it means the right-click route and the click-then-press-the-button route are the same
	/// path rather than two that can drift.
	///
	/// --- and the material half, which this menu was originally all of ---
	///
	/// The Face Material feature on the toolbar is how you paint a SET of faces in one go, and it is
	/// the wrong shape for the common case: one face, one slot, now. Opening a dialog, arming a
	/// selection box, clicking the face, closing the dialog is five actions for a thing you were
	/// already pointing at.
	///
	/// It still goes through the history. Writing the slot straight onto the mesh would work until
	/// the next rebuild and then quietly revert — bodies are rebuilt from scratch, which is the whole
	/// reason FaceMaterialFeature exists (see FaceMaterialTests: "the reason this is a feature").
	/// </summary>
	private void OpenFaceMenu( EffigyFaceHit hit )
	{
		if ( _studio is null || _viewport is null || hit.Body is null )
			return;

		var menu = new Menu( _viewport );

		AddFaceToolOptions( menu, hit );

		menu.AddHeading( $"Material — {_studio.NameForSlot( hit.Material )}" );

		foreach ( var slot in MenuMaterialSlots() )
		{
			var value = slot;

			// Slot 0 is the default every face starts on and the one the viewport deliberately does
			// not tint, so it gets the hollow marker — "no material" rather than "material zero".
			var option = menu.AddOption( _studio.NameForSlot( value ),
				value == 0 ? "panorama_fish_eye" : "lens",
				() => AssignFaceMaterial( hit, value ) );

			option.Checkable = true;
			option.Checked = hit.Material == value;
		}

		menu.AddSeparator();

		// The picker rather than the row widget the dialog and the Materials panel use: a menu closes
		// the moment you click anything in it, and it would take an embedded row — and the modal that
		// row had just parented to itself — down with it. Pick is the shared half that survives that.
		var choose = menu.AddOption( $"Choose material for {_studio.NameForSlot( hit.Material )}…", "palette",
			() => EffigyMaterialSlot.Pick( this, hit.Material, SlotMaterial( hit.Material ), SetSlotMaterial ) );

		choose.StatusTip = "Browse for the material this slot exports as";

		var rename = menu.AddOption( $"Rename {_studio.NameForSlot( hit.Material )}…", "edit",
			() => BeginMaterialSlotRename( hit.Material ) );

		rename.StatusTip = "The name every exporter writes for this slot";

		AddTextureScaleMenu( menu, hit );

		var shade = menu.AddOption( "Shade Material Slots", "palette",
			() => _viewport.ShadeMaterialSlots = !_viewport.ShadeMaterialSlots );

		shade.Checkable = true;
		shade.Checked = _viewport.ShadeMaterialSlots;

		menu.OpenAtCursor();
	}

	/// <summary>Every tool that will take a face, above the material entries because acting on the
	/// face is the bigger thing you can do to it.</summary>
	private void AddFaceToolOptions( Menu menu, EffigyFaceHit hit )
	{
		var tools = ToolsAccepting( GeometryKind.Face );

		if ( tools.Count == 0 )
			return;

		menu.AddHeading( $"Face of {hit.Body.Name ?? "part"}" );

		foreach ( var tool in tools )
		{
			// The KIND, captured, never the table entry — same rule as the strip: an enum crosses a
			// hotload and a reference into a table built by a dead assembly does not.
			var kind = tool.Kind;

			var option = menu.AddOption( tool.Label, tool.MenuIcon ?? "build",
				() => UseFaceWith( hit, kind ) );

			option.StatusTip = tool.Tip;
		}

		menu.AddSeparator();
	}

	/// <summary>Point a tool at the face that was right-clicked.</summary>
	private void UseFaceWith( EffigyFaceHit hit, ToolKind kind )
	{
		_viewport?.SelectFace( hit );
		AddFeature( NewFeature( kind, -1 ) );
	}

	/// <summary>
	/// The size of the material on the face you right-clicked, as a submenu.
	///
	/// WHY HERE FIRST. A material that is the wrong size is something you notice by LOOKING at it,
	/// and what you do next is right-click the thing that looks wrong. Every other route — a field
	/// in a dock, a UV Project feature in the tree — asks you to leave the face and go find the
	/// control, which is exactly the walk that made this feel like it did not exist.
	///
	/// THE VERBS ARE THE MESH EDITOR'S. s&amp;box's FaceTool offers doubling and halving buttons and a
	/// Fit with a repeat count, rather than a number field, because texture size is a thing you
	/// converge on by eye: you cannot look at a floor and say "38", you can say "bigger" four times.
	/// Fit is the one exact answer available without measuring anything, and it is also the only
	/// entry that is honest on an extrude SIDE, whose UVs are not in units at all — it measures the
	/// face rather than assuming what its numbers mean.
	///
	/// SLOT 0 IS EXCLUDED. It is every face nobody has painted, so resizing it from one face resizes
	/// most of the part — the same reason MaterialDrop refuses to allocate it. The entry is left
	/// visible and disabled rather than hidden, so the menu does not change shape depending on where
	/// you clicked.
	/// </summary>
	private void AddTextureScaleMenu( Menu menu, EffigyFaceHit hit )
	{
		var slot = hit.Material;
		var scale = MaterialScale.ScaleFor( _studio, slot );

		// Shown on the parent entry, because the current size is the thing you came to find out and
		// making you open a submenu to read it is a click for nothing.
		var square = MathF.Abs( scale.x - scale.y ) < 0.01f;
		var reading = square ? $"{Round( scale.x )}" : $"{Round( scale.x )} × {Round( scale.y )}";

		var scaleMenu = menu.AddMenu( $"Texture scale — {reading} u/tile", "aspect_ratio" );

		if ( slot <= 0 )
		{
			// AddOption rather than a note, so the reason is attached to something that looks like
			// the thing you were reaching for.
			var blocked = scaleMenu.AddOption( "Put this face on a material slot first", "block" );
			blocked.Enabled = false;
			blocked.StatusTip = "Slot 0 is every unpainted face — resizing it would resize the part";

			return;
		}

		scaleMenu.AddOption( "Bigger  (×2)", "zoom_in", () => ScaleFaceMaterial( slot, 2f ) );
		scaleMenu.AddOption( "Smaller  (÷2)", "zoom_out", () => ScaleFaceMaterial( slot, 0.5f ) );

		scaleMenu.AddSeparator();

		// One, two and four repeats rather than one alone: fitting a floor to a single repeat is a
		// tile the size of the room, which is right for a sign and never right for a tile.
		foreach ( var repeats in new[] { 1, 2, 4 } )
		{
			var count = repeats;

			scaleMenu.AddOption( count == 1 ? "Fit to face" : $"Fit — {count} across", "fit_screen",
				() => FitFaceMaterial( hit, count ) );
		}

		scaleMenu.AddSeparator();

		// The sizes a game texture is actually authored at, in inches. 48 is on the list because a
		// 12-inch tile four to a repeat is the case that started all of this.
		foreach ( var preset in new[] { 16f, 32f, 48f, 64f, 128f, 256f } )
		{
			var value = preset;

			var option = scaleMenu.AddOption( $"{Round( value )} u/tile", "straighten",
				() => SetMaterialScale( slot, new Vec2( value, value ) ) );

			option.Checkable = true;
			option.Checked = square && MathF.Abs( scale.x - value ) < 0.01f;
		}

		scaleMenu.AddSeparator();

		var reset = scaleMenu.AddOption( "Reset to 1:1", "restart_alt",
			() => SetMaterialScale( slot, MaterialScale.Unscaled ) );

		reset.StatusTip = "One repeat per unit — how the model was mapped before anything was set";

		// Off when there is nothing to reset, so the menu says whether this slot has been touched
		// without anyone having to read the number at the top and know what 1 means.
		reset.Enabled = _studio.MaterialScales.ContainsKey( slot );
	}

	/// <summary>Multiply a slot's size, which is the doubling and halving pair.</summary>
	private void ScaleFaceMaterial( int slot, float factor ) =>
		SetMaterialScale( slot, MaterialScale.ScaleFor( _studio, slot ) * factor );

	/// <summary>
	/// Size a slot so its material repeats a set number of times across the face that was clicked.
	///
	/// The face is looked up on the CURRENT bodies rather than through the FaceRef, because the
	/// index came out of a raycast against exactly those bodies moments ago and nothing has rebuilt
	/// since. The reference matters when an edit has to survive a rebuild; this one is consumed
	/// before the next one.
	/// </summary>
	private void FitFaceMaterial( EffigyFaceHit hit, int repeats )
	{
		if ( hit.Body?.Mesh is not { } mesh )
			return;

		SetMaterialScale( hit.Material,
			MaterialScale.Fit( mesh, hit.FaceIndex, MaterialScale.ScaleFor( _studio, hit.Material ), repeats ) );
	}

	/// <summary>
	/// Resize a slot — the one place every scale control lands, exactly as SetSlotMaterial is for
	/// naming one.
	///
	/// A document edit like any other: undo first, rebuild after. The rebuild is what re-divides the
	/// UVs, since MaterialScale.Apply runs at the end of one; nothing here touches a mesh directly.
	/// </summary>
	private void SetMaterialScale( int slot, Vec2 scale )
	{
		if ( _studio is null )
			return;

		RecordUndo();

		if ( !MaterialScale.SetScale( _studio, slot, scale ) )
			return;

		RebuildStudio();

		var now = MaterialScale.ScaleFor( _studio, slot );

		SetPrompt( $"{_studio.NameForSlot( slot )} at {Round( now.x )} × {Round( now.y )} units per tile." );
	}

	/// <summary>A size as you would say it out loud: 48 rather than 48.000, 0.5 rather than
	/// 0.500.</summary>
	private static string Round( float value ) =>
		value.ToString( MathF.Abs( value - MathF.Round( value ) ) < 0.005f ? "0" : "0.##" );

	/// <summary>
	/// Which slots the menu offers: zero through seven, plus anything the document already uses.
	///
	/// Seven is not arbitrary — it is how many colours the viewport tints with, so every slot on the
	/// menu is one you can tell apart on screen. The kernel allows 0..63 and nobody picks slot 40 off
	/// a list, but a document that arrived with one must not be unreachable, so the slots already in
	/// use are added back in however high they are.
	/// </summary>
	private IEnumerable<int> MenuMaterialSlots()
	{
		var slots = new SortedSet<int>();

		for ( var i = 0; i <= 7; i++ )
			slots.Add( i );

		foreach ( var slot in FaceMaterialEdit.UsedSlots( _studio ) )
			slots.Add( slot );

		return slots;
	}

	/// <summary>Name a slot, in the one-field popup the feature tree renames with.</summary>
	private void BeginMaterialSlotRename( int slot )
	{
		var menu = new Menu( this );
		var edit = new LineEdit( _studio.NameForSlot( slot ), menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			// Closed BEFORE the edit, because SetSlotMaterial rebuilds and this menu is a child of
			// the window it is rebuilding.
			var name = edit.Text?.Trim();
			menu.Close();

			SetSlotMaterial( slot, name );
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>
	/// What a slot carries, or null when it is still on its numbered default.
	///
	/// Not NameForSlot: that answers "what do the exporters write", which is never null, and the
	/// controls need "has anybody chosen anything", which is the question with an empty answer.
	/// </summary>
	private string SlotMaterial( int slot ) =>
		_studio is not null && _studio.MaterialNames.TryGetValue( slot, out var name )
			&& !string.IsNullOrWhiteSpace( name )
			? name
			: null;

	/// <summary>
	/// Give a slot a material — the one place all three controls land.
	///
	/// It is a document edit like any other: undo first, rebuild after. The rebuild is what repaints
	/// every face on the slot, refreshes the Materials panel, and pushes the new value back into a
	/// feature dialog that happens to be open on the same slot.
	/// </summary>
	private void SetSlotMaterial( int slot, string material )
	{
		if ( _studio is null || slot < 0 )
			return;

		var name = material?.Trim();

		// Clearing it puts the slot back on its numbered default rather than leaving it blank. Every
		// exporter has to write SOMETHING per slot, and an empty usemtl is not it. Typing the default
		// back in by hand means the same thing as clearing it, and is stored the same way — otherwise
		// the slot would read as assigned while exporting exactly what an unassigned one does.
		var clearing = string.IsNullOrWhiteSpace( name ) || name == ObjWriter.DefaultMaterialName( slot );

		if ( clearing ? !_studio.MaterialNames.ContainsKey( slot ) : SlotMaterial( slot ) == name )
			return;

		RecordUndo();

		if ( clearing )
			_studio.MaterialNames.Remove( slot );
		else
			_studio.MaterialNames[slot] = name;

		RebuildStudio();
	}

	/// <summary>Put one face on one slot. The bookkeeping — which assignment to reuse, what happens
	/// to the one the face is leaving, where a new one goes in a rolled-back tree — is
	/// FaceMaterialEdit in the kernel, where FaceMenuTests can hold it to account.</summary>
	private void AssignFaceMaterial( EffigyFaceHit hit, int slot )
	{
		if ( _studio is null || hit.Body is null || hit.Material == slot )
			return;

		RecordUndo();

		if ( FaceMaterialEdit.Assign( _studio, hit.Body.Id, hit.FaceIndex, hit.Reference, slot ) )
			RebuildStudio();
	}

	/// <summary>
	/// A material was dragged out of the browser and dropped on a face.
	///
	/// The same shape as AssignFaceMaterial above — undo, edit, rebuild — with one difference that
	/// is the whole reason MaterialDrop exists: the drop names a material and no slot, so the edit
	/// CHOOSES a slot, and the choice has to be said out loud. Nothing else on screen explains why
	/// the face went that particular shade of the slot palette, and the number is what you need if
	/// you then want to rename or rebind it from the Materials panel.
	/// </summary>
	private void OnMaterialDropped( EffigyFaceHit hit, string material )
	{
		if ( _studio is null || hit.Body is null || string.IsNullOrWhiteSpace( material ) )
			return;

		RecordUndo();

		// The slot the material is about to land on, asked BEFORE the drop, so "was this slot in use
		// already" has an answer. A material joining a slot it is already on must keep the size that
		// slot was given — the whole point of one-slot-per-material is that the second drop is the
		// same material, and re-guessing its size would undo a number somebody typed.
		var fresh = MaterialDrop.SlotCarrying( _studio, material ) < 0;

		if ( MaterialDrop.Drop( _studio, hit.Body.Id, hit.FaceIndex, hit.Reference, material,
			out var slot, out var released ) )
		{
			// Only a slot this drop INVENTED gets a guessed size. See EffigyMaterialSize for where
			// the number comes from and why it is the editor's own rule rather than one of ours.
			if ( fresh && slot > 0 )
				MaterialScale.SetScale( _studio, slot, EffigyMaterialSize.For( material ) );

			RebuildStudio();

			// The freed slot is said out loud for the same reason the chosen one is. Changing your
			// mind about a face used to leave the material you rejected bound to a slot forever,
			// which is invisible here and turns up as an export full of materials the part does not
			// wear. Now it is cleaned up — and a cleanup nobody is told about is its own surprise.
			SetPrompt( released >= 0
				? $"{MaterialFileName( material )} → slot {slot}, and slot {released} was freed. Ctrl+Z puts it back."
				: $"{MaterialFileName( material )} → slot {slot}. Ctrl+Z puts it back." );

			return;
		}

		// Nothing happened, which is two different situations and worth telling apart. A drop on a
		// face that already wears the material is an ordinary near-miss and needs no alarm; running
		// out of slots is a wall you have hit, and saying nothing there looks like the drag failed.
		SetPrompt( slot < 0
			? $"All {MaterialDrop.HighestSlot} material slots are in use — free one from the Materials panel."
			: $"That face is already on slot {slot}." );
	}

	/// <summary>
	/// Double-clicking a material in the browser: bind the part's BASE material, slot 0.
	///
	/// Slot 0 is every face nobody has painted, so this is "the part is made of this" — usually the
	/// largest surface on the model and the first thing you want bound. Dragging cannot do it,
	/// deliberately: MaterialDrop never allocates slot 0 because a drop points at ONE face, and
	/// giving it the slot the rest of the part is on would paint everything.
	/// </summary>
	private void SetBaseMaterial( string material )
	{
		if ( string.IsNullOrWhiteSpace( material ) )
			return;

		SetSlotMaterial( 0, material );
		SetPrompt( $"{MaterialFileName( material )} is the part's base material. Ctrl+Z puts it back." );
	}

	/// <summary>The last segment of a material path, for a status line that has no room for the
	/// rest of it. The same trimming EffigyMaterialSlot's label does, and for the same reason: the
	/// folders are what tell two materials apart in a picker, and noise in one line of feedback.
	/// </summary>
	private static string MaterialFileName( string path )
	{
		var cut = path.LastIndexOfAny( new[] { '/', '\\' } );

		return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
	}
}

// ============================================================================
//  The left panel — a flat feature tree matching Onshape's Part Studio layout:
//
//    FEATURES (2)
//    ├─ Origin
//    ├─ Top
//    ├─ Front
//    ├─ Right
//    ├─ Box
//    └─ Subdivide
//
//  Selecting a feature shows its parameters in the right panel.
//  Uses TreeView + TreeNode<T> — the same pattern as RigBonesPanel.
// ============================================================================

/// <summary>
/// The hover-reveal "eye" a tree row uses to toggle visibility — one implementation shared by the
/// Features tree (sketches, origin and planes) and the Parts tree (bodies).
///
/// Before this they were two independent copies of the same idea that had quietly drifted: the
/// Features tree reserved 34px of right margin for its secondary text and never hid it, the Parts
/// tree reserved only 30px and hid its face count on hover instead — two different answers to the
/// same "don't let anything sit under the eye" problem, which is exactly the kind of thing that
/// reads as the eye behaving inconsistently between the two trees even though neither was wrong on
/// its own. One rect, one show/hide rule, one click test, everywhere a row has an eye — and
/// SecondaryTextRightMargin so a row's own text picks a margin that is provably wide enough
/// rather than tracking Width by memory in a second place.
/// </summary>
internal static class TreeEyeIcon
{
	/// <summary>Width of the eye's own hit/paint rect, right-aligned to the tree.</summary>
	public const float Width = 24f;

	/// <summary>Gap kept clear between the eye and its own left edge.</summary>
	public const float Padding = 4f;

	/// <summary>How far from the row's right edge a row's OTHER text needs to stay clear of,
	/// whether or not the eye is actually drawn on this frame — the eye still needs the room the
	/// instant the row is hovered, so the margin cannot depend on hover state.</summary>
	public const float SecondaryTextRightMargin = Width + Padding + 6f;

	public static Rect Rect( TreeView tree, VirtualWidget item ) =>
		new( tree.LocalRect.Right - Width - Padding, item.Rect.Top, Width, item.Rect.Height );

	/// <summary>Shown on hover always, and whether or not hovered when the row is hidden — a
	/// hidden row stays obviously hidden rather than only announcing it while the mouse happens to
	/// be there.</summary>
	public static bool ShouldShow( VirtualWidget item, bool visible ) => item.Hovered || !visible;

	public static void Draw( TreeView tree, VirtualWidget item, bool visible )
	{
		if ( !ShouldShow( item, visible ) )
			return;

		Paint.SetPen( visible ? Theme.TextLight : Theme.Text );
		Paint.DrawIcon( Rect( tree, item ), visible ? "visibility" : "visibility_off", 16, TextFlag.Center );
	}

	public static bool WasClicked( TreeView tree, VirtualWidget item, MouseEvent e ) =>
		Rect( tree, item ).IsInside( e.LocalPosition );
}

/// <summary>What the feature tree's context menu asked the window to do. The panel knows what was
/// clicked; the window owns the studio, the dialog and the undo stack, so it does the doing.</summary>
internal enum EffigyFeatureCommand
{
	Edit,
	Rename,
	ToggleSuppress,
	Delete,
	MoveUp,
	MoveDown,
	RollbackTo,
	RollForward,
	Sculpt,
}

/// <summary>What the Parts list's context menu asked the window to do. Same split as
/// <see cref="EffigyFeatureCommand"/>: the panel knows the row, the window owns undo.</summary>
internal enum EffigyPartCommand
{
	Rename,
	ToggleVisibility,
	Edit,
	Delete,
	Isolate,
	ShowAll,
}

internal sealed class EffigyFeatureTreePanel : Widget
{
	private interface IVisibilityNode
	{
		bool IsVisible { get; }
		string VisibilityKey { get; }
		void ToggleVisibility();
	}

	private sealed class VisibilityTreeView : TreeView
	{
		public VisibilityTreeView( Widget parent ) : base( parent ) { }
		protected override bool OnItemPressed( VirtualWidget item, MouseEvent e )
		{
			if ( item.Object is IVisibilityNode node && TreeEyeIcon.WasClicked( this, item, e ) )
			{
				node.ToggleVisibility();
				return false;
			}
			return base.OnItemPressed( item, e );
		}
	}
	private PartStudio _studio;
	private TreeView _tree;
	private readonly Dictionary<Feature, FeatureNode> _nodes = new();

	public Feature SelectedFeature { get; private set; }

	public Action<Feature> FeatureSelected { get; set; }
	public Action StudioChanged { get; set; }
	public Action<string, bool> VisibilityToggled { get; set; }

	/// <summary>A context-menu item was picked.</summary>
	public Action<Feature, EffigyFeatureCommand> CommandRequested { get; set; }

	/// <summary>A rename was typed and confirmed. Separate from CommandRequested because it
	/// carries the new text, and because the window has to snapshot for undo BEFORE applying
	/// it.</summary>
	public Action<Feature, string> RenameCommitted { get; set; }

	/// <summary>
	/// What a sketch is attached to, shown on its row in the tree.
	///
	/// THE DIFFERENCE THIS MAKES IS THE WHOLE PARAMETRIC MODEL. A sketch on a face moves when that
	/// face moves, so everything built from it follows; a sketch on Top/Front/Right is anchored in
	/// world space and never follows anything. Both are legitimate and they look identical once
	/// the dialog is closed, which makes "why did that not update?" impossible to answer by
	/// looking at the tree. Now the row says which one it is.
	///
	/// WORDED EXACTLY AS THE PLANE BOX WORDS IT — "Face of Extrude 1", not "on Extrude 1". They are
	/// the same fact stated in two places a few pixels apart, and two phrasings of one fact read as
	/// two different facts: the box named a face and the row named something else, so the row had
	/// to be decoded rather than read. See EffigyFeatureDialog.FaceLabel, which is the other half
	/// of this and must keep saying the same thing.
	/// </summary>
	public string AttachmentLabel( SketchFeature sketch )
	{
		if ( sketch is null )
			return "";

		if ( sketch.Face is not { } face )
		{
			var offset = sketch.PlaneOffset.Value;

			return offset == 0f ? sketch.Plane.Value : $"{sketch.Plane.Value} {offset:+0.##;-0.##}";
		}

		var body = _studio?.Bodies.FirstOrDefault( b => b.Id == face.BodyId );

		// A face reference that resolves to nothing is the one case worth shouting about: the
		// sketch is about to fail, or already has.
		if ( body is null )
			return "Face of (missing)";

		var raised = sketch.PlaneOffset.Value;

		// The offset applies to a face-attached sketch exactly as it does to a global plane, and
		// leaving it off the row here said the sketch was ON a face when it was floating above it.
		return raised == 0f
			? $"Face of {body.Name ?? "part"}"
			: $"Face of {body.Name ?? "part"} {raised:+0.##;-0.##}";
	}

	/// <summary>True when the rollback bar sits above this feature, so it is not being evaluated.
	/// Painted dimmer, the way Onshape greys out everything below the bar.</summary>
	public bool IsRolledPast( Feature feature ) =>
		_studio is not null && _studio.Features.IndexOf( feature ) >= _studio.EffectiveCount;

	/// <summary>True for the FIRST feature below the bar - the one the bar is drawn above.</summary>
	public bool IsFirstRolledPast( Feature feature ) =>
		_studio is not null
		&& _studio.RollbackIndex < _studio.Features.Count
		&& _studio.Features.IndexOf( feature ) == _studio.EffectiveCount;

	/// <summary>
	/// Rename in place: a one-field popup at the cursor, which is what Menu.AddWidget is for.
	/// Opened by double-clicking a feature (TreeNode.OnActivated) or from the context menu.
	///
	/// The tree paints its rows virtually - there is no per-row widget to turn into a text box -
	/// so an editor has to be floated over it either way, and a popup is the one the editor
	/// already has machinery for.
	/// </summary>
	public void BeginRename( Feature feature )
	{
		if ( feature is null )
			return;

		var menu = new Menu( this );
		var edit = new LineEdit( feature.Name ?? feature.TypeName, menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			RenameCommitted?.Invoke( feature, edit.Text );
			menu.Close();
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>The right-click menu on a feature. Every entry acts on the feature that was
	/// clicked rather than on the selection, so right-clicking one row while another is selected
	/// does what it looks like it does.</summary>
	public void OpenFeatureMenu( Feature feature )
	{
		if ( feature is null )
			return;

		var menu = new Menu( this );

		var editLabel = feature switch
		{
			SketchFeature => "Edit Sketch",
			SculptFeature => "Edit Sculpt",
			_ => "Edit",
		};

		menu.AddOption( editLabel, "edit",
			() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.Edit ) );
		menu.AddOption( "Rename", "text_fields", () => BeginRename( feature ) );

		menu.AddSeparator();

		menu.AddOption( feature.Suppressed ? "Unsuppress" : "Suppress", "block",
			() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.ToggleSuppress ) );

		if ( feature is SketchFeature )
		{
			var key = $"sketch:{feature.Id}";

			menu.AddOption( IsVisible( key ) ? "Hide sketch" : "Show sketch",
				IsVisible( key ) ? "visibility_off" : "visibility", () => ToggleVisibility( key ) );
		}

		menu.AddSeparator();

		menu.AddOption( "Move up", "arrow_upward", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.MoveUp ) );
		menu.AddOption( "Move down", "arrow_downward", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.MoveDown ) );

		menu.AddSeparator();

		menu.AddOption( "Roll back to before this", "history",
			() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.RollbackTo ) );

		if ( _studio is not null && _studio.RollbackIndex < _studio.Features.Count )
		{
			menu.AddOption( "Roll forward to end", "last_page",
				() => CommandRequested?.Invoke( feature, EffigyFeatureCommand.RollForward ) );
		}

		menu.AddSeparator();

		menu.AddOption( "Delete", "delete", () => CommandRequested?.Invoke( feature, EffigyFeatureCommand.Delete ) );

		menu.OpenAtCursor();
	}

	/// <summary>Only keys the user has actually clicked the eye on. Everything else falls through
	/// to DefaultVisible, so an automatic decision (a consumed sketch hiding itself) can be
	/// overridden by hand and STAY overridden across rebuilds.</summary>
	private readonly Dictionary<string, bool> _visibility = new();

	public bool IsVisible( string key ) =>
		_visibility.TryGetValue( key, out var value ) ? value : DefaultVisible( key );

	/// <summary>Everything starts visible except a sketch some later feature has already built
	/// from - Onshape hides those the moment they are consumed, and so do we.</summary>
	private bool DefaultVisible( string key )
	{
		if ( _consumedSketchIds is null || !key.StartsWith( "sketch:" ) )
			return true;

		return !_consumedSketchIds.Contains( key["sketch:".Length..] );
	}

	/// <summary>Recomputed once per Rebuild rather than per eye paint - the tree repaints
	/// constantly and walking the feature list on every row of every frame would be wasteful.</summary>
	private HashSet<string> _consumedSketchIds;
	private void ToggleVisibility( string key )
	{
		var visible = !IsVisible( key );
		_visibility[key] = visible;
		VisibilityToggled?.Invoke( key, visible );
		_tree.Update();
	}
	private void PaintEye( VirtualWidget item, string key ) => TreeEyeIcon.Draw( _tree, item, IsVisible( key ) );

	public EffigyFeatureTreePanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Features";
		WindowTitle = "Features";
		SetWindowIcon( "account_tree" );

		_studio = studio;
		Layout = Layout.Column();

		var header = new Widget( this ) { Layout = Layout.Row() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 8;
		header.Layout.Add( new Editor.Label( "Features" ) { FixedWidth = 80 } );
		header.Layout.Add( new Editor.Label( "" ), 1 );
		Layout.Add( header );

		_tree = new VisibilityTreeView( this );
		_tree.OnSelectionChanged = objs =>
		{
			if ( objs?.FirstOrDefault() is FeatureNode node )
			{
				SelectedFeature = node.Feature;
				FeatureSelected?.Invoke( node.Feature );
			}
			else
			{
				SelectedFeature = null;
				FeatureSelected?.Invoke( null );
			}
		};
		Layout.Add( _tree, 1 );

		Rebuild();
	}

	public void SetStudio( PartStudio studio )
	{
		// This used to drop the argument on the floor, so File > New Studio rebuilt the tree
		// against the OLD studio and the window kept showing features that were gone.
		_studio = studio ?? new PartStudio();
		Rebuild();
	}

	/// <summary>Select a feature by identity. Rebuild throws the nodes away and makes new ones,
	/// so a caller holding a Feature cannot select it without this lookup.</summary>
	public void Select( Feature feature )
	{
		if ( feature is null || !_nodes.TryGetValue( feature, out var node ) )
			return;

		SelectedFeature = feature;
		_tree.SelectItem( node );
	}

	public void Rebuild()
	{
		_tree.Clear();
		_nodes.Clear();
		SelectedFeature = null;
		_consumedSketchIds = _studio?.ConsumedSketchIds();

		// Origin and the three reference planes - always present, at the top of the tree. They used
		// to hang under a "Default geometry" folder, which was a row whose only job was to be
		// expanded before you could reach the four rows inside it. The four rows sit here now.
		foreach ( var node in new DefaultGeometryChildNode[]
		{
			new( this, "Origin", "adjust", "origin" ),
			new( this, "Top (XY)", "crop_landscape", "top" ),
			new( this, "Front (XZ)", "crop_landscape", "front" ),
			new( this, "Right (YZ)", "crop_landscape", "right" ),
		} )
			_tree.AddItem( node );

		// Feature nodes
		foreach ( var feature in _studio.Features )
		{
			if ( IsHiddenFromTree( feature ) )
				continue;

			var node = new FeatureNode( this, feature );
			_nodes[feature] = node;
			_tree.AddItem( node );

			if ( feature.Suppressed )
				_tree.Close( node );
		}
	}

	/// <summary>
	/// Features that do their job without ever needing to be looked at.
	///
	/// FACE MATERIALS ARE BOOKKEEPING, NOT STEPS. Right-clicking a face and picking a slot creates
	/// one of these — one per slot, reused thereafter (FaceMaterialEdit.SlotFeature) — because the
	/// assignment has to live in the history or the next rebuild throws it away. That is a storage
	/// decision, and it was leaking into the tree as a row per slot: paint four faces four colours
	/// and the recipe for the part gained four entries that say nothing about how it was built.
	///
	/// Hiding the row does not hide the effect — the faces stay painted, undo still steps back
	/// through the assignments, and right-clicking the face again is how you change your mind.
	/// </summary>
	private static bool IsHiddenFromTree( Feature feature ) => feature is FaceMaterialFeature;

	// --- tree node types --------------------------------------------------------------------

	/// <summary>Origin and the three reference planes, at the top of the feature tree.</summary>
	private sealed class DefaultGeometryChildNode : TreeNode<string>
		, IVisibilityNode
	{
		private readonly string _icon;
		private readonly EffigyFeatureTreePanel _panel;
		public string VisibilityKey { get; }
		public bool IsVisible => _panel.IsVisible( VisibilityKey );
		public void ToggleVisibility() => _panel.ToggleVisibility( VisibilityKey );

		public DefaultGeometryChildNode( EffigyFeatureTreePanel panel, string name, string icon, string key ) : base( name )
		{
			_panel = panel;
			_icon = icon;
			VisibilityKey = key;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			Paint.SetPen( Theme.TextLight );
			Paint.DrawIcon( item.Rect, _icon, 14, TextFlag.LeftCenter );

			Paint.SetPen( Theme.Text );
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), Value, TextFlag.LeftCenter );
			_panel.PaintEye( item, VisibilityKey );
		}
	}

	/// <summary>A feature in the tree — icon + name + error/suppressed indicator.</summary>
	private sealed class FeatureNode : TreeNode<Feature>, IVisibilityNode
	{
		private readonly EffigyFeatureTreePanel _panel;
		public string VisibilityKey => $"sketch:{Feature.Id}";
		public bool IsVisible => Feature is SketchFeature && _panel.IsVisible( VisibilityKey );
		public void ToggleVisibility() { if ( Feature is SketchFeature ) _panel.ToggleVisibility( VisibilityKey ); }
		public Feature Feature => Value;

		public FeatureNode( EffigyFeatureTreePanel panel, Feature feature ) : base( feature ) { _panel = panel; }

		/// <summary>The problem line, so a broken feature is readable without opening it. A red
		/// icon with no words is the Onshape behaviour this dialog exists to beat.</summary>
		public override string GetTooltip()
		{
			if ( Value.Diagnostic is { } diagnostic && !string.IsNullOrEmpty( diagnostic.Tooltip ) )
				return diagnostic.Tooltip.Replace( "\n", "<br/>" );

			return Value.Error ?? Value.Warning;
		}

		/// <summary>Double click renames, which is where every tree in the editor puts it.</summary>
		public override void OnActivated() => _panel.BeginRename( Feature );

		/// <summary>Right click opens the feature menu. Returning true stops the tree falling back
		/// to its own (empty) menu.</summary>
		public override bool OnContextMenu()
		{
			_panel.OpenFeatureMenu( Feature );
			return true;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			// Below the rollback bar: this feature is not being evaluated at all, so it is drawn
			// as history rather than as part of the model. The bar itself is a line across the top
			// of the first such row - the same place Onshape draws it.
			var rolled = _panel.IsRolledPast( Value );

			if ( _panel.IsFirstRolledPast( Value ) )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Yellow.WithAlpha( 0.75f ) );
				Paint.DrawRect( new Rect( item.Rect.Left, item.Rect.Top, item.Rect.Width, 2f ) );
			}

			// Icon color: blue for active, grey for suppressed, red for error, yellow for warning
			if ( Value.Suppressed || rolled )
				Paint.SetPen( Theme.TextLight.WithAlpha( 0.5f ) );
			else if ( Value.Error is not null )
				Paint.SetPen( Theme.Red );
			else if ( Value.Warning is not null )
				Paint.SetPen( Theme.Yellow );
			else
				Paint.SetPen( Theme.Blue );

			Paint.DrawIcon( item.Rect, "category", 14, TextFlag.LeftCenter );

			Paint.SetPen( Value.Suppressed || rolled ? Theme.TextLight : Theme.Text );
			var label = $"{Value.Name ?? Value.TypeName}";
			if ( Value.Suppressed )
				label += " (suppressed)";
			Paint.DrawText( item.Rect.Shrink( 22, 0, 0, 0 ), label, TextFlag.LeftCenter );
			// Right-aligned, clear of the eye's strip: what this sketch is attached to, and
			// therefore whether anything built from it will follow an edit upstream.
			if ( Value is SketchFeature attached )
			{
				Paint.SetPen( Theme.TextLight.WithAlpha( 0.55f ) );
				Paint.DrawText( item.Rect.Shrink( 0, 0, TreeEyeIcon.SecondaryTextRightMargin, 0 ),
					_panel.AttachmentLabel( attached ), TextFlag.RightCenter );
			}

			if ( Value is SketchFeature ) _panel.PaintEye( item, VisibilityKey );
		}
	}
}

/// <summary>
/// One entry in a sketch tool's dropdown: the same kind of tool, done a different way. A corner
/// rectangle and a centre rectangle are one button in Onshape, not two.
///
/// A BUILD-TIME DESCRIPTION, not something the bar ever sees. BuildSketchStages turns each of
/// these into an EffigyStageVariant carrying a closure, which is what the bar understands; this
/// type exists so the stage table can be written as a list of tools and kinds rather than a list
/// of lambdas, and so registering every variant against its SketchToolKind stays one line.
/// </summary>
internal sealed class SketchToolVariant
{
	public readonly EffigyIcon Icon;
	public readonly string Label;
	public readonly string Tip;
	public readonly SketchToolKind Kind;

	public SketchToolVariant( EffigyIcon icon, string label, string tip, SketchToolKind kind )
	{
		Icon = icon;
		Label = label;
		Tip = tip;
		Kind = kind;
	}
}


// ============================================================================
//  The Parts list — the bodies the feature tree has actually produced, in
//  their own list below it. Onshape's Parts panel: the feature tree is the
//  RECIPE, this is the RESULT, and the two are not the same thing. Three
//  features can make one part and one pattern feature can make eight.
// ============================================================================

internal sealed class EffigyPartsPanel : Widget
{
	private PartStudio _studio;
	private readonly PartsTreeView _tree;

	/// <summary>Body id of the part whose eye was clicked. The window owns the studio and the
	/// rebuild, so the panel reports the click rather than acting on it.</summary>
	public Action<string> VisibilityToggled { get; set; }

	public Action<string, EffigyPartCommand> CommandRequested { get; set; }

	/// <summary>A rename was typed and confirmed. Carries the new text, and the window has to
	/// snapshot for undo BEFORE applying it.</summary>
	public Action<string, string> RenameCommitted { get; set; }

	/// <summary>The row highlight changed. Body ids of the selected parts, empty for none.</summary>
	public Action<IReadOnlyList<string>> SelectionChanged { get; set; }

	private readonly List<string> _selectedBodyIds = new();
	private readonly Dictionary<string, PartNode> _nodes = new();
	private bool _restoringSelection;

	public EffigyPartsPanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Parts";
		WindowTitle = "Parts";

		_studio = studio;
		Layout = Layout.Column();

		var header = new Widget( this ) { Layout = Layout.Row() };
		header.Layout.Margin = new Sandbox.UI.Margin( 8, 4 );
		header.Layout.Spacing = 8;
		header.Layout.Add( new Editor.Label( "Parts" ) { FixedWidth = 80 } );
		header.Layout.Add( new Editor.Label( "" ), 1 );
		Layout.Add( header );

		_tree = new PartsTreeView( this );
		_tree.OnSelectionChanged = objs =>
		{
			if ( _restoringSelection )
				return;

			_selectedBodyIds.Clear();

			if ( objs is not null )
			{
				foreach ( var obj in objs )
				{
					if ( obj is PartNode node )
						_selectedBodyIds.Add( node.Value.Id );
				}
			}

			SelectionChanged?.Invoke( _selectedBodyIds );
		};
		Layout.Add( _tree, 1 );

		// Tall enough for a few parts without taking the feature tree's room - the tree above it
		// is the one that grows.
		MinimumHeight = 118f;

		Refresh();
	}

	public void SetStudio( PartStudio studio )
	{
		_studio = studio ?? new PartStudio();
		Refresh();
	}

	public void Refresh()
	{
		_tree.Clear();
		_nodes.Clear();

		if ( _studio is null || _studio.Bodies.Count == 0 )
		{
			_selectedBodyIds.Clear();
			_tree.AddItem( new EmptyPartsNode() );
			return;
		}

		_selectedBodyIds.RemoveAll( id => _studio.Bodies.All( b => b.Id != id ) );

		foreach ( var body in _studio.Bodies )
		{
			var node = new PartNode( this, body );
			_nodes[body.Id] = node;
			_tree.AddItem( node );
		}

		RestoreTreeSelection();
	}

	/// <summary>Select these parts in the list. Used when the viewport picked a face so the row
	/// and the solid stay the same selection. Does not re-raise SelectionChanged.</summary>
	public void Select( IReadOnlyList<string> bodyIds )
	{
		_selectedBodyIds.Clear();

		if ( bodyIds is not null )
			_selectedBodyIds.AddRange( bodyIds.Where( id => !string.IsNullOrEmpty( id ) ) );

		RestoreTreeSelection();
	}

	private void RestoreTreeSelection()
	{
		_restoringSelection = true;

		if ( _selectedBodyIds.Count == 0 || !_nodes.TryGetValue( _selectedBodyIds[0], out var node ) )
			_tree.ClearPartSelection();
		else
			_tree.SelectItem( node );

		_restoringSelection = false;
	}

	/// <summary>Rename in place: a one-field popup at the cursor, same as the feature tree.</summary>
	public void BeginRename( string bodyId )
	{
		var body = BodyById( bodyId );

		if ( body is null )
			return;

		var menu = new Menu( this );
		var edit = new LineEdit( body.Name ?? "Part", menu ) { FixedWidth = 190 };

		edit.ReturnPressed += () =>
		{
			RenameCommitted?.Invoke( bodyId, edit.Text );
			menu.Close();
		};

		menu.AddWidget( edit );
		menu.OpenAtCursor();

		edit.Focus();
		edit.SelectAll();
	}

	/// <summary>The right-click menu on a part. Every entry acts on the row that was clicked
	/// rather than on the selection, so right-clicking one part while another is selected does
	/// what it looks like it does.</summary>
	public void OpenPartMenu( Body body )
	{
		if ( body is null )
			return;

		var menu = new Menu( this );
		var bodyId = body.Id;
		var visible = body.Visible;
		var othersHidden = _studio.HiddenBodyIds.Count > 0;

		menu.AddOption( "Rename", "text_fields", () => BeginRename( bodyId ) );
		menu.AddOption( "Edit", "edit", () => CommandRequested?.Invoke( bodyId, EffigyPartCommand.Edit ) );

		menu.AddSeparator();

		menu.AddOption( visible ? "Hide" : "Show",
			visible ? "visibility_off" : "visibility",
			() => CommandRequested?.Invoke( bodyId, EffigyPartCommand.ToggleVisibility ) );

		menu.AddOption( "Show only this", "center_focus_strong",
			() => CommandRequested?.Invoke( bodyId, EffigyPartCommand.Isolate ) );

		if ( othersHidden )
		{
			menu.AddOption( "Show all parts", "visibility",
				() => CommandRequested?.Invoke( bodyId, EffigyPartCommand.ShowAll ) );
		}

		menu.AddSeparator();

		var delete = menu.AddOption( "Delete", "delete",
			() => CommandRequested?.Invoke( bodyId, EffigyPartCommand.Delete ) );

		var siblings = _studio.Bodies.Count( b => b.FeatureId == body.FeatureId );

		if ( siblings > 1 )
			delete.StatusTip = "Removes the feature that made this part, and every other part it made.";

		menu.OpenAtCursor();
	}

	private Body BodyById( string bodyId ) =>
		_studio?.Bodies.FirstOrDefault( b => b.Id == bodyId );

	private sealed class PartsTreeView : TreeView
	{
		public PartsTreeView( Widget parent ) : base( parent ) { }

		public void ClearPartSelection()
		{
			foreach ( var item in SelectedItems.ToList() )
				SetSelected( item, false, skipEvents: true );
		}

		protected override bool OnItemPressed( VirtualWidget item, MouseEvent e )
		{
			if ( item.Object is PartNode node && TreeEyeIcon.WasClicked( this, item, e ) )
			{
				node.ToggleVisibility();
				return false;
			}

			return base.OnItemPressed( item, e );
		}
	}

	/// <summary>One body: name, face count, and an eye.</summary>
	private sealed class PartNode : TreeNode<Body>
	{
		private readonly EffigyPartsPanel _panel;

		public PartNode( EffigyPartsPanel panel, Body body ) : base( body ) { _panel = panel; }

		public void ToggleVisibility() => _panel.VisibilityToggled?.Invoke( Value.Id );

		/// <summary>Double click renames, which is where every tree in the editor puts it.</summary>
		public override void OnActivated() => _panel.BeginRename( Value.Id );

		/// <summary>Right click opens the part menu. Returning true stops the tree falling back
		/// to its own (empty) menu.</summary>
		public override bool OnContextMenu()
		{
			_panel.OpenPartMenu( Value );
			return true;
		}

		public override void OnPaint( VirtualWidget item )
		{
			PaintSelection( item );

			var visible = Value.Visible;

			Paint.SetPen( visible ? Theme.Green.WithAlpha( 0.8f ) : Theme.TextLight.WithAlpha( 0.5f ) );
			Paint.DrawIcon( item.Rect, "view_in_ar", 14, TextFlag.LeftCenter );

			Paint.SetPen( visible ? Theme.Text : Theme.TextLight );
			Paint.DrawText( item.Rect.Shrink( 22, 0, TreeEyeIcon.SecondaryTextRightMargin, 0 ),
				Value.Name ?? "Part", TextFlag.LeftCenter );

			// Always drawn, same as the Features tree's attachment label — the shared margin
			// already keeps it clear of the eye, so there is no need to make it vanish and
			// reappear on hover the way this row used to.
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 0, 0, TreeEyeIcon.SecondaryTextRightMargin, 0 ),
				$"{Value.Mesh?.FaceCount ?? 0}", TextFlag.RightCenter );

			TreeEyeIcon.Draw( _panel._tree, item, visible );
		}
	}

	/// <summary>Shown instead of an empty list, because an empty panel reads as broken.</summary>
	private sealed class EmptyPartsNode : TreeNode<string>
	{
		public EmptyPartsNode() : base( "No parts yet" ) { }

		public override void OnPaint( VirtualWidget item )
		{
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.6f ) );
			Paint.DrawText( item.Rect.Shrink( 8, 0, 0, 0 ), Value, TextFlag.LeftCenter );
		}
	}
}
