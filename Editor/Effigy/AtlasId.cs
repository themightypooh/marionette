using System;

namespace Effigy;

/// <summary>
/// A stable id for a mesh's atlas: every corner UV, in face order then corner order.
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
/// <see cref="MultiresSculpt.TopologyId"/> deliberately ignores UVs, because positions are what a
/// parametric edit is expected to change. That is exactly what makes it the wrong key for a cached
/// paint canvas: a re-unwrap keeps the topology and moves every island, so it produces the same
/// topology id, and a canvas keyed on that alone would be handed back against a rearranged atlas —
/// paint scattered onto unrelated faces, silently. This id sits beside TopologyId in the cache key,
/// so "same mesh, re-unwrapped" and "same mesh, same atlas" stop being the same thing.
///
/// Floats are hashed by their exact bits rather than rounded, because an island nudged a hair is a
/// different atlas and the id has to say so.
/// </summary>
public static class AtlasId
{
	public static long Of( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		// FNV-1a, written out rather than leaning on GetHashCode: this value goes in a file and has
		// to mean the same thing in the next process and on the next runtime.
		const long prime = 0x100000001b3;
		var hash = unchecked((long)0xcbf29ce484222325);

		void Mix( float value )
		{
			var bits = BitConverter.SingleToInt32Bits( value );

			for ( var b = 0; b < 4; b++ )
			{
				hash ^= (bits >> (b * 8)) & 0xff;
				hash = unchecked(hash * prime);
			}
		}

		foreach ( var face in mesh.Faces )
		{
			// The corner count delimits the faces, the same way TopologyId mixes face.Count: without
			// it, a quad's UVs and two triangles' UVs are the same flat sequence and the id cannot
			// tell them apart.
			Mix( face.UVs.Length );

			foreach ( var uv in face.UVs )
			{
				Mix( uv.x );
				Mix( uv.y );
			}
		}

		return hash;
	}
}
