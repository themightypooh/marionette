using System;
using System.Linq;
using Effigy;
using static Effigy.Tests.Report;

namespace Effigy.Tests;

/// <summary>
/// Paint strokes survive the document round trip.
///
/// WHY THIS MATTERS MORE THAN THE OTHER DOCUMENT TESTS. Strokes are a LOG — colour blending does not
/// commute, so a round trip that reordered them would be silent corruption rather than a diff. The
/// order assertion below is the whole reason paint is stored as strokes rather than dabs, and it is
/// the one thing a field-by-field comparison cannot catch.
/// </summary>
public static class PaintDocumentTests
{
	public static void Run()
	{
				Report.Section( "paint: a bare box is reachable" );
		TestBareBoxIsReachable();

Section( "paint: strokes survive the document round trip" );
		TestStrokesRoundTrip();
		TestOrderIsPreserved();
		TestEmptyPathRoundTrips();
		TestSavingTwiceIsIdentical();
		TestStaleness();
	}


	/// <summary>
	/// THE CASE NOTHING COVERED: a bare box, which is the first thing anybody paints.
	///
	/// Every other paint test and every sample subdivides first, so all of them missed that the
	/// default brush could not reach a single vertex on an unsubdivided part - it painted nothing,
	/// silently, and looked broken. The radius is floored at the vertex spacing now.
	/// </summary>
	static void TestBareBoxIsReachable()
	{
		var studio = new PartStudio();
		var box = studio.Add( new PrimitiveFeature() );
		box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 1f;
		studio.Rebuild();

		var mesh = studio.Bodies[0].Mesh;
		var session = new PaintSession( mesh );

		Report.Check( "a 1-unit box really does have only its corners",
			mesh.Positions.Count == 8, $"{mesh.Positions.Count} vertices" );

		// The old rule, kept here as the thing that must never come back.
		var boundsOnly = mesh.BoundsDiagonal / 12f;

		Report.Check( "a twelfth of the diagonal could not reach a corner from a face centre",
			boundsOnly < 0.5f, $"{boundsOnly}" );

		Report.Check( "the suggested radius now clears the vertex spacing",
			session.SuggestedRadius >= session.MeanEdgeLength * 0.5f,
			$"radius {session.SuggestedRadius}, spacing {session.MeanEdgeLength}" );

		// The point of all of it: a dab at the middle of the top face colours something.
		session.Radius = session.SuggestedRadius;

		var hit = session.Hover( new Vec3( 0, 0, 10f ), new Vec3( 0, 0, -1f ) );

		Report.Check( "a ray straight down finds the top face", hit is not null );

		Report.Check( "and the part is flagged as coarse, so the editor can say so", session.IsCoarse );

		if ( hit is { } h )
		{
			session.BeginStroke( new Vec3( 0, 0, 10f ), new Vec3( 0, 0, -1f ) );
			session.EndStroke();

			var painted = 0;

			for ( var i = 0; i < session.Colors.Length; i++ )
			{
				if ( session.Colors[i].w > 0f )
					painted++;
			}

			Report.Check( "a single dab on a bare box colours at least one vertex", painted > 0,
				$"{painted} of {session.Colors.Length}" );
		}
	}

	static PaintStroke MakeStroke( float r, float g, float b, float a, float radius, float strength,
		BrushFalloff falloff, float spacing, int points )
	{
		var stroke = new PaintStroke
		{
			R = r,
			G = g,
			B = b,
			A = a,
			Radius = radius,
			Strength = strength,
			Falloff = falloff,
			Spacing = spacing,
		};

		for ( var i = 0; i < points; i++ )
		{
			stroke.Path.Add( new PaintStrokePoint(
				new Vec3( i, i * 0.5f, -i * 0.25f ),
				new Vec3( 0f, 0f, 1f ) ) );
		}

		return stroke;
	}

	/// <summary>A studio with a paint feature carrying two strokes — red then green, several points
	/// each, deliberately distinct so an order swap would show up as the wrong colour.</summary>
	static PartStudio Painted()
	{
		var studio = new PartStudio();
		var paint = studio.Add( new PaintFeature() );

		paint.AddStroke( MakeStroke( 0.9f, 0.1f, 0.2f, 1f, 0.5f, 0.8f, BrushFalloff.Sharp, 0.25f, 3 ) );
		paint.AddStroke( MakeStroke( 0.1f, 0.9f, 0.3f, 0.5f, 0.2f, 0.4f, BrushFalloff.Linear, 0.5f, 4 ) );

		return studio;
	}

	static void TestStrokesRoundTrip()
	{
		var original = Painted();
		var originalPaint = original.Features.OfType<PaintFeature>().Single();

		var back = StudioDocument.Read( StudioDocument.Write( original ) ).Features.OfType<PaintFeature>().Single();

		Check( "the same number of strokes come back",
			back.Strokes.Count == originalPaint.Strokes.Count,
			$"{originalPaint.Strokes.Count} became {back.Strokes.Count}" );

		for ( var s = 0; s < originalPaint.Strokes.Count && s < back.Strokes.Count; s++ )
		{
			var a = originalPaint.Strokes[s];
			var b = back.Strokes[s];

			Check( $"stroke {s} keeps its point count", b.Path.Count == a.Path.Count,
				$"{a.Path.Count} became {b.Path.Count}" );

			Check( $"stroke {s} keeps its colour", b.R == a.R && b.G == a.G && b.B == a.B && b.A == a.A );
			Check( $"stroke {s} keeps its radius", b.Radius == a.Radius );
			Check( $"stroke {s} keeps its strength", b.Strength == a.Strength );
			Check( $"stroke {s} keeps its falloff", b.Falloff == a.Falloff );
			Check( $"stroke {s} keeps its spacing", b.Spacing == a.Spacing );

			for ( var p = 0; p < a.Path.Count && p < b.Path.Count; p++ )
			{
				Check( $"stroke {s} point {p} keeps its position", Close( a.Path[p].Position, b.Path[p].Position ) );
				Check( $"stroke {s} point {p} keeps its normal", Close( a.Path[p].Normal, b.Path[p].Normal ) );
			}
		}
	}

	static void TestOrderIsPreserved()
	{
		var original = Painted();
		var originalPaint = original.Features.OfType<PaintFeature>().Single();
		var back = StudioDocument.Read( StudioDocument.Write( original ) ).Features.OfType<PaintFeature>().Single();

		// Red then green, deliberately. Blending does not commute, so the exact sequence is the thing
		// a reorder destroys — assert the colours come back in the painted order, not sorted.
		var orderKept = back.Strokes.Count == originalPaint.Strokes.Count;

		for ( var s = 0; orderKept && s < originalPaint.Strokes.Count; s++ )
		{
			orderKept = back.Strokes[s].R == originalPaint.Strokes[s].R
				&& back.Strokes[s].G == originalPaint.Strokes[s].G
				&& back.Strokes[s].B == originalPaint.Strokes[s].B;
		}

		Check( "strokes come back in the order they were painted", orderKept );
	}

	static void TestEmptyPathRoundTrips()
	{
		var studio = new PartStudio();
		var paint = studio.Add( new PaintFeature() );
		paint.AddStroke( MakeStroke( 1f, 1f, 1f, 1f, 0.1f, 1f, BrushFalloff.Smooth, 0.5f, 0 ) );

		// Reaching the assertions at all is the "does not throw" half — a write that threw would
		// crash the runner rather than report a failed check.
		var back = StudioDocument.Read( StudioDocument.Write( studio ) ).Features.OfType<PaintFeature>().Single();

		Check( "a stroke with no points survives the round trip", back.Strokes.Count == 1,
			$"{back.Strokes.Count} strokes" );
		Check( "and keeps its empty path", back.Strokes[0].Path.Count == 0,
			$"{back.Strokes[0].Path.Count} points" );
	}

	static void TestSavingTwiceIsIdentical()
	{
		var studio = Painted();

		var first = StudioDocument.Write( studio );
		var second = StudioDocument.Write( studio );

		Check( "saving the same document twice gives the same bytes", first == second );
	}

	static void TestStaleness()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature() );

		var paint = studio.Add( new PaintFeature() );
		studio.Rebuild();

		Check( "a rebuilt paint feature is not stale", !paint.IsStale );

		paint.AddStroke( MakeStroke( 1f, 0f, 0f, 1f, 0.1f, 1f, BrushFalloff.Smooth, 0.5f, 2 ) );

		Check( "appending a stroke marks it stale", paint.IsStale );

		studio.Rebuild();

		Check( "and a rebuild clears it", !paint.IsStale );
	}

	static bool Close( Vec3 a, Vec3 b ) =>
		MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f && MathF.Abs( a.z - b.z ) < 1e-5f;
}
