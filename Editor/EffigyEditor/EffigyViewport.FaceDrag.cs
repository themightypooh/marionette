using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

/// <summary>
/// Dragging a selected face in the viewport — push and pull with the mouse.
///
/// WHAT MAKES IT FEEL LIKE DIRECT EDITING rather than a dialog with a face in it. Move Face and the
/// face extrude both do the geometry already; what they ask for is a number typed into a panel, and
/// typing 0.4 to find out whether 0.4 was right is not how anybody shapes a part. Select a face,
/// drag it, watch the solid grow.
///
/// ONE ARROW, ALONG THE FACE'S OWN NORMAL. Not three. Gizmo.Control.Arrow takes the axis directly
/// and hands back a scalar distance along it, which is exactly the shape of a push-pull: one degree
/// of freedom, and the handle says so.
///
/// THIS REPLACED A THREE-AXIS Gizmo.Control.Position, AND THE ARGUMENT FOR THAT IS WORTH KEEPING
/// because it was a good argument and it was still wrong. All three arrows WERE honest: the drag
/// goes to FaceMove in TRANSLATE mode, where each face travels by dot(normal, displacement), so
/// dragging one planar face sideways correctly moved nothing — sliding a plane within itself does
/// not change the solid — and on the facing pair of a wall those same two arrows slid the wall.
/// Nothing was special-cased and both cases were right.
///
/// It was still the wrong interaction, for the reason honesty does not settle: on the single-face
/// pick, which is overwhelmingly the common one, two of the three arrows did nothing when dragged.
/// An inert arrow is not read as "the geometry says this is a no-op". It is read as broken, and it
/// invites the drag that teaches you it was pointless. po, 4 September 2026, having used it:
/// "i only want the gizmo to be able to pull in the direction the face is actually facing, not up
/// and down or left and right."
///
/// SLIDING A WALL IS NOW MOVE FACE'S TRANSLATE MODE, typed, rather than a handle that appears only
/// for a selection shape the user cannot see they have made. That is the trade, and it is the right
/// way round: the common case gets an honest handle and the rare one keeps a dialog it already had.
///
/// THE 10-DEGREE CULL IS THE ENGINE'S AND IT IS PROTECTIVE. Arrow hides itself when its axis comes
/// within cullAngle of the view direction, because screen movement stops mapping to axis movement
/// there. With three arrows something else was always grabbable; with one, looking straight down a
/// face's normal leaves no handle. That is correct — the drag would be meaningless at that angle —
/// but it means the handle can vanish, so orbit a few degrees rather than assuming it broke.
///
/// ONLY WHILE A TOOL THAT CONSUMES A FACE IS OPEN, and this is the part that keeps it parametric.
/// An earlier cut of this put the arrows on any face you clicked, so dragging one appended a Move
/// Face to the tree behind your back — which is a mesh editor wearing a history's clothes. In a
/// parametric modeller a face does not move because you shoved it; it moves because a FEATURE says
/// it does. So the handle is a way of answering the open feature's distance with the mouse instead
/// of the keyboard, and it exists exactly as long as that feature's dialog does.
///
/// THE VIEWPORT RESOLVES THE DRAG AND STOPS THERE. What the displacement MEANS — which parameter it
/// writes, whether that is a distance along a normal or a direction and a length — is a question
/// about the feature, and that lives with the dialog that owns it. Same division as the face
/// context menu.
/// </summary>
internal sealed partial class EffigyViewport
{
	/// <summary>
	/// Set by the dialog of a feature that consumes faces, for as long as that dialog is open.
	///
	/// The gate that keeps this parametric. Without it the arrows sit on every face a click selects,
	/// and dragging one writes a feature into the tree that nobody asked for.
	/// </summary>
	public bool FaceDragEnabled { get; set; }

	/// <summary>Raised once when a drag starts, before anything has moved.</summary>
	public Action FaceDragBegan { get; set; }

	/// <summary>
	/// Raised every frame the handle moves: the displacement accumulated since the drag started —
	/// not the per-frame delta — and the axis the handle is aligned to.
	///
	/// The total rather than something to integrate, because the consumer sets a parameter from it
	/// and a parameter is a value, not an increment. The axis comes along because a feature that
	/// stores one distance (an Extrude) needs to know what to measure it against, and by the time
	/// this fires the face has already moved away from the normal it started with.
	/// </summary>
	public Action<Vec3, Vec3> FaceDragMoved { get; set; }

	/// <summary>Raised when the button comes up.</summary>
	public Action FaceDragEnded { get; set; }

	/// <summary>True while the handle is being dragged. Idle picking stands down for the duration,
	/// or the click that ends the drag would also re-pick whatever is under the cursor.</summary>
	public bool IsDraggingFace => _draggingFace;

	private bool _draggingFace;
	private Vector3 _faceDragAnchor;
	private Vector3 _faceDragDelta;
	private Vector3 _faceDragNormal = Vector3.Up;

	/// <summary>
	/// The handle for the current face selection, and the drag it reports.
	///
	/// Drawn only while a face-consuming tool is open and holding faces. A sketch being drawn, the
	/// sculpt brush and the bone tool all have a click of their own, and a set of arrows floating
	/// over the model while one of them is armed is an invitation to a click that will not do what
	/// it looks like.
	///
	/// The faces come from SelectedFaces — what the DIALOG is holding — rather than from the idle
	/// selection. They are the same faces most of the time, and the difference is the whole point:
	/// the handle answers the open feature, so it follows that feature's set.
	/// </summary>
	private void FaceDragFrame()
	{
		if ( !FaceDragEnabled || FaceDragMoved is null || SelectedFaces is not { Count: > 0 } )
		{
			EndFaceDrag();
			return;
		}

		if ( (IsSketching || IsSculpting || IsPainting || IsMaterialBrushing || IsNoting || BoneToolActive) && !_draggingFace )
		{
			EndFaceDrag();
			return;
		}

		// MID-DRAG THE FACE IS NOT WHERE ITS REFERENCE SAYS IT IS. A FaceRef resolves geometrically,
		// by plane and anchor point, and the face this one names is travelling several units away
		// from both while the button is held. Re-resolving every frame would eventually find nothing
		// — or worse, find a DIFFERENT face — and the drag would abort halfway or jump to the far
		// side of the part. So the axis is read once, when the drag starts, and held.
		if ( _draggingFace )
		{
			DrawFaceHandle( _faceDragAnchor + _faceDragDelta, _faceDragNormal );
			return;
		}

		if ( !TryFaceHandle( out var centre, out var normal ) )
		{
			EndFaceDrag();
			return;
		}

		DrawFaceHandle( centre, normal );
	}

	/// <summary>
	/// The arrow, and the drag it reports.
	///
	/// ANCHORED WHERE THE DRAG STARTED plus what has been dragged so far — never at the face's live
	/// centroid. The face moves as you drag it, so re-reading the centroid every frame would add that
	/// movement to the handle a second time and the arrow would run away from the cursor.
	///
	/// THE SCOPE CARRIES NO ROTATION, so the axis handed to Arrow is the world-space normal as it
	/// stands. That is why this no longer needs the Rotation.LookAt dance the three-axis version did:
	/// there is no basis to build when there is only one axis to point.
	///
	/// ONE ARROW STILL PUSHES BOTH WAYS. Dragging back past the tail gives a negative distance and
	/// the face goes in, which is what Onshape's push-pull does with the same single arrow. A second
	/// arrow along -normal would say so more loudly and would also double the hitboxes at the exact
	/// spot the user is aiming; not worth it.
	/// </summary>
	private void DrawFaceHandle( Vector3 origin, Vector3 normal )
	{
		using var scope = Gizmo.Scope( "face-drag", new Transform( origin ) );

		Gizmo.Hitbox.DepthBias = 0.01f;

		if ( Gizmo.Control.Arrow( "face-pull", normal, out var distance ) )
		{
			if ( !_draggingFace )
			{
				_draggingFace = true;
				_faceDragAnchor = origin;
				_faceDragNormal = normal;
				_faceDragDelta = Vector3.Zero;
				FaceDragBegan?.Invoke();
			}

			// Accumulated rather than assigned, for the same reason Position's displacement was:
			// these controls report the change since the last frame and return false on any frame
			// the value did not move (RigViewport.cs:1621). The axis is the one the drag STARTED
			// with, not the live normal, so the total stays a straight line even though the face
			// it was taken from is travelling.
			_faceDragDelta += _faceDragNormal * distance;

			FaceDragMoved.Invoke(
				new Vec3( _faceDragDelta.x, _faceDragDelta.y, _faceDragDelta.z ),
				new Vec3( _faceDragNormal.x, _faceDragNormal.y, _faceDragNormal.z ) );
			return;
		}

		EndFaceDrag();
	}

	private void EndFaceDrag()
	{
		if ( !_draggingFace )
			return;

		_draggingFace = false;
		_faceDragDelta = Vector3.Zero;
		FaceDragEnded?.Invoke();
	}

	/// <summary>
	/// Where the handle sits and which way it points: the centroid of the selected faces, and the
	/// average of their normals.
	///
	/// AVERAGED RATHER THAN TAKEN FROM THE FIRST, because a selection of several faces has no first
	/// in any sense the user would recognise — clicking the same two faces in the other order would
	/// point the arrow somewhere else. On the facing pair that Move Face exists for, the two normals
	/// cancel and there is no meaningful axis at all; that case falls back to the first face's normal
	/// so the arrow still points along the wall's thickness, which is the direction that means
	/// something there.
	/// </summary>
	private bool TryFaceHandle( out Vector3 centre, out Vector3 normal )
	{
		centre = Vector3.Zero;
		normal = Vector3.Up;

		var sum = Vec3.Zero;
		var normals = Vec3.Zero;
		var first = Vec3.Zero;
		var found = 0;

		foreach ( var reference in SelectedFaces )
		{
			if ( !FacePlane.TryResolveFace( _displayBodies, reference, out var body, out var index ) )
				continue;

			var face = body.Mesh.Faces[index];
			var n = body.Mesh.FaceNormal( face );

			sum += body.Mesh.FaceCentroid( face );
			normals += n;

			if ( found == 0 )
				first = n;

			found++;
		}

		if ( found == 0 )
			return false;

		var mid = sum / found;
		var axis = normals.LengthSquared > 1e-6f ? normals.Normal : first;

		centre = new Vector3( mid.x, mid.y, mid.z );
		normal = new Vector3( axis.x, axis.y, axis.z );

		return true;
	}
}
