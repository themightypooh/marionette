using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One stroke's worth of change, sparse and symmetric, so undo and redo cost the vertices the
/// stroke touched rather than the whole level.
///
/// At L4 a level is ~128k vertices and a full snapshot per stroke is a megabyte and a half. A stroke
/// usually moves a few hundred, which is why <see cref="BrushUndo"/> records a working set rather
/// than a copy, and why this keeps that shape instead of flattening it into two meshes.
/// </summary>
public sealed class SculptEdit
{
	public readonly int Level;

	// Exactly one of these pairs is set. A stroke either moved the surface or painted the mask;
	// nothing in the tool does both, and a single type keeps one undo stack rather than two that
	// can be popped out of order.
	readonly Dictionary<int, Vec3> _before;
	readonly Dictionary<int, Vec3> _after;
	readonly Dictionary<int, float> _maskBefore;
	readonly Dictionary<int, float> _maskAfter;

	internal SculptEdit( int level, Dictionary<int, Vec3> before, Dictionary<int, Vec3> after )
	{
		Level = level;
		_before = before;
		_after = after;
	}

	internal SculptEdit( int level, Dictionary<int, float> before, Dictionary<int, float> after )
	{
		Level = level;
		_maskBefore = before;
		_maskAfter = after;
	}

	/// <summary>A whole level that was removed, held so it can be put back.</summary>
	internal SculptEdit( int level, SculptLayer removed )
	{
		Level = level;
		_removed = removed;
	}

	readonly SculptLayer _removed;

	/// <summary>Whether this edit removed a level rather than moving vertices or painting a mask.</summary>
	public bool IsLevel => _removed is not null;

	/// <summary>The layer this edit took off, for putting back.</summary>
	internal SculptLayer Level_ => _removed;

	/// <summary>Whether this stroke painted the mask rather than moving the surface.</summary>
	public bool IsMask => _maskBefore is not null;

	/// <summary>How many vertices this stroke actually changed.</summary>
	public int Count => _before?.Count ?? _maskBefore?.Count ?? _removed.Count;

	internal void Write( PolyMesh mesh, bool forward )
	{
		foreach ( var (vertex, position) in forward ? _after : _before )
			mesh.Positions[vertex] = position;
	}

	internal void Write( SculptMask mask, bool forward )
	{
		foreach ( var (vertex, value) in forward ? _maskAfter : _maskBefore )
			mask[vertex] = value;
	}
}

/// <summary>
/// The sculpt tool, with no cursor in it.
///
/// WHY THIS IS IN THE KERNEL. The editor cannot be compiled outside s&amp;box, so anything living there
/// is verified by reading it — and reading it is how a bug that made every parameter edit a silent
/// no-op survived long enough to look like three unrelated UI faults. Everything a sculpt tool does
/// between the pointer and the mesh is arithmetic: project a ray, decide whether the cursor has moved
/// far enough to deserve a sample, fill in the gap when it moved too far, apply, record once. All of
/// it is testable with no engine anywhere, and all of it is where the bugs are. What is left for the
/// s&amp;box half is genuinely thin: hand this rays, draw <see cref="DisplayMesh"/>, and draw a ring at
/// <see cref="Hover"/>.
///
/// THE STROKE WORKS ON A MESH, NOT ON THE LEVEL STACK. Evaluating the stack and recording deltas costs
/// the whole level, and doing it per sample would make a stroke O(samples x vertices) and the tool
/// unusable at L3. So a stroke evaluates once at <see cref="BeginStroke"/>, brushes a working copy
/// that the viewport draws live, and calls <see cref="MultiresSculpt.Record"/> exactly once at
/// <see cref="EndStroke"/>. One stroke is therefore one revision and one undo entry, which is also
/// what a user means by "undo".
/// </summary>
public sealed class SculptSession
{
	readonly MultiresSculpt _sculpt;

	// Live only between BeginStroke and EndStroke.
	PolyMesh _working;
	SculptFrames _frames;
	MeshBVH _bvh;
	BrushUndo _undo;
	Vec3 _lastSample;

	// The mask as the stroke found it. Diffed against the mask afterwards, so what goes on the undo
	// stack is only what the stroke actually touched — the same sparse promise the geometry side
	// makes, and worth keeping: a full mask array at L4 is half a megabyte per stroke.
	float[] _maskBefore;

	// What the viewport draws when no stroke is running, rebuilt when the sculpt says it changed.
	PolyMesh _display;
	int _displayRevision = -1;
	int _displayLevel = -1;
	int _displayMaskRevision = -1;
	bool _displayHidden;

	readonly Stack<SculptEdit> _done = new();
	readonly Stack<SculptEdit> _undone = new();

	// One mask per level, made on demand. Kept per level rather than one shared array because a
	// mask is per vertex and the levels have different vertex counts; dropping to L1 and back must
	// not silently reinterpret L3's mask as L1's.
	readonly Dictionary<int, SculptMask> _masks = new();

	public SculptSession( MultiresSculpt sculpt )
	{
		_sculpt = sculpt ?? throw new ArgumentNullException( nameof( sculpt ) );
	}

	public MultiresSculpt Sculpt => _sculpt;

	public BrushKind Brush = BrushKind.Draw;
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Brush radius in world units, not pixels — the kernel has no screen.</summary>
	public float Radius = 0.25f;

	/// <summary>
	/// A starting radius that suits this model: a twelfth of the cage's diagonal, which is a brush
	/// you can see and can still cross the part with in a few strokes.
	///
	/// Here rather than in the editor because a fixed default is wrong in both directions - Effigy's
	/// units are dimensionless, so the same number is the whole model on one part and invisible on
	/// the next - and because "what a sensible brush size is" is not a thing about widgets.
	/// </summary>
	public float SuggestedRadius
	{
		get
		{
			var diagonal = _sculpt.Cage.BoundsDiagonal;
			return diagonal > 1e-6f ? diagonal / 12f : 0.25f;
		}
	}

	public float Strength = 0.05f;

	/// <summary>Mirror every sample across X. The cheap symmetry that covers most of what it is for.</summary>
	public bool MirrorX;

	/// <summary>
	/// Strokes paint the mask instead of moving the surface.
	///
	/// A separate mode rather than a seventh brush, because it is not one: every BrushKind moves
	/// vertices and this one does not, and putting it in that enum would mean every switch over
	/// brushes in the kernel and the editor grew a case that does something categorically different.
	/// </summary>
	public bool Masking;

	/// <summary>While masking, release protection instead of adding it. One control, both ways.</summary>
	public bool Erasing;

	/// <summary>
	/// Draw the level with the fully masked parts dropped, so you can get at what is behind them.
	///
	/// A VIEW, like ViewLevel, and it reaches the model exactly as far as that one does: nowhere. The
	/// feature still builds the whole surface. Hiding half a head to sculpt the inside of it must not
	/// export a head with half of it missing.
	///
	/// Mid-stroke the working mesh is drawn whole regardless — the brush is acting on all of it, and
	/// a mesh that changed shape the instant you pressed the mouse would read as the tool breaking.
	/// </summary>
	public bool HideMasked;

	/// <summary>
	/// How far the cursor must travel before it earns another sample, as a fraction of the radius.
	///
	/// A pointer produces events far faster than a brush needs them; without this, holding still
	/// would pile hundreds of samples on one spot and a slow drag would bite far harder than a quick
	/// one for the same gesture. A quarter of the radius is the usual figure and keeps overlapping
	/// dabs reading as one continuous stroke.
	/// </summary>
	public float Spacing = 0.25f;

	/// <summary>
	/// Most samples one pointer move may be split into. A drag right across the model in one frame
	/// would otherwise fill in thousands of dabs and stall — better to under-sample one flick than to
	/// freeze the tool.
	/// </summary>
	public int MaxSamplesPerMove = 64;

	/// <summary>Which level the brush works at. This IS the sculpt's view level; there is not a
	/// second copy of it to disagree.</summary>
	public int Level
	{
		get => _sculpt.ViewLevel;
		set => _sculpt.ViewLevel = value;
	}

	public bool IsStroking => _working is not null;

	public bool CanUndo => _done.Count > 0;
	public bool CanRedo => _undone.Count > 0;

	/// <summary>
	/// The mask for a level, made unprotected on first ask.
	///
	/// Rebuilt from scratch if the level's vertex count has changed under it — which happens when a
	/// level below is sculpted, since that changes nothing about the count, or when the cage is
	/// rebased, which can. A mask of the wrong length would protect arbitrary vertices, so it is
	/// dropped rather than resized.
	/// </summary>
	public SculptMask MaskFor( int level )
	{
		var count = _sculpt.Rest( level ).VertexCount;

		if ( !_masks.TryGetValue( level, out var mask ) || mask.Count != count )
		{
			mask = new SculptMask( count );
			_masks[level] = mask;
		}

		return mask;
	}

	/// <summary>The mask the brush is currently subject to, or null when nothing is protected.
	/// Null rather than an all-ones array so the common case costs no multiply.</summary>
	public SculptMask ActiveMask => _masks.TryGetValue( Level, out var mask ) && mask.Any ? mask : null;

	public void ClearMask() => MaskFor( Level ).Clear();

	public void InvertMask() => MaskFor( Level ).Invert();

	/// <summary>The level's surface with the fully protected parts dropped — "hide by mask".</summary>
	public PolyMesh HiddenByMask() => MaskFor( Level ).Hide( _sculpt.Evaluate( Level ) );

	/// <summary>Protect everything, which is where "mask all but this bit" starts - invert it and
	/// then paint free the part you want to work on.</summary>
	public void ProtectAll() => MaskFor( Level ).Protect();

	/// <summary>Vertex and face count at a level, for the slider that has to warn before the click.</summary>
	public (int Vertices, int Faces) Cost( int level ) => _sculpt.Cost( level );

	/// <summary>
	/// The mesh to draw: the live working copy mid-stroke, otherwise the evaluated level.
	///
	/// Cached against the sculpt's revision, because a viewport asks every frame and evaluating the
	/// level stack is not a per-frame cost. Treat it as read-only — it is the session's copy, not
	/// yours.
	/// </summary>
	public PolyMesh DisplayMesh
	{
		get
		{
			if ( _working is not null )
				return _working;

			// The mask's own revision is part of the key: the sculpt does not change when a mask is
			// painted, so a cache keyed on the sculpt alone would keep serving the mesh from before
			// the mask moved and hide-by-mask would look like it did nothing.
			var mask = HideMasked ? MaskFor( _sculpt.ViewLevel ) : null;
			var maskRevision = mask?.Revision ?? -1;

			if ( _display is null
				|| _displayRevision != _sculpt.Revision
				|| _displayLevel != _sculpt.ViewLevel
				|| _displayHidden != HideMasked
				|| _displayMaskRevision != maskRevision )
			{
				var evaluated = _sculpt.Display();

				_display = mask is not null ? mask.Hide( evaluated ) : evaluated;
				_displayRevision = _sculpt.Revision;
				_displayLevel = _sculpt.ViewLevel;
				_displayHidden = HideMasked;
				_displayMaskRevision = maskRevision;
			}

			return _display;
		}
	}

	/// <summary>
	/// Where the cursor sits on the surface, or null if the ray missed. The editor draws its ring
	/// here; nothing about it changes the model.
	/// </summary>
	public MeshHit? Hover( Vec3 origin, Vec3 direction )
	{
		var dir = direction.Normal;

		if ( dir.LengthSquared < 0.5f )
			return null;

		if ( _working is not null )
			return _bvh.Raycast( _working, origin, dir );

		return MeshRaycast.Raycast( DisplayMesh, origin, dir );
	}

	/// <summary>
	/// Start a stroke. Returns false if the ray missed, which is not a failure — clicking past the
	/// model is how a user deselects, and it must not begin a stroke that later lands somewhere
	/// surprising.
	///
	/// The first sample is applied here, so a single click leaves a mark rather than nothing.
	/// </summary>
	public bool BeginStroke( Vec3 origin, Vec3 direction )
	{
		if ( IsStroking )
			throw new InvalidOperationException( "A stroke is already running; end it before starting another." );

		if ( Radius <= 0f )
			throw new InvalidOperationException( $"A brush needs a radius; this one is {Radius}." );

		var dir = direction.Normal;
		var hit = MeshRaycast.Raycast( DisplayMesh, origin, dir );

		if ( hit is null )
			return false;

		_working = _sculpt.Evaluate( Level );
		_bvh = MeshBVH.Build( _working );
		_undo = new BrushUndo();

		// Frames are built once, from the surface as the stroke found it, and not rebuilt per sample.
		// Rebuilding them on 128k vertices per dab is the obvious way to make the tool unusable, and
		// a brush that re-reads its own output also feeds back — an Inflate would run away as it
		// followed the normals it had just moved. A stroke works against the surface it started on.
		_frames = SculptFrames.Build( _working );

		_lastSample = hit.Value.Point;
		_maskBefore = Masking ? (float[])MaskFor( Level ).Values.Clone() : null;
		Dab( hit.Value.Point, hit.Value.Normal, Vec3.Zero );

		return true;
	}

	/// <summary>
	/// Carry the stroke to a new pointer position. Returns how many samples it produced: zero when
	/// the cursor has not travelled far enough to earn one, and several when it travelled far enough
	/// that one would have left a gap.
	///
	/// A ray that misses the model does NOT end the stroke — dragging off the silhouette and back on
	/// is ordinary, and ending there would make the tool feel like it drops the gesture.
	/// </summary>
	public int MoveTo( Vec3 origin, Vec3 direction )
	{
		if ( !IsStroking )
			throw new InvalidOperationException( "No stroke is running." );

		var dir = direction.Normal;
		var hit = _bvh.Raycast( _working, origin, dir );

		if ( hit is null )
			return 0;

		var target = hit.Value.Point;
		var travelled = (target - _lastSample).Length;
		var spacing = MathF.Max( Radius * Spacing, 1e-6f );

		if ( travelled < spacing )
			return 0;

		// Fill the gap. The pointer's real path between two events is unknowable, so this walks the
		// straight line between them — which is what the gesture looked like at this sampling rate.
		var steps = Math.Min( (int)(travelled / spacing), MaxSamplesPerMove );
		var from = _lastSample;
		var direction3 = (target - from) / travelled;

		for ( var i = 1; i <= steps; i++ )
		{
			var t = (float)i / steps;
			var point = from + (target - from) * t;
			Dab( point, hit.Value.Normal, direction3 * (travelled / steps) );
		}

		_lastSample = from + (target - from) * ((float)steps / steps);
		return steps;
	}

	/// <summary>
	/// Finish the stroke and commit it as one edit. Returns what was committed, or null if the stroke
	/// moved nothing — a click that landed on the model but at zero strength is not worth an undo
	/// entry.
	/// </summary>
	public SculptEdit EndStroke()
	{
		if ( !IsStroking )
			throw new InvalidOperationException( "No stroke is running." );

		var level = Level;
		var working = _working;
		var undo = _undo;
		var maskBefore = _maskBefore;

		_working = null;
		_frames = null;
		_bvh = null;
		_undo = null;
		_maskBefore = null;

		var edit = maskBefore is not null
			? CommitMask( level, maskBefore )
			: CommitGeometry( level, working, undo );

		if ( edit is null )
			return null;

		_done.Push( edit );
		_undone.Clear();

		return edit;
	}

	SculptEdit CommitGeometry( int level, PolyMesh working, BrushUndo undo )
	{
		if ( undo.Count == 0 )
			return null;

		var before = new Dictionary<int, Vec3>( undo.Previous );
		var after = new Dictionary<int, Vec3>( before.Count );

		foreach ( var vertex in before.Keys )
			after[vertex] = working.Positions[vertex];

		_sculpt.Record( level, working );

		return new SculptEdit( level, before, after );
	}

	SculptEdit CommitMask( int level, float[] snapshot )
	{
		var mask = MaskFor( level );

		if ( mask.Count != snapshot.Length )
			return null;

		var before = new Dictionary<int, float>();
		var after = new Dictionary<int, float>();

		for ( var i = 0; i < snapshot.Length; i++ )
		{
			if ( snapshot[i] == mask[i] )
				continue;

			before[i] = snapshot[i];
			after[i] = mask[i];
		}

		return before.Count == 0 ? null : new SculptEdit( level, before, after );
	}

	/// <summary>Abandon the stroke, leaving the model as it was before it started.</summary>
	public void CancelStroke()
	{
		// A mask stroke paints straight into the mask rather than into a working copy, so this is
		// the one thing cancelling has to actively put back.
		if ( _maskBefore is not null && MaskFor( Level ) is { } mask && mask.Count == _maskBefore.Length )
		{
			for ( var i = 0; i < _maskBefore.Length; i++ )
				mask[i] = _maskBefore[i];
		}

		_working = null;
		_frames = null;
		_bvh = null;
		_undo = null;
		_maskBefore = null;
	}

	/// <summary>
	/// Drop the finest level, and put it on the undo stack.
	///
	/// EXPOSED ONLY BECAUSE IT CAN BE UNDONE. Removing a level throws away every delta on it, and a
	/// destructive button with no way back is one nobody should be given - which is why this sat in
	/// the kernel unexposed until the session could hold the layer it dropped.
	/// </summary>
	public bool RemoveTopLevel()
	{
		if ( IsStroking || _sculpt.TopLevel == 0 )
			return false;

		var level = _sculpt.TopLevel;
		var dropped = _sculpt.RemoveTopLevel();

		_done.Push( new SculptEdit( level, dropped ) );
		_undone.Clear();

		return true;
	}

	public bool Undo() => Step( _done, _undone, forward: false );

	public bool Redo() => Step( _undone, _done, forward: true );

	bool Step( Stack<SculptEdit> from, Stack<SculptEdit> to, bool forward )
	{
		if ( IsStroking || from.Count == 0 )
			return false;

		var edit = from.Pop();

		if ( edit.IsLevel )
		{
			// A level edit is its own inverse read backwards: undoing a removal puts the level back,
			// and redoing it takes it away again.
			if ( forward )
				_sculpt.RemoveTopLevel();
			else
				_sculpt.RestoreTopLevel( edit.Level_ );

			_masks.Remove( edit.Level );
			to.Push( edit );

			return true;
		}

		if ( edit.IsMask )
		{
			edit.Write( MaskFor( edit.Level ), forward );
		}
		else
		{
			var mesh = _sculpt.Evaluate( edit.Level );
			edit.Write( mesh, forward );
			_sculpt.Record( edit.Level, mesh );
		}

		to.Push( edit );
		return true;
	}

	void Dab( Vec3 point, Vec3 normal, Vec3 direction )
	{
		if ( Masking )
		{
			var mask = MaskFor( Level );
			mask.Paint( _working, _bvh, point, Radius, Erasing ? -Strength : Strength, Falloff );

			if ( MirrorX )
				mask.Paint( _working, _bvh, new Vec3( -point.x, point.y, point.z ), Radius,
					Erasing ? -Strength : Strength, Falloff );

			return;
		}

		var stroke = new BrushStroke { Kind = Brush, Falloff = Falloff, MirrorX = MirrorX };
		stroke.Samples.Add( new BrushSample( point, normal, Radius, Strength, direction ) );

		// The mask is passed EVERY stroke, not applied afterwards. Brush.Apply folds it into the
		// per-vertex weight, so a half-masked vertex moves half as far; masking after the fact would
		// mean snapping protected vertices back, which leaves a hard edge where the mask fades.
		var undo = Effigy.Brush.Apply( _working, stroke, _frames, ActiveMask?.Values, _bvh );
		_undo.Absorb( undo );
	}
}
