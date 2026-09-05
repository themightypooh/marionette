using System;

namespace Effigy;

/// <summary>
/// The kernel's own vector types.
///
/// THIS IS DELIBERATE, NOT AN OVERSIGHT. The kernel exists to be portable — the same source
/// compiles under s&amp;box, Godot's C#, or a plain console runner, and the engine question stays
/// reversible. The moment it references Sandbox.Vector3 or Godot.Vector3 that stops being true and
/// a port becomes a rewrite.
///
/// Engine glue converts at the boundary. That is a handful of lines per engine, paid once.
/// </summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
	public readonly float x, y, z;

	public Vec3( float x, float y, float z )
	{
		this.x = x;
		this.y = y;
		this.z = z;
	}

	public static readonly Vec3 Zero = new( 0, 0, 0 );
	public static readonly Vec3 One = new( 1, 1, 1 );

	public static Vec3 operator +( Vec3 a, Vec3 b ) => new( a.x + b.x, a.y + b.y, a.z + b.z );
	public static Vec3 operator -( Vec3 a, Vec3 b ) => new( a.x - b.x, a.y - b.y, a.z - b.z );
	public static Vec3 operator -( Vec3 a ) => new( -a.x, -a.y, -a.z );
	public static Vec3 operator *( Vec3 a, float s ) => new( a.x * s, a.y * s, a.z * s );
	public static Vec3 operator *( float s, Vec3 a ) => a * s;
	public static Vec3 operator /( Vec3 a, float s ) => new( a.x / s, a.y / s, a.z / s );

	public float LengthSquared => x * x + y * y + z * z;
	public float Length => MathF.Sqrt( LengthSquared );

	/// <summary>Unit-length copy. Returns Zero for a degenerate vector rather than NaN, because a
	/// zero-area face should produce a harmless normal instead of poisoning everything downstream
	/// of it.</summary>
	public Vec3 Normal
	{
		get
		{
			var len = Length;
			return len > 1e-12f ? this / len : Zero;
		}
	}

	public static float Dot( Vec3 a, Vec3 b ) => a.x * b.x + a.y * b.y + a.z * b.z;

	public static Vec3 Cross( Vec3 a, Vec3 b ) => new(
		a.y * b.z - a.z * b.y,
		a.z * b.x - a.x * b.z,
		a.x * b.y - a.y * b.x );

	public static Vec3 Lerp( Vec3 a, Vec3 b, float t ) => a + (b - a) * t;

	public bool Equals( Vec3 o ) => x == o.x && y == o.y && z == o.z;
	public override bool Equals( object obj ) => obj is Vec3 v && Equals( v );
	public override int GetHashCode() => HashCode.Combine( x, y, z );
	public override string ToString() => $"({x:0.####}, {y:0.####}, {z:0.####})";

	/// <summary>Component-wise comparison with a tolerance, for tests and for weld thresholds.</summary>
	public bool AlmostEquals( Vec3 o, float eps = 1e-4f ) =>
		MathF.Abs( x - o.x ) <= eps && MathF.Abs( y - o.y ) <= eps && MathF.Abs( z - o.z ) <= eps;
}

/// <summary>
/// A four-component vector — RGBA colour. Same portability rule as Vec3: the kernel has no engine
/// type, so a vertex colour is this rather than Sandbox.Color or Godot.Color, and engine glue
/// converts at the boundary the same way it does for positions and UVs.
/// </summary>
public readonly struct Vec4
{
	public readonly float x, y, z, w;

	public Vec4( float x, float y, float z, float w )
	{
		this.x = x;
		this.y = y;
		this.z = z;
		this.w = w;
	}

	public static readonly Vec4 Zero = new( 0, 0, 0, 0 );

	public static Vec4 operator +( Vec4 a, Vec4 b ) => new( a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w );
	public static Vec4 operator -( Vec4 a, Vec4 b ) => new( a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w );
	public static Vec4 operator *( Vec4 a, float s ) => new( a.x * s, a.y * s, a.z * s, a.w * s );
	public static Vec4 operator *( float s, Vec4 a ) => a * s;

	public static Vec4 Lerp( Vec4 a, Vec4 b, float t ) => a + (b - a) * t;

	/// <summary>
	/// This colour as the TINT a vertex-colour consumer expects. "No paint" — <c>w == 0</c> — maps to
	/// white, because a material multiplies by vertex colour and multiply-by-white leaves it
	/// unchanged; coverage then fades the vertex from white toward the paint colour. This is the
	/// multiply flavour of compositing, the one the engine's standard material does natively; an
	/// alpha-blend (where the paint REPLACES the material under it) is a different read of the same
	/// stored colour and needs a shader.
	/// </summary>
	public Vec3 Tint() => new(
		1f - (1f - x) * w,
		1f - (1f - y) * w,
		1f - (1f - z) * w );

	public bool Equals( Vec4 o ) => x == o.x && y == o.y && z == o.z && w == o.w;
	public override bool Equals( object obj ) => obj is Vec4 v && Equals( v );
	public override int GetHashCode() => HashCode.Combine( x, y, z, w );
	public override string ToString() => $"({x:0.####}, {y:0.####}, {z:0.####}, {w:0.####})";
}

/// <summary>Texture coordinate. Same portability rule as Vec3.</summary>
public readonly struct Vec2
{
	public readonly float x, y;

	public Vec2( float x, float y )
	{
		this.x = x;
		this.y = y;
	}

	public static readonly Vec2 Zero = new( 0, 0 );

	public static Vec2 operator +( Vec2 a, Vec2 b ) => new( a.x + b.x, a.y + b.y );
	public static Vec2 operator -( Vec2 a, Vec2 b ) => new( a.x - b.x, a.y - b.y );
	public static Vec2 operator *( Vec2 a, float s ) => new( a.x * s, a.y * s );
	public static Vec2 operator /( Vec2 a, float s ) => new( a.x / s, a.y / s );

	public float LengthSquared => x * x + y * y;
	public float Length => MathF.Sqrt( LengthSquared );

	/// <summary>Unit-length copy, or Zero for a degenerate vector — same reasoning as Vec3.Normal:
	/// a zero-length edge should produce something harmless rather than NaN.</summary>
	public Vec2 Normal
	{
		get
		{
			var len = Length;
			return len > 1e-12f ? this / len : Zero;
		}
	}

	public static float Dot( Vec2 a, Vec2 b ) => a.x * b.x + a.y * b.y;

	/// <summary>The 2D cross product — a scalar. Its sign says which side of `a` the vector `b`
	/// falls on, which is how winding and "is this point left of that line" get decided.</summary>
	public static float Cross( Vec2 a, Vec2 b ) => a.x * b.y - a.y * b.x;

	public override string ToString() => $"({x:0.####}, {y:0.####})";
}
