using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>One committed weight stroke, held so it can be undone and redone.</summary>
public sealed class WeightEdit
{
	readonly Dictionary<int, BoneWeight[]> _before;
	readonly Dictionary<int, BoneWeight[]> _after;

	public WeightEdit( Dictionary<int, BoneWeight[]> before, Dictionary<int, BoneWeight[]> after )
	{
		_before = before;
		_after = after;
	}

	/// <summary>How many vertices this stroke actually changed.</summary>
	public int Count => _before.Count;

	internal void Apply( SkinWeights weights, bool forward )
	{
		var source = forward ? _after : _before;

		foreach ( var (vertex, value) in source )
		{
			if ( vertex >= 0 && vertex < weights.Count )
				weights[vertex] = value;
		}
	}

	internal IEnumerable<int> Vertices => _before.Keys;
}

/// <summary>
/// Everything about weight painting that is arithmetic rather than widgets.
///
/// SAME SPLIT AS SculptSession, AND FOR THE SAME REASON: the half that can be tested without s&amp;box
/// is worth having as its own object, and it turns out to be nearly all of it. The editor's job
/// reduces to turning a mouse into a ray and drawing a ring.
///
/// A STROKE IS ONE UNDO ENTRY. The brush is applied live so the viewport can colour the weights as
/// the cursor moves, and the whole gesture is committed once at <see cref="EndStroke"/> — because
/// one dab is not what a user means by "undo".
///
/// **The layer is written at the same moment.** A stroke that changed the weights and did not reach
/// <see cref="Layer"/> would look correct until the next rebuild and then vanish, which is the
/// single worst failure this tool can have: it is invisible at the moment it happens and blamed on
/// something else later. So committing a stroke and recording it as paint are one operation, and
/// undo takes both back.
/// </summary>
public sealed class WeightPaintSession
{
	readonly PolyMesh _mesh;
	readonly SkinWeights _weights;
	readonly Skeleton _skeleton;
	readonly MeshBVH _bvh;

	readonly Stack<WeightEdit> _done = new();
	readonly Stack<WeightEdit> _undone = new();

	// Live only between BeginStroke and EndStroke.
	WeightUndo _undo;
	Vec3 _lastSample;
	bool _stroking;

	public WeightPaintSession( PolyMesh mesh, SkinWeights weights, Skeleton skeleton )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_weights = weights ?? throw new ArgumentNullException( nameof( weights ) );
		_skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		// Built once. The mesh does not move while weights are painted, which is the one thing that
		// makes this cheaper than the sculpt session - there is no working copy and no refit.
		_bvh = MeshBVH.Build( mesh );
		Layer = new WeightPaintLayer( mesh );
	}

	public PolyMesh Mesh => _mesh;
	public SkinWeights Weights => _weights;
	public Skeleton Skeleton => _skeleton;

	/// <summary>What was painted, in the form that survives a rebuild. See WeightPaintLayer.</summary>
	public WeightPaintLayer Layer { get; private set; }

	public WeightBrushKind Brush = WeightBrushKind.Add;
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Which bone the brush paints. -1 is nothing, and every kind but Smooth refuses.</summary>
	public int Bone = -1;

	/// <summary>Brush radius in world units, not pixels — the kernel has no screen.</summary>
	public float Radius = 0.25f;

	/// <summary>
	/// A starting radius that means something on THIS model. Effigy's units are dimensionless, so a
	/// constant is a brush that covers a whole prop and a pixel of a room - same reasoning as
	/// SculptSession's.
	/// </summary>
	public float SuggestedRadius => MathF.Max( _mesh.BoundsDiagonal * 0.08f, 1e-4f );

	/// <summary>
	/// How much one dab moves the weight. Low on purpose: weight painting is built out of many light
	/// passes, and a brush that takes a vertex to 1.0 in one dab cannot be used to blend anything.
	/// </summary>
	public float Strength = 0.15f;

	/// <summary>Where Set is heading, when the brush is Set.</summary>
	public float Target = 1f;

	public bool MirrorX;

	/// <summary>Fraction of the radius the cursor must travel to earn another sample.</summary>
	public float Spacing = 0.25f;

	/// <summary>Most samples one pointer move may become, so a flick across the model cannot stall
	/// the tool.</summary>
	public int MaxSamplesPerMove = 64;

	public bool IsStroking => _stroking;
	public bool CanUndo => _done.Count > 0;
	public bool CanRedo => _undone.Count > 0;

	/// <summary>
	/// How much of the painted bone each vertex carries, for a viewport that colours the model by
	/// weight. Nothing else in the kernel can produce this, and every tool that paints weights shows
	/// it — painting blind is not painting.
	/// </summary>
	public float[] Influence( int bone )
	{
		var values = new float[_mesh.VertexCount];

		if ( bone < 0 )
			return values;

		for ( var v = 0; v < _mesh.VertexCount; v++ )
		{
			foreach ( var w in _weights[v] )
			{
				if ( w.Bone != bone )
					continue;

				values[v] = w.Weight;
				break;
			}
		}

		return values;
	}

	/// <summary>
	/// Start a stroke at whatever the ray hits. False when it misses, which is an ordinary outcome
	/// and not an error.
	/// </summary>
	public bool BeginStroke( Vec3 origin, Vec3 direction )
	{
		if ( _stroking )
			throw new InvalidOperationException( "A stroke is already running; end it before starting another." );

		if ( Radius <= 0f )
			throw new InvalidOperationException( $"A brush needs a radius; this one is {Radius}." );

		if ( Brush != WeightBrushKind.Smooth && Bone < 0 )
			throw new InvalidOperationException( "Pick a bone to paint before painting." );

		var hit = _bvh.Raycast( _mesh, origin, direction.Normal );

		if ( hit is null )
			return false;

		_stroking = true;
		_undo = new WeightUndo();
		_lastSample = hit.Value.Point;

		Dab( hit.Value.Point );
		return true;
	}

	/// <summary>
	/// Carry the stroke to a new pointer position, filling in the gap the pointer skipped. Returns
	/// how many samples that took.
	///
	/// A ray that misses does NOT end the stroke: dragging off the silhouette and back on is
	/// ordinary, and ending there makes the tool feel like it drops the gesture.
	/// </summary>
	public int MoveTo( Vec3 origin, Vec3 direction )
	{
		if ( !_stroking )
			throw new InvalidOperationException( "No stroke is running." );

		var hit = _bvh.Raycast( _mesh, origin, direction.Normal );

		if ( hit is null )
			return 0;

		var target = hit.Value.Point;
		var travelled = (target - _lastSample).Length;
		var spacing = MathF.Max( Radius * Spacing, 1e-6f );

		if ( travelled < spacing )
			return 0;

		var steps = Math.Min( (int)(travelled / spacing), MaxSamplesPerMove );
		var from = _lastSample;

		for ( var i = 1; i <= steps; i++ )
			Dab( from + (target - from) * ((float)i / steps) );

		_lastSample = target;
		return steps;
	}

	/// <summary>
	/// Finish the stroke, commit it as one edit, and write what it painted into the layer. Null when
	/// the stroke changed nothing — a click at zero strength is not worth an undo entry.
	/// </summary>
	public WeightEdit EndStroke()
	{
		if ( !_stroking )
			throw new InvalidOperationException( "No stroke is running." );

		var undo = _undo;

		_stroking = false;
		_undo = null;

		if ( undo.Count == 0 )
			return null;

		var before = new Dictionary<int, BoneWeight[]>( undo.Count );
		var after = new Dictionary<int, BoneWeight[]>( undo.Count );

		foreach ( var (vertex, previous) in undo.Previous )
		{
			before[vertex] = previous;
			after[vertex] = _weights[vertex];

			Layer.Capture( vertex, _weights[vertex], _skeleton );
		}

		var edit = new WeightEdit( before, after );

		_done.Push( edit );
		_undone.Clear();

		return edit;
	}

	public bool Undo() => Step( _done, _undone, forward: false );

	public bool Redo() => Step( _undone, _done, forward: true );

	bool Step( Stack<WeightEdit> from, Stack<WeightEdit> to, bool forward )
	{
		if ( _stroking || from.Count == 0 )
			return false;

		var edit = from.Pop();

		edit.Apply( _weights, forward );

		// THE LAYER GOES BACK TOO. Undoing the weights and leaving the paint recorded would look
		// right until the next rebuild put the undone stroke back on - a bug that only appears
		// several actions later and gets blamed on the rebuild.
		foreach ( var vertex in edit.Vertices )
			Layer.Capture( vertex, _weights[vertex], _skeleton );

		to.Push( edit );
		return true;
	}

	/// <summary>
	/// Take the paint off a set of vertices so the auto weights show through again — the "reset this
	/// bit" every paint tool needs, and the only way back to the binder's own answer short of
	/// clearing everything.
	///
	/// It is NOT an undo: the auto weights are recomputed by the caller, so this only forgets the
	/// paint. Committed as an edit so it is undoable like a stroke.
	/// </summary>
	public WeightEdit ClearPaint( IEnumerable<int> vertices, SkinWeights auto )
	{
		if ( _stroking )
			throw new InvalidOperationException( "A stroke is running." );

		if ( auto is null )
			throw new ArgumentNullException( nameof( auto ) );

		if ( auto.Count != _weights.Count )
			throw new ArgumentException( $"auto weights ({auto.Count}) and the mesh ({_weights.Count}) disagree" );

		var before = new Dictionary<int, BoneWeight[]>();
		var after = new Dictionary<int, BoneWeight[]>();

		foreach ( var vertex in vertices )
		{
			if ( vertex < 0 || vertex >= _weights.Count || !Layer.Clear( vertex ) )
				continue;

			before[vertex] = _weights[vertex];
			_weights[vertex] = auto[vertex];
			after[vertex] = auto[vertex];
		}

		if ( before.Count == 0 )
			return null;

		var edit = new WeightEdit( before, after );

		_done.Push( edit );
		_undone.Clear();

		return edit;
	}

	/// <summary>
	/// How many vertices under the current brush cannot move because the painted bone is their only
	/// influence. Asked before painting, so the tool can say why a stroke is about to do nothing.
	/// </summary>
	public int LockedUnder( Vec3 point )
	{
		var stroke = NewStroke();
		stroke.Samples.Add( new WeightSample( point, Radius, Strength ) );

		return WeightBrush.CountLocked( _mesh, _weights, stroke, _bvh );
	}

	void Dab( Vec3 point )
	{
		var stroke = NewStroke();
		stroke.Samples.Add( new WeightSample( point, Radius, Strength ) );

		_undo.Absorb( WeightBrush.Apply( _mesh, _weights, stroke, mask: null, _bvh ) );
	}

	WeightStroke NewStroke() => new()
	{
		Kind = Brush,
		Falloff = Falloff,
		Bone = Bone,
		Target = Target,
		MirrorX = MirrorX,
	};
}
