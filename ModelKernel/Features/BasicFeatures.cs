using System;
using System.Collections.Generic;
using System.Linq;

namespace ModelKernel;

/// <summary>
/// Creates a primitive solid. Onshape's own Primitives feature works this way — one dropdown, and
/// the dialog shows only the fields that shape actually has.
///
/// Parameters deliberately changes with Shape rather than showing every field greyed out. That is
/// the behaviour being copied: a box dialog asks for three lengths and nothing else.
/// </summary>
public sealed class PrimitiveFeature : Feature
{
	public override string TypeName => Shape.Value;

	public readonly ChoiceParam Shape = new( "Shape",
		new[] { "Box", "Cylinder", "Sphere", "Wedge", "Tube", "Plane" } );

	public readonly FloatParam SizeX = new( "Width", 1f, 0.0001f, unit: "u" );
	public readonly FloatParam SizeY = new( "Depth", 1f, 0.0001f, unit: "u" );
	public readonly FloatParam SizeZ = new( "Height", 1f, 0.0001f, unit: "u" );
	public readonly FloatParam Radius = new( "Radius", 0.5f, 0.0001f, unit: "u" );
	public readonly FloatParam InnerRadius = new( "Inner radius", 0.3f, 0f, unit: "u" );
	public readonly IntParam Segments = new( "Segments", 16, 3, 512 );
	public readonly IntParam Divisions = new( "Divisions", 4, 1, 64 );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 );

	public override IReadOnlyList<IParam> Parameters => Shape.Value switch
	{
		"Box" => new IParam[] { Shape, SizeX, SizeY, SizeZ, Position, Material },
		"Cylinder" => new IParam[] { Shape, Radius, SizeZ, Segments, Position, Material },
		"Sphere" => new IParam[] { Shape, Radius, Divisions, Position, Material },
		"Wedge" => new IParam[] { Shape, SizeX, SizeY, SizeZ, Position, Material },
		"Tube" => new IParam[] { Shape, Radius, InnerRadius, SizeZ, Segments, Position, Material },
		"Plane" => new IParam[] { Shape, SizeX, SizeY, Segments, Position, Material },
		_ => new IParam[] { Shape }
	};

	protected override void Execute( FeatureContext ctx )
	{
		var mesh = Shape.Value switch
		{
			"Box" => Primitives.Box( SizeX.Clamped, SizeY.Clamped, SizeZ.Clamped, Material.Clamped ),
			"Cylinder" => Primitives.Cylinder( Radius.Clamped, SizeZ.Clamped, Segments.Clamped, Material.Clamped ),
			"Sphere" => Primitives.QuadSphere( Radius.Clamped, Divisions.Clamped, Material.Clamped ),
			"Wedge" => Primitives.Wedge( SizeX.Clamped, SizeY.Clamped, SizeZ.Clamped, Material.Clamped ),
			"Tube" => BuildTube(),
			"Plane" => Primitives.Plane( SizeX.Clamped, SizeY.Clamped, Segments.Clamped, Segments.Clamped, Material.Clamped ),
			_ => throw new InvalidOperationException( $"unknown shape '{Shape.Value}'" )
		};

		if ( Position.Value.LengthSquared > 0f )
			MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}

	PolyMesh BuildTube()
	{
		// Caught here rather than left to Primitives, so the message names the parameter the user
		// can actually see in the dialog.
		if ( InnerRadius.Clamped >= Radius.Clamped )
			throw new InvalidOperationException( "Inner radius must be smaller than radius" );

		return Primitives.Tube( Radius.Clamped, InnerRadius.Clamped, SizeZ.Clamped, Segments.Clamped, Material.Clamped );
	}
}

/// <summary>Move, rotate and scale bodies. Onshape's Transform.</summary>
public sealed class TransformFeature : Feature
{
	public override string TypeName => "Transform";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param Translate = new( "Translate", Vec3.Zero );
	public readonly Vec3Param RotationAxis = new( "Rotation axis", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam RotationAngle = new( "Angle", 0f, unit: "deg" );
	public readonly Vec3Param Scale = new( "Scale", Vec3.One );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Translate, RotationAxis, RotationAngle, Scale };

	protected override void Execute( FeatureContext ctx )
	{
		var scale = Scale.Value;

		if ( scale.x == 0f || scale.y == 0f || scale.z == 0f )
			throw new InvalidOperationException( "Scale cannot be zero on any axis" );

		// Scale, then rotate, then translate — the order a user expects, and the one that keeps a
		// rotation about the origin from being skewed by a non-uniform scale applied after it.
		var xform =
			Xform.Translate( Translate.Value )
			* Xform.Rotate( RotationAxis.Value, RotationAngle.Value * MathF.PI / 180f )
			* Xform.Scale( scale );

		foreach ( var body in ctx.Bodies.Where( Bodies.Matches ) )
			MeshTransform.Apply( body.Mesh, xform );
	}
}

/// <summary>Copies along a direction. Onshape's Linear pattern.</summary>
public sealed class LinearPatternFeature : Feature
{
	public override string TypeName => "Linear pattern";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 1, 0, 0 ) );
	public readonly FloatParam Spacing = new( "Spacing", 1f, unit: "u" );
	public readonly IntParam Count = new( "Instances", 3, 1, 4096 );
	public readonly BoolParam Merge = new( "Merge into one body", false );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Direction, Spacing, Count, Merge };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Direction.Value.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Direction cannot be zero" );

		var dir = Direction.Value.Normal;
		var sources = ctx.Bodies.Where( Bodies.Matches ).ToList();

		if ( sources.Count == 0 )
			throw new InvalidOperationException( "No bodies selected" );

		// Instance 0 is the original, so a count of 3 means the original plus two copies — which
		// is what Onshape's instance count means too.
		foreach ( var source in sources )
		{
			// Snapshot BEFORE the loop. With Merge on, the loop appends into source.Mesh, so
			// reading source.Mesh each iteration would copy the copies too and the instance count
			// would double rather than increment: 6, 12, 24, 48 faces instead of 6, 12, 18, 24.
			var original = source.Mesh.Clone();

			for ( var i = 1; i < Count.Clamped; i++ )
			{
				var copy = MeshTransform.Transformed( original, Xform.Translate( dir * (Spacing.Value * i) ) );

				if ( Merge.Value )
					MeshTransform.Append( source.Mesh, copy );
				else
					ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name} {i}", copy ) );
			}
		}
	}
}

/// <summary>Copies around an axis. Onshape's Circular pattern.</summary>
public sealed class CircularPatternFeature : Feature
{
	public override string TypeName => "Circular pattern";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param AxisPoint = new( "Axis through", Vec3.Zero );
	public readonly Vec3Param AxisDirection = new( "Axis", new Vec3( 0, 0, 1 ) );
	public readonly IntParam Count = new( "Instances", 4, 1, 4096 );
	public readonly FloatParam TotalAngle = new( "Angle", 360f, unit: "deg" );
	public readonly BoolParam Merge = new( "Merge into one body", false );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, AxisPoint, AxisDirection, Count, TotalAngle, Merge };

	protected override void Execute( FeatureContext ctx )
	{
		if ( AxisDirection.Value.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Axis cannot be zero" );

		var sources = ctx.Bodies.Where( Bodies.Matches ).ToList();

		if ( sources.Count == 0 )
			throw new InvalidOperationException( "No bodies selected" );

		var count = Count.Clamped;

		// A full turn puts instance N back on instance 0, so the step divides by count. A partial
		// sweep spreads the instances across the arc inclusive of both ends instead.
		var full = MathF.Abs( MathF.Abs( TotalAngle.Value ) - 360f ) < 1e-3f;
		var step = count <= 1 ? 0f : TotalAngle.Value / (full ? count : count - 1);

		foreach ( var source in sources )
		{
			// Snapshot before the loop — same compounding trap as the linear pattern.
			var original = source.Mesh.Clone();

			for ( var i = 1; i < count; i++ )
			{
				var xform = Xform.RotateAbout(
					AxisPoint.Value, AxisDirection.Value, step * i * MathF.PI / 180f );

				var copy = MeshTransform.Transformed( original, xform );

				if ( Merge.Value )
					MeshTransform.Append( source.Mesh, copy );
				else
					ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name} {i}", copy ) );
			}
		}
	}
}

/// <summary>Reflects bodies in a plane. Onshape's Mirror.</summary>
public sealed class MirrorFeature : Feature
{
	public override string TypeName => "Mirror";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param PlanePoint = new( "Plane through", Vec3.Zero );
	public readonly Vec3Param PlaneNormal = new( "Plane normal", new Vec3( 1, 0, 0 ) );
	public readonly BoolParam KeepOriginal = new( "Keep original", true );
	public readonly BoolParam Merge = new( "Merge into one body", false );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, PlanePoint, PlaneNormal, KeepOriginal, Merge };

	protected override void Execute( FeatureContext ctx )
	{
		if ( PlaneNormal.Value.LengthSquared < 1e-12f )
			throw new InvalidOperationException( "Plane normal cannot be zero" );

		var xform = Xform.Mirror( PlanePoint.Value, PlaneNormal.Value );
		var sources = ctx.Bodies.Where( Bodies.Matches ).ToList();

		if ( sources.Count == 0 )
			throw new InvalidOperationException( "No bodies selected" );

		foreach ( var source in sources )
		{
			// MeshTransform.Apply reverses winding for us — the mirror flips handedness, and
			// without that reversal every mirrored face would point into the solid.
			var copy = MeshTransform.Transformed( source.Mesh, xform );

			if ( Merge.Value )
				MeshTransform.Append( source.Mesh, copy );
			else
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{Name}", copy ) );
		}

		if ( !KeepOriginal.Value )
		{
			foreach ( var source in sources )
				ctx.Bodies.Remove( source );
		}
	}
}

/// <summary>
/// Catmull-Clark subdivision as a history feature.
///
/// Onshape has no equivalent, and this is the deliberate place where the tool stops being CAD.
/// Putting subdivision IN the tree rather than after it is what keeps the pipeline honest: roll
/// the bar back above this feature and you are editing the low-poly cage, roll it forward and you
/// see the dense surface. The cage is what the sculpt eventually bakes down onto, so it has to
/// stay reachable rather than being consumed by an export step.
/// </summary>
public sealed class SubdivideFeature : Feature
{
	public override string TypeName => "Subdivide";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly IntParam Levels = new( "Levels", 1, 0, 6 );

	/// <summary>Off by default so an existing tree's behaviour never changes underneath it — this
	/// is an opt-in fix, not a silent change to what Subdivide already did. See CreaseTools for
	/// why a default cube needs this to stay a cube rather than becoming a ball.</summary>
	public readonly BoolParam AutoCrease = new( "Preserve hard edges", false );
	public readonly FloatParam CreaseAngle = new( "Crease angle", 30f, 0f, 180f, unit: "deg" );

	public override IReadOnlyList<IParam> Parameters => AutoCrease.Value
		? new IParam[] { Bodies, Levels, AutoCrease, CreaseAngle }
		: new IParam[] { Bodies, Levels, AutoCrease };

	/// <summary>What this feature will cost at the current settings, for a UI that warns before
	/// rather than after. Levels are exponential and the jump from 4 to 6 is 16x.</summary>
	public (int Vertices, int Faces) PredictCost( IEnumerable<Body> bodies )
	{
		var v = 0;
		var f = 0;

		foreach ( var body in bodies.Where( Bodies.Matches ) )
		{
			var (bv, bf) = CatmullClark.PredictCost( body.Mesh, Levels.Clamped );
			v += bv;
			f += bf;
		}

		return (v, f);
	}

	protected override void Execute( FeatureContext ctx )
	{
		if ( Levels.Clamped == 0 )
			return;

		foreach ( var body in ctx.Bodies.Where( Bodies.Matches ) )
		{
			// Marking runs on the CAGE, before subdividing — the same order Blender's "mark
			// sharp by angle" runs in, and the only order that makes sense: the angle between two
			// adjacent faces is only meaningful on the coarse mesh that still has those faces.
			if ( AutoCrease.Value )
				CreaseTools.MarkSharpByAngle( body.Mesh, CreaseAngle.Clamped );

			body.Mesh = CatmullClark.Subdivide( body.Mesh, Levels.Clamped );
		}
	}
}
