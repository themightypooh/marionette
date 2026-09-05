using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;

namespace Marionette.EditorTools;

/// <summary>
/// Idle geometry selection — click a face or a part, then use a tool on it.
///
/// Separate from FacePickMode / BodyPickMode. Those are a dialog asking a question; this is the
/// selection that exists when nobody is asking. Onshape works the same way: you point at a face,
/// it lights up, you click it, then Draft/Hole/Sketch consume what you already picked instead of
/// making you pick it again inside a dialog.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>Faces currently selected while idle. Empty when the last click was a whole part
	/// in the Parts list, or nothing.</summary>
	public IReadOnlyList<FaceRef> IdleFaces => _idleFaces;

	/// <summary>Edges currently selected while idle. Empty unless the last click landed near a
	/// rim rather than in the middle of a face.</summary>
	public IReadOnlyList<EdgeRef> IdleEdges => _idleEdges;

	/// <summary>Bodies currently selected while idle. A face click names its owning body so the
	/// Parts list can highlight the same row; a Parts-list click names the body and no faces.
	/// </summary>
	public IReadOnlyList<string> IdleBodyIds => _idleBodyIds;

	/// <summary>Committed sketch currently selected while idle, or null. Extrude/Revolve read
	/// this instead of waiting for a second click.</summary>
	public string IdleSketchFeatureId => _idleSketchFeatureId;

	/// <summary>Region seeds on <see cref="IdleSketchFeatureId"/>. Empty means the whole sketch.
	/// </summary>
	public IReadOnlyList<Vec2> IdleRegionSeeds => _idleRegionSeeds;

	public bool HasIdleSelection =>
		_idleFaces.Count > 0 || _idleEdges.Count > 0 || _idleBodyIds.Count > 0
		|| _idleSketchFeatureId is not null;

	/// <summary>Raised after a click in the viewport changes the idle selection, so the Parts
	/// list can stay in step. Not raised when the list itself drove the change.</summary>
	public Action IdleSelectionChanged { get; set; }

	private readonly List<FaceRef> _idleFaces = new();
	private readonly List<EdgeRef> _idleEdges = new();
	private readonly List<string> _idleBodyIds = new();
	private string _idleSketchFeatureId;
	private readonly List<Vec2> _idleRegionSeeds = new();

	/// <summary>How close a click must be to a rim, in screen pixels, before it counts as an
	/// edge rather than the face. Wide enough to grab, tight enough that a click in the middle
	/// of a face stays a face.</summary>
	private const float EdgePickPixels = 10f;

	/// <summary>Whether a left click on a face is a selection rather than an answer to a dialog.
	/// Anything else with a click of its own owns the mouse while it is armed.</summary>
	private bool IdlePickingAllowed =>
		!IsSketching && !IsSculpting && !IsPainting && !IsMaterialBrushing && !IsNoting
		&& !PlanePickMode && !SketchPickMode && !FacePickMode && !EdgePickMode && !BodyPickMode
		&& !BoneToolActive && !RigMode
		&& !_draggingOrigin && !_draggingLight && !_draggingFace;

	/// <summary>
	/// Replace the idle selection with whole bodies, clearing any face picks.
	///
	/// This is what the Parts list does: clicking a row selects that part, not one of its faces.
	/// Fillet, shell, transform then act on the part; Sketch/Draft still need a face, so they
	/// open asking for one.
	/// </summary>
	public void SelectBodies( IReadOnlyList<string> bodyIds )
	{
		_idleFaces.Clear();
		_idleEdges.Clear();
		_idleBodyIds.Clear();
		ClearIdleSketch();

		if ( bodyIds is not null )
		{
			foreach ( var id in bodyIds )
			{
				if ( !string.IsNullOrEmpty( id ) && !_idleBodyIds.Contains( id ) )
					_idleBodyIds.Add( id );
			}
		}

		IdleSelectionChanged?.Invoke();
	}

	public void ClearIdleSelection()
	{
		if ( !HasIdleSelection )
			return;

		_idleFaces.Clear();
		_idleEdges.Clear();
		_idleBodyIds.Clear();
		ClearIdleSketch();
		IdleSelectionChanged?.Invoke();
	}

	void ClearIdleSketch()
	{
		_idleSketchFeatureId = null;
		_idleRegionSeeds.Clear();
	}

	/// <summary>Select a committed sketch, optionally a single closed region of it.</summary>
	public void SelectIdleSketch( string featureId, Vec2? seed, bool add = false )
	{
		if ( string.IsNullOrEmpty( featureId ) )
		{
			if ( _idleSketchFeatureId is null )
				return;

			ClearIdleSketch();
			IdleSelectionChanged?.Invoke();
			return;
		}

		if ( add && _idleSketchFeatureId == featureId && seed is { } extra )
		{
			for ( var i = 0; i < _idleRegionSeeds.Count; i++ )
			{
				var existing = _idleRegionSeeds[i];

				if ( MathF.Abs( existing.x - extra.x ) > 1e-4f || MathF.Abs( existing.y - extra.y ) > 1e-4f )
					continue;

				_idleRegionSeeds.RemoveAt( i );

				if ( _idleRegionSeeds.Count == 0 )
					_idleSketchFeatureId = null;

				IdleSelectionChanged?.Invoke();
				return;
			}

			_idleRegionSeeds.Add( extra );
			IdleSelectionChanged?.Invoke();
			return;
		}

		_idleFaces.Clear();
		_idleEdges.Clear();
		_idleBodyIds.Clear();
		_idleSketchFeatureId = featureId;
		_idleRegionSeeds.Clear();

		if ( seed is { } one )
			_idleRegionSeeds.Add( one );

		IdleSelectionChanged?.Invoke();
	}

	/// <summary>Drop faces and parts whose bodies no longer exist after a rebuild. Called from
	/// SetDisplayBodies so a deleted part cannot stay selected against nothing.</summary>
	private void PruneIdleSelection()
	{
		if ( !HasIdleSelection )
			return;

		var ids = new HashSet<string>();

		foreach ( var body in _displayBodies )
		{
			if ( body?.Id is { } id )
				ids.Add( id );
		}

		var changed = _idleFaces.RemoveAll( f => !ids.Contains( f.BodyId ) ) > 0
			| _idleEdges.RemoveAll( e => !ids.Contains( e.BodyId ) ) > 0
			| _idleBodyIds.RemoveAll( id => !ids.Contains( id ) ) > 0;

		if ( _idleSketchFeatureId is not null )
		{
			var found = false;

			foreach ( var pickable in _pickableSketches )
			{
				if ( pickable.FeatureId == _idleSketchFeatureId )
				{
					found = true;
					break;
				}
			}

			if ( !found )
			{
				ClearIdleSketch();
				changed = true;
			}
		}

		if ( changed )
			IdleSelectionChanged?.Invoke();
	}

	/// <summary>
	/// Hover the face under the cursor, keep already-picked faces and parts lit, and take a click
	/// as a selection when nothing else owns the mouse.
	///
	/// Runs after the origin handle so a click on the origin is not also a click on the face that
	/// happens to sit behind it. Gizmo.HasHovered covers the origin, the plane-corner handles and
	/// anything else that registered a hitbox this frame.
	/// </summary>
	private void IdleSelectionFrame()
	{
		DrawIdleSelection();

		if ( !IdlePickingAllowed )
			return;

		if ( !_canvasHasCursor )
			return;

		if ( TryResolveSketchHover( out var sketchId, out var sketchSeed, out var sketchDistance )
			&& SketchBeatsFace( sketchDistance ) )
		{
			_hoveredFaceBodyId = sketchId;
			DrawSketchPickHighlight( sketchId, sketchSeed );

			if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered )
			{
				if ( OriginSelected )
					DeselectOrigin();

				SelectIdleSketch( sketchId, sketchSeed, Gizmo.IsShiftPressed );
			}

			return;
		}

		if ( TryPickFaceUnderCursor( out var hit ) )
		{
			_hoveredFaceBodyId = hit.Body.Id;

			if ( TryIdleEdge( hit, out var edge, out var key ) )
			{
				DrawEdge( hit.Body, key, FaceHighlightColor );

				if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered )
				{
					if ( OriginSelected )
						DeselectOrigin();

					SelectIdleEdge( hit.Body, edge, Gizmo.IsShiftPressed );
				}

				return;
			}

			DrawHoveredFace( hit.Body, hit.FaceIndex );

			if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered )
			{
				if ( OriginSelected )
					DeselectOrigin();

				SelectIdleFace( hit, Gizmo.IsShiftPressed );
			}

			return;
		}

		if ( Gizmo.WasLeftMousePressed && !Gizmo.HasHovered && !Gizmo.IsHovered )
			ClearIdleSelection();
	}

	/// <summary>
	/// Chosen idle geometry, in the same amber the dialog uses for a committed pick.
	///
	/// Skipped when a dialog is already drawing its own SelectedFaces / SelectedBodyIds, so the
	/// two never paint the same face twice in slightly different states.
	/// </summary>
	/// <summary>A sketch on a face sits at the same depth as that face. Prefer the sketch when
	/// they tie; prefer a solid that is genuinely in front of the sketch plane.</summary>
	private bool SketchBeatsFace( float sketchDistance )
	{
		if ( !TryPickFaceUnderCursor( out var hit ) )
			return true;

		var faceDistance = (hit.Reference.Point - _cursorRayOrigin).Length;

		return sketchDistance <= faceDistance + 0.05f;
	}

	private void DrawIdleSelection()
	{
		if ( (SelectedSketchFeatureId is null || SelectedSketchFeatureId.Length == 0)
			&& _idleSketchFeatureId is not null )
		{
			var seed = _idleRegionSeeds.Count == 1 ? _idleRegionSeeds[0] : (Vec2?)null;

			if ( _idleRegionSeeds.Count <= 1 )
				DrawSketchPickHighlight( _idleSketchFeatureId, seed );
			else
			{
				foreach ( var region in _idleRegionSeeds )
					DrawSketchPickHighlight( _idleSketchFeatureId, region );
			}
		}

		var drawFaces = SelectedFaces is null || SelectedFaces.Count == 0;
		var drawEdges = SelectedEdges is null || SelectedEdges.Count == 0;
		var drawBodies = SelectedBodyIds is null || SelectedBodyIds.Count == 0;

		if ( drawEdges && _idleEdges.Count > 0 )
		{
			foreach ( var edge in _idleEdges )
			{
				if ( FacePlane.TryResolveEdge( _displayBodies, edge, out var body, out var key ) )
					DrawEdge( body, key, FaceSelectedColor );
			}
		}

		if ( drawFaces && _idleFaces.Count > 0 )
		{
			foreach ( var face in _idleFaces )
			{
				if ( FacePlane.TryResolveFace( _displayBodies, face, out var body, out var index ) )
					DrawFace( body, index, FaceSelectedColor );
			}

			return;
		}

		if ( !drawBodies || _idleBodyIds.Count == 0 )
			return;

		foreach ( var body in _displayBodies )
		{
			if ( body?.Id is { } id && _idleBodyIds.Contains( id ) )
				DrawBodyHighlight( body, BodySelectedColor );
		}
	}

	/// <summary>
	/// Select one face outright, from something other than a click — the right-click menu, which
	/// has already resolved the face under the cursor and now wants a tool pointed at it.
	///
	/// Replaces the selection rather than adding to it: the face you just right-clicked is the face
	/// you meant, and quietly bundling it with whatever was lit a moment ago would hand the tool
	/// more than you asked for.
	/// </summary>
	public void SelectFace( EffigyFaceHit hit ) => SelectIdleFace( hit, add: false );

	/// <summary>Click a face: replace the selection, or Shift-click to toggle it in.</summary>
	private void SelectIdleFace( EffigyFaceHit hit, bool add )
	{
		var face = hit.Reference;

		if ( add )
		{
			for ( var i = 0; i < _idleFaces.Count; i++ )
			{
				if ( !SameIdleFace( _idleFaces[i], face ) )
					continue;

				_idleFaces.RemoveAt( i );
				RebuildIdleBodyIdsFromFaces();
				IdleSelectionChanged?.Invoke();
				return;
			}

			_idleEdges.Clear();
			ClearIdleSketch();
			_idleFaces.Add( face );

			if ( !_idleBodyIds.Contains( hit.Body.Id ) )
				_idleBodyIds.Add( hit.Body.Id );
		}
		else
		{
			_idleFaces.Clear();
			_idleEdges.Clear();
			_idleBodyIds.Clear();
			ClearIdleSketch();
			_idleFaces.Add( face );
			_idleBodyIds.Add( hit.Body.Id );
		}

		IdleSelectionChanged?.Invoke();
	}

	private void SelectIdleEdge( Body body, EdgeRef edge, bool add )
	{
		if ( add )
		{
			for ( var i = 0; i < _idleEdges.Count; i++ )
			{
				if ( !SameIdleEdge( _idleEdges[i], edge ) )
					continue;

				_idleEdges.RemoveAt( i );
				RebuildIdleBodyIdsFromFaces();
				IdleSelectionChanged?.Invoke();
				return;
			}

			_idleFaces.Clear();
			ClearIdleSketch();
			_idleEdges.Add( edge );

			if ( !_idleBodyIds.Contains( body.Id ) )
				_idleBodyIds.Add( body.Id );
		}
		else
		{
			_idleFaces.Clear();
			_idleEdges.Clear();
			_idleBodyIds.Clear();
			ClearIdleSketch();
			_idleEdges.Add( edge );
			_idleBodyIds.Add( body.Id );
		}

		IdleSelectionChanged?.Invoke();
	}

	private bool TryIdleEdge( EffigyFaceHit hit, out EdgeRef edge, out EdgeKey key )
	{
		edge = default;
		key = default;

		if ( !TryClosestEdge( hit.Body.Mesh, hit.FaceIndex, hit.Reference.Point, out key, out var distance ) )
			return false;

		var point = hit.Reference.Point;
		var threshold = WorldRadiusAt( new Vector3( point.x, point.y, point.z ), EdgePickPixels );

		if ( distance > threshold )
			return false;

		edge = FacePlane.Capture( hit.Body, key );
		return true;
	}

	private void RebuildIdleBodyIdsFromFaces()
	{
		_idleBodyIds.Clear();

		foreach ( var face in _idleFaces )
		{
			if ( !_idleBodyIds.Contains( face.BodyId ) )
				_idleBodyIds.Add( face.BodyId );
		}

		foreach ( var edge in _idleEdges )
		{
			if ( !_idleBodyIds.Contains( edge.BodyId ) )
				_idleBodyIds.Add( edge.BodyId );
		}
	}

	private bool SameIdleEdge( EdgeRef a, EdgeRef b )
	{
		if ( !FacePlane.TryResolveEdge( _displayBodies, a, out var bodyA, out var keyA )
			|| !FacePlane.TryResolveEdge( _displayBodies, b, out var bodyB, out var keyB ) )
			return false;

		return bodyA.Id == bodyB.Id && keyA.Equals( keyB );
	}

	/// <summary>Match by resolved face, not by stored FaceRef equality — two clicks on the same
	/// face produce two references with slightly different hit points.</summary>
	private bool SameIdleFace( FaceRef a, FaceRef b )
	{
		if ( !FacePlane.TryResolveFace( _displayBodies, a, out var bodyA, out var indexA )
			|| !FacePlane.TryResolveFace( _displayBodies, b, out var bodyB, out var indexB ) )
			return false;

		return bodyA.Id == bodyB.Id && indexA == indexB;
	}
}
