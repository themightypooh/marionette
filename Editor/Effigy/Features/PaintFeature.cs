using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A paint layer in the feature tree.
///
/// WHERE THE PAINT LIVES: strokes in object space, replayed onto whatever the mesh currently is.
/// The vertex colours are a derived artifact — the same bet the rest of the kernel already made by
/// keeping the mesh a function of the feature history. Nothing else in the document holds paint, undo
/// is the feature tree's undo, and a stroke is one entry in the list.
///
/// EXECUTE REPLAYS THE STROKES ONTO PER-VERTEX COLOURS. The dab — vertices in radius, reject the far
/// side by its normal, falloff-weighted source-over blend — lives in PaintReplay, shared with the
/// live session so a stroke painted by hand and the same stroke rebuilt later produce identical
/// colours. Vertex colours rather than a texture atlas because that is what the engine composites
/// over a material natively, and it needs no UVs — the whole unwrap gate the texture path required is
/// gone.
///
/// ITS STALENESS GUARD IS COPIED FROM SculptFeature FOR THE SAME REASON. A paint session appends
/// strokes nowhere near the studio, so nothing calls MarkDirty and the rebuild would happily reuse
/// the cached body from before the stroke — the paint would stop following the brush, which reads as
/// "the paint tool does nothing" rather than as a caching bug. A revision counter bumped when the
/// stroke list changes, compared here to what the last rebuild built from, is the guard.
/// </summary>
public sealed class PaintFeature : Feature
{
	public override string TypeName => "Paint";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );

	/// <summary>
	/// Whether the paint tints what is underneath it or stands in for it.
	///
	/// BOTH ARE THE SAME ONE MULTIPLY. Vertex colour is a tint and there is no shader here to make
	/// it anything else - what changes is the surface it multiplies into. Tint keeps
	/// <c>materials/default.vmat</c>, whose colour texture carries the default surface, so the
	/// paint darkens and colours that. Replace binds <c>materials/default/white.vmat</c> instead,
	/// and a multiply against white IS the paint colour, so it reads as covering.
	///
	/// WHY NOT A SHADER, which is what docs/dev/PAINTING.md assumed covering would need: it would,
	/// to composite by alpha over an arbitrary material. Swapping the material underneath gets the
	/// covering LOOK for a slot nobody has bound, which is the case that was compiling to red
	/// anyway. A slot with a material dropped on it is untouched by this either way - the drop is
	/// a deliberate choice and paint tints it.
	///
	/// IT ONLY MOVES UNBOUND SLOTS, and it is read across the whole document rather than per body:
	/// what an unbound slot compiles to is one line in the model's remap list, and there is one of
	/// those per model. Setting any paint layer to Replace sets it for the export.
	/// </summary>
	public readonly ChoiceParam Blend = new( "Blend", new[] { "Tint", "Replace" } );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Blend };

	/// <summary>
	/// The strokes, in the order they were painted.
	///
	/// NULL UNTIL THE FIRST STROKE LANDS, the same "not yet populated" idiom SculptFeature uses for
	/// its levels. A never-painted feature serialises to nothing at all: StudioDocument writes a null
	/// field as absent, and the reflection sweep in DocumentTests round-trips a null list as null.
	/// </summary>
	public List<PaintStroke> Strokes;

	/// <summary>Bumped each time the stroke list changes, so <see cref="IsStale"/> can notice
	/// without anyone remembering to call MarkDirty.</summary>
	public int Revision { get; private set; }

	// The revision this feature last built from. See the class comment — a stroke lands nowhere near
	// the studio, so nothing calls MarkDirty and this is what catches it.
	int _builtRevision = -1;

	// The replay cache: the colours last produced, and the topology + revision they were produced
	// from. Keyed on TOPOLOGY (vertex count and face indices, deliberately not positions) and
	// revision, so a parametric edit that moves the geometry without changing its structure reuses
	// the colours rather than re-replaying, and a new stroke invalidates them.
	Vec4[] _cachedColors;
	long _topologyId;
	int _colorsRevision = -1;

	public override bool IsStale => Revision != _builtRevision;

	/// <summary>Append a stroke and mark the feature stale, so the next rebuild replays it. The list
	/// is lazily created here so a fresh feature never has to check for null before painting.</summary>
	public void AddStroke( PaintStroke stroke )
	{
		(Strokes ??= new()).Add( stroke );
		Revision++;
	}

	/// <summary>
	/// Replace the whole stroke list — undo/redo's route in.
	///
	/// The revision is bumped, not merely the list swapped, because the replay cache is keyed on it: a
	/// plain assignment would leave <see cref="Revision"/> unchanged, the cache would see no reason to
	/// re-render, and the model would keep serving colours the restored strokes do not describe. The
	/// strokes themselves are copied by reference — they are immutable once painted, so sharing them
	/// across undo snapshots is the correct and cheapest read.
	/// </summary>
	public void ReplaceStrokes( IReadOnlyList<PaintStroke> strokes )
	{
		Strokes = strokes is null ? null : new List<PaintStroke>( strokes );
		Revision++;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		// Paint paints ONE body at a time — one stroke list, one set of vertex colours. A studio
		// with several bodies needs a picked body, which is exactly what the editor's door gate asks
		// for before a session starts.
		if ( Strokes is { Count: > 0 } && targets.Count == 1 )
		{
			var mesh = targets[0].Mesh;
			var topology = MultiresSculpt.TopologyId( mesh );

			if ( _cachedColors is null || _topologyId != topology || _colorsRevision != Revision )
			{
				_cachedColors = PaintReplay.ReplayColors( mesh, Strokes );
				_topologyId = topology;
				_colorsRevision = Revision;
			}

			mesh.VertexColors = _cachedColors;
		}

		// Last, so a failure above leaves the feature stale and the next rebuild tries again.
		_builtRevision = Revision;
	}
}
