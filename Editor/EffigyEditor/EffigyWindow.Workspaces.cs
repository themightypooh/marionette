using Editor;
using Effigy;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.EditorTools;

// ============================================================================
//  Workspaces — CAD, Sculpt, Paint, Rig.
//
//  WHY THIS FILE EXISTS AT ALL. Every toolset the editor grew went onto the one
//  stage bar, and the only thing that said which was showing was a word painted
//  small at the right of the tab row. Rig did not even get that: its tools lived
//  in a right-hand dock while every other toolset lived in the bar, so starting
//  a rig was a different KIND of action from starting a sculpt for no reason a
//  user could see. EffigyWorkspaceBar's header has the rest of that argument.
//
//  WHAT A WORKSPACE DRIVES. Three things, and the point of putting them together
//  is that they were previously three unrelated decisions made in three places:
//
//    1. THE STAGE BAR — which stage set is on it. Already existed as
//       EffigyBarMode; this file adds the fourth member and its stage set.
//    2. THE DOCKS — Materials matters in Paint, the rig tree matters in Rig, and
//       neither wants to be open the rest of the time. Per-workspace, remembered.
//    3. THE VIEWPORT'S INPUT — which of its eleven partial files owns a click.
//       Enforced AT THE DOOR (see LeaveCurrentWorkspace) rather than by each
//       handler re-checking the others, which is what it grew into.
//
//  NO SEPARATE WINDOWS, AND NO SECOND VIEWPORT. That was the first idea and it
//  is the wrong one. Each viewport is a live scene render, so four of them is
//  four times the GPU for panes nobody is looking at; worse, one mesh behind four
//  cameras means four selections, four undo stacks and four answers to "which
//  body is active", and the undo snapshot in EffigyWindow is window-global. And
//  the pipeline is not actually linear — paint reveals a bad face and you go back
//  to re-extrude it — so separate windows would tax the move people make most.
//  One window, one document, four ways of looking at it.
//
//  THE WORKSPACE IS NOT STORED. It is derived from _barMode, which the enter and
//  finish paths already maintain and already treat as the truth. A second field
//  would be a second thing to keep in step, and the one bug this whole change is
//  meant to make impossible is chrome disagreeing with mode.
// ============================================================================

public sealed partial class EffigyWindow
{
	private EffigyWorkspaceBar _workspaceBar;

	/// <summary>The rig stage set, built once at startup alongside the other four.</summary>
	private List<EffigyStage> _rigStages;

	private EffigyStageTool _boneTool, _boneAssignTool, _boneMirrorTool, _boneDeleteTool;
	private EffigyStageTool _boneSoftTool, _softPreviewTool, _softRestTool;

	/// <summary>Which part-studio stage was last looked at in the Rig workspace, so leaving and
	/// coming back lands where you were — same courtesy _partStage does for CAD.</summary>
	private int _rigStage;

	/// <summary>
	/// Which workspace the window is in, derived rather than stored.
	///
	/// SKETCH READS AS CAD. A sketch is opened inside CAD and finished again — the stage bar goes
	/// somewhere else for the duration, but you did not leave the workspace, and lighting a
	/// different pill in the switcher while someone draws a rectangle would be the switcher lying
	/// about a thing it exists to state.
	/// </summary>
	private EffigyWorkspace CurrentWorkspace => BarMode switch
	{
		EffigyBarMode.Sculpt => EffigyWorkspace.Sculpt,
		EffigyBarMode.Paint => EffigyWorkspace.Paint,
		EffigyBarMode.Rig => EffigyWorkspace.Rig,
		_ => EffigyWorkspace.Cad,
	};

	/// <summary>
	/// The bar mode, and the one place that notices it changed.
	///
	/// A PROPERTY RATHER THAN THE FIELD IT WAS, because six different methods assign it and every
	/// one of them would otherwise have to remember to re-light the switcher and re-lay the docks
	/// afterwards. Five of them remembering and one forgetting is precisely the failure this
	/// change exists to remove, and "you must also call SyncWorkspace" is a rule that only holds
	/// until the next tool gets added. Here it cannot be forgotten because there is nowhere to
	/// forget it.
	/// </summary>
	private EffigyBarMode BarMode
	{
		get => _barMode;
		set
		{
			if ( _barMode == value )
				return;

			var before = CurrentWorkspace;

			_barMode = value;

			var after = CurrentWorkspace;

			SyncViewportMode();

			if ( before != after )
				ApplyWorkspaceDocks( before, after );

			if ( _workspaceBar is not null )
				_workspaceBar.Selected = after;
		}
	}

	private EffigyBarMode _barMode = EffigyBarMode.Part;

	/// <summary>
	/// Push the workspace down onto the viewport's input, where a bone is the only clickable thing
	/// in the rig workspace and the CAD selection runs everywhere else. See EffigyViewport.RigMode.
	///
	/// NOT ONLY FROM THE BarMode SETTER, which is where it belongs and is not sufficient on its
	/// own: that setter returns early when the mode has not changed, so a hotload taken while the
	/// rig workspace was already open would leave a freshly-defaulted RigMode at false with nothing
	/// left to switch it back on - the workspace would look right and behave as though it were CAD.
	/// Called from RebuildStages for exactly that, and it is cheap enough to call from anywhere.
	/// </summary>
	private void SyncViewportMode()
	{
		if ( _viewport is not null )
			_viewport.RigMode = CurrentWorkspace == EffigyWorkspace.Rig;
	}

	// --- switching ------------------------------------------------------------------------------

	/// <summary>
	/// A workspace was asked for. This is the whole of what a pill click means.
	///
	/// ASKED FOR, NOT SET. Sculpt and Paint can both refuse at the door — a sculpt feature whose
	/// cage did not build, a body whose UVs cannot carry paint — and when they do, EnterSculpt and
	/// EnterPaint leave the bar mode alone and put the reason in the prompt. Because the switcher
	/// paints from CurrentWorkspace, which is derived from that mode, a refusal simply leaves the
	/// old pill lit. Nothing here has to know which refusals exist.
	/// </summary>
	private void SetWorkspace( EffigyWorkspace workspace )
	{
		if ( _viewport is null || _studio is null )
			return;

		// Re-clicking the workspace you are already in is not a no-op: it is the only way to ask
		// for the dock layout back after dragging it around, and it is what someone reaches for
		// when a panel has gone missing. Everything downstream is idempotent.
		if ( workspace == CurrentWorkspace )
		{
			ApplyWorkspaceDocks( workspace, workspace, force: true );
			return;
		}

		switch ( workspace )
		{
			case EffigyWorkspace.Cad:
				LeaveCurrentWorkspace();
				ShowPartStages( force: true );
				break;

			case EffigyWorkspace.Sculpt:
				EnterSculptWorkspace();
				break;

			case EffigyWorkspace.Paint:
				EnterPaintWorkspace();
				break;

			case EffigyWorkspace.Rig:
				EnterRig();
				break;
		}

		// The switcher is painted from CurrentWorkspace, and CurrentWorkspace only moved if the
		// entry above actually took. A refusal leaves the pill where it was, which is the honest
		// answer — see the summary.
		if ( _workspaceBar is not null )
			_workspaceBar.Selected = CurrentWorkspace;
	}

	/// <summary>
	/// Close whatever session owns the pointer right now.
	///
	/// THE EXCLUSIVITY, IN ONE PLACE. Sketching, sculpting, painting and the bone tool all claim a
	/// left-click in the viewport, and each of the four entry points used to carry its own partial
	/// list of the other three to shut down first — EnterSculpt finished sketches and cancelled the
	/// bone tool, AddPaint finished sketches and sculpts, EnterPaint did both again, and nothing
	/// anywhere finished a paint before arming a bone. Four hand-maintained lists of three is four
	/// chances to miss one, and the one that was missed is exactly the pair that could both be live.
	///
	/// Every entry point now calls this instead. Adding a fifth pointer-owning mode later means
	/// adding one line here rather than finding four lists.
	/// </summary>
	private void LeaveCurrentWorkspace()
	{
		if ( _viewport is null )
			return;

		if ( _viewport.IsSketching )
			FinishSketch();

		if ( _viewport.IsSculpting )
			FinishSculpt();

		if ( _viewport.IsPainting )
			FinishPaint();

		// The material brush has no feature and no session to commit, so leaving it is only a
		// matter of disarming it - but it MUST be disarmed, or the ring keeps taking clicks in
		// whatever workspace you switched to.
		if ( _viewport.IsMaterialBrushing )
			LeaveMaterialBrush();

		_rigPanel?.CancelBoneTool();

		// The rig bar is the one mode with nothing to finish — no feature, no session, just a stage
		// set on the bar — so leaving it is only a matter of not still claiming to be in it. The
		// CAD stages are what everything falls back to.
		if ( BarMode == EffigyBarMode.Rig )
			BarMode = EffigyBarMode.Part;
	}

	// --- entering sculpt and paint from the switcher ---------------------------------------------

	/// <summary>
	/// "Sculpt" as a workspace, given that sculpting is a thing you do to a FEATURE.
	///
	/// This is the one place the switcher is not a plain mode flip, and it is worth being explicit
	/// about why: there is no ambient state called sculpting. EnterSculpt needs a SculptFeature,
	/// which needs a body under it and a spot in the tree. So the pill has to resolve one, and the
	/// order it tries is the order that makes the fewest surprises:
	///
	///   1. The sculpt you were last in this session, if it is still in the tree. Coming back to
	///      where you were is what leaving and returning should mean.
	///   2. The only sculpt in the document, if there is exactly one. With one candidate there is
	///      nothing to choose and asking would be ceremony.
	///   3. Otherwise land on the workspace's own bar — Subdivide and Sculpt — which is where those
	///      tools live now that they have left CAD. The brushes arrive once a sculpt is open.
	///
	/// Deliberately NOT "the last sculpt in the tree" for case 2 when there are several: which of
	/// three sculpts you meant is a real question, and guessing at it silently rolls the model back
	/// to a different place than you expected. With several and no memory, the home bar is the
	/// answer that cannot be wrong about your intent.
	/// </summary>
	private void EnterSculptWorkspace()
	{
		LeaveCurrentWorkspace();

		var sculpts = _studio.Features.OfType<SculptFeature>().ToList();

		var target = _lastSculptFeature is not null && sculpts.Contains( _lastSculptFeature )
			? _lastSculptFeature
			: sculpts.Count == 1 ? sculpts[0] : null;

		if ( target is not null )
		{
			EnterSculpt( target );
			return;
		}

		// Nothing to re-enter: land on the sculpt workspace's own bar, where Subdivide and Sculpt
		// live. The brushes arrive once a sculpt is actually open, not with the workspace itself.
		ShowSculptHome();
	}

	/// <summary>"Paint" as a workspace. Same three-step resolution as sculpt, for the same reason —
	/// see EnterSculptWorkspace, which carries the argument for both.</summary>
	private void EnterPaintWorkspace()
	{
		LeaveCurrentWorkspace();

		var paints = _studio.Features.OfType<PaintFeature>().ToList();

		var target = _lastPaintFeature is not null && paints.Contains( _lastPaintFeature )
			? _lastPaintFeature
			: paints.Count == 1 ? paints[0] : null;

		if ( target is not null )
		{
			EnterPaint( target );
			return;
		}

		ShowPaintHome();
	}

	/// <summary>
	/// Land on the Sculpt workspace's own bar, with no feature open.
	///
	/// BarMode.Sculpt with nothing sculpting is a state that did not exist before the landing bars:
	/// the workspace now has two faces — this home (Subdivide and Sculpt) and the brushes, which
	/// <see cref="EnterSculpt"/> swaps to once a feature is open. RebuildStages tells the two apart
	/// by asking the viewport whether a sculpt is actually running.
	/// </summary>
	private void ShowSculptHome()
	{
		if ( _stageBar is null )
			return;

		BarMode = EffigyBarMode.Sculpt;

		_stageBar.Mode = "SCULPT";
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _sculptHomeStages );

		SetPrompt( _studio.Bodies.Count == 0
			? "Sculpt needs a body — draw a sketch and extrude it, or add a primitive first."
			: "Sculpt: add a Subdivide to build a cage, or a Sculpt to brush detail onto one." );
	}

	/// <summary>The Paint workspace's home — UV Project and Paint — with no feature open.</summary>
	private void ShowPaintHome()
	{
		if ( _stageBar is null )
			return;

		BarMode = EffigyBarMode.Paint;

		_stageBar.Mode = "PAINT";
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _paintHomeStages );

		SetPrompt( _studio.Bodies.Count == 0
			? "Paint needs a body — draw a sketch and extrude it, or add a primitive first."
			: "Paint: add a UV Project to unwrap the mesh, or a Paint to brush colour on." );
	}

	/// <summary>
	/// The sculpt and paint features last entered, so the switcher can come back to them.
	///
	/// SEPARATE FROM _sculptFeature and _paintFeature, which are cleared on Finish — those answer
	/// "what am I editing", this answers "what was I editing", and the switcher needs the second
	/// one precisely when the first is null. Held as a reference and checked against the tree
	/// before use, because Undo can take the feature away without telling anyone.
	/// </summary>
	private SculptFeature _lastSculptFeature;

	private PaintFeature _lastPaintFeature;

	// --- the rig workspace ------------------------------------------------------------------------

	/// <summary>
	/// The rig tools, moved out of the panel's header and onto the bar.
	///
	/// SAME ACTIONS, NOT A SECOND COPY. Every one of these calls straight into EffigyRigPanel,
	/// which still owns the skeleton, the pending chain and every refusal — see the block above
	/// CancelBoneTool there. The panel keeps its buttons too: a bar tool and a panel button running
	/// one method is one control reachable from wherever you are looking, and the panel is where
	/// you are looking when you have just clicked a bone in the tree.
	///
	/// THREE TOOLS AND TWO STAGES, not the four-stage set the other workspaces run to. There is no
	/// Pose stage because there is no posing yet — the gizmo in EffigyViewport drags a bone that
	/// already exists and that is all — and a tab with nothing behind it is the thing this editor's
	/// icon set already refuses to draw. Stages get added when tools do.
	/// </summary>
	private List<EffigyStage> BuildRigStages()
	{
		var bones = new EffigyStage { Name = "Bones" };

		_boneTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Bone,
			Label = "Add Bone",
			Tip = "Click the model to place a bone. Click again to extend a chain from it. "
				+ "Select a bone first to branch a new chain from ITS tail.",
			Checkable = true,
			Clicked = () =>
			{
				_rigPanel?.ToggleBoneTool();
				UpdateRigChecks();
			},
		};

		bones.Add( _boneTool );

		_boneDeleteTool = new EffigyStageTool
		{
			Icon = EffigyIcon.CutTool,
			Label = "Delete",
			Tip = "Delete the selected bone. Its children re-parent to its parent.",
			// No RecordUndo here, unlike most destructive buttons in this window: DeleteBone fires
			// RigChanging on its way in, and that IS RecordUndo (wired in BuildDocks). Recording
			// again would put two identical snapshots on the stack and cost two Ctrl+Zs to undo one
			// delete. Same reason Mirror does not record either.
			Clicked = () =>
			{
				_rigPanel?.DeleteSelected();
				UpdateRigChecks();
			},
		};

		bones.Add( _boneDeleteTool );

		var bind = new EffigyStage { Name = "Bind" };

		_boneAssignTool = new EffigyStageTool
		{
			Icon = EffigyIcon.BoneBind,
			Label = "Assign Body",
			Tip = "Click bodies in the viewport to pin them to the selected bone. "
				+ "Anything left unassigned falls back to the nearest bone.",
			Checkable = true,
			Clicked = () =>
			{
				_rigPanel?.ToggleAssignBodyTool();
				UpdateRigChecks();
			},
		};

		bind.Add( _boneAssignTool );

		_boneMirrorTool = new EffigyStageTool
		{
			Icon = EffigyIcon.Mirror,
			Label = "Mirror",
			Tip = "Mirror the selected bone and everything under it across the centre line, "
				+ "onto the same parent.",
			Clicked = () =>
			{
				_rigPanel?.MirrorSelected();
				UpdateRigChecks();
			},
		};

		bind.Add( _boneMirrorTool );

		// --- Soft ---
		//
		// The stage the kernel has been waiting for. SoftBone, SoftPose and SoftSolver have been
		// written, tested and shipped to the game assembly since before this bar existed, and
		// RigDiagnostics has been checking soft bones the whole time - it will tell you one has a
		// zero cone - while nothing in the editor could make a bone soft for it to complain about.
		//
		// THE NUMBERS ARE NOT HERE. Stiffness, damping, weight and cone are properties of ONE bone,
		// the way its head and tail are, so they live in the rig panel's inspector beside them
		// rather than on a bar that is about verbs. What the bar gets is the verb - make this bone
		// soft - and the two controls that only make sense while something is wobbling.
		var soft = new EffigyStage { Name = "Soft" };

		_boneSoftTool = new EffigyStageTool
		{
			Icon = EffigyIcon.BoneSoft,
			Label = "Make Soft",
			Tip = "Let the selected bone lag and swing behind the pose. "
				+ "Stiffness, damping, weight and cone are in the Rig panel under the bone's head and tail.",
			Checkable = true,
			Clicked = () =>
			{
				_rigPanel?.ToggleSelectedSoft();
				UpdateRigChecks();
			},
		};

		soft.Add( _boneSoftTool );

		_softPreviewTool = new EffigyStageTool
		{
			Icon = EffigyIcon.SoftPreview,
			Label = "Preview",
			Tip = "Run the soft-bone solver on the rig, so you can see the swing while you tune it. "
				+ "Drag the part around to push the bones.",
			Checkable = true,
			Clicked = () =>
			{
				ToggleSoftPreview();
				UpdateRigChecks();
			},
		};

		soft.Add( _softPreviewTool );

		_softRestTool = new EffigyStageTool
		{
			Icon = EffigyIcon.SoftRest,
			Label = "Rest",
			Tip = "Forget the motion and put every soft bone back on its pose. "
				+ "What you want after flinging the model about.",
			Clicked = () =>
			{
				_viewport?.RestSoftPreview();
				UpdateRigChecks();
			},
		};

		soft.Add( _softRestTool );

		return new List<EffigyStage> { bones, bind, soft };
	}

	/// <summary>
	/// Start or stop the soft-bone preview, and say so in the prompt.
	///
	/// The prompt is doing real work here rather than narrating: the preview is driven by gravity
	/// and by the pose gizmo and by nothing else, which is the right design (see
	/// EffigyViewport.SoftPreview.cs) but is not guessable from a button called Preview. A rig that
	/// sags an inch and stops looks like a broken preview until you know that settling is the
	/// point.
	/// </summary>
	private void ToggleSoftPreview()
	{
		if ( _viewport is null )
			return;

		SetPrompt( _viewport.ToggleSoftPreview()
			? "Soft preview: gravity is pulling the soft bones. Drag a bone to make the ones below it swing. "
				+ "Rest puts them back."
			: "" );
	}

	/// <summary>
	/// Push the panel's state onto the bar.
	///
	/// The panel is the one that knows, and it changes its mind without being asked — Escape closes
	/// a chain, clicking a different bone in the tree disarms an assign — so this is wired to its
	/// ToolStateChanged as well as being called after every tool click. A tick that can go stale
	/// on the two commonest gestures in the workspace is worse than no tick.
	/// </summary>
	private void UpdateRigChecks()
	{
		if ( _rigPanel is null )
			return;

		var hasBone = _rigPanel.HasSelectedBone;

		if ( _boneTool is not null )
		{
			_boneTool.Checked = _rigPanel.BoneToolActive;

			// The panel's own button rewrites itself to "Branch from 'upper_arm'" when a bone is
			// selected, because that is the gesture nobody discovers on their own. Worth carrying
			// onto the bar for the same reason — but on the TIP rather than the label, which has a
			// measured button around it that would jump width on every selection change.
			_boneTool.Tip = hasBone
				? $"Click the model to extend a new chain from '{_rigPanel.SelectedBoneName}'. Escape when done."
				: "Click the model to place a bone. Click again to extend the chain. "
					+ "Select a bone first to branch from its tail.";
		}

		if ( _boneAssignTool is not null )
			_boneAssignTool.Checked = _rigPanel.AssigningBody;

		if ( _boneSoftTool is not null )
			_boneSoftTool.Checked = _rigPanel.SelectedBoneIsSoft;

		if ( _softPreviewTool is not null )
			_softPreviewTool.Checked = _viewport?.SoftPreviewRunning ?? false;

		// Preview and Rest are about the rig as a whole rather than the selection, so they follow a
		// different rule from the three below: something to simulate, not something selected. A rig
		// with no soft bones would preview a skeleton that cannot move, which is a button that
		// appears to do nothing.
		var anySoft = _rigPanel.SoftBoneCount > 0;

		foreach ( var tool in new[] { _softPreviewTool, _softRestTool } )
		{
			if ( tool is null )
				continue;

			tool.Enabled = anySoft;
			tool.DisabledReason = anySoft ? null : "Nothing is soft yet - select a bone and press Make Soft";
		}

		// Assign, Mirror and Delete all act on "the selected bone" and do nothing without one. The
		// lock reason is the tooltip, the same contract the CAD stages' starter lock uses.
		foreach ( var tool in new[] { _boneAssignTool, _boneMirrorTool, _boneDeleteTool, _boneSoftTool } )
		{
			if ( tool is null )
				continue;

			tool.Enabled = hasBone;
			tool.DisabledReason = hasBone ? null : "Select a bone first — in the viewport or the Rig panel";
		}

		_stageBar?.Refresh();
	}

	/// <summary>
	/// Enter the rig workspace.
	///
	/// THE ONLY WORKSPACE WITH NOTHING TO OPEN. Sculpt and paint are scoped to a feature and roll
	/// the model back to it; a rig is one skeleton over the whole finished studio, owned by the
	/// panel for the life of the window. So this is a plain mode change — which is what makes it
	/// the odd one out in the other direction, and worth saying rather than leaving as an absence
	/// somebody later reads as a missing rollback.
	/// </summary>
	private void EnterRig()
	{
		if ( _viewport is null || _stageBar is null )
			return;

		LeaveCurrentWorkspace();

		// A dialog and the rig bar would be two things claiming the model at once, the same
		// argument EnterSculpt makes for closing it.
		_dialog?.Close();

		BarMode = EffigyBarMode.Rig;

		// Whatever was picked in CAD is still lit, and in a workspace where faces can no longer be
		// clicked a highlighted face is a selection you cannot clear by clicking off it. It is also
		// what the face-drag arrow hangs off, which is one more thing over the model competing for
		// a click meant for a bone.
		_viewport.ClearIdleSelection();

		_stageBar.Mode = "RIG";

		// NO FINISH BUTTON. Finish means "commit what you have been doing to the feature tree", and
		// there is no feature here to commit to — every bone edit already landed on the skeleton
		// when it was made. A green tick that only changed which tools were on screen would be
		// teaching the wrong thing about what green means in this editor.
		_stageBar.SetFinish( null, null );
		_stageBar.SetStages( _rigStages, _rigStage );

		UpdateRigChecks();

		SetPrompt( _rigPanel is { HasBones: true }
			? "Rig: click a bone to select it, or place more with Add Bone."
			: "Rig: press Add Bone and click the model to place your first bone." );
	}

	// --- dock layouts ----------------------------------------------------------------------------

	/// <summary>
	/// Which docks each workspace opens with.
	///
	/// A STARTING POINT, NOT A CAGE. Whatever you do to the docks while you are in a workspace is
	/// captured on the way out and restored on the way back (see ApplyWorkspaceDocks), so these
	/// values only decide what the FIRST visit looks like. A layout table that overrode the user
	/// every time would be the tool rearranging itself under someone who had already arranged it.
	///
	/// Tutorial and Console are deliberately absent. They are not about which part of the pipeline
	/// you are in — they are open because you opened them — so a workspace switch leaves them
	/// exactly as it found them.
	/// </summary>
	private static readonly Dictionary<EffigyWorkspace, (bool Features, bool Materials, bool Rig)> WorkspaceDocks = new()
	{
		// The feature tree is on in three of four: it is the document's history, and going back to
		// re-extrude something is the move that made a single window right in the first place.
		[EffigyWorkspace.Cad] = (Features: true, Materials: false, Rig: false),
		[EffigyWorkspace.Sculpt] = (Features: true, Materials: false, Rig: false),
		[EffigyWorkspace.Paint] = (Features: true, Materials: true, Rig: false),

		// Rig is the exception. The skeleton tree wants the right-hand side to itself, and by the
		// time you are placing bones the feature tree is history you are no longer editing.
		[EffigyWorkspace.Rig] = (Features: false, Materials: false, Rig: true),
	};

	/// <summary>What the docks looked like the last time each workspace was left.</summary>
	private readonly Dictionary<EffigyWorkspace, (bool Features, bool Materials, bool Rig)> _workspaceDocks = new();

	/// <summary>
	/// Capture the layout the old workspace is being left in, then lay out the new one.
	///
	/// The capture is what makes the table above a default rather than a rule: open the Materials
	/// browser while sculpting and it is still there next time you sculpt, without any of this
	/// having to know you did it.
	/// </summary>
	private void ApplyWorkspaceDocks( EffigyWorkspace from, EffigyWorkspace to, bool force = false )
	{
		if ( _rigPanel is null || _materialsPanel is null )
			return; // Still building. BuildDocks lays out the first workspace itself.

		if ( from != to )
			_workspaceDocks[from] = ReadDockState();

		var wanted = _workspaceDocks.TryGetValue( to, out var remembered ) && !force
			? remembered
			: WorkspaceDocks[to];

		SetDockIfChanged( "Features", wanted.Features );
		SetDockIfChanged( "Materials", wanted.Materials );
		SetDockIfChanged( "Rig", wanted.Rig );

		// The one dock a workspace is actually ABOUT gets raised, not merely opened — it may be
		// tabbed behind another on the same edge, and an open-but-hidden panel is the same as a
		// closed one to the person looking for it.
		switch ( to )
		{
			case EffigyWorkspace.Paint: DockManager.RaiseDock( "Materials" ); break;
			case EffigyWorkspace.Rig: DockManager.RaiseDock( "Rig" ); break;
		}

		// The View menu's ticks were written for the layout that just went away.
		SyncDockChecks();
	}

	private (bool Features, bool Materials, bool Rig) ReadDockState() =>
		(DockManager.IsDockOpen( "Features" ),
			DockManager.IsDockOpen( "Materials" ),
			DockManager.IsDockOpen( "Rig" ));

	/// <summary>Asked before set, because SetDockState on a dock already in that state still costs
	/// a relayout — and four of those per switch is a visible flicker on a window this size.
	/// </summary>
	private void SetDockIfChanged( string title, bool open )
	{
		if ( DockManager.IsDockOpen( title ) != open )
			DockManager.SetDockState( title, open );
	}
}
