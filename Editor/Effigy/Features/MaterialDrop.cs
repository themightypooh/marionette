using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Dropping a material onto a face.
///
/// THE PROBLEM THIS SOLVES. Faces carry a slot number, not a material — see FaceMaterialEdit for
/// why that has to stay true — and PartStudio.MaterialNames maps the number to a name. Every
/// existing way in names a slot you have already chosen: the Materials panel browses FOR slot 5,
/// the face menu browses for the slot the face is already on. Dragging a material out of a browser
/// and letting go over a face names no slot at all. It says "this face, this material" and leaves
/// the number entirely to us.
///
/// So this is the half that was missing: turn a material into the slot that should carry it, then
/// do the ordinary face assignment with it. The rule is one slot per material, reused —
/// <see cref="SlotFor"/> hands back the slot that already carries the material if there is one, so
/// dropping the same material on thirty faces produces one slot and one assignment feature rather
/// than thirty of each. Only a material nobody has used yet takes a fresh slot.
///
/// AND IT PUTS BACK WHAT IT TOOK. A drop that moves a face off a slot nothing else is holding
/// retires that slot's name too — see <see cref="ReleaseVacatedSlot"/>. Without it, changing your
/// mind about one face is a one-way ratchet: the slot count only ever goes up, the rejected
/// materials stay bound to slots no face wears, and the exporters write every one of them.
///
/// It edits the HISTORY, never the mesh, exactly as FaceMaterialEdit does, and for the same reason:
/// bodies are remade from scratch on every rebuild.
/// </summary>
public static class MaterialDrop
{
	/// <summary>The highest slot a face can be on — FaceMaterialFeature.Material clamps to 0..63,
	/// so a slot past this could be stored and would never come back.</summary>
	public const int HighestSlot = 63;

	/// <summary>
	/// Which slot should carry <paramref name="material"/>, or -1 when there is nowhere to put it.
	///
	/// Three answers, in order:
	///
	/// 1. THE SLOT ALREADY CARRYING IT. Checked first and by name, so a second drop of the same
	///    material joins the first rather than opening a second slot that renders identically. The
	///    lowest such slot wins if a document somehow named two, purely so the answer is stable.
	///
	/// 2. THE LOWEST SLOT NOBODY IS USING, counting from 1. Used means named OR painted on — a slot
	///    with an assignment feature and no name is the result of the face menu's "put this face on
	///    slot 3", and taking it here would silently repaint those faces with the dropped material.
	///
	/// 3. NOTHING, when all 63 are spoken for.
	///
	/// SLOT 0 IS NEVER ALLOCATED, though it is returned by rule 1 if somebody has named it. It is
	/// the slot every face starts on and the one the viewport pointedly does not tint: handing it to
	/// a drop would paint the whole part instead of the one face under the cursor. Naming slot 0
	/// remains something you do deliberately, from the Materials panel, where the consequence is on
	/// screen next to it.
	/// </summary>
	public static int SlotFor( PartStudio studio, string material )
	{
		if ( studio is null )
			return -1;

		if ( Normalise( material ) is null )
			return -1;

		if ( SlotCarrying( studio, material ) is var carrying && carrying >= 0 )
			return carrying;

		var taken = new HashSet<int>( FaceMaterialEdit.UsedSlots( studio ) );

		for ( var slot = 1; slot <= HighestSlot; slot++ )
		{
			if ( !taken.Contains( slot ) )
				return slot;
		}

		return -1;
	}

	/// <summary>
	/// The slot already carrying <paramref name="material"/>, or -1 if no slot does.
	///
	/// Rule 1 of <see cref="SlotFor"/>, on its own, because a browser asking "does this part already
	/// use this material, and where" must not be answered with the free slot SlotFor would hand back
	/// — that would badge every material in the project with the same number and claim the document
	/// uses all of them.
	///
	/// The LOWEST such slot if a document somehow named two, purely so the answer is stable, and
	/// matched through <see cref="Normalise"/> so a slot named with backslashes still recognises the
	/// asset a picker hands over with forward ones.
	/// </summary>
	public static int SlotCarrying( PartStudio studio, string material )
	{
		if ( studio is null )
			return -1;

		var wanted = Normalise( material );

		if ( wanted is null )
			return -1;

		foreach ( var (slot, name) in studio.MaterialNames.OrderBy( kv => kv.Key ) )
		{
			if ( Normalise( name ) == wanted )
				return slot;
		}

		return -1;
	}

	/// <summary>
	/// Put <paramref name="material"/> on one face, and report whether anything changed.
	///
	/// The face is identified the way the right-click menu identifies it — the body and face index
	/// a raycast just returned, plus the FaceRef captured at the hit point, which is the half that
	/// survives a rebuild. <paramref name="slot"/> comes back so the caller can say which slot it
	/// landed on, because that number is the only thing on screen afterwards that explains where the
	/// material went; it is -1 when nothing was done.
	///
	/// Call Rebuild afterwards. Deliberately not done here, for the same reason FaceMaterialEdit
	/// does not: a caller dropping onto several faces should pay for one rebuild, not one each.
	/// </summary>
	public static bool Drop( PartStudio studio, string bodyId, int faceIndex, FaceRef reference,
		string material, out int slot ) =>
		Drop( studio, bodyId, faceIndex, reference, material, out slot, out _ );

	/// <summary>
	/// The same drop, also reporting the slot it retired — see <see cref="ReleaseVacatedSlot"/> —
	/// or -1 when it retired none.
	///
	/// Worth saying out loud rather than doing quietly. The drop already has to announce the slot it
	/// chose, because nothing else on screen explains where the material went; a slot that stopped
	/// existing on the same gesture is the same kind of fact, and the Materials panel's count is
	/// about to change because of it.
	/// </summary>
	public static bool Drop( PartStudio studio, string bodyId, int faceIndex, FaceRef reference,
		string material, out int slot, out int released )
	{
		slot = -1;
		released = -1;

		if ( studio is null )
			return false;

		var name = material?.Trim();

		if ( string.IsNullOrWhiteSpace( name ) )
			return false;

		slot = SlotFor( studio, name );

		if ( slot < 0 )
			return false;

		// The NAME first, then the face. Both are edits and either can be the only one: dropping a
		// material the document has never seen names a fresh slot and moves the face onto it, while
		// dropping it onto a second face names nothing new and only moves the face.
		//
		// Compared through Normalise, not by string equality, so re-dropping the same asset spelled
		// with backslashes does not rewrite the name to the other spelling. The stored value would
		// still resolve to the same material, but the document would come back dirty, an undo step
		// would appear, and every open control would refresh — for a change nobody made.
		// STORED AS THE SOURCE PATH, whatever the caller was handed. The engine appends `_c` itself
		// when it loads a material, so a slot holding `x.vmat_c` sends it looking for `x.vmat_c_c`
		// - which is not a file, and a face bound to it renders in the missing-material shader.
		var stored = AsSourcePath( name );

		var named = false;

		var hasExisting = studio.MaterialNames.TryGetValue( slot, out var existing );

		// A COMPILED NAME IS UPGRADED even though it compares equal. Normalise says `.vmat_c` and
		// `.vmat` are the same asset, which is right for "does this part already use it" and wrong
		// as a reason to leave the broken spelling in place - without this, a document that bound a
		// material before the suffix was understood keeps the name that does not resolve, and
		// re-dropping the same material looks like it did nothing.
		var stale = hasExisting && !string.Equals( existing, AsSourcePath( existing ), StringComparison.Ordinal );

		if ( !hasExisting || Normalise( existing ) != Normalise( stored ) || stale )
		{
			studio.MaterialNames[slot] = stored;
			named = true;
		}

		// Whether the face is ALREADY on this slot, asked before Assign rather than inferred from
		// what it returns. Assign detaches before it attaches, so putting a face back where it
		// already was reports a change every time — true of the mechanism, wrong as an answer, and
		// the reason the right-click menu checks the same thing before calling it. Here it is not
		// an optimisation: dropping a material onto the face already wearing it is the ordinary way
		// to MISS by a few pixels, and reporting it as an edit puts a do-nothing step on the undo
		// stack that then has to be pressed through.
		var previous = FaceSlot( studio, bodyId, faceIndex );
		var moved = previous != slot
			&& FaceMaterialEdit.Assign( studio, bodyId, faceIndex, reference, slot );

		// The face has left a slot behind. If it was the last thing holding that slot, the slot goes
		// with it — otherwise re-dropping onto one face walks it through a trail of named slots that
		// nothing wears and every exporter still writes.
		if ( moved && ReleaseVacatedSlot( studio, previous, slot ) )
			released = previous;

		return named || moved || released >= 0;
	}

	/// <summary>
	/// Let go of the binding on the slot a drop just emptied, and say whether it did.
	///
	/// WHY A DROP HAS TO CLEAN UP AFTER ITSELF. Every other way of naming a slot names a slot you
	/// picked; a drop invents the number, so the numbers it invents are the ones nobody is watching.
	/// Changing your mind about one face five times walks it through five slots, and Detach does
	/// retire the four assignment features it emptied — but the four NAMES stay, and a name is what
	/// the exporters write. A box wearing three materials exports nine, and the first anyone hears
	/// of it is a material list in the engine that does not match the part.
	///
	/// NARROW ON PURPOSE. This retires ONE slot — the one this face just left — and only when
	/// nothing else is holding it:
	///
	/// - An assignment feature still targeting it means other faces are on it. A SUPPRESSED one
	///   counts as holding it too, because un-suppressing is one click away and the name has to
	///   still be there when it happens.
	/// - More than one face on it in the mesh means the slot did not come from an assignment at all
	///   — a feature that built geometry straight onto it — and those faces still wear it. The mesh
	///   read here is the one from BEFORE this edit, so the face being moved is still counted on its
	///   old slot: a count of one is that face alone, two is somebody else as well.
	///
	/// Slot 0 is never retired. It is the absence of an assignment rather than a binding this drop
	/// is entitled to clear, and a name on it is the part's base material that every untouched face
	/// is still wearing.
	///
	/// A slot named in the Materials panel and never painted is untouched by all of this, because no
	/// face ever left it. Reserving a slot now and filling it in later stays a thing you can do.
	/// </summary>
	private static bool ReleaseVacatedSlot( PartStudio studio, int vacated, int landedOn )
	{
		if ( vacated <= 0 || vacated == landedOn )
			return false;

		if ( !studio.MaterialNames.ContainsKey( vacated ) )
			return false;

		if ( studio.Features.OfType<FaceMaterialFeature>().Any( f => f.Material.Clamped == vacated ) )
			return false;

		if ( FacesOn( studio, vacated ) > 1 )
			return false;

		// The SIZE goes with the name. A slot number that has been handed back is going to be handed
		// out again by SlotFor, and a scale left on it is inherited by whatever material lands there
		// next — brushed steel arriving at 48 units per tile because a floor tile used to be on slot
		// 3. The scale is only meaningful alongside the binding it was chosen for.
		MaterialScale.SetScale( studio, vacated, MaterialScale.Unscaled );

		return studio.MaterialNames.Remove( vacated );
	}

	/// <summary>How many faces sit on a slot, across every body, in the mesh as it currently
	/// stands.</summary>
	private static int FacesOn( PartStudio studio, int slot )
	{
		var count = 0;

		foreach ( var body in studio.Bodies ?? Enumerable.Empty<Body>() )
		{
			if ( body?.Mesh is not { } mesh )
				continue;

			count += mesh.Faces.Count( f => f.Material == slot );
		}

		return count;
	}

	/// <summary>
	/// The slot a face is on right now, or -1 if the body or face cannot be found.
	///
	/// Read off the BUILT mesh rather than worked out from the assignments in the tree, because the
	/// mesh is where they have all already been applied in order — including a later assignment
	/// overriding an earlier one on the same face, which reading the features would have to redo.
	/// </summary>
	private static int FaceSlot( PartStudio studio, string bodyId, int faceIndex )
	{
		var body = studio?.Bodies?.FirstOrDefault( b => b?.Id == bodyId );

		if ( body?.Mesh is not { } mesh || faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			return -1;

		return mesh.Faces[faceIndex].Material;
	}

	/// <summary>
	/// Put <paramref name="material"/> on every face a brush dab covered, in one pass.
	///
	/// WHY THIS EXISTS RATHER THAN THE CALLER LOOPING <see cref="Drop"/>. It can, and this does -
	/// the per-face rules are subtle enough (reuse the slot already carrying the material, do not
	/// report a face already on it as a change, retire a slot the last face just left) that a second
	/// implementation of them for the brush would drift from this one. What the brush needs on top
	/// is the aggregate: which slot everything landed on, and every slot the dab emptied, since a
	/// stroke that sweeps a material off thirty faces can retire several at once.
	///
	/// FACE INDICES ARE INTO THE BODY'S CURRENT MESH, and stay valid for the whole call because
	/// nothing here rebuilds - this edits the history exactly as <see cref="Drop"/> does. Call
	/// Rebuild once afterwards, which is the whole point of doing the dab in one call.
	///
	/// The reference for each face is captured at its CENTROID rather than at the point the brush
	/// passed over. A dab covers whole faces or none of them, so the centroid is the honest anchor
	/// - and it is stable, where the brush point would make the same face resolve differently
	/// depending on which way the stroke crossed it.
	/// </summary>
	public static int Brush( PartStudio studio, Body body, IEnumerable<int> faceIndices,
		string material, out int slot, out List<int> released )
	{
		slot = -1;
		released = new List<int>();

		if ( studio is null || body?.Mesh is not { } mesh || faceIndices is null )
			return 0;

		if ( Normalise( material ) is null )
			return 0;

		var changed = 0;

		// Sorted and de-duplicated: a dab reports faces in whatever order the BVH walked them, and
		// the same face twice would be a second Assign that reports no change anyway. Ordering keeps
		// the feature's face list stable between two dabs that covered the same faces, which is what
		// stops a redundant document edit.
		foreach ( var faceIndex in faceIndices.Distinct().OrderBy( i => i ) )
		{
			if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count )
				continue;

			var reference = FacePlane.Capture( body, faceIndex, mesh.FaceCentroid( mesh.Faces[faceIndex] ) );

			if ( Drop( studio, body.Id, faceIndex, reference, material, out var landed, out var freed ) )
				changed++;

			if ( landed >= 0 )
				slot = landed;

			if ( freed >= 0 && !released.Contains( freed ) )
				released.Add( freed );
		}

		return changed;
	}

	/// <summary>
	/// A material path reduced to something two spellings of the same asset agree on.
	///
	/// Separators and case, because a path typed by hand, one from an asset picker and one from a
	/// drag can differ in both while naming one file, and a document that disagrees with itself
	/// about that grows a second slot for a material it already has.
	///
	/// Public because the Materials dock has to key a lookup of every material in the project by the
	/// same rule this file matches slots with. It could have asked <see cref="SlotCarrying"/> once
	/// per material instead, and that is a scan of the whole project against the whole slot table on
	/// every rebuild — which includes every tick of a dragged parameter. Exporting the rule lets it
	/// build the index once and walk the handful of named slots instead. What must not happen is a
	/// second copy of the rule over there: the two would agree until one of them learned about
	/// trailing slashes.
	/// </summary>
	public static string Normalise( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		var n = path.Trim().Replace( '\\', '/' ).ToLowerInvariant();

		// A COMPILED PATH IS THE SAME ASSET AS ITS SOURCE. The asset browser reports
		// `materials/x.vmat_c` for a material that ships compiled - most of the engine's own
		// content - while a project's own material is `materials/x.vmat`, and a document may hold
		// either depending on which one was bound. Comparing them as different strings meant the
		// browser's "bound" badge missed a material the part was wearing, and SlotFor handed out a
		// second slot for a material the document already had.
		return n.EndsWith( "_c", StringComparison.Ordinal ) ? n[..^2] : n;
	}

	/// <summary>
	/// The reference a DOCUMENT should store for a material, given whatever the asset browser
	/// called it.
	///
	/// Normalise answers "are these the same asset"; this answers "what do we write down", and the
	/// two are different jobs. A `.vmat_c` is the compiler's output - naming it in a model or a
	/// preview resolves to nothing, which is the bright red missing-material shader - so the source
	/// path is what goes in the file even when the compiled one is all the browser knows about.
	/// Case is left alone here, unlike Normalise: this value is shown to people and written to
	/// disk, and lowercasing a path is a change nobody asked for.
	/// </summary>
	public static string AsSourcePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return path;

		var n = path.Trim().Replace( '\\', '/' );

		return n.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ? n[..^2] : n;
	}
}
