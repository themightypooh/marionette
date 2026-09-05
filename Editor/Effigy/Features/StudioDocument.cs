using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Effigy;

/// <summary>
/// Reading and writing a Part Studio.
///
/// WHY THIS IS THE MOST IMPORTANT FILE IN THE FOLDER. Everything else here exists to make the model
/// parametric — the ordered history, rollback, incremental rebuild, references that survive an edit
/// — and none of it means anything if the history dies with the window. Without this, every session
/// is a one-shot bake: you keep the OBJ and you lose the model, and the feature tree is decoration.
/// The whole point of a history is coming back to it in a week and changing the 4 to a 6.
///
/// TEXT, HAND-WRITTEN, LIKE EVERY OTHER FORMAT IN HERE. ObjWriter, SmdWriter and DmxWriter are all
/// written by hand and so is the expression evaluator, for the reason the README gives: the kernel
/// has no dependencies, and it has none because it is meant to be dropped into s&amp;box or Godot or a
/// console runner as loose .cs files. A serializer that reached for a library would be the first
/// thing to break that. Text also diffs, which matters more than it sounds for a format holding
/// somebody's model: a corrupt binary is a shrug, a corrupt text file is usually one bad line you
/// can see.
///
/// FIELDS ARE FOUND BY REFLECTION, NOT LISTED. A feature's Parameters property is not usable as the
/// list to save: PrimitiveFeature changes its parameters with the shape dropdown, so a box saved
/// today would not know what to do with the radius it will want tomorrow. The public FIELDS are
/// stable — SizeX is SizeX whatever the dropdown says — and reflecting over them means a new
/// feature is saved the moment it is written. There is no step to forget.
///
/// It also means an unhandled field type is possible, so DocumentTests asserts that every feature
/// type in the assembly round-trips every field it declares. Adding state a save cannot carry fails
/// the suite rather than quietly not saving, which is the failure this design could otherwise have.
/// </summary>
public static class StudioDocument
{
	/// <summary>The newest format this build can WRITE and the newest it can read. A file claiming
	/// more than this is refused by name rather than by crash.</summary>
	public const int Version = 2;

	/// <summary>
	/// The oldest format that can express any document - everything before the rig block existed.
	///
	/// A DOCUMENT IS STAMPED WITH WHAT IT ACTUALLY NEEDS, not with whatever this build happens to
	/// be. That is what keeps the version honest in both directions: a part with no rig in it is
	/// still perfectly readable by the build that shipped before rigs were saved, so telling that
	/// build to refuse it would be a lie - and stamping every existing document with a 2 the moment
	/// it is re-saved would rewrite the first line of every file in the repository for a feature
	/// none of them use. Same rule the origin and the material scales follow one level down.
	/// </summary>
	const int VersionBase = 1;

	/// <summary>The format that first carried a rig block.</summary>
	const int VersionRig = 2;

	public const string Extension = ".effigy";

	// --- writing ------------------------------------------------------------------------------

	public static void WriteFile( PartStudio studio, string path ) =>
		File.WriteAllText( path, Write( studio ) );

	public static string Write( PartStudio studio )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		var sb = new StringBuilder();

		// See VersionBase: the stamp says what a reader NEEDS, so a part nobody has rigged still
		// claims 1 and still opens in the build that shipped before this block existed.
		var version = studio.Rig is { Count: > 0 } ? VersionRig : VersionBase;

		sb.Append( "effigy " ).Append( version ).Append( '\n' );
		sb.Append( "rollback " ).Append( studio.RollbackIndex ).Append( '\n' );

		// Only when it has been moved. A pivot at zero is what a reader that has never heard of
		// this line already has, so writing it unconditionally would add a line to every existing
		// document and change the bytes of files nobody edited.
		if ( studio.Origin.Length > 0f )
			sb.Append( "origin " ).Append( Vec( studio.Origin ) ).Append( '\n' );

		// Sorted, so two saves of the same document are the same bytes. A dictionary's order is not
		// promised, and a format that reshuffles itself makes every diff useless.
		foreach ( var (slot, name) in studio.MaterialNames.OrderBy( kv => kv.Key ) )
		{
			if ( !string.IsNullOrWhiteSpace( name ) )
				sb.Append( "material " ).Append( slot ).Append( ' ' ).Append( OneLine( name ) ).Append( '\n' );
		}

		// Only the slots somebody has resized. A slot at 1:1 renders exactly as a reader that has
		// never heard of this line already renders it, so writing them all would add lines to every
		// existing document and change the bytes of files nobody edited — the same rule the origin
		// follows above. MaterialScale.SetScale removes the entry when it returns to 1:1, so this
		// stays true without a check here.
		foreach ( var (slot, scale) in studio.MaterialScales.OrderBy( kv => kv.Key ) )
			sb.Append( "materialscale " ).Append( slot ).Append( ' ' )
				.Append( Num( scale.x ) ).Append( ' ' ).Append( Num( scale.y ) ).Append( '\n' );

		foreach ( var (id, name) in studio.BodyNames.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
		{
			if ( !string.IsNullOrWhiteSpace( id ) && !string.IsNullOrWhiteSpace( name ) )
				sb.Append( "bodyname " ).Append( id ).Append( ' ' ).Append( OneLine( name ) ).Append( '\n' );
		}

		foreach ( var id in studio.HiddenBodyIds.OrderBy( k => k, StringComparer.Ordinal ) )
		{
			if ( !string.IsNullOrWhiteSpace( id ) )
				sb.Append( "hiddenbody " ).Append( id ).Append( '\n' );
		}

		// BEFORE the features, with the rest of the document-level state, because that is what a
		// note is — see PartStudio.Notes. Writing them after the tree would read as if they were
		// part of it, and the one thing this format should not imply is that a note is a feature.
		foreach ( var note in studio.Notes )
			WriteNote( sb, note );

		// WITH THE NOTES, BEFORE THE TREE, and for the same reason they are: a rig is document
		// state, not a modelling operation. Writing it after the features would read as if it were
		// one of them, which is the single thing this format should not imply about a rig.
		WriteRig( sb, studio );

		foreach ( var feature in studio.Features )
			WriteFeature( sb, feature );

		return sb.ToString();
	}

	/// <summary>
	/// One grease-pencil note as a block.
	///
	/// A BLOCK RATHER THAN A LINE, and one point per line inside it, which is what every sketch
	/// already does. A stroke is however many samples the hand made — hundreds is ordinary — and
	/// putting them on one line gives the format its first unbounded line, which is exactly the
	/// thing that turns a readable diff into a wall. Per line, moving a note shows up as the lines
	/// that moved.
	/// </summary>
	static void WriteNote( StringBuilder sb, Note note )
	{
		if ( note is null || note.IsEmpty )
			return;

		sb.Append( "note " ).Append( note.Color ).Append( ' ' ).Append( Num( note.Width ) ).Append( '\n' );

		if ( !string.IsNullOrEmpty( note.Text ) )
			sb.Append( "\ttext " ).Append( OneLine( note.Text ) ).Append( '\n' );

		foreach ( var p in note.Points )
			sb.Append( "\tp " ).Append( Vec( p ) ).Append( '\n' );

		sb.Append( "endnote\n" );
	}

	/// <summary>
	/// The skeleton, its softness and its body bindings.
	///
	/// NOTHING AT ALL WHEN THERE ARE NO BONES, which is most documents. The same rule the origin
	/// and the material scales follow: a document that has never been rigged should have exactly
	/// the bytes it had before this block existed, so adding the feature does not rewrite every
	/// file in the repository the first time each is opened and saved.
	///
	/// ONE BONE PER BLOCK, its numbers one per line. A bone carries a name, a parent, twelve floats
	/// of basis and origin, a length, and possibly four more for softness - as one line that is an
	/// unreadable run of nineteen numbers, and the format already made this call once for notes
	/// (see WriteNote, "the first unbounded line ... turns a readable diff into a wall"). Per line,
	/// nudging one bone shows up as the one line that moved.
	///
	/// THE PARENT IS AN INDEX, not a name, even though the bindings below key on names. Skeleton
	/// stores bones in topological order and the reader rebuilds them in file order through
	/// AddBone, which refuses a parent that does not exist yet - so an index is checked by
	/// construction on the way back in, where a name would have to be resolved against a
	/// half-built skeleton and could name a bone that had not arrived.
	/// </summary>
	static void WriteRig( StringBuilder sb, PartStudio studio )
	{
		var rig = studio.Rig;

		if ( rig is null || rig.Count == 0 )
			return;

		sb.Append( "rig\n" );

		foreach ( var bone in rig.Bones )
		{
			sb.Append( "\tbone " ).Append( OneLine( bone.Name ) ).Append( '\n' );
			sb.Append( "\t\tparent " ).Append( bone.Parent ).Append( '\n' );
			sb.Append( "\t\tx " ).Append( Vec( bone.Local.X ) ).Append( '\n' );
			sb.Append( "\t\ty " ).Append( Vec( bone.Local.Y ) ).Append( '\n' );
			sb.Append( "\t\tz " ).Append( Vec( bone.Local.Z ) ).Append( '\n' );
			sb.Append( "\t\torigin " ).Append( Vec( bone.Local.Origin ) ).Append( '\n' );
			sb.Append( "\t\tlength " ).Append( Num( bone.Length ) ).Append( '\n' );

			// Absent means rigid, which is what Bone.Soft being null means and what nearly every
			// bone is. Four zeros would be a different thing entirely - a bone with no spring that
			// still gets simulated - so this is not a case where a default can stand in.
			if ( bone.Soft is { } soft )
			{
				sb.Append( "\t\tsoft " ).Append( Num( soft.Stiffness ) ).Append( ' ' )
					.Append( Num( soft.Damping ) ).Append( ' ' )
					.Append( Num( soft.Weight ) ).Append( ' ' )
					.Append( Num( soft.MaxAngle ) ).Append( '\n' );
			}

			sb.Append( "\tendbone\n" );
		}

		// Sorted by body id, for the reason the material names are: two saves of one document
		// should be the same bytes, and a dictionary promises no order.
		foreach ( var (body, bone) in studio.BodyBoneMap.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
		{
			if ( !string.IsNullOrWhiteSpace( body ) && !string.IsNullOrWhiteSpace( bone ) )
				sb.Append( "\tbind " ).Append( body ).Append( ' ' ).Append( OneLine( bone ) ).Append( '\n' );
		}

		sb.Append( "endrig\n" );
	}

	static void WriteFeature( StringBuilder sb, Feature feature )
	{
		sb.Append( "feature " ).Append( feature.GetType().Name ).Append( '\n' );
		sb.Append( "\tid " ).Append( feature.Id ).Append( '\n' );

		// A name can be anything the user typed, so it takes the rest of the line and newlines are
		// stripped rather than escaped — a name spanning two lines is not worth a quoting scheme.
		if ( !string.IsNullOrEmpty( feature.Name ) )
			sb.Append( "\tname " ).Append( OneLine( feature.Name ) ).Append( '\n' );

		sb.Append( "\tsuppressed " ).Append( feature.Suppressed ? 1 : 0 ).Append( '\n' );
		sb.Append( "\tvisible " ).Append( feature.Visible ? 1 : 0 ).Append( '\n' );

		foreach ( var field in StateFields( feature.GetType() ) )
			WriteField( sb, feature, field );

		sb.Append( "end\n" );
	}

	static void WriteField( StringBuilder sb, Feature feature, FieldInfo field )
	{
		var value = field.GetValue( feature );

		switch ( value )
		{
			case FloatParam p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( Num( p.Value ) ).Append( '\n' );
				return;

			case IntParam p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( p.Value ).Append( '\n' );
				return;

			case BoolParam p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( p.Value ? 1 : 0 ).Append( '\n' );
				return;

			case ChoiceParam p:
				// The INDEX, not the label. Labels are user-facing text and get reworded; an index
				// survives that. It does not survive the options being reordered, which is why
				// ResultRemove exists as a named constant rather than a bare 3.
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( p.Index ).Append( '\n' );
				return;

			case Vec3Param p:
				sb.Append( "\tparam " ).Append( field.Name ).Append( ' ' ).Append( Vec( p.Value ) ).Append( '\n' );
				return;

			case BodySelectionParam p:
				sb.Append( "\tbodies " ).Append( field.Name );

				foreach ( var id in p.BodyIds )
					sb.Append( ' ' ).Append( id );

				sb.Append( '\n' );
				return;

			case Sketch sketch:
				WriteSketch( sb, field.Name, sketch );
				return;

			case FaceRef face:
				sb.Append( "\tface " ).Append( field.Name ).Append( ' ' ).Append( Face( face ) ).Append( '\n' );
				return;

			case List<int> ints:
				// Shell's OpenFaces, and anything like it. Written even when empty, unlike a null
				// nullable: an empty list and an unmentioned one are the same on load, and writing
				// the line keeps a diff between two saves readable.
				sb.Append( "\tints " ).Append( field.Name );

				foreach ( var n in ints )
					sb.Append( ' ' ).Append( n );

				sb.Append( '\n' );
				return;

			case List<string> texts:
				// Loft's Sections, and anything like it. One line with every entry on it, like
				// ints rather than like facelist, because these are short ids and a line each
				// would bury the rest of the feature.
				sb.Append( "\ttexts " ).Append( field.Name );

				foreach ( var text in texts )
					sb.Append( ' ' ).Append( text );

				sb.Append( '\n' );
				return;

			case List<Vec2> vecs:
				// Extrude's RegionSeeds, and anything like it. Pairs on one line, same shape as
				// ints, because a handful of in-plane points does not deserve a line each.
				sb.Append( "\tvec2s " ).Append( field.Name );

				foreach ( var v in vecs )
					sb.Append( ' ' ).Append( Num( v.x ) ).Append( ' ' ).Append( Num( v.y ) );

				sb.Append( '\n' );
				return;

			case List<FaceRef> faces:
				foreach ( var f in faces )
					sb.Append( "\tfacelist " ).Append( field.Name ).Append( ' ' ).Append( Face( f ) ).Append( '\n' );

				return;

			case List<EdgeRef> edges:
				foreach ( var e in edges )
					sb.Append( "\tedgelist " ).Append( field.Name ).Append( ' ' ).Append( Edge( e ) ).Append( '\n' );

				return;

			case List<PaintStroke> strokes:
				// Paint's stroke log. One line per stroke — colour, brush and spacing once, then the
				// path as position/normal pairs — so a painted document still diffs one stroke at a
				// time rather than becoming one unbounded line. Written in list order on purpose:
				// strokes are a log and colour blending does not commute, so the order the writer
				// emits is the order replay must reproduce.
				foreach ( var s in strokes )
					sb.Append( "\tstroke " ).Append( field.Name ).Append( ' ' ).Append( Stroke( s ) ).Append( '\n' );

				return;

			case Vec2 v:
				sb.Append( "\tvec2 " ).Append( field.Name ).Append( ' ' )
					.Append( Num( v.x ) ).Append( ' ' ).Append( Num( v.y ) ).Append( '\n' );
				return;

			case string s:
				if ( s.Length > 0 )
					sb.Append( "\ttext " ).Append( field.Name ).Append( ' ' ).Append( OneLine( s ) ).Append( '\n' );

				return;

			case null:
				// A null nullable — no Face — is written as nothing at all. Absence IS the value,
				// and a reader starting from a fresh feature already has it.
				return;
		}

		// Unreachable while DocumentTests passes: it asserts every field of every feature type is a
		// type this switch handles. Throwing rather than skipping is what makes that test able to
		// fail — a silent skip would save a file that quietly lost half a feature.
		throw new InvalidOperationException(
			$"{feature.GetType().Name}.{field.Name} is a {field.FieldType.Name}, which StudioDocument cannot save. "
			+ "Add a case for it here and in ReadField." );
	}

	static void WriteSketch( StringBuilder sb, string fieldName, Sketch sketch )
	{
		sb.Append( "\tsketch " ).Append( fieldName ).Append( '\n' );
		sb.Append( "\t\ttolerance " ).Append( Num( sketch.Tolerance ) ).Append( '\n' );
		sb.Append( "\t\tplane " ).Append( Vec( sketch.Plane.Origin ) ).Append( ' ' )
			.Append( Vec( sketch.Plane.XAxis ) ).Append( ' ' ).Append( Vec( sketch.Plane.YAxis ) ).Append( '\n' );

		foreach ( var p in sketch.Points )
			sb.Append( "\t\tpoint " ).Append( Num( p.x ) ).Append( ' ' ).Append( Num( p.y ) ).Append( '\n' );

		foreach ( var curve in sketch.Curves )
		{
			switch ( curve )
			{
				case SketchLine line:
					sb.Append( "\t\tline " ).Append( line.Start ).Append( ' ' ).Append( line.End );
					break;

				case SketchArc arc:
					sb.Append( "\t\tarc " ).Append( arc.Center ).Append( ' ' ).Append( arc.Start ).Append( ' ' )
						.Append( arc.End ).Append( ' ' ).Append( arc.Clockwise ? 1 : 0 );
					break;

				case SketchCircle circle:
					sb.Append( "\t\tcircle " ).Append( circle.Center ).Append( ' ' ).Append( Num( circle.Radius ) );
					break;

				case SketchEllipse ellipse:
					sb.Append( "\t\tellipse " ).Append( ellipse.Center ).Append( ' ' )
						.Append( ellipse.MajorPoint ).Append( ' ' ).Append( Num( ellipse.MinorRadius ) );
					break;

				// The point COUNT is written before the points, because everything else in this
				// format has a fixed field count and the reader finds a curve's id and construction
				// flag at a known offset. A variable-length record without a count would make that
				// offset unknowable without counting backwards from the end, which works right up
				// until a field is added.
				case SketchSpline spline:
					sb.Append( "\t\tspline " ).Append( spline.Closed ? 1 : 0 ).Append( ' ' )
						.Append( spline.Points.Count );

					foreach ( var index in spline.Points )
						sb.Append( ' ' ).Append( index );

					break;

				default:
					throw new InvalidOperationException( $"StudioDocument cannot save a {curve.GetType().Name}" );
			}

			// Id and construction come last and in the same order for every curve type, so the
			// reader can strip them before it looks at what kind of curve it has.
			sb.Append( ' ' ).Append( curve.Id ).Append( ' ' ).Append( curve.Construction ? 1 : 0 ).Append( '\n' );
		}

		foreach ( var c in sketch.Constraints )
		{
			sb.Append( "\t\tconstraint " ).Append( (int)c.Kind ).Append( ' ' )
				.Append( c.PointA ).Append( ' ' ).Append( c.PointB ).Append( ' ' )
				.Append( c.PointC ).Append( ' ' ).Append( c.PointD ).Append( ' ' )
				.Append( Num( c.Value ) ).Append( ' ' )
				.Append( string.IsNullOrEmpty( c.CurveId ) ? "-" : c.CurveId ).Append( ' ' )
				.Append( Num( c.ValueY ) ).Append( '\n' );
		}

		sb.Append( "\tendsketch\n" );
	}

	// --- reading ------------------------------------------------------------------------------

	public static PartStudio ReadFile( string path ) => Read( File.ReadAllText( path ) );

	/// <summary>
	/// Parse a document back into a studio. Throws with the line number on anything malformed.
	///
	/// The studio comes back NOT rebuilt. Loading is about restoring the tree; running it is the
	/// caller's business and its errors are the model's errors, not the file's — an editor wants to
	/// show a file that loads and fails to build, because that is exactly the state you opened it to
	/// fix.
	/// </summary>
	public static PartStudio Read( string text )
	{
		var studio = new PartStudio();
		var lines = (text ?? "").Replace( "\r\n", "\n" ).Split( '\n' );
		var rollback = int.MaxValue;
		var i = 0;

		string Line() => lines[i];

		if ( lines.Length == 0 || !Line().StartsWith( "effigy " ) )
			throw new InvalidDataException( "Not an Effigy document — the first line should read 'effigy <version>'." );

		var version = ParseInt( Line()[7..].Trim(), 1 );

		if ( version > Version )
		{
			throw new InvalidDataException(
				$"This file was written by a newer Effigy (format {version}; this build reads {Version})." );
		}

		i++;

		for ( ; i < lines.Length; i++ )
		{
			var line = Line().Trim();

			if ( line.Length == 0 )
				continue;

			if ( line.StartsWith( "rollback " ) )
			{
				rollback = ParseInt( line[9..], int.MaxValue );
				continue;
			}

			if ( line.StartsWith( "origin " ) )
			{
				studio.Origin = ParseVec3( line[7..] );
				continue;
			}

			// BEFORE "material ", so the longer key is never read as the shorter one with a strange
			// name. They do not actually collide today — "materialscale" has no space at index 8 —
			// but that is a property of the spelling rather than of the parser, and the next key
			// starting with "material" would not be so lucky.
			if ( line.StartsWith( "materialscale " ) )
			{
				var parts = line[14..].Split( ' ', StringSplitOptions.RemoveEmptyEntries );

				if ( parts.Length >= 3 )
				{
					// Through SetScale rather than straight into the dictionary, so a hand-edited
					// zero is caught here rather than dividing every UV on the slot into infinity,
					// and a 1:1 written by an older tool leaves no entry behind.
					MaterialScale.SetScale( studio, ParseInt( parts[0], -1 ),
						new Vec2( ParseFloat( parts[1] ), ParseFloat( parts[2] ) ) );
				}

				continue;
			}

			if ( line.StartsWith( "material " ) )
			{
				var (slot, name) = Split( line[9..] );
				studio.MaterialNames[ParseInt( slot, 0 )] = name;
				continue;
			}

			if ( line.StartsWith( "bodyname " ) )
			{
				var (id, name) = Split( line[9..] );

				if ( !string.IsNullOrWhiteSpace( id ) && !string.IsNullOrWhiteSpace( name ) )
					studio.BodyNames[id] = name;

				continue;
			}

			if ( line.StartsWith( "hiddenbody " ) )
			{
				var id = line[11..].Trim();

				if ( !string.IsNullOrWhiteSpace( id ) )
					studio.HiddenBodyIds.Add( id );

				continue;
			}

			if ( line.StartsWith( "note " ) )
			{
				var note = ReadNote( lines, ref i );

				// A note with nothing in it is dropped rather than loaded. WriteNote never produces
				// one, so this only fires on a hand-edited file, and an invisible entry that only
				// the file knows about is worse than no entry.
				if ( !note.IsEmpty )
					studio.Notes.Add( note );

				continue;
			}

			// Exact rather than StartsWith: the block header is the bare word, and a prefix test
			// would also swallow any later key beginning "rig".
			if ( line == "rig" )
			{
				ReadRig( studio, lines, ref i );
				continue;
			}

			if ( !line.StartsWith( "feature " ) )
				throw new InvalidDataException( $"Line {i + 1}: expected a feature, found '{line}'" );

			studio.Add( ReadFeature( lines, ref i ) );
		}

		// After the features, so it can be clamped against a tree that actually exists. A rollback
		// index past the end is not corruption — deleting the last feature of a rolled-back tree
		// leaves exactly that — and PartStudio treats it as "roll to end".
		studio.RollbackIndex = Math.Min( rollback, studio.Features.Count );

		return studio;
	}

	/// <summary>
	/// Read one note block, positioned on its "note" line and leaving <paramref name="i"/> on the
	/// "endnote".
	///
	/// UNFAMILIAR KEYS INSIDE THE BLOCK ARE SKIPPED rather than thrown on, which is the opposite of
	/// what ReadFeature does with them, and deliberately so. A feature carrying a key this build
	/// cannot read is a model that will rebuild into the wrong shape, and refusing to open is the
	/// honest answer. A note is a scribble: losing a property a later build added costs the user a
	/// colour, and taking the whole document down over it would cost them the part.
	/// </summary>
	/// <summary>
	/// Read the rig block back.
	///
	/// SKIPS WHAT IT DOES NOT RECOGNISE, following ReadNote rather than ReadFeature. The reasoning
	/// is the same and it is worth repeating because the two rules look inconsistent side by side:
	/// a feature carrying an unknown key rebuilds into the WRONG SHAPE, so refusing to open is the
	/// honest answer, while a bone carrying one loses a property a later build added - a wobble
	/// setting, say - and taking the whole part down over a wobble setting is a bad trade.
	///
	/// A BONE WITH NO NAME OR A BAD PARENT IS DROPPED, not thrown on, and its children go with it
	/// because AddBone will refuse a parent index that never arrived. That is a hand-edited file or
	/// a truncated one; the rest of the rig is still worth having.
	/// </summary>
	static void ReadRig( PartStudio studio, string[] lines, ref int i )
	{
		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "endrig" )
				return;

			if ( line.StartsWith( "bind " ) )
			{
				var parts = line[5..].Split( ' ', 2, StringSplitOptions.RemoveEmptyEntries );

				if ( parts.Length == 2 && !string.IsNullOrWhiteSpace( parts[0] ) )
					studio.BodyBoneMap[parts[0]] = parts[1].Trim();

				continue;
			}

			if ( line.StartsWith( "bone " ) )
			{
				ReadBone( studio.Rig, lines, ref i );
				continue;
			}
		}
	}

	/// <summary>One bone block. Everything is read before anything is added, because AddBone takes
	/// the whole bone at once and can refuse it.</summary>
	static void ReadBone( Skeleton rig, string[] lines, ref int i )
	{
		var name = lines[i].Trim()[5..].Trim();

		var parent = -1;
		var length = 1f;
		Vec3 x = new( 1, 0, 0 ), y = new( 0, 1, 0 ), z = new( 0, 0, 1 ), origin = default;
		SoftBone soft = null;

		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "endbone" )
				break;

			if ( line.StartsWith( "parent " ) ) { parent = ParseInt( line[7..], -1 ); continue; }
			if ( line.StartsWith( "x " ) ) { x = ParseVec3( line[2..] ); continue; }
			if ( line.StartsWith( "y " ) ) { y = ParseVec3( line[2..] ); continue; }
			if ( line.StartsWith( "z " ) ) { z = ParseVec3( line[2..] ); continue; }
			if ( line.StartsWith( "origin " ) ) { origin = ParseVec3( line[7..] ); continue; }
			if ( line.StartsWith( "length " ) ) { length = ParseFloat( line[7..] ); continue; }

			if ( line.StartsWith( "soft " ) )
			{
				var parts = line[5..].Split( ' ', StringSplitOptions.RemoveEmptyEntries );

				// A short soft line keeps SoftBone's own defaults for whatever is missing, rather
				// than zeroing them. Zero stiffness and a zero cone are both meaningful values - a
				// dead limb and a bone pinned to its pose - so filling a gap with one would invent
				// a deliberate-looking setting nobody chose.
				soft = new SoftBone();

				if ( parts.Length > 0 ) soft.Stiffness = ParseFloat( parts[0] );
				if ( parts.Length > 1 ) soft.Damping = ParseFloat( parts[1] );
				if ( parts.Length > 2 ) soft.Weight = ParseFloat( parts[2] );
				if ( parts.Length > 3 ) soft.MaxAngle = ParseFloat( parts[3] );

				continue;
			}
		}

		if ( string.IsNullOrWhiteSpace( name ) )
			return;

		try
		{
			var index = rig.AddBone( name, parent, new Xform( x, y, z, origin ), length );
			rig.Bones[index].Soft = soft;
		}
		catch ( ArgumentException )
		{
			// A duplicate name, a blank one, or a parent that is not there yet - a hand-edited or
			// truncated file. AddBone is the only thing that knows the rules, so it is left to
			// enforce them rather than having them restated here and allowed to drift.
			//
			// ONE CATCH COVERS BOTH throws: AddBone raises ArgumentOutOfRangeException for a bad
			// parent, and that derives from ArgumentException, so a second clause for it would be
			// unreachable rather than thorough.
		}
	}

	static Note ReadNote( string[] lines, ref int i )
	{
		var header = lines[i].Trim()[5..].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		var note = new Note
		{
			Color = header.Length > 0 ? ParseInt( header[0], 0 ) : 0,
			Width = header.Length > 1 ? ParseFloat( header[1] ) : 0.4f,
		};

		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "endnote" )
				return note;

			var (key, rest) = Split( line );

			if ( key == "p" )
				note.Points.Add( ParseVec3( rest ) );
			else if ( key == "text" )
				note.Text = rest;
		}

		throw new InvalidDataException( "A note block was never closed with 'endnote'." );
	}

	static Feature ReadFeature( string[] lines, ref int i )
	{
		var typeName = lines[i].Trim()[8..].Trim();
		var feature = Create( typeName )
			?? throw new InvalidDataException( $"Line {i + 1}: no feature type named '{typeName}' in this build." );

		var fields = StateFields( feature.GetType() ).ToDictionary( f => f.Name, f => f );

		// A list field accumulates across lines, so it is cleared the first time one is seen rather
		// than up front — otherwise loading would wipe a default that the file simply does not
		// mention.
		var clearedLists = new HashSet<string>();

		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "end" )
				return feature;

			var (key, rest) = Split( line );

			switch ( key )
			{
				case "id": feature.Id = rest; continue;
				case "name": feature.Name = rest; continue;
				case "suppressed": feature.Suppressed = rest == "1"; continue;
				case "visible": feature.Visible = rest == "1"; continue;
			}

			var (fieldName, value) = Split( rest );

			if ( !fields.TryGetValue( fieldName, out var field ) )
			{
				// Documents written before RegionSeeds was a list stored one point as
				// `vec2 RegionSeed`. The field is gone; the list is what it became.
				if ( fieldName == "RegionSeed" && key == "vec2"
					&& fields.TryGetValue( "RegionSeeds", out field ) )
				{
					ReadField( feature, field, key, value, lines, ref i, clearedLists );
					continue;
				}

				// A field this build does not have. Ignored on purpose: a file written by a version
				// with an extra parameter should still open, minus that parameter, rather than
				// refusing outright.
				if ( key == "sketch" )
					SkipSketch( lines, ref i );

				continue;
			}

			ReadField( feature, field, key, value, lines, ref i, clearedLists );
		}

		throw new InvalidDataException( $"The document ends inside a {typeName} — no 'end' line." );
	}

	static void ReadField( Feature feature, FieldInfo field, string key, string value, string[] lines, ref int i,
		HashSet<string> clearedLists )
	{
		var current = field.GetValue( feature );

		switch ( key )
		{
			case "param":
				switch ( current )
				{
					case FloatParam p: p.Value = ParseFloat( value ); return;
					case IntParam p: p.Value = ParseInt( value, p.Value ); return;
					case BoolParam p: p.Value = value == "1"; return;
					case ChoiceParam p: p.Index = ParseInt( value, p.Index ); return;
					case Vec3Param p: p.Value = ParseVec3( value ); return;
				}

				return;

			case "bodies":
				if ( current is BodySelectionParam bodies )
				{
					bodies.BodyIds.Clear();
					bodies.BodyIds.AddRange( value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) );
				}

				return;

			case "text":
				field.SetValue( feature, value );
				return;

			case "vec2":
			{
				var parts = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
				var parsed = new Vec2( ParseFloat( parts[0] ), ParseFloat( parts[1] ) );

				// Old documents, and the RegionSeed alias above, write one point as vec2. The
				// field it now lands on is a list.
				if ( field.GetValue( feature ) is List<Vec2> one )
				{
					if ( clearedLists.Add( field.Name ) )
						one.Clear();

					one.Add( parsed );
					return;
				}

				field.SetValue( feature, parsed );
				return;
			}

			case "vec2s":
			{
				if ( current is not List<Vec2> vecs )
					return;

				vecs.Clear();

				var parts = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

				for ( var n = 0; n + 1 < parts.Length; n += 2 )
					vecs.Add( new Vec2( ParseFloat( parts[n] ), ParseFloat( parts[n + 1] ) ) );

				return;
			}

			case "face":
				field.SetValue( feature, ParseFace( value ) );
				return;

			case "texts":
			{
				if ( current is not List<string> texts )
					return;

				texts.Clear();

				foreach ( var part in value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
					texts.Add( part );

				return;
			}

			case "ints":
			{
				if ( current is not List<int> ints )
					return;

				ints.Clear();

				foreach ( var part in value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
					ints.Add( ParseInt( part, 0 ) );

				return;
			}

			case "facelist":
			{
				if ( field.GetValue( feature ) is not List<FaceRef> list )
					return;

				if ( clearedLists.Add( field.Name ) )
					list.Clear();

				list.Add( ParseFace( value ) );
				return;
			}

			case "edgelist":
			{
				if ( field.GetValue( feature ) is not List<EdgeRef> list )
					return;

				if ( clearedLists.Add( field.Name ) )
					list.Clear();

				list.Add( ParseEdge( value ) );
				return;
			}

			case "stroke":
			{
				// Strokes is null until the first one lands, so a document that paints has to create
				// the list rather than assume it — unlike the facelist/edgelist fields, which are
				// never null and can rely on their initialiser.
				if ( field.GetValue( feature ) is List<PaintStroke> strokes )
				{
					if ( clearedLists.Add( field.Name ) )
						strokes.Clear();
				}
				else
				{
					strokes = new List<PaintStroke>();
					field.SetValue( feature, strokes );
					clearedLists.Add( field.Name );
				}

				strokes.Add( ParseStroke( value ) );
				return;
			}

			case "sketch":
				field.SetValue( feature, ReadSketch( lines, ref i ) );
				return;
		}
	}

	static Sketch ReadSketch( string[] lines, ref int i )
	{
		var sketch = new Sketch();

		for ( i++; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();

			if ( line.Length == 0 )
				continue;

			if ( line == "endsketch" )
				return sketch;

			var (key, rest) = Split( line );
			var parts = rest.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

			switch ( key )
			{
				case "tolerance":
					sketch.Tolerance = ParseFloat( rest );
					break;

				case "plane":
					sketch.Plane = new SketchPlane(
						new Vec3( ParseFloat( parts[0] ), ParseFloat( parts[1] ), ParseFloat( parts[2] ) ),
						new Vec3( ParseFloat( parts[3] ), ParseFloat( parts[4] ), ParseFloat( parts[5] ) ),
						new Vec3( ParseFloat( parts[6] ), ParseFloat( parts[7] ), ParseFloat( parts[8] ) ) );
					break;

				case "point":
					sketch.AddPoint( ParseFloat( parts[0] ), ParseFloat( parts[1] ) );
					break;

				case "line":
					sketch.Add( Tagged( new SketchLine( ParseInt( parts[0], 0 ), ParseInt( parts[1], 0 ) ), parts, 2 ) );
					break;

				case "arc":
					sketch.Add( Tagged( new SketchArc(
						ParseInt( parts[0], 0 ), ParseInt( parts[1], 0 ), ParseInt( parts[2], 0 ),
						parts[3] == "1" ), parts, 4 ) );
					break;

				case "circle":
					sketch.Add( Tagged( new SketchCircle( ParseInt( parts[0], 0 ), ParseFloat( parts[1] ) ), parts, 2 ) );
					break;

				case "ellipse":
					sketch.Add( Tagged( new SketchEllipse(
						ParseInt( parts[0], 0 ), ParseInt( parts[1], 0 ), ParseFloat( parts[2] ) ), parts, 3 ) );
					break;

				case "spline":
				{
					var count = ParseInt( parts[1], 0 );
					var indices = new List<int>( count );

					for ( var k = 0; k < count && 2 + k < parts.Length; k++ )
						indices.Add( ParseInt( parts[2 + k], 0 ) );

					sketch.Add( Tagged( new SketchSpline( indices, parts[0] == "1" ), parts, 2 + count ) );
					break;
				}

				case "constraint":
				{
					var constraint = new SketchConstraint( (SketchConstraintKind)ParseInt( parts[0], 0 ),
						ParseInt( parts[1], -1 ), ParseInt( parts[2], -1 ) )
					{
						PointC = ParseInt( parts[3], -1 ),
						PointD = ParseInt( parts[4], -1 ),
						Value = ParseFloat( parts[5] ),
						CurveId = parts[6] == "-" ? null : parts[6],

						// Appended after the CurveId rather than beside Value, so every index before it
						// keeps its meaning and a document written before Fixed existed still reads.
						// Absent means zero, which is what those documents meant.
						ValueY = parts.Length > 7 ? ParseFloat( parts[7] ) : 0f
					};

					sketch.Constraints.Add( constraint );
					break;
				}
			}
		}

		throw new InvalidDataException( "The document ends inside a sketch — no 'endsketch' line." );
	}

	/// <summary>Attach the id and construction flag every curve line ends with.</summary>
	static T Tagged<T>( T curve, string[] parts, int at ) where T : SketchCurve
	{
		if ( parts.Length > at )
			curve.Id = parts[at];

		if ( parts.Length > at + 1 )
			curve.Construction = parts[at + 1] == "1";

		return curve;
	}

	/// <summary>Walk past a sketch belonging to a field this build does not know about, so its
	/// contents are not read as feature lines.</summary>
	static void SkipSketch( string[] lines, ref int i )
	{
		for ( i++; i < lines.Length; i++ )
		{
			if ( lines[i].Trim() == "endsketch" )
				return;
		}
	}

	// --- shared -------------------------------------------------------------------------------

	/// <summary>
	/// The fields a feature's state lives in.
	///
	/// Public instance fields, minus the four the writer handles by name. Declared-only would miss
	/// what a feature inherits — SketchFeatureId and RegionSeeds live on SketchConsumingFeature, and
	/// forgetting them would lose which sketch an extrude consumes.
	/// </summary>
	static IEnumerable<FieldInfo> StateFields( Type type ) => type
		.GetFields( BindingFlags.Public | BindingFlags.Instance )
		.Where( f => f.Name is not ("Id" or "Name" or "Suppressed" or "Visible") )
		.OrderBy( f => f.Name, StringComparer.Ordinal );

	/// <summary>
	/// What a feature type used to be called, for documents written before it was renamed.
	///
	/// A SAVED FILE IS A PROMISE. The type token in it is a C# class name, so renaming a class is a
	/// breaking change to every document already on disk unless the old name keeps resolving.
	/// `BevelFeature` became `ChamferFeature` when the flat cut and the rounded one were split into
	/// the two operations Onshape names — the parameters are unchanged, so an old bevel loads as
	/// the chamfer it always was, with its width and angle intact.
	///
	/// Entries are never removed. The cost of one line is nothing next to a document that opens
	/// with a line number and a type name nobody recognises.
	/// </summary>
	static readonly Dictionary<string, string> RenamedFeatures = new()
	{
		["BevelFeature"] = "ChamferFeature",
	};

	/// <summary>Find a feature type by name, in whatever assembly the kernel ended up in.</summary>
	static Feature Create( string typeName )
	{
		if ( RenamedFeatures.TryGetValue( typeName, out var current ) )
			typeName = current;

		var type = typeof( Feature ).Assembly.GetTypes()
			.FirstOrDefault( t => t.Name == typeName && !t.IsAbstract && typeof( Feature ).IsAssignableFrom( t ) );

		return type is null ? null : (Feature)Activator.CreateInstance( type );
	}

	/// <summary>Round-trip float formatting. "R" rather than a fixed number of decimals: a
	/// dimension typed as 0.1 has to come back as 0.1, and a rounded one comes back as a model that
	/// has moved very slightly every time it is opened and saved.</summary>
	static string Num( float f ) => f.ToString( "R", CultureInfo.InvariantCulture );

	static string Vec( Vec3 v ) => $"{Num( v.x )} {Num( v.y )} {Num( v.z )}";

	static string Face( FaceRef f ) =>
		$"{f.BodyId} {Vec( f.Point )} {Vec( f.Normal )} {Num( f.Anchor.x )} {Num( f.Anchor.y )} "
		+ $"{(f.AnchorFromMaxX ? 1 : 0)} {(f.AnchorFromMaxY ? 1 : 0)} {(f.Anchored ? 1 : 0)}";

	static string Edge( EdgeRef e ) => $"{e.BodyId} {Vec( e.Point )} {Vec( e.Direction )}";

	/// <summary>One stroke as a single line: colour, radius, strength, falloff and spacing, then the
	/// path as position/normal pairs. The point count is implied by what is left after the header.</summary>
	static string Stroke( PaintStroke s )
	{
		var sb = new StringBuilder();

		sb.Append( Num( s.R ) ).Append( ' ' ).Append( Num( s.G ) ).Append( ' ' ).Append( Num( s.B ) ).Append( ' ' ).Append( Num( s.A ) );
		sb.Append( ' ' ).Append( Num( s.Radius ) );
		sb.Append( ' ' ).Append( Num( s.Strength ) );
		sb.Append( ' ' ).Append( (int)s.Falloff );
		sb.Append( ' ' ).Append( Num( s.Spacing ) );

		foreach ( var p in s.Path )
			sb.Append( ' ' ).Append( Vec( p.Position ) ).Append( ' ' ).Append( Vec( p.Normal ) );

		return sb.ToString();
	}

	static EdgeRef ParseEdge( string value )
	{
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		return new EdgeRef( p[0],
			new Vec3( ParseFloat( p[1] ), ParseFloat( p[2] ), ParseFloat( p[3] ) ),
			new Vec3( ParseFloat( p[4] ), ParseFloat( p[5] ), ParseFloat( p[6] ) ) );
	}

	static FaceRef ParseFace( string value )
	{
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		var point = new Vec3( ParseFloat( p[1] ), ParseFloat( p[2] ), ParseFloat( p[3] ) );
		var normal = new Vec3( ParseFloat( p[4] ), ParseFloat( p[5] ), ParseFloat( p[6] ) );

		// Anchored is the last flag, and it decides which constructor is right: the unanchored one
		// leaves Anchored false, which means "sit at the centre of whatever face this resolves to".
		// Reading an anchor into a reference that never had one would move every old sketch.
		if ( p.Length < 12 || p[11] != "1" )
			return new FaceRef( p[0], point, normal );

		return new FaceRef( p[0], point, normal,
			new Vec2( ParseFloat( p[7] ), ParseFloat( p[8] ) ), p[9] == "1", p[10] == "1" );
	}

	static PaintStroke ParseStroke( string value )
	{
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		var stroke = new PaintStroke
		{
			R = ParseFloat( p[0] ),
			G = ParseFloat( p[1] ),
			B = ParseFloat( p[2] ),
			A = ParseFloat( p[3] ),
			Radius = ParseFloat( p[4] ),
			Strength = ParseFloat( p[5] ),
			Falloff = (BrushFalloff)ParseInt( p[6], 0 ),
			Spacing = ParseFloat( p[7] ),
		};

		// The path follows the fixed header: six floats per point, position then normal. An empty
		// path is a header with nothing after it, which the loop simply never visits.
		for ( var n = 8; n + 5 < p.Length; n += 6 )
		{
			stroke.Path.Add( new PaintStrokePoint(
				new Vec3( ParseFloat( p[n] ), ParseFloat( p[n + 1] ), ParseFloat( p[n + 2] ) ),
				new Vec3( ParseFloat( p[n + 3] ), ParseFloat( p[n + 4] ), ParseFloat( p[n + 5] ) ) ) );
		}

		return stroke;
	}

	static Vec3 ParseVec3( string value )
	{
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		return new Vec3( ParseFloat( p[0] ), ParseFloat( p[1] ), ParseFloat( p[2] ) );
	}

	static (string Key, string Value) Split( string line )
	{
		var space = line.IndexOf( ' ' );

		return space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
	}

	static float ParseFloat( string s ) =>
		float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ? f : 0f;

	static int ParseInt( string s, int fallback ) =>
		int.TryParse( s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i ) ? i : fallback;

	static string OneLine( string s ) => s.Replace( '\n', ' ' ).Replace( '\r', ' ' ).Trim();
}
