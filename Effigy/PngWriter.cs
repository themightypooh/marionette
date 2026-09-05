using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Effigy;

/// <summary>
/// An 8-bit RGB or RGBA PNG, written by hand.
///
/// WRITTEN BY HAND FOR THE REASON EVERY OTHER FORMAT HERE IS: the kernel has no dependencies, so it
/// can be dropped into s&amp;box or Godot or a console runner as loose .cs files. ObjWriter, SmdWriter
/// and DmxWriter all make the same trade, and a normal map that only the test project could save
/// would be a bake nobody can use.
///
/// It is not a general encoder and does not try to be. Two colour types (truecolour RGB and
/// truecolour with alpha), one bit depth (8), filter 0 on every scanline, everything in a single
/// IDAT. That is a valid PNG by the spec's own reading and it is what a bake needs; adding filters
/// would shrink the file and is not worth a byte of the complexity until something is actually short
/// of disk.
///
/// The deflate comes from System.IO.Compression, which is the BCL rather than a dependency.
/// DeflateStream emits a RAW deflate stream, so the two-byte zlib header and the trailing Adler-32
/// are written here — a PNG whose IDAT is raw deflate opens in nothing, and the failure looks like
/// a corrupt file rather than a missing wrapper.
/// </summary>
public static class PngWriter
{
	/// <summary>Write RGB bytes — three per pixel, row-major, first row at the TOP of the
	/// image — to a PNG file.</summary>
	public static void WriteFile( string path, byte[] rgb, int width, int height )
	{
		using var stream = File.Create( path );
		Write( stream, rgb, width, height );
	}

	/// <summary>The RGBA counterpart of <see cref="WriteFile(string, byte[], int, int)"/> — four bytes
	/// per pixel, written as colour type 6.</summary>
	public static void WriteFileRgba( string path, byte[] rgba, int width, int height )
	{
		using var stream = File.Create( path );
		WriteRgba( stream, rgba, width, height );
	}

	/// <summary>The same bytes as a PNG in memory, for a caller that has somewhere else to put
	/// them.</summary>
	public static byte[] ToBytes( byte[] rgb, int width, int height )
	{
		using var ms = new MemoryStream();
		Write( ms, rgb, width, height );
		return ms.ToArray();
	}

	/// <summary>The RGBA counterpart of <see cref="ToBytes(byte[], int, int)"/>.</summary>
	public static byte[] ToBytesRgba( byte[] rgba, int width, int height )
	{
		using var ms = new MemoryStream();
		WriteRgba( ms, rgba, width, height );
		return ms.ToArray();
	}

	/// <summary>
	/// A baked normal map as a PNG.
	///
	/// ROW ORDER IS A DECISION AND IT IS MADE HERE. BakedMap holds row 0 at v = 0; a PNG's first
	/// row is the top of the image. Passing <paramref name="flipVertically"/> writes v = 0 at the
	/// bottom instead, which is what most engines' UV convention wants. Which one s&amp;box wants is
	/// not something this file can know — see BakeOptions.FlipGreen for the other half of the same
	/// question, and the sample written by the test suite for something to look at.
	/// </summary>
	public static void WriteFile( string path, BakedMap map, bool flipVertically = false )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );

		WriteFile( path, flipVertically ? Flipped( map ) : map.Rgb, map.Width, map.Height );
	}

	static byte[] Flipped( BakedMap map )
	{
		var stride = map.Width * 3;
		var result = new byte[map.Rgb.Length];

		for ( var y = 0; y < map.Height; y++ )
			Array.Copy( map.Rgb, y * stride, result, (map.Height - 1 - y) * stride, stride );

		return result;
	}

	public static void Write( Stream stream, byte[] rgb, int width, int height )
	{
		if ( stream is null )
			throw new ArgumentNullException( nameof( stream ) );

		if ( rgb is null )
			throw new ArgumentNullException( nameof( rgb ) );

		if ( width < 1 || height < 1 )
			throw new ArgumentOutOfRangeException( nameof( width ), "A PNG needs at least one pixel." );

		if ( rgb.Length < width * height * 3 )
			throw new ArgumentException(
				$"{width}x{height} needs {width * height * 3} bytes of RGB; got {rgb.Length}." );

		WritePixels( stream, rgb, width, height, 3, 2 );
	}

	/// <summary>The RGBA counterpart of <see cref="Write"/> — four bytes per pixel, colour type 6.
	/// The RGB path is deliberately left untouched beside it; a normal-map bake still flows through
	/// <see cref="Write"/> and its output must stay byte-identical.</summary>
	public static void WriteRgba( Stream stream, byte[] rgba, int width, int height )
	{
		if ( stream is null )
			throw new ArgumentNullException( nameof( stream ) );

		if ( rgba is null )
			throw new ArgumentNullException( nameof( rgba ) );

		if ( width < 1 || height < 1 )
			throw new ArgumentOutOfRangeException( nameof( width ), "A PNG needs at least one pixel." );

		if ( rgba.Length < width * height * 4 )
			throw new ArgumentException(
				$"{width}x{height} needs {width * height * 4} bytes of RGBA; got {rgba.Length}." );

		WritePixels( stream, rgba, width, height, 4, 6 );
	}

	// The shared writer behind both colour types. Validation lives in the callers so each reports its
	// own byte count and colour name; below that the two paths are identical except how many bytes a
	// scanline carries and which colour type the header names.
	static void WritePixels( Stream stream, byte[] pixels, int width, int height, int channels, int colourType )
	{
		// Every scanline is prefixed with its filter type. 0 means None.
		var raw = new byte[height * (width * channels + 1)];
		var o = 0;

		for ( var y = 0; y < height; y++ )
		{
			raw[o++] = 0;
			Array.Copy( pixels, y * width * channels, raw, o, width * channels );
			o += width * channels;
		}

		stream.Write( new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } );

		var ihdr = new byte[13];
		WriteBe( ihdr, 0, width );
		WriteBe( ihdr, 4, height );
		ihdr[8] = 8;  // bit depth
		ihdr[9] = (byte)colourType;  // 2 = truecolour RGB, 6 = truecolour with alpha
		Chunk( stream, "IHDR", ihdr );

		Chunk( stream, "IDAT", ZlibCompress( raw ) );
		Chunk( stream, "IEND", Array.Empty<byte>() );
	}

	static void WriteBe( byte[] b, int offset, int value )
	{
		b[offset] = (byte)(value >> 24);
		b[offset + 1] = (byte)(value >> 16);
		b[offset + 2] = (byte)(value >> 8);
		b[offset + 3] = (byte)value;
	}

	static void Chunk( Stream s, string type, byte[] data )
	{
		var length = new byte[4];
		WriteBe( length, 0, data.Length );
		s.Write( length );

		var typeBytes = Encoding.ASCII.GetBytes( type );
		s.Write( typeBytes );
		s.Write( data );

		var crc = Crc32( typeBytes, data );
		var crcBytes = new byte[4];
		WriteBe( crcBytes, 0, unchecked((int)crc) );
		s.Write( crcBytes );
	}

	/// <summary>DeflateStream emits a raw deflate stream; zlib wants a 2-byte header in front and
	/// an Adler-32 of the UNCOMPRESSED data on the end.</summary>
	static byte[] ZlibCompress( byte[] data )
	{
		using var ms = new MemoryStream();

		ms.WriteByte( 0x78 );
		ms.WriteByte( 0x01 );

		using ( var deflate = new DeflateStream( ms, CompressionLevel.Optimal, leaveOpen: true ) )
			deflate.Write( data );

		uint a = 1, b = 0;

		foreach ( var x in data )
		{
			a = (a + x) % 65521;
			b = (b + a) % 65521;
		}

		var adler = (b << 16) | a;
		ms.WriteByte( (byte)(adler >> 24) );
		ms.WriteByte( (byte)(adler >> 16) );
		ms.WriteByte( (byte)(adler >> 8) );
		ms.WriteByte( (byte)adler );

		return ms.ToArray();
	}

	static readonly uint[] CrcTable = BuildCrcTable();

	static uint[] BuildCrcTable()
	{
		var table = new uint[256];

		for ( uint n = 0; n < 256; n++ )
		{
			var c = n;

			for ( var k = 0; k < 8; k++ )
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

			table[n] = c;
		}

		return table;
	}

	static uint Crc32( byte[] a, byte[] b )
	{
		var c = 0xFFFFFFFFu;

		foreach ( var x in a )
			c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);

		foreach ( var x in b )
			c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);

		return c ^ 0xFFFFFFFFu;
	}
}
