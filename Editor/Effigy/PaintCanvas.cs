using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// An RGBA canvas a paint stroke composites into, held CPU-side.
///
/// PARKED, NOT LIVE. Nothing in the shipped tool calls this - the paint that ships is vertex
/// colour, and its path is PaintReplay.ReplayColors. This texture side is reachable only from its
/// own tests. It is kept rather than deleted because docs/dev/PAINTING.md §6 closes the question
/// "for now" and names texels as the better long-term answer: resolution independent of mesh
/// density, exporting as a real texture. Reviving it needs the authoring half nobody has written -
/// bake the canvas to PNG, compile a .vtex, generate a .vmat, bind the slot.
///
/// SO DO NOT READ IT AS THE PAINT PIPELINE. If you are following how a stroke reaches the screen,
/// you want PaintReplay.ReplayColors and PaintFeature, not this.
///
/// DELIBERATELY THE SAME SHAPE AS BakedMap. A baked normal map is a `byte[] Rgb` plus a `bool[]`
/// mask, written straight out by PngWriter; this is a `byte[] Rgba`, four bytes a texel with the
/// alpha the normal map has no use for. Matching that shape means the PNG writer grows one colour
/// type instead of a second writer, and the editor's texture upload is a memcpy either way.
///
/// The one thing BakedMap does not have is what this exists to carry: a DIRTY RECT. The old paint
/// project stamped a dab into a CPU buffer and then shipped the entire 1024^2 image back to the GPU
/// on every mouse-move — four megabytes a frame — see docs/dev/PAINTING.md section 2 cause 5. The
/// rect below is the fix: the smallest bounds a dab touched, so the editor re-uploads only that.
/// </summary>
public sealed class PaintCanvas
{
	public readonly int Width;
	public readonly int Height;

	/// <summary>
	/// Four bytes per texel, row-major, row 0 at the TOP of the image. Straight (non-premultiplied)
	/// alpha, because that is what a PNG colour type 6 stores and what a texture upload consumes; a
	/// premultiplied buffer would round-trip through the writer wrong.
	/// </summary>
	public readonly byte[] Rgba;

	bool _dirty;
	int _minX, _minY, _maxX, _maxY;

	public PaintCanvas( int width, int height )
	{
		Width = width;
		Height = height;
		Rgba = new byte[width * height * 4];
	}

	/// <summary>
	/// Source-over composite of a solid colour at <paramref name="weight"/> coverage onto one texel.
	///
	/// THE COLOUR HAS NO ALPHA OF ITS OWN. A stroke is one RGBA colour carried once for the whole
	/// stroke, and per-texel coverage comes from the falloff curve; so the incoming source is the
	/// colour with alpha equal to the coverage. Two half-weight dabs of red are therefore a lighter
	/// red than one full-weight dab — the alpha rises both times, and the result is not the same.
	///
	/// weight &lt;= 0 is a no-op and does not touch the dirty rect: the caller rasterises a triangle's
	/// bounding box, so it hands back plenty of texels that take no paint at all. Out-of-bounds
	/// coordinates are ignored rather than thrown for the same reason — the caller already clamps its
	/// loop, and a second clamp here is one more place to get it wrong.
	/// </summary>
	public void Blend( int x, int y, byte r, byte g, byte b, float weight )
	{
		if ( weight <= 0f )
			return;

		if ( x < 0 || y < 0 || x >= Width || y >= Height )
			return;

		var i = (y * Width + x) * 4;

		var sa = Math.Min( weight, 1f );
		var da = Rgba[i + 3] / 255f;
		var oa = sa + da * (1f - sa);

		// Source-over, computed in premultiplied form and divided back out to straight alpha: the
		// incoming colour scaled by its own coverage, the colour already there by what of it the new
		// coverage does not cover. oa is never zero here — sa > 0 implies oa >= sa.
		var or = (r / 255f) * sa + (Rgba[i] / 255f) * da * (1f - sa);
		var og = (g / 255f) * sa + (Rgba[i + 1] / 255f) * da * (1f - sa);
		var ob = (b / 255f) * sa + (Rgba[i + 2] / 255f) * da * (1f - sa);

		or /= oa;
		og /= oa;
		ob /= oa;

		Rgba[i] = (byte)MathF.Round( or * 255f );
		Rgba[i + 1] = (byte)MathF.Round( og * 255f );
		Rgba[i + 2] = (byte)MathF.Round( ob * 255f );
		Rgba[i + 3] = (byte)MathF.Round( oa * 255f );

		Mark( x, y );
	}

	/// <summary>Whether anything has touched the canvas since the last <see cref="ClearDirty"/>.</summary>
	public bool HasDirty => _dirty;

	/// <summary>The leftmost column touched, when <see cref="HasDirty"/> is true.</summary>
	public int MinX => _minX;

	/// <summary>The topmost row touched, when <see cref="HasDirty"/> is true.</summary>
	public int MinY => _minY;

	/// <summary>The rightmost column touched, when <see cref="HasDirty"/> is true.</summary>
	public int MaxX => _maxX;

	/// <summary>The bottommost row touched, when <see cref="HasDirty"/> is true.</summary>
	public int MaxY => _maxY;

	public void ClearDirty() => _dirty = false;

	/// <summary>Back to transparent, the whole canvas marked dirty so the next upload repaints it all.</summary>
	public void Clear()
	{
		Array.Clear( Rgba, 0, Rgba.Length );

		_dirty = true;
		_minX = 0;
		_minY = 0;
		_maxX = Width - 1;
		_maxY = Height - 1;
	}

	/// <summary>
	/// Bleed filled texels outward, so a shader filtering across an island's edge finds colour there
	/// instead of the transparent gutter. Same idea as <see cref="NormalBake.Dilate"/> — each pass
	/// averages the filled neighbours into the unfilled ones — but over RGBA, where "filled" is
	/// simply a non-zero alpha and the alpha itself is one of the channels being averaged.
	///
	/// Marks the whole canvas dirty: dilation spreads outward and could touch anywhere, so there is
	/// no smaller rect worth tracking.
	/// </summary>
	internal void Dilate( int passes )
	{
		if ( passes <= 0 )
			return;

		var filled = new bool[Width * Height];

		for ( var i = 0; i < filled.Length; i++ )
			filled[i] = Rgba[i * 4 + 3] > 0;

		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int Index, int R, int G, int B, int A)>();

			for ( var y = 0; y < Height; y++ )
			{
				for ( var x = 0; x < Width; x++ )
				{
					var index = y * Width + x;

					if ( filled[index] )
						continue;

					int r = 0, g = 0, b = 0, a = 0, n = 0;

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							var nx = x + dx;
							var ny = y + dy;

							if ( nx < 0 || ny < 0 || nx >= Width || ny >= Height )
								continue;

							var other = ny * Width + nx;

							if ( !filled[other] )
								continue;

							var o = other * 4;
							r += Rgba[o];
							g += Rgba[o + 1];
							b += Rgba[o + 2];
							a += Rgba[o + 3];
							n++;
						}
					}

					if ( n > 0 )
						added.Add( (index, r / n, g / n, b / n, a / n) );
				}
			}

			if ( added.Count == 0 )
				return;

			foreach ( var (index, r, g, b, a) in added )
			{
				var o = index * 4;
				Rgba[o] = (byte)r;
				Rgba[o + 1] = (byte)g;
				Rgba[o + 2] = (byte)b;
				Rgba[o + 3] = (byte)a;
				filled[index] = true;
			}
		}

		_dirty = true;
		_minX = 0;
		_minY = 0;
		_maxX = Width - 1;
		_maxY = Height - 1;
	}

	void Mark( int x, int y )
	{
		if ( !_dirty )
		{
			_dirty = true;
			_minX = _maxX = x;
			_minY = _maxY = y;
			return;
		}

		if ( x < _minX ) _minX = x;
		if ( x > _maxX ) _maxX = x;
		if ( y < _minY ) _minY = y;
		if ( y > _maxY ) _maxY = y;
	}
}
