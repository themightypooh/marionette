using Editor;
using Marionette;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Tools;

/// <summary>
/// The strip along the bottom of the window: what you're hovering on the left, the current
/// tutorial step on the right.
///
/// This is the answer to the documentation being overwhelming. The old approach put every
/// explanation on screen at once, before the reader had done anything or had any reason to care
/// about most of it - so it read as a wall and got skipped, which means it may as well not have
/// been written. Same words, delivered one at a time at the moment they're relevant, are
/// something people actually read. Modelled on the in-game PressApp's status bar.
/// </summary>
/// Derives from Editor.StatusBar rather than plain Widget because that's the only type
/// Window.StatusBar accepts, and Window.StatusBar is the only place a DockWindow will put a
/// full-width strip - the dock manager owns everything else in the client area.
internal sealed class RigStatusBar : Editor.StatusBar
{
	/// <summary>Set by whatever the mouse is over. Static because the things that want to explain
	/// themselves - bone dots in a gizmo viewport, keyframes on a custom-painted timeline - are
	/// scattered across widgets that have no reference to this bar and shouldn't need one.</summary>
	public static string Hint { get; private set; }

	private static RigStatusBar _instance;

	public static void Show( string hint )
	{
		if ( Hint == hint )
			return;

		Hint = hint;
		_instance?.Update();
	}

	public static void Clear( string ifMatching = null )
	{
		if ( ifMatching is not null && Hint != ifMatching )
			return;

		Hint = null;
		_instance?.Update();
	}

	public RigTutorial Tutorial { get; set; }

	/// <summary>
	/// A Button that reports itself to the status bar on hover.
	///
	/// s&amp;box's Widget exposes OnMouseEnter/OnMouseLeave as virtuals, not as events, so there's no
	/// way to attach a hint to a stock Button from outside - it has to be a subclass. Hence this:
	/// every control built with it explains itself in the bar, instead of the bar only ever
	/// describing the viewport (which was the complaint - most of the tool was silent).
	/// </summary>
	public sealed class HintButton : Button
	{
		private readonly string _hint;

		public HintButton( string icon, string hint, Action clicked, Widget parent = null, string text = "" )
			: base( text, icon, parent )
		{
			_hint = hint;

			// Native tooltip as well as the status bar - the bar is easy to miss while your eyes
			// are on the control you're hovering.
			ToolTip = hint;
			Clicked = clicked;
		}

		/// <summary>Tints it, for marking the one control in a row that isn't like the others.</summary>
		public HintButton WithAccent( Color color )
		{
			SetStyles( $"color: {color.Hex}; font-weight: 600;" );
			return this;
		}

		// Qualified - unqualified Show/Clear would bind to Widget's own Show(), which does
		// something entirely different.
		protected override void OnMouseEnter()
		{
			base.OnMouseEnter();
			RigStatusBar.Show( _hint );
		}

		protected override void OnMouseLeave()
		{
			base.OnMouseLeave();
			RigStatusBar.Clear( _hint );
		}
	}

	public RigStatusBar( Widget parent ) : base( parent )
	{
		_instance = this;
		FixedHeight = 24;
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( _instance == this )
			_instance = null;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceBackground );
		Paint.DrawRect( LocalRect );

		Paint.SetPen( Theme.WindowBackground );
		Paint.DrawLine( new Vector2( 0f, 0f ), new Vector2( Width, 0f ) );

		// HOVER HINTS ONLY. The tutorial used to share this strip and was unreadable in it - one
		// line of text at the bottom of the window, competing with whatever the cursor was over,
		// in a band the eye reads as chrome. It lives in its own dock now, where it can be looked
		// at. This bar does the one thing a status bar is good at: saying what's under the cursor.
		Paint.SetDefaultFont( 9 );

		if ( !string.IsNullOrWhiteSpace( Hint ) )
		{
			Paint.SetPen( Theme.TextControl );
			Paint.DrawText( new Rect( 12f, 0f, Width - 24f, Height ), Hint, TextFlag.LeftCenter );
			return;
		}

		Paint.SetPen( Theme.TextControl.WithAlpha( 0.35f ) );
		Paint.DrawText( new Rect( 12f, 0f, Width - 24f, Height ), "Ready", TextFlag.LeftCenter );
	}
}

/// <summary>
/// A guided build of one real animation - a wave - rather than a description of what the buttons
/// do.
///
/// Steps advance by WATCHING THE DOCUMENT, not by the reader clicking "Next". You can't tick a
/// step off without having actually done it, so nobody arrives at the end having read six things
/// and animated nothing. The pose-to-pose order it walks through (extremes first, in-betweens
/// after) is the order that makes hand-keyed animation read, and doing it once in the right order
/// teaches more than a paragraph saying so.
/// </summary>
internal sealed class RigTutorial
{
	/// <summary>A drawn glyph per step. Painted rather than an image asset so it ships with the
	/// code, scales with the panel and follows the editor theme - and so the tool has no binary
	/// art to keep in sync with anything.</summary>
	public enum StepArt
	{
		Model,
		Bone,
		Rotate,
		Keyframe,
		Play
	}

	public sealed class Step
	{
		public string Instruction { get; init; }

		/// <summary>The actions, one per bullet. Kept separate from the prose because they are
		/// different things doing different jobs: this is what you DO, and it should be scannable
		/// in a glance without reading a sentence.</summary>
		public string[] Bullets { get; init; }

		/// <summary>The why, in one or two lines under the bullets. Instructions tell you what to
		/// press; this is the part that means you still know what you're doing afterwards.</summary>
		public string Detail { get; init; }

		public StepArt Art { get; init; }

		/// <summary>Dock this step is about, so the panel can offer to open it. Answers "where is
		/// that?", which is the question a written instruction always leaves behind.</summary>
		public string Panel { get; init; }

		/// <summary>True once the reader has actually done this.</summary>
		public Func<RigAnimDocument, string, bool> IsDone { get; init; }
	}

	private readonly List<Step> _steps;

	public RigTutorial()
	{
		_steps = new List<Step>
		{
			new()
			{
				Instruction = "To pose a bone, you first have to select it",
				Bullets = new[]
				{
					"Click its dot in the viewport",
					"Or click its name in the timeline's left column",
					"Or click it in the bone tree in the BonesObject tab"
				},
				Detail = "All three do the same thing. The selected bone turns yellow and gets a gizmo you can drag. Select arm_upper_R - the right shoulder - to carry on.",
				Art = StepArt.Bone,
				IsDone = ( _, bone ) => !string.IsNullOrEmpty( bone )
			},
			new()
			{
				Instruction = "To reach for something, you need something to reach for",
				Bullets = new[]
				{
					"In the BonesObject tab, add a Reference Prop",
					"Pick models/lightswitch/lightswitch_plate.vmdl",
					"Drag its green dot in the viewport, out in front of the hand"
				},
				Detail = "Reference props are shown in the viewport only - never keyed, never exported. Posing at a real object beats imagining where one would be.",
				Art = StepArt.Model,
				Panel = "BonesObject",

				// Assigning a model isn't the step - PLACING it is. This used to tick the instant a
				// model was picked, while the prop was still sitting at the origin inside the
				// model's chest, so the reader was waved on to "pose at the switch" with nothing
				// to pose at. A prop still at 0,0,0 has not been placed.
				IsDone = ( anim, _ ) => anim?.ReferenceProps?.Any( p =>
					p?.Model is not null && p.Position.Length > 1f ) ?? false
			},
			new()
			{
				Instruction = "Every action needs a pose to leave from and come back to",
				Bullets = new[]
				{
					"Go to frame 0",
					"Press K"
				},
				Detail = "That keys the pose the arm already has - no posing needed. You'll copy this exact key to the end of the clip later so the whole thing settles back.",
				Art = StepArt.Keyframe,
				Panel = "Timeline",
				IsDone = ( anim, _ ) => KeyNear( anim, 0, 2 )
			},
			// THE BACK HALF USED TO BE SEVEN STEPS AND IS NOW THREE.
			//
			// It ran anticipation, reach, wrist, contact, overshoot, loop-back, timing - each with
			// a paragraph explaining why the beat matters. Every one of those is true and none of
			// them belong in a first run. The reader is trying to find out whether they can make
			// the arm move at all, and seven principle-steps between them and a thing that plays
			// reads as homework.
			//
			// So: reach, wrist, play. Enough to get something that looks decent, which is the only
			// thing that earns the reader a second sitting. The craft beats can be taught to
			// someone who already has a clip they like.
			//
			// THE ROTATION VALUES BELOW ARE NOT INVENTED - they are read out of
			// Assets/animations/reach_and_flip_switch.riganim, which RigSampleBuilder generates by
			// running this tool's own two-bone IK solver at a world-space target for the hand. So
			// they are the pose the solver picked for THIS rig, converted from the stored
			// quaternion to the pitch/yaw/roll the Inspector displays.
			//
			// If the model or the switch placement changes, rebuild the sample (Editor menu ->
			// Marionette -> Rebuild Example Clip, or rig_build_sample) and re-read frames 14 and
			// 17 from it. Do NOT hand-pick replacements: nobody knows which local axis of
			// arm_upper_R is "forward" without testing, which is the same reason RigSampleBuilder
			// solves for a target instead of authoring angles.
			new()
			{
				Instruction = "Now the reach itself",
				Bullets = new[]
				{
					$"Go to frame {ReachFrame}",
					$"In the Inspector, set arm_upper_R Rotation to  {Show( ReachUpper )}",
					$"Then arm_lower_R to  {Show( ReachLower )}"
				},
				Detail = "Those are pitch, yaw, roll. Shoulder first, elbow second - the elbow hangs off the shoulder.",
				Art = StepArt.Rotate,
				Panel = "Inspector",

				// BOTH bones, at the named values. The old check was "any bone has any key past
				// frame 10", which ticked the moment you keyed anything at all - including the
				// shoulder alone, with the elbow still hanging where the bind pose left it. Being
				// waved on from a half-finished pose is worse than no check, because the next step
				// then builds on something that doesn't look like what the tutorial describes.
				IsDone = ( anim, _ ) =>
					PosedAt( anim, "arm_upper_R", ReachFrame, ReachUpper )
					&& PosedAt( anim, "arm_lower_R", ReachFrame, ReachLower )
			},
			new()
			{
				Instruction = "Aim the wrist",
				Bullets = new[]
				{
					$"Go to frame {WristFrame}",
					$"Set hand_R Rotation to  {Show( WristHand )}"
				},
				Detail = "The palm turns to face the switch. Until now the hand has just been dragged along by the arm.",
				Art = StepArt.Rotate,
				Panel = "Inspector",
				IsDone = ( anim, _ ) => PosedAt( anim, "hand_R", WristFrame, WristHand )
			},
			new()
			{
				Instruction = "Find the timing",
				Bullets = new[]
				{
					"Press Play",
					"Drag the keys closer together",
					"Play it again"
				},
				Detail = "Almost every first animation runs at half the speed it should.",
				Art = StepArt.Play,
				Panel = "Timeline",
				IsDone = ( _, _ ) => false
			}
		};
	}

	// The poses the tutorial asks for, in one place, so the numbers printed in the bullets and the
	// numbers checked by IsDone cannot drift apart. They were separate literals for about ten
	// minutes and that was already one edit away from a step that can never be completed.
	//
	// Read out of the generated example clip - see the comment above the reach step.
	private const int ReachFrame = 14;
	private const int WristFrame = 17;

	private static readonly Angles ReachUpper = new( -18f, 63f, 36f );
	private static readonly Angles ReachLower = new( 41f, -32f, 4f );
	private static readonly Angles WristHand = new( -15f, -24f, 6f );

	/// <summary>How the values are written into the instructions - the same three numbers, in the
	/// same order the Inspector shows them.</summary>
	private static string Show( Angles a ) => $"{a.pitch:0.#}, {a.yaw:0.#}, {a.roll:0.#}";

	/// <summary>
	/// True when one named bone is keyed near a frame at (near enough) a named rotation.
	///
	/// Compared component-wise in Euler rather than as a quaternion angle, because this is
	/// checking the literal thing the step asked for: that these three numbers were typed into
	/// those three fields. A quaternion distance would also pass a rotation that LOOKS the same
	/// but reads differently in the boxes, which is not what "put all the values in" means.
	///
	/// Five degrees of slack absorbs the rounding - the step prints whole numbers and the values
	/// behind them have decimals - without being loose enough to pass a pose you eyeballed.
	/// </summary>
	private static bool PosedAt( RigAnimDocument anim, string bone, int frame, Angles target, float tolerance = 5f )
	{
		if ( anim?.FindTrack( bone ) is not { } track )
			return false;

		foreach ( var key in track.Keyframes )
		{
			if ( Math.Abs( key.Frame - frame ) > 2 )
				continue;

			var angles = key.Local.Rotation.Angles();

			if ( Apart( angles.pitch, target.pitch ) <= tolerance
				&& Apart( angles.yaw, target.yaw ) <= tolerance
				&& Apart( angles.roll, target.roll ) <= tolerance )
				return true;
		}

		return false;
	}

	/// <summary>Degrees between two angles the short way round, so -179 and 179 are two apart
	/// rather than three hundred and fifty eight.</summary>
	private static float Apart( float a, float b )
	{
		var d = MathF.Abs( a - b ) % 360f;

		return d > 180f ? 360f - d : d;
	}

	/// <summary>Any bone keyed within tolerance of a frame - the reader shouldn't have to land on
	/// an exact frame for the tutorial to notice they did the thing.</summary>
	private static bool KeyNear( RigAnimDocument anim, int frame, int tolerance ) =>
		anim?.BoneTracks.Any( t => t.Keyframes.Any( k => Math.Abs( k.Frame - frame ) <= tolerance ) ) ?? false;

	/// <summary>
	/// Whether the tutorial dock opens itself when the tool starts.
	///
	/// EditorCookie, so it survives restarts and lives nowhere near a document - which panels you
	/// like seeing is a property of you, not of the clip you happen to have open. Same mechanism
	/// the editor's own preview widgets use for their settings.
	/// </summary>
	public static bool OpenOnStartup
	{
		get => EditorCookie.Get( "marionette.tutorial.openonstartup", true );
		set => EditorCookie.Set( "marionette.tutorial.openonstartup", value );
	}

	/// <summary>Starts inactive so the panel shows its start screen first. Nobody should be
	/// dropped into step one of something they never asked for.</summary>
	public bool Active { get; private set; }

	public int CurrentIndex { get; private set; }

	public int StepCount => _steps.Count;

	public Step CurrentStep => Active && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

	/// <summary>Any step by index, so the panel can list the whole run rather than only the one
	/// you're on - seeing what's done and what's left is most of what makes it feel finishable.</summary>
	public Step StepAt( int index ) => index >= 0 && index < _steps.Count ? _steps[index] : null;

	public void Restart()
	{
		Active = true;
		CurrentIndex = 0;
		_furthest = 0;
	}

	public void Dismiss() => Active = false;

	/// <summary>Advance past every step already satisfied. Loops rather than stepping once, so a
	/// reader who does three things before looking down isn't left three steps behind.</summary>
	/// <summary>The furthest step reached, so stepping back doesn't immediately snap forward again.
	/// See Evaluate.</summary>
	private int _furthest;

	public bool CanGoBack => Active && CurrentIndex > 0;

	public bool CanGoForward => Active && CurrentIndex < _steps.Count;

	/// <summary>Step back one. Auto-advance stays out of the way until you catch up again.</summary>
	public void Back()
	{
		if ( !CanGoBack )
			return;

		CurrentIndex--;
	}

	/// <summary>Skip forward without having done the step - some are worth reading and not
	/// following, and a tutorial that can only be advanced by obeying it is a cage.</summary>
	public void Forward()
	{
		if ( !CanGoForward )
			return;

		CurrentIndex++;
		_furthest = Math.Max( _furthest, CurrentIndex );
	}

	public bool Evaluate( RigAnimDocument anim, string selectedBone )
	{
		if ( !Active )
			return false;

		// DON'T FIGHT A MANUAL REWIND. Steps tick off when their condition holds, and those
		// conditions stay true once satisfied - a keyframe at frame 6 is still there afterwards.
		// So stepping back would re-satisfy the step you just left and snap forward again the
		// same frame, making the Back button look broken. While you're behind the furthest point
		// reached, auto-advance stops entirely; it picks up again once you're back at the front.
		if ( CurrentIndex < _furthest )
			return false;

		var moved = false;

		while ( CurrentIndex < _steps.Count && _steps[CurrentIndex].IsDone( anim, selectedBone ) )
		{
			CurrentIndex++;
			moved = true;
		}

		_furthest = Math.Max( _furthest, CurrentIndex );

		return moved;
	}
}
