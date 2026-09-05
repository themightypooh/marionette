using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// The Materials dock: the project's materials, in their folders, as a grid you drag out of.
///
/// WHAT THIS REPLACED. First a column of eight rows — "Slot 3 · material_3 (default) ·
/// [Browse...] · [×]" — which made you start from a NUMBER: to put brushed steel on something you
/// picked a slot you had no opinion about, opened a modal picker, found the material in there,
/// closed it, and then went and painted faces. Seven eighths of the dock was permanently a list of
/// names of things that did not exist yet, and the materials themselves — the only part with a
/// picture — were never on screen at all.
///
/// Then, briefly, EVERY MATERIAL IN A FLAT ALPHABETICAL GRID, which is why the folders below exist.
/// This project can see 1248 materials, 363 of them its own, and the overwhelming majority are
/// `materials/halo/characters/&lt;something&gt;/halo_0.vmat` through `halo_12.vmat` — hundreds of
/// cells whose names are all `halo_3`. The FOLDER is where the meaning is: `elite` tells you what
/// you are looking at and `halo_3` tells you nothing, so a listing that throws the folders away and
/// sorts the leaves alphabetically is strictly worse than no listing at all. It is also exactly what
/// the editor's own asset browser does not do, which is the whole point of the comparison.
///
/// So: navigate, do not enumerate. You land in `materials`, folders come first with a count each,
/// double-click to descend, the path bar walks you back out. Search is the escape hatch and searches
/// RECURSIVELY from wherever you are standing, the way the real browser's does.
///
/// WHAT THE SLOT DOES HERE. A face carries a slot number, not a material, and that has to stay true
/// — see FaceMaterialEdit. The slot is now shown rather than asked for: drag a material onto a face
/// and <see cref="Effigy.MaterialDrop"/> picks the slot, a material the document uses wears its slot
/// number as a badge in that slot's viewport tint, and the right-click menu is where you go when you
/// do want to bind a specific one by hand.
/// </summary>
internal sealed class EffigyMaterialsPanel : Widget, AssetSystem.IEventListener
{
	private PartStudio _studio;

	private readonly LineEdit _search;
	private readonly Button _scopeButton;
	private readonly EffigyPathBar _pathBar;
	private readonly ListView _list;
	private readonly Editor.Label _status;

	/// <summary>Every material in the current scope, whatever folder it is in. The folder view is
	/// computed from this on navigation rather than stored as a tree: there is one list, and where
	/// you are standing is a string prefix into it.</summary>
	private List<AssetEntry> _all = new();

	/// <summary>Which slot each material is bound to, by the entry drawn for it. Rebuilt on
	/// <see cref="Refresh"/> so <see cref="PaintCell"/> — which runs per cell per frame — reads a
	/// dictionary rather than asking the kernel once per cell.</summary>
	private readonly Dictionary<AssetEntry, int> _slots = new();

	/// <summary>The same materials keyed by MaterialDrop.Normalise of their path, so a slot's name
	/// finds its cell in one lookup.</summary>
	private readonly Dictionary<string, AssetEntry> _byPath = new();

	/// <summary>Project materials only, or everything the editor can see. Defaults to the project:
	/// 363 against 1248 here, and the 885 are engine and mounted content you are not modelling
	/// with.</summary>
	private bool _projectOnly = true;

	/// <summary>Where we are standing, as a path prefix with no trailing slash. Empty is the root.
	/// </summary>
	private string _folder = "";

	/// <summary>Side of one cell. Big enough that two greys are different pictures rather than two
	/// grey squares, which is the whole reason to show thumbnails instead of file names.</summary>
	private const int CellSize = 92;

	/// <summary>A slot was given a material, or cleared back to its numbered default. The same
	/// contract this panel has always had, still wired to the window's SetSlotMaterial: the panel
	/// reports the pick, the window owns the studio, the undo stack and the rebuild.</summary>
	public Action<int, string> MaterialChanged { get; set; }

	/// <summary>
	/// A material was double-clicked.
	///
	/// The window binds this to the part's BASE material — slot 0, what every face nobody has
	/// painted is on — which is the one assignment dragging cannot make: a drop lands on ONE face,
	/// and MaterialDrop never allocates slot 0 precisely because doing so would paint the whole
	/// part. Double-click is where "paint the whole part" belongs, so the two gestures cover the two
	/// things you actually want and neither can be the other by accident.
	/// </summary>
	public Action<string> MaterialActivated { get; set; }

	public EffigyMaterialsPanel( Widget parent, PartStudio studio ) : base( parent )
	{
		Name = "Materials";
		WindowTitle = "Materials";
		SetWindowIcon( "palette" );

		_studio = studio;

		Layout = Layout.Column();

		var header = Layout.AddRow();
		header.Margin = new Sandbox.UI.Margin( 6, 6, 6, 2 );
		header.Spacing = 6;

		_search = new LineEdit( this ) { PlaceholderText = "Search this folder" };
		_search.TextEdited += _ => Populate();
		header.Add( _search, 1 );

		_scopeButton = new Button( "", "home", this ) { FixedWidth = 26 };
		_scopeButton.Clicked = ToggleScope;
		header.Add( _scopeButton );

		var reload = new Button( "", "refresh", this ) { FixedWidth = 26, ToolTip = "Look for materials again" };
		reload.Clicked = Rescan;
		header.Add( reload );

		_pathBar = new EffigyPathBar( this ) { Navigated = GoTo };
		Layout.Add( _pathBar );

		_list = Layout.Add( new ListView( this ), 1 );
		_list.ItemSize = new Vector2( CellSize, CellSize + 22 );
		_list.ItemSpacing = 2;
		_list.MultiSelect = false;
		_list.Margin = new Sandbox.UI.Margin( 4 );
		_list.ItemPaint = PaintCell;

		// Thumbnails are rendered on demand and cost real time, so the list only asks for the ones
		// scrolled into view — the same deal the editor's own asset list makes. Without this, every
		// material in the folder would be rendered on the frame you walked into it.
		_list.ItemScrollEnter = item => (item as AssetEntry)?.OnScrollEnter();
		_list.ItemScrollExit = item => (item as AssetEntry)?.OnScrollExit();

		_list.ItemSelected = ShowItem;
		_list.ItemActivated = Activate;
		_list.ItemContextMenu = item => OpenCellMenu( item as AssetEntry );
		_list.ItemDrag = BeginDrag;

		BuildScaleStrip();

		var footer = Layout.AddRow();
		footer.Margin = new Sandbox.UI.Margin( 8, 2, 8, 6 );

		_status = new Editor.Label( "" ) { Color = Theme.TextLight.WithAlpha( 0.6f ) };
		footer.Add( _status, 1 );

		Rescan( keepFolder: false );
	}

	public void SetStudio( PartStudio studio )
	{
		_studio = studio ?? new PartStudio();
		Refresh();
	}

	// --- how big the material is -----------------------------------------------------------------
	//
	// The face menu is where you FIX a material that is the wrong size, because that is where you
	// notice it. This is where you READ one: which of the materials the part uses have been resized,
	// and to what. Same fact, two questions, and the grid is the only place that can answer the
	// second — a menu shows you one slot at a time and only if you can find a face wearing it.
	//
	// It mirrors the mesh editor's "Texture Selection" group, minus Fit, which needs a face and has
	// no meaning against a cell in a browser.

	private Widget _scaleStrip;
	private Editor.Label _scaleLabel;
	private EffigyNumericField _scaleU;
	private EffigyNumericField _scaleV;

	/// <summary>The slot the strip is currently editing, or -1 when it is hidden. Held rather than
	/// re-derived from the selection, because the fields fire after a rebuild has already moved the
	/// selection on.</summary>
	private int _scaleSlot = -1;

	/// <summary>A slot was resized. Wired to the window for the same reason MaterialChanged is: the
	/// panel reports the edit, the window owns the studio, the undo stack and the rebuild.</summary>
	public Action<int, Vec2> ScaleChanged { get; set; }

	private void BuildScaleStrip()
	{
		_scaleStrip = Layout.Add( new Widget( this ) );
		_scaleStrip.Layout = Layout.Row();
		_scaleStrip.Layout.Margin = new Sandbox.UI.Margin( 8, 2, 8, 2 );
		_scaleStrip.Layout.Spacing = 4;

		_scaleLabel = new Editor.Label( "" ) { Color = Theme.TextLight.WithAlpha( 0.75f ) };
		_scaleStrip.Layout.Add( _scaleLabel );
		_scaleStrip.Layout.AddStretchCell();

		// Two fields rather than one linked number, matching the Vector2 the mesh editor shows.
		// A tile is usually square and a plank never is, and a single field cannot say so.
		_scaleU = new EffigyNumericField( _scaleStrip, 1f, "u" ) { Min = 0.0001f, FixedWidth = 74 };
		_scaleU.ValueEdited = _ => PushScale();
		_scaleU.ToolTip = "Units across one repeat";
		_scaleStrip.Layout.Add( _scaleU );

		_scaleV = new EffigyNumericField( _scaleStrip, 1f, "u" ) { Min = 0.0001f, FixedWidth = 74 };
		_scaleV.ValueEdited = _ => PushScale();
		_scaleV.ToolTip = "Units up one repeat";
		_scaleStrip.Layout.Add( _scaleV );

		var smaller = new Button( "", "zoom_out", _scaleStrip ) { FixedWidth = 26, ToolTip = "Half the size" };
		smaller.Clicked = () => Nudge( 0.5f );
		_scaleStrip.Layout.Add( smaller );

		var bigger = new Button( "", "zoom_in", _scaleStrip ) { FixedWidth = 26, ToolTip = "Twice the size" };
		bigger.Clicked = () => Nudge( 2f );
		_scaleStrip.Layout.Add( bigger );

		_scaleStrip.Visible = false;
	}

	/// <summary>
	/// Point the strip at whatever is selected, or hide it.
	///
	/// UNBOUND MATERIALS HIDE IT rather than showing a disabled row. A size is a fact about a slot,
	/// and a material nobody has dropped is not on one — offering a field there would take a number
	/// with nowhere to put it, and the fix is to drag the material onto a face, which the grid is
	/// already for.
	/// </summary>
	private void ShowScaleFor( object item )
	{
		if ( !_scaleStrip.IsValid() )
			return;

		var slot = item is AssetEntry entry && _slots.TryGetValue( entry, out var found ) ? found : -1;

		_scaleSlot = slot;
		_scaleStrip.Visible = slot >= 0;

		if ( slot < 0 )
			return;

		var scale = MaterialScale.ScaleFor( _studio, slot );

		_scaleLabel.Text = $"Slot {slot} · units per tile";

		PullScale();
	}

	/// <summary>
	/// Bring the strip up to date with the studio, on every rebuild.
	///
	/// TWO THINGS, and the second is the one with a trap in it. The slot can stop existing under the
	/// strip — a drop elsewhere frees it — and a size field pointed at a binding that is gone is
	/// worse than no field. And the number can move without the fields having touched it, because
	/// the face menu resizes the same slot.
	///
	/// THE TRAP: this also runs on the rebuild caused by the keystroke you are still in the middle
	/// of. Type 4 into a 48 and the edit lands, the model rebuilds, and writing the value back would
	/// put "4" in the box under your cursor and move the caret before you reached the 8. So the
	/// write goes through <see cref="PullScale"/>, which asks the field whether it is being typed in
	/// first. EffigyFeatureDialog splits the same two jobs for the same reason — RefreshState after
	/// every rebuild, RefreshValues only when something else is driving the number.
	/// </summary>
	private void RefreshScale()
	{
		if ( !_scaleStrip.IsValid() || _scaleSlot < 0 )
			return;

		if ( _studio is null || !_studio.MaterialNames.ContainsKey( _scaleSlot ) )
		{
			_scaleSlot = -1;
			_scaleStrip.Visible = false;

			return;
		}

		// The face menu can resize the slot this strip is pointed at, and a rebuild is all the
		// notice the panel gets. So the numbers are re-read after all — just never into a box
		// somebody is typing in, which is the case the split exists for.
		PullScale();
	}

	/// <summary>Put the studio's number into the fields, unless they are being typed in.</summary>
	private void PullScale()
	{
		if ( !_scaleStrip.IsValid() || _scaleSlot < 0 )
			return;

		if ( _scaleU.IsEditing || _scaleV.IsEditing )
			return;

		var scale = MaterialScale.ScaleFor( _studio, _scaleSlot );

		// SetValue, not the text, so this cannot echo back out as an edit and drive the two in a
		// loop.
		_scaleU.SetValue( scale.x );
		_scaleV.SetValue( scale.y );
	}

	private void PushScale()
	{
		if ( _scaleSlot >= 0 )
			ScaleChanged?.Invoke( _scaleSlot, new Vec2( _scaleU.Value, _scaleV.Value ) );
	}

	private void Nudge( float factor )
	{
		if ( _scaleSlot < 0 )
			return;

		ScaleChanged?.Invoke( _scaleSlot, MaterialScale.ScaleFor( _studio, _scaleSlot ) * factor );

		// After the invoke, because it rebuilds synchronously and the number the buttons produce is
		// one nobody typed — the fields have no other way to hear about it.
		PullScale();
	}

	/// <summary>
	/// Bring the panel up to date with the studio.
	///
	/// This runs on EVERY rebuild, which includes every tick of a dragged parameter, so it must stay
	/// cheap — and it is: what the project CONTAINS cannot change on a rebuild, so nothing here
	/// touches the list's items or re-walks a folder. All that moves is which slot each material is
	/// bound to, which is a dictionary of a handful of entries and a line of footer text.
	/// </summary>
	public void Refresh()
	{
		MapSlots();
		ShowSummary();
		RefreshScale();

		// Repaint, because the badges are drawn from _slots and nothing else would ask for a frame.
		_list?.Update();
	}

	// --- where we are standing -------------------------------------------------------------------

	/// <summary>Walk into a folder, or back out to one. The path bar and the grid are both just
	/// views of <see cref="_folder"/>, so this is the only thing navigation changes.</summary>
	private void GoTo( string folder )
	{
		_folder = folder ?? "";

		// The search box is scoped to where you are standing, so carrying a query through a
		// navigation would land you in a folder showing a filtered subset of it with no sign of why.
		_search.Text = "";

		_pathBar.SetPath( _folder, RootLabel );
		Populate();
	}

	private string RootLabel => _projectOnly ? "Project" : "All";

	private void ToggleScope()
	{
		_projectOnly = !_projectOnly;
		Rescan( keepFolder: true );
	}

	/// <summary>
	/// Where a fresh panel opens: `materials`, when there is one.
	///
	/// Not the root. Every material this project owns is under `materials/`, so opening at the root
	/// means the dock's first screen is one folder cell and nothing else — a click you would make
	/// every single time. The path bar makes going up obvious, so nothing is hidden by starting one
	/// level in.
	/// </summary>
	private string DefaultFolder() =>
		_all.Any( e => Relative( e ).StartsWith( "materials/", StringComparison.OrdinalIgnoreCase ) )
			? "materials"
			: "";

	// --- the listing ------------------------------------------------------------------------------

	/// <summary>One folder in the grid: where it is, what to call it, and how many materials are
	/// under it including its own subfolders. The count is the thing that makes a folder cell worth
	/// clicking rather than a guess.</summary>
	private sealed class FolderEntry
	{
		public string Path;
		public string Name;
		public int Count;
	}

	/// <summary>An asset's path, always with forward slashes, so every prefix test below compares
	/// like with like.</summary>
	private static string Relative( AssetEntry entry ) =>
		entry.Asset?.RelativePath?.Replace( '\\', '/' ) ?? "";

	/// <summary>An asset's path relative to the folder we are standing in, or null when it is not
	/// under it at all. The whole folder view is this one function plus a look for the next slash.
	/// </summary>
	private string Within( AssetEntry entry )
	{
		var path = Relative( entry );

		if ( _folder.Length == 0 )
			return path;

		return path.StartsWith( _folder + "/", StringComparison.OrdinalIgnoreCase )
			? path[(_folder.Length + 1)..]
			: null;
	}

	/// <summary>
	/// Fill the grid: folders first, then the materials in this folder.
	///
	/// Runs on navigation and on a keystroke, not per frame, so scanning the scope's whole list is
	/// the right shape — a stored tree would have to be rebuilt on every asset import and would be
	/// one more thing to keep true.
	/// </summary>
	private void Populate()
	{
		var query = _search.Value?.Trim();
		var here = new List<AssetEntry>();
		var folders = new Dictionary<string, FolderEntry>( StringComparer.OrdinalIgnoreCase );

		foreach ( var entry in _all )
		{
			if ( Within( entry ) is not { } rest )
				continue;

			// SEARCH IS RECURSIVE, and matched against the rest of the path rather than the file
			// name: folders are what carry the meaning here, so typing "elite" has to find the
			// twelve materials in the elite folder even though not one of them is called that.
			if ( !string.IsNullOrEmpty( query ) )
			{
				if ( rest.Contains( query, StringComparison.OrdinalIgnoreCase ) )
					here.Add( entry );

				continue;
			}

			var cut = rest.IndexOf( '/' );

			if ( cut < 0 )
			{
				here.Add( entry );
				continue;
			}

			var name = rest[..cut];

			if ( !folders.TryGetValue( name, out var folder ) )
			{
				folder = new FolderEntry
				{
					Name = name,
					Path = _folder.Length == 0 ? name : $"{_folder}/{name}",
				};

				folders[name] = folder;
			}

			folder.Count++;
		}

		var items = folders.Values
			.OrderBy( f => f.Name, StringComparer.OrdinalIgnoreCase )
			.Cast<object>()
			.Concat( here );

		_list.SetItems( items );
		// Back to the top: walking into a folder while scrolled halfway down the last one lands you
		// in the middle of the new listing with no sign that there is anything above.
		_list.ScrollTo( 0f, 0f );

		ShowSummary( folders.Count, here.Count, !string.IsNullOrEmpty( query ) );
	}

	/// <summary>Walk the asset system again — a dock opening, an import, a scope change.</summary>
	private void Rescan() => Rescan( keepFolder: true );

	private void Rescan( bool keepFolder )
	{
		// Resolved once rather than per asset: Project.Current walks up to a config file.
		var root = _projectOnly ? Project.Current?.GetAssetsPath()?.Replace( '\\', '/' ) : null;

		_all = AssetSystem.All
			.Where( a => a is not null && a.AssetType == AssetType.Material && InScope( a, root ) )
			.OrderBy( a => a.RelativePath, StringComparer.OrdinalIgnoreCase )
			.Select( a => new AssetEntry( a ) )
			.ToList();

		_byPath.Clear();

		foreach ( var entry in _all )
		{
			// First wins, so the index agrees with the ordering above rather than with whichever
			// duplicate the asset system happened to hand over last.
			if ( MaterialDrop.Normalise( Relative( entry ) ) is { } key )
				_byPath.TryAdd( key, entry );
		}

		MapSlots();

		// A folder that exists in one scope need not exist in the other, and standing in one that
		// has gone would show an empty grid with a path bar insisting you are somewhere.
		if ( !keepFolder || (_folder.Length > 0 && !_all.Any( e => Within( e ) is not null )) )
			_folder = DefaultFolder();

		// The ICON carries the state, not just the tooltip - a button that looks identical in both
		// modes makes "why can I not find that material" a question with no answer on screen. Both are
		// classic Material Icons names: s&box ships MaterialIcons-Regular.ttf, and a Material *Symbols*
		// name renders as nothing at all.
		_scopeButton.Icon = _projectOnly ? "home" : "public";

		_scopeButton.ToolTip = _projectOnly
			? $"This project's materials only ({_all.Count}) — click for everything the editor can see"
			: $"Every material the editor can see ({_all.Count}) — click for this project's alone";

		GoTo( _folder );
	}

	/// <summary>Whether an asset counts as the project's own. By where it lives on disk, because
	/// that is what "mine" means here — mounted games and engine content are elsewhere.</summary>
	private static bool InScope( Asset asset, string root )
	{
		if ( root is null )
			return true;

		return asset.AbsolutePath?.Replace( '\\', '/' )
			.StartsWith( root, StringComparison.OrdinalIgnoreCase ) ?? false;
	}

	// --- slots ------------------------------------------------------------------------------------

	/// <summary>
	/// Work out which materials the document has bound to a slot.
	///
	/// Walks the SLOTS and looks each one up, not the materials asking each one which slot it is on.
	/// The two give the same answer and cost wildly different amounts: there are a handful of named
	/// slots and over a thousand materials, and this runs on every rebuild — which is every tick of
	/// a dragged parameter.
	///
	/// Matched through MaterialDrop.Normalise rather than by comparing strings directly, because a
	/// slot named with backslashes and an asset path with forward ones are the same material. That
	/// rule lives in the kernel and is used from there rather than restated here: a second copy
	/// would agree with the first until one of them learned something.
	///
	/// Lowest slot wins if a document names two with the same material, matching SlotCarrying — the
	/// badge must show the slot a drop would actually reuse.
	/// </summary>
	private void MapSlots()
	{
		_slots.Clear();

		if ( _studio is null )
			return;

		foreach ( var (slot, name) in _studio.MaterialNames.OrderBy( kv => kv.Key ) )
		{
			if ( MaterialDrop.Normalise( name ) is not { } key )
				continue;

			if ( _byPath.TryGetValue( key, out var entry ) && !_slots.ContainsKey( entry ) )
				_slots[entry] = slot;
		}
	}

	/// <summary>What a slot carries, or null. Deliberately not NameForSlot, which substitutes
	/// material_N for an unbound slot and so has no empty answer — and "is anything bound here" is
	/// the question with one.</summary>
	private string SlotMaterial( int slot ) =>
		_studio is not null && _studio.MaterialNames.TryGetValue( slot, out var name )
			&& !string.IsNullOrWhiteSpace( name )
			? name
			: null;

	// --- what the mouse does ----------------------------------------------------------------------

	/// <summary>
	/// Start the drag. This is the feature.
	///
	/// Data.Text is the RELATIVE path, and it has to be: it is what the editor's own asset list puts
	/// there, so anything in the editor that already accepts a dragged material accepts one from
	/// this dock too, and it is what EffigyMaterialSlot stores when you pick through browse — two
	/// routes to the same slot must not write two different spellings of the same asset.
	///
	/// Data.Url is the absolute path as a file:// URI, again matching the asset list. Some drop
	/// targets read one, some the other, and a drag that fills in only half of it works everywhere
	/// you tested and nowhere else.
	/// </summary>
	private bool BeginDrag( object item )
	{
		// Folders are not draggable. Dropping one on a face has no meaning, and a drag that starts
		// and does nothing reads as the dock being broken rather than as the gesture being wrong.
		if ( item is not AssetEntry entry || entry.Asset is null )
			return false;

		var drag = new Drag( this );

		drag.Data.Text = entry.Asset.RelativePath;
		drag.Data.Url = new Uri( "file:///" + entry.Asset.AbsolutePath );
		drag.Execute();

		return true;
	}

	/// <summary>Double-click: walk into a folder, or give the whole part a material. Reported rather
	/// than acted on for the material case — this dock does not own the studio.</summary>
	private void Activate( object item )
	{
		if ( item is FolderEntry folder )
		{
			GoTo( folder.Path );
			return;
		}

		if ( item is AssetEntry entry && entry.Asset is { } asset )
			MaterialActivated?.Invoke( asset.RelativePath );
	}

	/// <summary>
	/// Right-click a material: bind it to a slot by hand, or take it off the one it is on.
	///
	/// This is where the old eight rows went. Everything they could do is here — put a material on
	/// slot 5 without touching any geometry, take it off again — but reached from the material,
	/// which is the thing you have in mind, rather than from a number you do not. It is also the
	/// only route to a slot above seven, for a document that arrived using one.
	/// </summary>
	private void OpenCellMenu( AssetEntry entry )
	{
		if ( entry?.Asset is not { } asset || _studio is null )
			return;

		var menu = new Menu( this );
		var path = asset.RelativePath;
		var current = _slots.TryGetValue( entry, out var bound ) ? bound : -1;

		menu.AddHeading( Path.GetFileName( path ) );

		var whole = menu.AddOption( "Use for the whole part", "format_paint", () => MaterialActivated?.Invoke( path ) );
		whole.StatusTip = "Slot 0 — every face nobody has painted";
		whole.Checkable = true;
		whole.Checked = current == 0;

		var slots = menu.AddMenu( "Bind to slot", "layers" );

		foreach ( var slot in BindableSlots( current ) )
		{
			var option = slots.AddOption( _studio.NameForSlot( slot ), null, () => MaterialChanged?.Invoke( slot, path ) );

			option.Checkable = true;
			option.Checked = slot == current;
		}

		if ( current >= 0 )
		{
			var clear = menu.AddOption( $"Unbind from slot {current}", "backspace",
				() => MaterialChanged?.Invoke( current, null ) );

			clear.StatusTip = $"Back to the default name — exports as {ObjWriter.DefaultMaterialName( current )}";
		}

		menu.OpenAtCursor();
	}

	/// <summary>
	/// Which slots the bind menu offers: zero through seven, plus anything the document already
	/// uses, plus whichever one this material is on.
	///
	/// Seven is not arbitrary — it is how many colours the viewport tints slots with, so every slot
	/// offered is one you can tell apart on screen. The kernel allows 0..63 and nobody picks slot 40
	/// off a list, but a document that arrived with one must not be unreachable.
	/// </summary>
	private IEnumerable<int> BindableSlots( int current )
	{
		var slots = new SortedSet<int>();

		for ( var i = 0; i <= 7; i++ )
			slots.Add( i );

		foreach ( var slot in FaceMaterialEdit.UsedSlots( _studio ) )
			slots.Add( slot );

		if ( current >= 0 )
			slots.Add( current );

		return slots;
	}

	// --- the footer -------------------------------------------------------------------------------

	/// <summary>
	/// The material the list has highlighted, or null when the selection is a folder or nothing.
	///
	/// EXPOSED FOR THE MATERIAL BRUSH, which has no picker of its own on purpose: this panel is
	/// already open in the Paint workspace and already the place materials are chosen, so a second
	/// control naming one would be two answers to the same question. Click here, brush there.
	/// </summary>
	public string SelectedMaterial { get; private set; }

	/// <summary>Raised when <see cref="SelectedMaterial"/> changes, so a brush already armed can
	/// pick up the new material without being re-entered.</summary>
	public Action SelectedMaterialChanged { get; set; }

	private void ShowItem( object item )
	{
		ShowScaleFor( item );

		var chosen = (item as AssetEntry)?.Asset?.RelativePath;

		if ( chosen != SelectedMaterial )
		{
			SelectedMaterial = chosen;
			SelectedMaterialChanged?.Invoke();
		}

		if ( !_status.IsValid() )
			return;

		if ( item is FolderEntry folder )
			_status.Text = $"{folder.Path} — {folder.Count} material{(folder.Count == 1 ? "" : "s")}";
		else if ( item is AssetEntry entry && entry.Asset is { } asset )
			_status.Text = asset.RelativePath;
	}

	private void ShowSummary() => ShowSummary( -1, -1, false );

	/// <summary>
	/// The footer: what is in front of you, and what the part is still missing.
	///
	/// The second half is the one thing the old row list was genuinely good at — an unbound slot was
	/// visible as a gap rather than discovered on export. It does NOT survive as a bound-over-total
	/// ratio, which was the obvious translation and a useless one: the total would be the slots the
	/// document has an opinion about, and naming a slot is what gives it one, so the two numbers
	/// would be equal almost always and would read as "everything is fine" while a slot the geometry
	/// paints sat unnamed.
	///
	/// The number that means something is the count of slots a FaceMaterialFeature paints that
	/// nobody has bound a material to. Those export as `material_4` and are exactly the thing you
	/// find out about too late. Which ones they are is answered by the badges: a painted slot with
	/// no material has no badge anywhere in the grid.
	/// </summary>
	private void ShowSummary( int folders, int materials, bool searching )
	{
		if ( !_status.IsValid() )
			return;

		if ( _all.Count == 0 )
		{
			_status.Text = _projectOnly
				? "No materials in this project — click the box to see them all"
				: "No materials found";

			return;
		}

		// -1 means "this is a Refresh, not a Populate" — the studio changed and the listing did not,
		// so the counts already on screen are still right and only the slot half needs redoing.
		var listing = folders < 0
			? _status.Text?.Split( '·' ).FirstOrDefault()?.Trim()
			: searching
				? $"{materials} match{(materials == 1 ? "" : "es")}"
				: Describe( folders, materials );

		var bound = _studio?.MaterialNames.Count( kv => !string.IsNullOrWhiteSpace( kv.Value ) ) ?? 0;

		if ( bound == 0 )
		{
			_status.Text = listing;
			return;
		}

		var unnamed = FaceMaterialEdit.UsedSlots( _studio )
			.Count( slot => string.IsNullOrWhiteSpace( SlotMaterial( slot ) ) );

		_status.Text = unnamed == 0
			? $"{listing} · {bound} bound"
			: $"{listing} · {bound} bound, {unnamed} slot{(unnamed == 1 ? "" : "s")} unnamed";
	}

	private static string Describe( int folders, int materials )
	{
		if ( folders == 0 && materials == 0 )
			return "Empty folder";

		var parts = new List<string>( 2 );

		if ( folders > 0 )
			parts.Add( $"{folders} folder{(folders == 1 ? "" : "s")}" );

		if ( materials > 0 )
			parts.Add( $"{materials} material{(materials == 1 ? "" : "s")}" );

		return string.Join( ", ", parts );
	}

	// --- painting ---------------------------------------------------------------------------------

	private void PaintCell( VirtualWidget item )
	{
		var rect = item.Rect.Shrink( 2 );

		if ( Paint.HasSelected || Paint.HasPressed )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.Blue.Darken( 0.4f ) );
			Paint.DrawRect( rect, Theme.ControlRadius );
		}
		else if ( Paint.HasMouseOver )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.SurfaceLightBackground.WithAlpha( 0.4f ) );
			Paint.DrawRect( rect, Theme.ControlRadius );
		}

		var icon = rect.Shrink( 4 );
		icon.Height = icon.Width;

		var text = rect.Shrink( 4, 0 );
		text.Top = icon.Bottom + 2;

		if ( item.Object is FolderEntry folder )
		{
			PaintFolder( icon, text, folder );
			return;
		}

		if ( item.Object is not AssetEntry entry )
			return;

		Paint.BilinearFiltering = true;
		entry.DrawIcon( icon );
		Paint.BilinearFiltering = false;

		Paint.SetDefaultFont( 7 );
		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( 0.8f ) );

		var name = Path.GetFileNameWithoutExtension( entry.Name );

		Paint.DrawText( text, Paint.GetElidedText( name, text.Width, ElideMode.Middle ), TextFlag.LeftTop );

		if ( _slots.TryGetValue( entry, out var slot ) )
			PaintSlotBadge( icon, slot );
	}

	/// <summary>A folder cell: the icon, the name, and the count that tells you whether walking in
	/// is worth it. Drawn plainly rather than with a thumbnail because there is nothing to render —
	/// and a folder that looked like a material would be dragged onto a face.</summary>
	private static void PaintFolder( Rect icon, Rect text, FolderEntry folder )
	{
		Paint.ClearBrush();
		Paint.SetPen( Theme.Yellow.WithAlpha( 0.75f ) );
		Paint.DrawIcon( icon, "folder", icon.Height * 0.6f, TextFlag.Center );

		Paint.SetDefaultFont( 7 );
		Paint.SetPen( Theme.Text.WithAlpha( 0.9f ) );
		Paint.DrawText( text, Paint.GetElidedText( folder.Name, text.Width, ElideMode.Middle ), TextFlag.LeftTop );

		Paint.SetDefaultFont( 6 );
		Paint.SetPen( Theme.TextLight.WithAlpha( 0.5f ) );
		Paint.DrawText( icon.Shrink( 2 ), folder.Count.ToString(), TextFlag.RightBottom );
	}

	/// <summary>
	/// The slot number, in the slot's own viewport colour.
	///
	/// The COLOUR is the point, more than the number: the viewport shades painted faces with a
	/// per-slot palette, so a badge in the matching colour is what connects the green patch on the
	/// model to the material that put it there. Slot 0 gets the neutral treatment because the
	/// viewport pointedly does not tint it — it is the part's base, not a painted patch.
	/// </summary>
	private static void PaintSlotBadge( Rect icon, int slot )
	{
		var badge = new Rect( icon.Right - 20, icon.Top + 2, 18, 14 );

		Paint.ClearPen();
		Paint.SetBrush( slot == 0 ? Theme.ControlBackground : EffigyViewport.SlotColor( slot ) );
		Paint.DrawRect( badge, 3 );

		Paint.SetDefaultFont( 6 );
		Paint.ClearBrush();
		Paint.SetPen( slot == 0 ? Theme.Text.WithAlpha( 0.8f ) : Color.Black.WithAlpha( 0.85f ) );
		Paint.DrawText( badge, slot.ToString(), TextFlag.Center );
	}

	/// <summary>
	/// A material was added, deleted or reimported somewhere else in the editor.
	///
	/// Worth listening for rather than leaving to the reload button: the ordinary way to get a
	/// material into an Effigy part is to make one in the material editor and then come here for it,
	/// and a browser that cannot see the material you just made is a browser you stop trusting.
	/// </summary>
	void AssetSystem.IEventListener.OnAssetSystemChanges() => Rescan();
}

/// <summary>
/// The path bar above the material grid — an up arrow, then the folders you are standing inside,
/// each one clickable to go back to it.
///
/// HAND-PAINTED, WITH NO CHILD WIDGETS, and that is the whole design. The obvious build is a row of
/// small buttons rebuilt on every navigation, and it has a trap in it: the click handler navigates,
/// navigating rebuilds the row, and the row is rebuilt from inside the Clicked callback of one of
/// the buttons being deleted. That is the same hazard the old Materials panel documented about its
/// × button. One widget that paints text and hit-tests it cannot delete anything, so the callback is
/// free to repopulate whatever it likes.
/// </summary>
internal sealed class EffigyPathBar : Widget
{
	/// <summary>Where to go. The empty string is the root.</summary>
	public Action<string> Navigated { get; set; }

	private string _folder = "";
	private string _root = "All";

	/// <summary>Segment rects and where each one leads, filled in during paint and read on click.
	/// Measuring text needs a paint scope, and a bar nobody has drawn is a bar nobody can click.
	/// </summary>
	private readonly List<(Rect Rect, string Target)> _hits = new();

	/// <summary>Where the cursor is, in this widget's own coordinates, or off it. Tracked from the
	/// move events rather than read from Application at paint time, because the ambient cursor is a
	/// SCREEN position and converting it back is a round trip to answer a question the event we
	/// already got had the answer to.</summary>
	private Vector2 _cursor = new( -1f, -1f );

	private const float Height_ = 22f;
	private const float UpWidth = 20f;

	public EffigyPathBar( Widget parent ) : base( parent )
	{
		// Same pair every hand-painted widget in this tool sets: a plain Widget paints the system
		// background, which here is a band across the dock.
		TranslucentBackground = true;
		NoSystemBackground = true;
		MouseTracking = true;

		Cursor = CursorShape.Finger;
		FixedHeight = Height_;
	}

	public void SetPath( string folder, string root )
	{
		_folder = folder ?? "";
		_root = root;

		Update();
	}

	/// <summary>The folder one level up, or null at the root.</summary>
	private string ParentFolder()
	{
		if ( _folder.Length == 0 )
			return null;

		var cut = _folder.LastIndexOf( '/' );

		return cut < 0 ? "" : _folder[..cut];
	}

	protected override void OnPaint()
	{
		_hits.Clear();

		var up = ParentFolder();

		Paint.SetPen( Theme.TextLight.WithAlpha( up is null ? 0.25f : 0.8f ) );
		Paint.ClearBrush();
		Paint.DrawIcon( new Rect( 4f, 0f, UpWidth, Height ), "arrow_upward", 14, TextFlag.Center );

		if ( up is not null )
			_hits.Add( (new Rect( 4f, 0f, UpWidth, Height ), up) );

		var x = 4f + UpWidth + 4f;

		x = DrawSegment( x, _root, "", _folder.Length == 0 );

		if ( _folder.Length == 0 )
			return;

		var walked = "";

		foreach ( var segment in _folder.Split( '/' ) )
		{
			walked = walked.Length == 0 ? segment : $"{walked}/{segment}";

			Paint.SetDefaultFont( 7 );
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.35f ) );

			var caret = new Rect( x, 0f, 10f, Height );
			Paint.DrawText( caret, "›", TextFlag.Center );

			x = DrawSegment( x + 10f, segment, walked, walked == _folder );
		}
	}

	/// <summary>One clickable name. Returns where the next one starts.</summary>
	private float DrawSegment( float x, string text, string target, bool last )
	{
		Paint.SetDefaultFont( 7, last ? 600 : 400 );

		var width = Paint.MeasureText( text ).x + 8f;
		var rect = new Rect( x, 2f, width, Height - 4f );

		var hovered = rect.IsInside( _cursor );

		if ( hovered && !last )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.SurfaceLightBackground.WithAlpha( 0.4f ) );
			Paint.DrawRect( rect, 3f );
		}

		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( last ? 0.95f : 0.65f ) );
		Paint.DrawText( rect, text, TextFlag.Center );

		// The last segment is where you already are, so it is not offered as somewhere to go.
		if ( !last )
			_hits.Add( (rect, target) );

		return x + width;
	}

	/// <summary>Taking the press is what guarantees the release arrives here rather than at whatever
	/// is underneath — the same reason every other painted control in this tool accepts it.</summary>
	protected override void OnMousePress( MouseEvent e )
	{
		if ( e.LeftMouseButton )
			e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( !e.LeftMouseButton )
			return;

		foreach ( var (rect, target) in _hits )
		{
			if ( !rect.IsInside( e.LocalPosition ) )
				continue;

			e.Accepted = true;
			Navigated?.Invoke( target );

			return;
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		_cursor = e.LocalPosition;
		Update();
	}

	protected override void OnMouseLeave()
	{
		_cursor = new Vector2( -1f, -1f );
		Update();
	}
}
