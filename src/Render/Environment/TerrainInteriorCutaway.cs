// Port of packages/client/src/render/environment/terrainInteriorCutaway.ts — keep in lockstep with the original.
//
// PORT NOTES
// * The uniform bank is main-thread-only renderer state — ONE bank shared by identity with every compiled terrain
//   material. It therefore stays a plain static (NOT [ThreadStatic]: a per-thread copy would silently split the bank the
//   materials are bound to). Do not touch it from worker threads.
// * three.js value types → ThreeValues.cs (`{ value: number }` → ThreeUniform<int>, `{ value: Vector4[] }` →
//   ThreeUniform<ThreeVector4[]>; three's `new Vector4()` is (0, 0, 0, 1)).
// * `bindTerrainInteriorCutawayUniforms(shader)` writes into three's `shader.uniforms` map; the engine-free port takes
//   that name → uniform map directly (IDictionary&lt;string, object&gt;), which is all the function touches.
// * STUDIO: the studio has no embedded rooms, so the bank stays empty (count 0); the room registry of the original is
//   not ported. The terrain materials still bind the bank, so their uniforms keep the original's layout.
using System.Collections.Generic;

namespace Fluitown.Render;

/// <summary>
/// The uniform bank of the dynamic dollhouse cuts for rooms whose shell is the native terrain itself (always empty in
/// the studio).
/// </summary>
public static partial class TerrainInteriorCutaway
{
    /// <summary>Simultaneously visible embedded rooms. Closed rooms consume no slot.</summary>
    public const int TERRAIN_INTERIOR_CUTAWAY_CAPACITY = 8;

    private sealed class TerrainInteriorCutawayUniformBank
    {
        public ThreeUniform<int> count;
        /// <summary>minX, minZ, maxX, maxZ in source-world pixels.</summary>
        public ThreeUniform<ThreeVector4[]> bounds;
        /// <summary>sillY, amount, viewX, viewZ.</summary>
        public ThreeUniform<ThreeVector4[]> state;
    }

    private static ThreeVector4[] newVector4Bank()
    {
        ThreeVector4[] bank = new ThreeVector4[TERRAIN_INTERIOR_CUTAWAY_CAPACITY];
        for (int i = 0; i < bank.Length; i++) bank[i] = new ThreeVector4();
        return bank;
    }

    private static readonly TerrainInteriorCutawayUniformBank uniforms = new TerrainInteriorCutawayUniformBank
    {
        count = new ThreeUniform<int>(0),
        bounds = new ThreeUniform<ThreeVector4[]>(newVector4Bank()),
        state = new ThreeUniform<ThreeVector4[]>(newVector4Bank()),
    };

    /// <summary>Bind the stable uniform objects. Their identities never change after a material has compiled.</summary>
    /// <param name="shaderUniforms">three's `shader.uniforms` (uniform name → shared uniform object).</param>
    public static void bindTerrainInteriorCutawayUniforms(IDictionary<string, object> shaderUniforms)
    {
        shaderUniforms["uTerrainInteriorCutawayCount"] = uniforms.count;
        shaderUniforms["uTerrainInteriorCutawayBounds"] = uniforms.bounds;
        shaderUniforms["uTerrainInteriorCutawayState"] = uniforms.state;
    }
}
