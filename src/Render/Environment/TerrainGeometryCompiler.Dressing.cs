// Port of packages/client/src/render/environment/terrainGeometryCompiler.ts — keep in lockstep with the original.
//
// PART C2 (TS lines 3550–6201): `buildRunDressing` … `buildUnderpassBridge` — run/theme dressing, wind blades,
// integrated floor growth, reed beds, lily colonies, wall strands, the cathedral / prismglass / abyssal / carnival /
// clockwork / noir / olympian / rainbowland dressings, procedural trees, cleft cells and underpass bridges.
// Instance fields and module-level helpers (ELEV, SURF, SHADE4, WIND4, UNIT_SHADE, UNIT_ZERO, setShade4, setWind4,
// quad, hash, terrainOrganicPointMask …) live in the other parts.
//
// PORT NOTES
// * The compiler-local `type TerrainTileset = TerrainMaterialTileset & { surfaceForTile?: unknown }` is
//   `TerrainMaterialTileset` (as in PaintedThemeVisual.tileset).
// * `list[id]` reads whose index is not provably in range go through `dressingItemAt` (JS `undefined` → null).
// * Structural typing bridges (C# typing is nominal):
//   - a `TreeVisual & { id }` handed to treeGeometry / worldDecorationGeometry becomes a `TerrainTreeLike` copy
//     (`dressingTreeLike`, field-for-field via TreeVisual.CopyTreeVisualFrom; nothing reads object identity);
//   - the tileset's `chasm` record handed to `addChasmMist` becomes a `ChasmMistPalette` (`dressingChasmMistPalette`).
// * `{ ...plan, effects: { ...plan.effects, … } }` is `TerrainRenderPlan.Clone()` plus a field-by-field copy of
//   TerrainProceduralEffects (which has no Clone()).
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainGeometryCompiler
{
    /// <summary>JS `list[index]` on an object array: `undefined` (null) outside the array.</summary>
    private static T? dressingItemAt<T>(IReadOnlyList<T> list, int index)
        where T : class =>
        (uint)index < (uint)list.Count ? list[index] : null;

    /// <summary>
    /// A `TreeVisual &amp; { id }` passed where treeGeometry expects its `TerrainTreeLike` — the structural TS pass of the
    /// same record. Every phenotype field is copied (TreeVisual.CopyTreeVisualFrom), so every read sees the same value.
    /// </summary>
    private static TerrainTreeLike dressingTreeLike(double id, TreeVisual visual)
    {
        var like = new TerrainTreeLike { id = id };
        like.CopyTreeVisualFrom(visual);
        return like;
    }

    /// <summary>`TerrainTileset['chasm'] | undefined` passed where `addChasmMist` expects `ChasmMistPalette | undefined`.</summary>
    private static ChasmMistPalette? dressingChasmMistPalette(TerrainTilesetChasm? chasm) =>
        chasm == null ? null : new ChasmMistPalette { deep = chasm.deep, mist = chasm.mist };

    internal void buildRunDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        // Fluitown comic look: everything below stands on the uneven ground (TerrainGeometryCompiler.Relief.cs).
        this.prepareGroundRelief(terrain, frame);
        // An explicit zero blade budget is the software-WebGL compact geometry contract. The walkable terrain,
        // water, cliffs and bridges above are gameplay-bearing and remain exact; everything
        // below is volumetric ecology/dressing. Omitting that lane prevents a CPU rasterizer from receiving several
        // hundred thousand decorative vertices on first entry. Normal/mobile hardware never selects this tier.
        if (this.vegetationBladeCap <= 0) return;
        TerrainRenderPlan dressingPlan = plan;
        bool rainbow = this.tileset?.decal.kind == "rainbow";
        bool reef = this.tileset?.decal.kind == "reef";
        bool carnival = this.tileset?.decal.kind == "carnival";
        bool clockwork = this.clockworkTileset;
        bool glass = this.prismglassTileset || this.tileset?.decal.kind == "glassShard";
        bool cathedral = this.cathedralTileset || this.tileset?.decal.kind == "cathedralStar";
        bool naturalGeometry =
            !rainbow &&
            !reef &&
            !carnival &&
            !clockwork &&
            !glass &&
            !cathedral &&
            !this.cityTileset &&
            !this.olympianTileset;
        // The canopy slot grows THE tree — one generator, every world. `authored` only means the world composed
        // the placement, so it skips the renderer's siting rules; it never picks which object is drawn.
        void emitCanopy(
            IReadOnlyList<TerrainTreeDressingEffect> list,
            Action<TerrainCell, TerrainTreeDressingEffect, TerrainMaterialTileset> draw)
        {
            foreach (TerrainTreeDressingEffect tree in list)
            {
                TerrainCell? cell = dressingItemAt(terrain.cells, tree.id);
                if (cell == null || !this.ownCell(cell)) continue;
                if (tree.authored == true)
                {
                    draw(
                        cell,
                        tree,
                        this.themeVisualForKey(tree.themeKey)?.tileset ??
                            this.paintedThemeVisualForCell(cell)?.tileset ??
                            this.tileset!);
                    continue;
                }
                if (
                    cell.type != TileType.Solid &&
                    !(tree.floorAnchor == true && cell.type == TileType.Floor && cell.walkable))
                    continue;
                draw(cell, tree, this.tileset!);
            }
        }

        // The White Citadel plants no groves: its authored canopy placements ARE its ink spires, and it is a
        // sealed raid rather than one of the ten Endless worlds the shared tree exists for.
        bool citadel = this.biome?.materialDialect == "aegis-citadel";
        emitCanopy(dressingPlan.effects.trees, (cell, tree, tileset) =>
        {
            if (citadel) this.addAegisInkSpire(builder, frame, cell, tree, tileset);
            else this.addProceduralTree(builder, frame, cell, tree, tileset);
        });
        foreach (TerrainWorldDecorationEffect decoration in dressingPlan.effects.worldDecorations)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, decoration.id);
            if (cell == null || !this.ownCell(cell)) continue;
            bool validAnchor =
                decoration.authored == true ||
                cell.type == TileType.Solid ||
                (cell.type == TileType.Floor && cell.walkable);
            if (!validAnchor) continue;
            WorldDecorationGeometry.addWorldDecorationGeometry(builder, new WorldDecorationGeometryOptions
            {
                tileset =
                    this.themeVisualForKey(decoration.themeKey)?.tileset ??
                    this.paintedThemeVisualForCell(cell)?.tileset ??
                    this.tileset!,
                decoration = decoration,
                felledTree = decoration.felledTree == null
                    ? null
                    : dressingTreeLike(decoration.felledTree.id, decoration.felledTree),
                // Undergrowth uses the SAME registry row as the canopy above it: one ecology per run.
                foliageProfile = TreeVisualModule.treeVisualProfileForBiome(decoration.themeKey ?? this.biome?.key),
                x = frame.originX + (frame.i0 + cell.x + decoration.ox) * frame.tileSize,
                z = frame.originY + (frame.j0 + cell.y + decoration.oy) * frame.tileSize,
                // Fluitown comic look: the prop stands on the uneven ground (TerrainGeometryCompiler.Relief.cs).
                y0 = cell.surfaceZ * ELEV + 0.08 +
                    this.groundReliefLiftAtCell(terrain, frame, cell.x + decoration.ox, cell.y + decoration.oy),
                tileSize = frame.tileSize,
            });
        }
        foreach (TerrainChasmMistEffect mist in plan.effects.chasmMist)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, mist.id);
            TerrainMaterial? material = dressingItemAt(plan.materials, mist.id);
            if (cell == null || material == null || !this.ownCell(cell) || cell.type != TileType.Chasm) continue;
            TerrainChasmGeometry.addChasmMist(
                builder,
                frame,
                cell,
                material,
                mist,
                dressingChasmMistPalette(this.chasmPaletteForCell(cell)));
        }
        if (naturalGeometry)
        {
            // Natural grass is not an effect decal. Every blade is rooted directly in the continuous habitat field
            // shared by floor pigment and route clearance, so meadows cross tile seams while real paths cut through
            // them. `grassPatches` remain thematic anchors for the bespoke world renderers below, never pale
            // polygons pasted onto ordinary ground.
            if (this.naturalGroundCover)
            {
                foreach (TerrainCell cell in terrain.cells)
                {
                    if (
                        !this.ownCell(cell) ||
                        ((cell.type != TileType.Floor || !cell.walkable) &&
                            !TerrainModel.terrainCellCarriesChasmFloor(cell)))
                        continue;
                    this.addIntegratedFloorGrowth(builder, frame, terrain, plan, cell);
                }
            }
            foreach (TerrainReedBedEffect reed in dressingPlan.effects.reedBeds)
            {
                TerrainCell? cell = dressingItemAt(terrain.cells, reed.id);
                if (cell == null || !this.ownCell(cell) || cell.type != TileType.Water) continue;
                this.addNaturalReedBed(builder, frame, cell, reed);
            }
            foreach (TerrainLilyPadEffect lily in dressingPlan.effects.lilyPads)
            {
                TerrainCell? cell = dressingItemAt(terrain.cells, lily.id);
                if (cell == null || !this.ownCell(cell) || cell.type != TileType.Water) continue;
                this.addNaturalLilyColony(builder, frame, cell, lily);
            }
            foreach (TerrainWallStrandEffect strand in dressingPlan.effects.wallStrands)
            {
                TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
                if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable)
                    continue;
                if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
                this.addNaturalWallStrand(builder, frame, cell, strand);
            }
        }
        if (clockwork) this.buildClockworkBazaarDressing(builder, frame, terrain, dressingPlan);
        if (glass) this.buildPrismglassArchiveDressing(builder, frame, terrain, dressingPlan);
        if (cathedral) this.buildCathedralDressing(builder, frame, terrain, dressingPlan);
        if (this.cityTileset) this.buildNoirSprawlDressing(builder, frame, terrain, dressingPlan);
        if (this.olympianTileset) this.buildOlympianDressing(builder, frame, terrain, dressingPlan);
        if (rainbow) this.buildRainbowlandDressing(builder, frame, terrain, dressingPlan);
        if (carnival || this.carnivalTileset)
            this.buildCarnivalDressing(builder, frame, terrain, dressingPlan);
        if (reef) this.buildAbyssalDressing(builder, frame, terrain, dressingPlan);
    }

    /// <summary>
    /// One tapered, lit blade in the shared terrain batch. Delegates to the ONE implementation in
    /// floorGrassGeometry so floor grass, reed beds and every thematic tendril bend identically.
    /// </summary>
    internal void addWindBlade(
        TileGeometryBuilder builder,
        double rootX,
        double rootZ,
        double y0,
        double height,
        double width,
        double angle,
        double leanX,
        double leanZ,
        int color,
        double wind,
        double bend = 0,
        double normalY = 0.38)
    {
        FloorGrassGeometry.addTerrainWindBlade(builder, new WindBladeParams
        {
            rootX = rootX,
            rootZ = rootZ,
            y0 = y0,
            height = height,
            width = width,
            angle = angle,
            leanX = leanX,
            leanZ = leanZ,
            color = color,
            wind = wind,
            bend = bend,
            normalY = normalY,
        });
    }

    /// <summary>
    /// The standing growth of one walkable floor cell.
    ///
    /// The PATCH is not built here and is not geometry at all: the cap publishes `turfCoverAt` per vertex
    /// and the fragment shader paints it as ground, exactly as a trail is painted. What this adds is the blades,
    /// sampled from that same cover so they can only stand inside the painted patch, and pigmented from the mat
    /// they grow out of. Wildflowers consume `plan.floor.flowers` in this same call: patch cores and isolated
    /// blooms are therefore floor composition, not decoration records placed after the bake.
    /// </summary>
    internal void addIntegratedFloorGrowth(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        bool organic = this.visualGrounding && this.terrainSurfaceProfile.organicGround;
        bool deepFloor = TerrainModel.terrainCellCarriesChasmFloor(cell);
        double? deepFloorZ = deepFloor ? TerrainModel.terrainChasmFloorZAt(terrain, cell) : null;
        TerrainMaterial grassMaterial = deepFloor
            ? TerrainChasmGeometry.resolveChasmFloorMaterial(terrain, plan, cell)
            : (dressingItemAt(plan.materials, cell.id) ?? this.materialForCell(cell, terrain));
        int grassCapColor = deepFloor
            ? grassMaterial.top
            : TerrainRenderPlanModule.terrainMaterialSurfaceTopColor(grassMaterial, cell);
        int grassCornerColorAt(int cornerX, int cornerY) =>
            deepFloor
                ? grassCapColor
                : this.compatibleCapColorAt(terrain, plan, cell, cornerX, cornerY, false, grassCapColor);
        TerrainFloorGrowthGeometry.addIntegratedFloorGrowthGeometry(builder, new IntegratedFloorGrowthOptions
        {
            frame = frame,
            terrain = terrain,
            plan = plan,
            cell = cell,
            groundDetail = this.groundDetail,
            lushPole = this.floorLushPole,
            litPole = this.tileset.terrain.floorLit,
            inkPole = this.tileset.decal.ink,
            flowerProfile = FloorFlowerGeometry.floorFlowerProfileForBiome(this.biome?.key),
            y0 = (deepFloorZ ?? cell.surfaceZ) * ELEV + 0.1,
            bladeBudget = deepFloor ? Math.min(2, this.vegetationBladeCap) : this.vegetationBladeCap,
            // `...(deepFloor ? { standingCoverScale: 0.18, flowerBudget: 0 } : {})`
            standingCoverScale = deepFloor ? 0.18 : null,
            flowerBudget = deepFloor ? 0 : null,
            cornerColorAt = grassCornerColorAt,
            groundLift = (u, v) =>
                // Fluitown comic look: grass and flowers stand on the uneven ground (TerrainGeometryCompiler.Relief.cs).
                TerrainGroundRelief.Enabled && !deepFloor
                    ? this.groundReliefLiftAtCell(terrain, frame, cell.x + u, cell.y + v)
                : organic
                    ? TerrainVisualGround.terrainOrganicHeightAt(
                          frame.originX + (frame.i0 + cell.x + u) * ts,
                          frame.originY + (frame.j0 + cell.y + v) * ts,
                          this.terrainSurfaceProfile) *
                      (deepFloor
                          ? TerrainChasmFloorGeometry.terrainChasmFloorPointMask(
                                terrain,
                                cell.x + u,
                                cell.y + v,
                                deepFloorZ)
                          : terrainOrganicPointMask(terrain, cell.x + u, cell.y + v, cell.surfaceZ))
                    : 0,
            // `...(deepFloor ? { depthTint: { color: this.tileset.decal.ink, mix: 0.84 } } : {})`
            depthTint = deepFloor
                ? new IntegratedFloorGrowthDepthTint { color = this.tileset.decal.ink, mix = 0.84 }
                : null,
        });
    }

    internal void addNaturalReedBed(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainReedBedEffect reed)
    {
        if (this.tileset == null) return;
        // Fluitown comic look: reeds and water grass grow in the vegetation layer (FluitownSmallPlants).
        if (FluitownSmallPlants.recordReedBed(builder, frame, cell, reed, this.tileset, this.floorLushPole, this.biome?.key)) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + reed.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + reed.oy) * ts;
        double y0 = (cell.waterLevel ?? cell.surfaceZ) * ELEV + 0.18;
        double count = Math.min(this.vegetationBladeCap, reed.blades);
        double clumps = Math.max(1, Math.min(3, reed.clumps));
        int mid = mix(this.tileset.decal.mid, this.tileset.terrain.floorLit, 0.2);
        int dark = mix(this.tileset.decal.ink, mid, 0.3);
        for (int i = 0; i < count; i++)
        {
            double h = hash(reed.id * 2237 + i * 53);
            double clump = i % clumps;
            double rootX = cx + (clump - (clumps - 1) * 0.5) * 4.6 + (h - 0.5) * 4.2;
            double rootZ = cz + (hash(reed.id * 2243 + i * 59) - 0.5) * 5.4;
            double height = reed.height * (0.74 + h * 0.48);
            double angle = reed.phase + i * 1.71;
            double wind = reed.sway * (2.1 + h * 1.2) * (1 - reed.stiffness * 0.58);
            this.addWindBlade(
                builder,
                rootX,
                rootZ,
                y0,
                height,
                0.34 + h * 0.28,
                angle,
                (h - 0.5) * height * 0.08,
                Math.sin(angle) * height * 0.025,
                i % 3 == 0 ? dark : mid,
                wind);
            if (h > 1 - reed.seedHeads * 0.52)
            {
                propFrustum(
                    builder,
                    rootX,
                    rootZ,
                    y0 + height * 0.82,
                    y0 + height * 1.05,
                    0.72 + h * 0.32,
                    0.48,
                    5,
                    angle,
                    mix(this.tileset.decal.accent, dark, 0.3),
                    dark,
                    0.72,
                    wind,
                    wind);
            }
        }
    }

    internal void addNaturalLilyColony(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainLilyPadEffect lily)
    {
        if (this.tileset == null) return;
        // Fluitown comic look: floating, drifting lily pads in the vegetation layer (FluitownSmallPlants).
        if (FluitownSmallPlants.recordLilyColony(builder, frame, cell, lily, this.tileset, this.floorLushPole, this.biome?.key)) return;
        double ts = frame.tileSize;
        double baseX = frame.originX + (frame.i0 + cell.x + lily.ox) * ts;
        double baseZ = frame.originY + (frame.j0 + cell.y + lily.oy) * ts;
        double y = (cell.waterLevel ?? cell.surfaceZ) * ELEV + 0.2;
        double leaves = Math.max(1, Math.min(3, lily.cluster));
        int leafBase = mix(this.tileset.decal.mid, this.tileset.decal.ink, 0.16);
        int color = mix(leafBase, this.tileset.decal.accent, lily.bloomHue * 0.12);
        for (int leaf = 0; leaf < leaves; leaf++)
        {
            double h = hash(lily.id * 2267 + leaf * 61);
            double scale = leaf == 0 ? 1 : 0.48 + h * 0.24;
            double r = lily.radius * scale;
            double a = lily.notchAngle + leaf * 2.23 + h * 0.5;
            double distance = leaf == 0 ? 0 : lily.radius * (0.48 + h * 0.28);
            double cx = baseX + Math.cos(a + 1.1) * distance;
            double cz = baseZ + Math.sin(a + 1.1) * distance * 0.72;
            const double segments = 10;
            double notch = Math.floor(((((a % PROP_TAU) + PROP_TAU) % PROP_TAU) / PROP_TAU) * segments);
            for (int i = 0; i < segments; i++)
            {
                if (i == notch) continue;
                double a0 = (i / segments) * PROP_TAU;
                double a1 = ((i + 1) / segments) * PROP_TAU;
                setWind4(0.35, 0.55, 0.55, 0.35);
                builder.addSurface(
                    new P3[]
                    {
                        new P3(cx, y, cz),
                        new P3(cx + Math.cos(a0) * r, y, cz + Math.sin(a0) * r * 0.7),
                        new P3(cx + Math.cos(a1) * r, y, cz + Math.sin(a1) * r * 0.7),
                    },
                    0,
                    1,
                    0,
                    color,
                    SURF.floor,
                    0.025,
                    UNIT_SHADE,
                    WIND4);
            }
        }
    }

    /// <param name="direction">A <see cref="TerrainEdgeDirection"/> key.</param>
    internal (double x, double z, double y) wallDressingAnchor(
        TerrainBakeFrame frame,
        TerrainCell cell,
        string direction,
        double t)
    {
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        TerrainEdge edge = cell.edges[direction]!;
        return (
            x: direction == "e" ? x0 + ts * 0.84 : direction == "w" ? x0 + ts * 0.16 : x0 + ts * t,
            z: direction == "n" ? z0 + ts * 0.16 : direction == "s" ? z0 + ts * 0.84 : z0 + ts * t,
            y: Math.max(cell.surfaceZ + 0.35, Math.max(edge.fromZ, edge.toZ) - 0.18) * ELEV);
    }

    internal void addNaturalWallStrand(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        // Fluitown comic look: ivy and roots hang on the wall face in the vegetation layer (FluitownSmallPlants).
        if (FluitownSmallPlants.recordWallStrand(builder, frame, cell, strand, this.tileset, this.floorLushPole, this.biome?.key)) return;
        (double x, double z, double y) p = this.wallDressingAnchor(frame, cell, strand.direction, strand.t);
        double topY = p.y + frame.tileSize * 0.08;
        double bottomY = Math.max(cell.surfaceZ * ELEV + 1.5, topY - strand.length * ELEV);
        bool alongX = strand.direction == "n" || strand.direction == "s";
        int color =
            strand.style == "root"
                ? mix(this.tileset.bridge.bodyShadow, this.tileset.decal.ink, 0.22)
                : mix(this.tileset.decal.mid, this.tileset.decal.ink, 0.2);
        double count = Math.min(4, strand.strands);
        for (int i = 0; i < count; i++)
        {
            double h = hash(strand.id * 2293 + i * 67);
            double offset = (i - (count - 1) * 0.5) * 2.6 + (h - 0.5) * 1.4;
            double x = p.x + (alongX ? offset : 0);
            double z = p.z + (alongX ? 0 : offset);
            double width = 0.48 + h * 0.38;
            double sx = alongX ? width : 0.28;
            double sz = alongX ? 0.28 : width;
            setShade4(0.9, 0.96, 0.72, 0.76);
            setWind4(0, 0, strand.sway * (2.2 + h), strand.sway * (2.2 + h));
            builder.addSurface(
                new P3[]
                {
                    new P3(x - sx, topY, z - sz),
                    new P3(x + sx, topY, z + sz),
                    new P3(x + sx * 0.72, bottomY, z + sz * 0.72),
                    new P3(x - sx * 0.72, bottomY, z - sz * 0.72),
                },
                alongX ? 0 : 1,
                0.08,
                alongX ? 1 : 0,
                color,
                SURF.floor,
                0.035,
                SHADE4,
                WIND4);
        }
    }

    /// <summary>
    /// The imported citadel carries hundreds of deliberate tree anchors. A single chalk-white, ink-edged
    /// cypress/obelisk keeps every authored placement while using only two low-sided prisms per anchor.
    /// </summary>
    internal void addAegisInkSpire(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainTreeDressingEffect tree,
        TerrainMaterialTileset tileset)
    {
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + tree.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + tree.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double scale = 0.72 + tree.scale * 0.16;
        double radius = ts * scale * (0.04 + tree.canopyHue * 0.008);
        double plinthHeight = ts * scale * 0.032;
        double height = ts * scale * (0.32 + tree.height * 0.08);
        double rotation = tree.phase * PROP_TAU + Math.PI / 5;
        int chalk = mix(tileset.terrain.wallLit, tileset.flood.foam, 0.12);
        int shade = mix(tileset.terrain.wallFace, tileset.terrain.wallDeep, 0.32);

        propShadow(builder, cx, cz, y0, radius * 2.2, radius, 0.09 * tree.alpha);
        propFrustum(
            builder,
            cx,
            cz,
            y0,
            y0 + plinthHeight,
            radius * 1.35,
            radius * 1.16,
            6,
            rotation,
            chalk,
            shade,
            0.72);
        propFrustum(
            builder,
            cx,
            cz,
            y0 + plinthHeight,
            y0 + height,
            radius,
            radius * 0.1,
            5,
            rotation,
            chalk,
            shade,
            0.68);
        builder.addOverlayLineFlat(
            y0 + plinthHeight + 0.05,
            cx - radius * 0.72,
            cz + radius * 0.72,
            cx + radius * 0.72,
            cz + radius * 0.72,
            0.75,
            tileset.decal.ink,
            0.24 * tree.alpha);
    }

    internal void buildCathedralDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int mosaics = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            if (mosaics >= 14) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            this.addCathedralStarMosaic(builder, frame, cell, patch);
            mosaics++;
        }
    }

    internal void addCathedralStarMosaic(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.11;
        double r = ts * (0.07 + patch.radius * 0.3);
        int gold = TerrainGeometryCompilerFields.cathedralStarfire(patch.id * 1031 + 43);
        int bone = this.tileset.terrain.wallLit;
        builder.addOverlayLineFlat(y, cx - r, cz, cx + r, cz, 0.52, gold, 0.24 + patch.alpha * 0.12);
        builder.addOverlayLineFlat(
            y + 0.01,
            cx,
            cz - r * 0.62,
            cx,
            cz + r * 0.62,
            0.46,
            bone,
            0.18 + patch.alpha * 0.1);
        builder.addOverlayLineFlat(
            y + 0.02,
            cx - r * 0.44,
            cz - r * 0.28,
            cx + r * 0.5,
            cz + r * 0.34,
            0.42,
            gold,
            0.2);
    }

    internal void buildPrismglassArchiveDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int shards = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            if (shards >= 12) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            this.addPrismglassFloorShards(builder, frame, cell, patch);
            shards++;
        }
    }

    internal void addPrismglassFloorShards(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.12;
        double count = Math.min(7, Math.max(3, Math.round(patch.blades * 0.74)));
        for (int i = 0; i < count; i++)
        {
            double h = hash(patch.id * 2099 + i * 37);
            double a = patch.phase * PROP_TAU + i * 1.43 + h * 0.6;
            double d = ts * patch.radius * (0.24 + h * 0.82);
            double px = cx + Math.cos(a) * d;
            double pz = cz + Math.sin(a) * d * 0.55;
            double len = ts * (0.045 + h * 0.04);
            double wid = len * (0.28 + h * 0.24);
            double dx = Math.cos(a) * len;
            double dz = Math.sin(a) * len * 0.5;
            double nx = -Math.sin(a) * wid;
            double nz = Math.cos(a) * wid * 0.5;
            builder.addOverlay(
                new P3[]
                {
                    new P3(px - dx - nx * 0.25, y, pz - dz - nz * 0.25),
                    new P3(px + dx, y, pz + dz),
                    new P3(px - dx * 0.1 + nx, y, pz - dz * 0.1 + nz),
                },
                i % 2 != 0 ? this.tileset.flood.glint : this.tileset.decal.mid,
                0.18 + patch.alpha * 0.16);
        }
    }

    /// <summary>
    /// Deep-sea floor dressing for the Abyssal trench: the seabed between the big coral stands is ALIVE at
    /// ankle height — coral knobs, tube-worm colonies, luminescent anemones (the run's only "light plants"),
    /// silt drifts and the timber the abyss swallowed. Anemone tendrils ride the shared GPU current; everything
    /// else is still, the way deep water is.
    /// </summary>
    internal void buildAbyssalDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int knobs = 0;
        int anemones = 0;
        int silt = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            double pick = hash(patch.id * 3407 + 11);
            if (pick < 0.4)
            {
                if (knobs >= 6) continue;
                this.addAbyssalCoralKnobs(builder, frame, cell, patch);
                knobs++;
            }
            else if (pick < 0.7)
            {
                if (anemones >= 5) continue;
                this.addAbyssalAnemoneNode(builder, frame, cell, patch);
                anemones++;
            }
            else
            {
                if (silt >= 5) continue;
                this.addAbyssalSiltMound(builder, frame, cell, patch);
                silt++;
            }
        }
        int planks = 0;
        foreach (TerrainWallStrandEffect strand in plan.effects.wallStrands)
        {
            if (planks >= 3) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
            this.addAbyssalWreckPlank(builder, frame, cell, strand);
            planks++;
        }
    }

    /// <summary>
    /// Low coral knobs: a family of rounded polyp buds hugging the silt, each tipped with a pale living mouth —
    /// the small sibling of the big reef stands, so the seabed never reads as bare rock between them.
    /// </summary>
    internal void addAbyssalCoralKnobs(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        int side = mix(this.tileset.decal.ink, this.tileset.flood.deep, 0.36);
        double count = 3 + Math.floor(hash(patch.id * 3413 + 3) * 3);
        propShadow(builder, cx, cz, y0, ts * 0.13, ts * 0.06, 0.08 * patch.alpha);
        for (int i = 0; i < count; i++)
        {
            double h = hash(patch.id * 3419 + i * 43);

            double a = patch.phase * PROP_TAU + i * 1.94 + h * 0.5;
            double d = ts * patch.radius * (0.14 + h * 0.6);
            double px = cx + Math.cos(a) * d;
            double pz = cz + Math.sin(a) * d * 0.62;
            double r = ts * (0.024 + h * 0.022);
            double knobH = ts * (0.045 + h * 0.05);
            int body = mix(this.tileset.decal.mid, this.tileset.decal.accent, 0.14 + h * 0.34);
            propFrustum(builder, px, pz, y0, y0 + knobH, r, r * 0.66, 6, a, body, side, 0.78);
            if (h > 0.36)
            {
                double mouth = r * 0.42;
                builder.addOverlay(
                    new P3[]
                    {
                        new P3(px - mouth, y0 + knobH + 0.06, pz - mouth * 0.62),
                        new P3(px + mouth, y0 + knobH + 0.06, pz - mouth * 0.62),
                        new P3(px + mouth, y0 + knobH + 0.06, pz + mouth * 0.62),
                        new P3(px - mouth, y0 + knobH + 0.06, pz + mouth * 0.62),
                    },
                    mix(this.tileset.flood.foam, this.tileset.decal.accent, 0.24),
                    0.24 + patch.alpha * 0.12);
            }
        }
    }

    /// <summary>
    /// A luminescent anemone: dark pedal disc, living tendrils swaying in the shared current (the reed/grass
    /// wind path, reused), and the bead-ring of photophore dots that makes it the trench's own lamp.
    /// </summary>
    internal void addAbyssalAnemoneNode(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double discR = ts * (0.045 + hash(patch.id * 3433 + 5) * 0.02);
        int disc = mix(this.tileset.decal.mid, this.tileset.decal.ink, 0.4);
        int glow = mix(this.tileset.decal.accent, this.tileset.flood.glint, 0.4);
        propFrustum(
            builder,
            cx,
            cz,
            y0,
            y0 + ts * 0.03,
            discR,
            discR * 0.8,
            7,
            patch.phase * PROP_TAU,
            disc,
            mix(disc, this.tileset.flood.deep, 0.4),
            0.82);
        // Always a float division below (`i / tendrils`), whatever the blade cap's C# type.
        double tendrils = Math.min(this.vegetationBladeCap, 7);
        for (int i = 0; i < tendrils; i++)
        {
            double h = hash(patch.id * 3449 + i * 37);
            double a = patch.phase * PROP_TAU + (i / tendrils) * PROP_TAU + h * 0.4;
            double rootX = cx + Math.cos(a) * discR * 0.5;
            double rootZ = cz + Math.sin(a) * discR * 0.32;
            double height = ts * (0.1 + h * 0.08) * (0.8 + patch.height * 0.4);
            this.addWindBlade(
                builder,
                rootX,
                rootZ,
                y0 + ts * 0.028,
                height,
                0.34 + h * 0.24,
                a + Math.PI * 0.5,
                Math.cos(a) * height * 0.16,
                Math.sin(a) * height * 0.1,
                mix(this.tileset.decal.accent, this.tileset.flood.foam, 0.18 + h * 0.3),
                patch.sway * (1.5 + h * 1.1));
        }
        for (int i = 0; i < 4; i++)
        {
            double a = patch.phase * PROP_TAU + i * (PROP_TAU / 4) + 0.5;
            double px = cx + Math.cos(a) * discR * 1.5;
            double pz = cz + Math.sin(a) * discR * 0.95;
            double dot = 0.7 + hash(patch.id * 3457 + i * 29) * 0.5;
            builder.addOverlay(
                new P3[]
                {
                    new P3(px - dot, y0 + 0.12, pz - dot * 0.62),
                    new P3(px + dot, y0 + 0.12, pz - dot * 0.62),
                    new P3(px + dot, y0 + 0.12, pz + dot * 0.62),
                    new P3(px - dot, y0 + 0.12, pz + dot * 0.62),
                },
                glow,
                0.4 + patch.alpha * 0.14);
        }
    }

    /// <summary>
    /// A silt mound: sediment heaped into one soft pale cone with the settling halo it rained out of — the
    /// quiet negative space between the living clusters.
    /// </summary>
    internal void addAbyssalSiltMound(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double h = hash(patch.id * 3461 + 7);
        double r = ts * (0.09 + patch.radius * 0.14 + h * 0.03);
        int top = mix(this.tileset.terrain.floorLit, this.tileset.flood.foam, 0.14);
        int side = mix(this.tileset.terrain.floorLit, this.tileset.flood.deep, 0.3);
        propFrustum(
            builder,
            cx,
            cz,
            y0,
            y0 + ts * (0.035 + h * 0.03),
            r,
            r * 0.42,
            7,
            patch.phase * PROP_TAU,
            top,
            side,
            0.86);
        for (int i = 0; i < 3; i++)
        {
            double a = patch.phase * PROP_TAU + i * 2.09 + h;
            double d = r * (1.5 + hash(patch.id * 3467 + i * 19) * 0.7);
            builder.addOverlayLineFlat(
                y0 + 0.07,
                cx + Math.cos(a) * d,
                cz + Math.sin(a) * d * 0.6,
                cx + Math.cos(a) * (d + 2.2),
                cz + Math.sin(a) * (d + 2.2) * 0.6,
                0.7,
                side,
                0.14);
        }
    }

    /// <summary>
    /// A tube-worm colony at a cliff foot: chalky mineral tubes leaning out of the rock contact, each mouth
    /// ringed pale with a feather-plume dot — the wall's own plumbing, grown not built.
    /// </summary>
    /// <summary>
    /// A swallowed wreck plank leaning against the rock: bridge timber (the world's one wood truth) keeled
    /// over into the silt, its grain still readable under an ink edge — proof something sank here.
    /// </summary>
    internal void addAbyssalWreckPlank(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        bool alongX = strand.direction == "n" || strand.direction == "s";
        double wallX =
            strand.direction == "e"
                ? x0 + ts * 0.86
                : strand.direction == "w"
                    ? x0 + ts * 0.14
                    : x0 + ts * strand.t;
        double wallZ =
            strand.direction == "n"
                ? z0 + ts * 0.14
                : strand.direction == "s"
                    ? z0 + ts * 0.86
                    : z0 + ts * strand.t;
        int timber = mix(this.tileset.bridge.body, this.tileset.flood.deep, 0.3);
        int timberDark = mix(this.tileset.bridge.bodyShadow, this.tileset.decal.ink, 0.34);
        int count = 1 + (hash(strand.id * 3511 + 5) > 0.5 ? 1 : 0);
        for (int i = 0; i < count; i++)
        {
            double h = hash(strand.id * 3517 + i * 47);
            double off = (i - (count - 1) * 0.5) * ts * 0.14 + (h - 0.5) * 3;
            double len = ts * (0.2 + strand.length * 0.06 + h * 0.06);
            double wid = ts * (0.035 + h * 0.015);
            double highY = y0 + ts * (0.1 + h * 0.07);
            // High end rests on the rock contact, low end sinks into the silt — a genuinely slanted lit slab.
            double hx = wallX + (alongX ? off : 0);
            double hz = wallZ + (alongX ? 0 : off);
            double lx = hx + (alongX ? len * 0.3 : strand.direction == "e" ? -len : len);
            double lz = hz + (alongX ? (strand.direction == "n" ? len : -len) : len * 0.3);
            double px = alongX ? wid : wid * 0.4;
            double pz = alongX ? wid * 0.4 : wid;
            setShade4(1, 1, 0.8, 0.8);
            builder.addSurface(
                new P3[]
                {
                    new P3(hx - px, highY, hz - pz),
                    new P3(hx + px, highY, hz + pz),
                    new P3(lx + px, y0 + 0.6, lz + pz),
                    new P3(lx - px, y0 + 0.6, lz - pz),
                },
                (hx - lx) * 0.2,
                1,
                (hz - lz) * 0.2 + 0.35,
                timber,
                SURF.bridge,

                0.07,
                SHADE4);
            builder.addOverlayLineFlat(y0 + 0.66, lx - px, lz, lx + px, lz, 0.7, timberDark, 0.3);
        }
    }

    /// <summary>
    /// Fairground floor dressing for the Sugarstorm Carnival: the midway BETWEEN the tents — pennant poles,
    /// the pegs and ropes that hold the canvas down, yesterday's confetti and the syrup nobody mopped. All
    /// baked-still: the carnival's motion belongs to its lights and actors, not the litter.
    /// </summary>
    internal void buildCarnivalDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int stakes = 0;
        foreach (TerrainWallStrandEffect strand in plan.effects.wallStrands)
        {
            if (stakes >= 6) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
            this.addCarnivalTentStake(builder, frame, cell, strand);
            stakes++;
        }
        int confetti = 0;
        int syrup = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (hash(patch.id * 3527 + 9) < 0.58)
            {
                if (confetti >= 9) continue;
                this.addCarnivalConfettiDrift(builder, frame, cell, patch);
                confetti++;
            }
            else
            {
                if (syrup >= 6) continue;
                this.addCarnivalSyrupPuddle(builder, frame, cell, patch);
                syrup++;
            }
        }
    }

    /// <summary>
    /// A candy-striped pennant pole: two-tone shaft, sugar-ball finial and a rope of three flags staked out
    /// into the lane — the vertical punctuation the tent line needs between its big tops.
    /// </summary>
    /// <summary>
    /// A striped tent stake with its guy-rope still tensioned into the rock face — the working hardware that
    /// makes the canvas skyline believable at ground level.
    /// </summary>
    internal void addCarnivalTentStake(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double wallX =
            strand.direction == "e"
                ? x0 + ts * 0.86
                : strand.direction == "w"
                    ? x0 + ts * 0.14
                    : x0 + ts * strand.t;
        double wallZ =
            strand.direction == "n"
                ? z0 + ts * 0.14
                : strand.direction == "s"
                    ? z0 + ts * 0.86
                    : z0 + ts * strand.t;
        int ax = strand.direction == "e" ? -1 : strand.direction == "w" ? 1 : 0;
        int az = strand.direction == "n" ? 1 : strand.direction == "s" ? -1 : 0;
        IReadOnlyList<int> colors = TerrainGeometryCompilerTheme.TERRAIN_GEOMETRY_CARNIVAL_COLORS;
        int cream = this.tileset.flood.foam;
        int side = mix(this.tileset.bridge.bodyShadow, this.tileset.decal.ink, 0.3);
        double count = Math.min(3, Math.max(1, strand.strands - 1));
        for (int i = 0; i < count; i++)
        {
            double h = hash(strand.id * 3541 + i * 53);
            double along = (i - (count - 1) * 0.5) * ts * 0.16 + (h - 0.5) * 3;
            double @out = ts * (0.16 + h * 0.1);
            double px = wallX + ax * @out + (az != 0 ? along : 0);
            double pz = wallZ + az * @out + (ax != 0 ? along : 0);
            double stakeH = ts * (0.06 + h * 0.03);
            double r = ts * 0.016;
            int stripe = colors[(int)(Math.floor(h * colors.Count) % colors.Count)];
            propFrustum(
                builder,
                px,
                pz,
                y0,
                y0 + stakeH * 0.55,
                r * 1.1,
                r,
                5,
                h * PROP_TAU,
                stripe,
                side,
                0.8);
            propFrustum(
                builder,
                px,
                pz,
                y0 + stakeH * 0.55,
                y0 + stakeH,
                r,
                r * 0.55,
                5,
                h * PROP_TAU,
                cream,
                side,
                0.84);
            // Guy-rope: taut from the stake head back up into the canvas anchorage on the face.
            double ropeTopY = y0 + ts * (0.2 + strand.length * 0.08);
            builder.addOverlay(
                quad(
                    px,
                    y0 + stakeH,
                    pz,
                    px + 0.45,
                    y0 + stakeH,
                    pz,
                    wallX + 0.45,
                    ropeTopY,
                    wallZ,
                    wallX,
                    ropeTopY,
                    wallZ),
                mix(cream, side, 0.35),
                0.44);
        }
    }

    /// <summary>
    /// A confetti drift: a pale sugar swash holding a scatter of bright flecks, packed the way wind piles
    /// litter — dense heart, ragged tail. Pure baked ground speckle.
    /// </summary>
    internal void addCarnivalConfettiDrift(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.11;
        IReadOnlyList<int> colors = TerrainGeometryCompilerTheme.TERRAIN_GEOMETRY_CARNIVAL_COLORS;
        double drift = patch.phase * PROP_TAU;
        double r = ts * patch.radius * 0.8;
        builder.addOverlay(
            new P3[]
            {
                new P3(cx - r, y, cz),
                new P3(cx - r * 0.2, y, cz - r * 0.5),
                new P3(cx + r * 1.1, y, cz - r * 0.16),
                new P3(cx + r * 0.7, y, cz + r * 0.42),
                new P3(cx - r * 0.4, y, cz + r * 0.5),
            },
            mix(this.tileset.flood.foam, this.tileset.terrain.floorLit, 0.4),
            0.1 + patch.alpha * 0.08);
        double count = Math.min(12, Math.max(7, Math.round(patch.blades)));
        for (int i = 0; i < count; i++)
        {
            double h = hash(patch.id * 3547 + i * 23);
            double a = drift + i * 2.399;
            double d = r * (0.1 + h * h * 1.05); // quadratic falloff: the heart stays dense, the tail thins
            double px = cx + Math.cos(a) * d + Math.cos(drift) * r * 0.18;
            double pz = cz + Math.sin(a) * d * 0.56 + Math.sin(drift) * r * 0.1;
            double s = 0.8 + h * 0.9;
            double ca = Math.cos(a + h * 3);
            double sa = Math.sin(a + h * 3) * 0.58;
            builder.addOverlay(
                new P3[]
                {
                    new P3(px - ca * s, y + 0.02, pz - sa * s),
                    new P3(px - sa * s * 0.6, y + 0.02, pz + ca * s * 0.6),
                    new P3(px + ca * s, y + 0.02, pz + sa * s),
                    new P3(px + sa * s * 0.6, y + 0.02, pz - ca * s * 0.6),
                },
                colors[(int)(Math.floor(h * 977) % colors.Count)],
                0.3 + patch.alpha * 0.2);
        }
    }

    /// <summary>
    /// A syrup spill: one glossy candy-dark pour with a bright sugar glint and its own runaway drips — the
    /// carnival's stickiness made literal, in three flat layers.
    /// </summary>
    internal void addCarnivalSyrupPuddle(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.11;
        double r = ts * (0.06 + patch.radius * 0.2);
        int syrup = mix(this.tileset.decal.accent, this.tileset.decal.ink, 0.34);
        var pts = new List<P3>();
        for (int i = 0; i < 7; i++)
        {
            double a = patch.phase * PROP_TAU + (i / 7.0) * PROP_TAU;
            double rr = r * (0.66 + hash(patch.id * 3557 + i * 17) * 0.55);
            pts.push(new P3(cx + Math.cos(a) * rr, y, cz + Math.sin(a) * rr * 0.6));
        }
        builder.addOverlay(pts, syrup, 0.3 + patch.alpha * 0.14);
        builder.addOverlayLineFlat(
            y + 0.02,
            cx - r * 0.42,
            cz - r * 0.1,
            cx + r * 0.3,
            cz - r * 0.22,
            0.8,
            this.tileset.flood.foam,
            0.3);
        for (int i = 0; i < 2; i++)
        {
            double h = hash(patch.id * 3559 + i * 41);
            double a = patch.phase * PROP_TAU + 1.2 + i * 2.6 + h;
            double d = r * (1.2 + h * 0.6);
            double px = cx + Math.cos(a) * d;
            double pz = cz + Math.sin(a) * d * 0.6;
            double dot = 0.8 + h * 0.7;
            builder.addOverlay(
                new P3[]
                {
                    new P3(px - dot, y, pz - dot * 0.6),
                    new P3(px + dot, y, pz - dot * 0.6),
                    new P3(px + dot, y, pz + dot * 0.6),
                    new P3(px - dot, y, pz + dot * 0.6),
                },
                syrup,
                0.26);
        }
    }

    internal void buildClockworkBazaarDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int awnings = 0;
        foreach (TerrainWallStrandEffect strand in plan.effects.wallStrands)
        {
            if (awnings >= 7) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
            this.addClockworkAwning(builder, frame, cell, strand);
            awnings++;
        }

        int scraps = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            if (scraps >= 12) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            this.addClockworkFloorScrap(builder, frame, cell, patch);
            scraps++;
        }
    }

    internal void addClockworkAwning(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double wallX =
            strand.direction == "e"
                ? x0 + ts * 0.84
                : strand.direction == "w"
                    ? x0 + ts * 0.16
                    : x0 + ts * strand.t;
        double wallZ =
            strand.direction == "n"
                ? z0 + ts * 0.16
                : strand.direction == "s"
                    ? z0 + ts * 0.84
                    : z0 + ts * strand.t;
        bool alongX = strand.direction == "n" || strand.direction == "s";
        int brass = this.tileset.decal.mid;
        int teal = this.tileset.decal.accent;
        int shade = mix(this.tileset.decal.ink, this.tileset.terrain.wallDeep, 0.2);
        double width = ts * (0.28 + strand.length * 0.24);
        double depth = ts * 0.09;
        double y1 = y0 + ts * (0.28 + strand.length * 0.08);
        double y2 = y1 + ts * 0.055;

        if (alongX)
        {
            propBox(
                builder,
                wallX - width,
                wallX + width,
                wallZ - depth,
                wallZ + depth,
                y1,
                y2,
                teal,
                shade,
                0.76);
            builder.addOverlayLineFlat(
                y2 + 0.04,
                wallX - width * 0.82,
                wallZ,
                wallX + width * 0.82,
                wallZ,
                0.9,
                brass,
                0.38);
            for (int i = 0; i < 3; i++)
            {
                double px = wallX - width * 0.55 + i * width * 0.55;
                propBox(
                    builder,
                    px - 0.9,
                    px + 0.9,
                    wallZ - depth * 0.5,
                    wallZ + depth * 0.5,
                    y0,
                    y1,
                    brass,
                    shade,
                    0.74);
            }
        }
        else
        {
            propBox(
                builder,
                wallX - depth,
                wallX + depth,
                wallZ - width,
                wallZ + width,
                y1,
                y2,
                teal,
                shade,
                0.76);
            builder.addOverlayLineFlat(
                y2 + 0.04,
                wallX,
                wallZ - width * 0.82,
                wallX,
                wallZ + width * 0.82,
                0.9,
                brass,
                0.38);
            for (int i = 0; i < 3; i++)
            {
                double pz = wallZ - width * 0.55 + i * width * 0.55;
                propBox(
                    builder,
                    wallX - depth * 0.5,
                    wallX + depth * 0.5,
                    pz - 0.9,
                    pz + 0.9,
                    y0,
                    y1,
                    brass,
                    shade,
                    0.74);
            }
        }
    }

    internal void addClockworkFloorScrap(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.11;
        double seed = patch.id * 2011 + Math.round(patch.phase * 1000);
        double r = ts * patch.radius * (0.56 + hash(seed + 3) * 0.36);
        int brass = mix(this.tileset.decal.mid, this.tileset.terrain.floorLit, 0.14);
        int ink = this.tileset.terrain.wallLine;

        builder.addOverlay(
            propRingPts(cx, cz, y, r * 0.3, 4, patch.phase * PROP_TAU + Math.PI * 0.25),
            brass,
            0.16 + patch.alpha * 0.1);
        for (int i = 0; i < 6; i++)
        {
            double a = patch.phase * PROP_TAU + i * (PROP_TAU / 6);
            builder.addOverlayLineFlat(
                y + 0.02,
                cx + Math.cos(a) * r * 0.24,
                cz + Math.sin(a) * r * 0.16,
                cx + Math.cos(a) * r,
                cz + Math.sin(a) * r * 0.62,
                0.44,
                i % 2 != 0 ? ink : this.tileset.decal.accent,
                0.16 + patch.alpha * 0.08);
        }
    }

    /// <summary>
    /// Wet-street furniture for the Noir Sprawl: the city's GROUND storey — standing ad signs, breathing steam
    /// gullies, service cabling slumped against the rock bases and rain puddles holding the neon. Deliberately zero
    /// vegetation: this district's life is electric, not botanical.
    /// </summary>
    internal void buildNoirSprawlDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int cables = 0;
        foreach (TerrainWallStrandEffect strand in plan.effects.wallStrands)
        {
            if (cables >= 6) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
            this.addNoirCableBundle(builder, frame, cell, strand);
            cables++;
        }
        int gullies = 0;
        int puddles = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (hash(patch.id * 3571 + 7) < 0.42)
            {
                if (gullies >= 6) continue;
                this.addNoirSteamGully(builder, frame, cell, patch);
                gullies++;
            }
            else
            {
                if (puddles >= 9) continue;
                this.addNoirPuddleGlint(builder, frame, cell, patch);
                puddles++;
            }
        }
    }

    /// <summary>
    /// A steam gully: dark slotted service plate flush with the asphalt, one held plume of vapour standing off
    /// it — the street's underworld leaking through.
    /// </summary>
    internal void addNoirSteamGully(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double r = ts * (0.085 + hash(patch.id * 3581 + 3) * 0.04);
        int plate = mix(this.tileset.terrain.wallDeep, this.tileset.decal.ink, 0.5);
        int rim = mix(this.tileset.terrain.wallFace, this.tileset.decal.ink, 0.16);
        propFrustum(
            builder,
            cx,
            cz,
            y0,
            y0 + 1.1,
            r * 1.04,
            r,
            8,
            patch.phase * PROP_TAU,
            plate,
            mix(plate, this.tileset.decal.ink, 0.4),
            0.9);
        outlineCap(builder, cx, cz, y0 + 1.16, r, 8, patch.phase * PROP_TAU, rim, 0.9, 0.3);
        for (int i = 0; i < 3; i++)
        {
            double off = (i - 1) * r * 0.42;
            builder.addOverlayLineFlat(
                y0 + 1.2,
                cx - r * 0.52,
                cz + off,
                cx + r * 0.52,
                cz + off,
                0.8,
                this.tileset.decal.ink,
                0.34);
        }
        int steam = mix(this.tileset.flood.foam, this.tileset.terrain.wallLit, 0.4);
        double sh = y0 + ts * (0.18 + hash(patch.id * 3583 + 9) * 0.12);
        builder.addOverlay(
            new P3[]
            {
                new P3(cx - r * 0.5, y0 + 1.2, cz),
                new P3(cx + r * 0.44, y0 + 1.2, cz),
                new P3(cx + r * 0.72, sh, cz - r * 0.2),
                new P3(cx - r * 0.8, sh, cz - r * 0.1),
            },
            steam,
            0.1);
        builder.addOverlay(
            new P3[]
            {
                new P3(cx - r * 0.3, y0 + 1.2, cz + 0.3),
                new P3(cx + r * 0.26, y0 + 1.2, cz + 0.3),
                new P3(cx + r * 0.5, sh + r * 0.5, cz + 0.1),
                new P3(cx - r * 0.6, sh + r * 0.5, cz + 0.2),
            },
            steam,
            0.07);
    }

    /// <summary>
    /// Service cabling slumped along a block base: sagging trunk lines, a junction box with one live status
    /// tell, and a neon feed threading off to the nearest sign — infrastructure as set dressing.
    /// </summary>
    internal void addNoirCableBundle(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double y = cell.surfaceZ * ELEV + 0.1;
        bool alongX = strand.direction == "n" || strand.direction == "s";
        double wallX =
            strand.direction == "e"
                ? x0 + ts * 0.9
                : strand.direction == "w"
                    ? x0 + ts * 0.1
                    : x0 + ts * strand.t;
        double wallZ =
            strand.direction == "n"
                ? z0 + ts * 0.1
                : strand.direction == "s"
                    ? z0 + ts * 0.9
                    : z0 + ts * strand.t;
        int outX = strand.direction == "e" ? -1 : strand.direction == "w" ? 1 : 0;
        int outZ = strand.direction == "n" ? 1 : strand.direction == "s" ? -1 : 0;
        int ink = mix(this.tileset.decal.ink, this.tileset.terrain.wallDeep, 0.3);
        double len = ts * (0.28 + strand.length * 0.18);
        double count = Math.min(3, Math.max(2, strand.strands - 1));
        for (int i = 0; i < count; i++)
        {
            double h = hash(strand.id * 3593 + i * 41);
            double off = 1.1 + i * 1.2 + h * 0.8;
            double bx = wallX + outX * off;
            double bz = wallZ + outZ * off;
            double sag = outX != 0 ? outX * (1.3 + h) : outZ * (1.3 + h);
            double mx = bx + (alongX ? (h - 0.5) * 3 : sag);
            double mz = bz + (alongX ? sag : (h - 0.5) * 3);
            double a0x = alongX ? bx - len : bx;
            double a0z = alongX ? bz : bz - len;
            double a1x = alongX ? bx + len : bx;
            double a1z = alongX ? bz : bz + len;
            builder.addOverlayLineFlat(y, a0x, a0z, mx, mz, 0.9, ink, 0.38);
            builder.addOverlayLineFlat(y, mx, mz, a1x, a1z, 0.9, ink, 0.38);
        }
        int neon = TerrainGeometryCompilerFields.cityNeonColor(strand.id * 3607 + 17);
        builder.addOverlayLineFlat(
            y + 0.02,
            alongX ? wallX - len * 0.8 : wallX + outX * 0.7,
            alongX ? wallZ + outZ * 0.7 : wallZ - len * 0.8,
            alongX ? wallX + len * 0.8 : wallX + outX * 0.7,
            alongX ? wallZ + outZ * 0.7 : wallZ + len * 0.8,
            0.5,
            neon,
            0.24);
        // Junction box: the bundle's one solid anchor, wearing a live status pane.
        double jh = hash(strand.id * 3613 + 5);
        double jx = wallX + outX * 2 + (alongX ? (jh - 0.5) * len : 0);
        double jz = wallZ + outZ * 2 + (alongX ? 0 : (jh - 0.5) * len);
        double jr = ts * 0.035;
        propBox(
            builder,
            jx - jr,
            jx + jr,
            jz - jr * 0.7,
            jz + jr * 0.7,
            y - 0.02,
            y + ts * 0.075,
            mix(ink, this.tileset.terrain.wallFace, 0.3),
            ink,
            0.85);
        // `southPanel` is the import alias of worldPropPrimitives' `propSouthPanel`.
        propSouthPanel(
            builder,
            jx - jr * 0.4,
            jx + jr * 0.4,
            y + ts * 0.03,
            y + ts * 0.055,
            jz + jr * 0.74,
            neon,
            0.6);
    }

    /// <summary>
    /// A rain puddle holding the district's light: a dark mirror blob, one neon reflection stripe, one pale
    /// sky glint — the wet-asphalt read in three flat layers.
    /// </summary>
    internal void addNoirPuddleGlint(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,

        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.11;
        double r = ts * (0.07 + patch.radius * 0.24);
        int mirror = mix(this.tileset.flood.deep, this.tileset.decal.ink, 0.35);
        int neon = TerrainGeometryCompilerFields.cityNeonColor(patch.id * 3617 + 19);
        var pts = new List<P3>();
        for (int i = 0; i < 6; i++)
        {
            double a = patch.phase * PROP_TAU + (i / 6.0) * PROP_TAU;
            double rr = r * (0.68 + hash(patch.id * 3623 + i * 17) * 0.5);
            pts.push(new P3(cx + Math.cos(a) * rr, y, cz + Math.sin(a) * rr * 0.58));
        }
        builder.addOverlay(pts, mirror, 0.28 + patch.alpha * 0.12);
        builder.addOverlayLineFlat(
            y + 0.02,
            cx - r * 0.1,
            cz + r * 0.26,
            cx + r * 0.16,
            cz - r * 0.34,
            1.1,
            neon,
            0.24);
        builder.addOverlayLineFlat(
            y + 0.03,
            cx - r * 0.44,
            cz - r * 0.06,
            cx + r * 0.1,
            cz - r * 0.16,
            0.7,
            mix(this.tileset.flood.foam, this.tileset.terrain.wallLit, 0.3),
            0.2);
    }

    /// <summary>
    /// Ground dressing for the Olympian sky borough: a marble ruinscape of balustrade survivors, fallen column
    /// drums, votive plinths and living laurel tubs (the borough's one permitted green, breathing in the shared GPU
    /// wind). Every piece is marble + one gold accent.
    /// </summary>
    internal void buildOlympianDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int rails = 0;
        foreach (TerrainWallStrandEffect strand in plan.effects.wallStrands)
        {
            if (rails >= 3) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
            this.addOlympianBalustradeRun(builder, frame, cell, strand);
            rails++;
        }
        int drums = 0;
        int plinths = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (hash(patch.id * 3631 + 5) < 0.52)
            {
                if (drums >= 5) continue;
                this.addOlympianColumnDrum(builder, frame, cell, patch);
                drums++;
            }
            else
            {
                if (plinths >= 4) continue;
                this.addOlympianVotivePlinth(builder, frame, cell, patch);
                plinths++;
            }
        }
    }

    /// <summary>
    /// A surviving stretch of marble balustrade at a terrace foot: rail, turned balusters, and the one gap
    /// where time won — the borough's parapet grammar carried down to ground scale.
    /// </summary>
    internal void addOlympianBalustradeRun(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        bool alongX = strand.direction == "n" || strand.direction == "s";
        double cx =
            strand.direction == "e"
                ? x0 + ts * 0.82
                : strand.direction == "w"
                    ? x0 + ts * 0.18
                    : x0 + ts * strand.t;
        double cz =
            strand.direction == "n"
                ? z0 + ts * 0.18
                : strand.direction == "s"
                    ? z0 + ts * 0.82
                    : z0 + ts * strand.t;
        int marbleTop = mix(this.tileset.terrain.wallLit, this.tileset.terrain.floorLit, 0.28);
        int marbleSide = mix(this.tileset.terrain.wallFace, this.tileset.terrain.wallDeep, 0.18);
        int marbleInk = mix(
            this.tileset.terrain.wallLine,
            this.biome?.materialDialect == "aegis-citadel" ? this.tileset.decal.ink : 0x735d2d,
            0.22);
        int gold = this.olympianMetal(strand.id * 3643 + 7);
        double half = ts * (0.2 + strand.length * 0.09);
        double depth = ts * 0.032;
        double railH = ts * 0.028;
        double balH = ts * 0.1;
        double x0r = cx - (alongX ? half : depth);
        double x1r = cx + (alongX ? half : depth);
        double z0r = cz - (alongX ? depth : half);
        double z1r = cz + (alongX ? depth : half);
        propShadow(builder, cx, cz, y0, half * 0.9, ts * 0.06, 0.1);
        propBox(builder, x0r, x1r, z0r, z1r, y0, y0 + railH, marbleTop, marbleSide, 0.86);
        const int count = 4;
        double gap = Math.floor(hash(strand.id * 3659 + 11) * count);
        for (int i = 0; i < count; i++)
        {
            if (i == gap) continue; // the missing baluster — a ruin, not a catalogue piece
            double t = (i + 0.5) / count - 0.5;
            double px = cx + (alongX ? t * half * 2 : 0);
            double pz = cz + (alongX ? 0 : t * half * 2);
            propFrustum(
                builder,
                px,
                pz,
                y0 + railH,
                y0 + railH + balH,
                ts * 0.02,
                ts * 0.015,
                6,
                strand.phase + i * 0.4,
                marbleTop,
                marbleSide,
                0.78);
        }
        propBox(
            builder,
            x0r,
            x1r,

            z0r,
            z1r,
            y0 + railH + balH,
            y0 + railH + balH + railH,
            marbleTop,
            marbleSide,
            0.8);
        outlineBoxCap(builder, x0r, x1r, z0r, z1r, y0 + railH * 2 + balH + 0.05, marbleInk, 1.0, 0.24);
        builder.addOverlayLineFlat(
            y0 + railH * 2 + balH + 0.08,
            alongX ? cx - half * 0.9 : cx,
            alongX ? cz : cz - half * 0.9,
            alongX ? cx + half * 0.9 : cx,
            alongX ? cz : cz + half * 0.9,
            0.7,
            gold,
            0.3);
    }

    /// <summary>
    /// Fallen column drums: one or two marble cylinders resting where the shaft came down, cap edges still
    /// drawn in ink, one fillet of gold leaf clinging on.
    /// </summary>
    internal void addOlympianColumnDrum(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        int marbleTop = mix(this.tileset.terrain.wallLit, this.tileset.terrain.floorLit, 0.28);
        int marbleSide = mix(this.tileset.terrain.wallFace, this.tileset.terrain.wallDeep, 0.18);
        int marbleInk = mix(
            this.tileset.terrain.wallLine,
            this.biome?.materialDialect == "aegis-citadel" ? this.tileset.decal.ink : 0x735d2d,
            0.22);
        double h0 = hash(patch.id * 3671 + 3);
        int count = 1 + (h0 > 0.45 ? 1 : 0);
        for (int i = 0; i < count; i++)
        {
            double h = hash(patch.id * 3673 + i * 37);
            double a = patch.phase * PROP_TAU + i * 2.4;
            double d = i == 0 ? 0 : ts * (0.12 + h * 0.08);
            double px = cx + Math.cos(a) * d;
            double pz = cz + Math.sin(a) * d * 0.64;
            double r = ts * (0.055 + h * 0.025) * (i == 0 ? 1 : 0.78);
            double drumH = ts * (0.055 + h * 0.035);
            propShadow(builder, px, pz, y0, r * 1.5, r * 0.8, 0.09 * patch.alpha);
            propFrustum(
                builder,
                px,
                pz,
                y0,
                y0 + drumH,
                r,
                r * 0.96,
                8,
                a + h,
                marbleTop,
                marbleSide,
                0.8);
            outlineCap(builder, px, pz, y0 + drumH + 0.05, r * 0.96, 8, a + h, marbleInk, 1.0, 0.24);
            // The flute lines the shaft carried, read on the fallen cap as parallel chords.
            for (int f = -1; f <= 1; f++)
            {
                builder.addOverlayLineFlat(
                    y0 + drumH + 0.07,
                    px - r * 0.6,
                    pz + f * r * 0.34,
                    px + r * 0.6,
                    pz + f * r * 0.34,
                    0.55,
                    marbleSide,
                    0.18);
            }
            if (i == 0 && h0 > 0.3)
            {
                outlineCap(
                    builder,
                    px,
                    pz,
                    y0 + drumH + 0.06,
                    r * 0.62,
                    8,
                    patch.phase * PROP_TAU,
                    this.olympianMetal(patch.id * 3677 + 13),
                    0.7,
                    0.26);
            }
        }
    }

    /// <summary>
    /// A votive laurel tub against the face: marble basin, gold lip, and live laurel blades breathing in the
    /// same wind field as every meadow below the borough.
    /// </summary>
    /// <summary>
    /// A votive plinth: two stacked marble blocks under a gold-edged cap with its offering bowl — the small
    /// altars strewn across the borough's open floors.
    /// </summary>
    internal void addOlympianVotivePlinth(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        int marbleTop = mix(this.tileset.terrain.wallLit, this.tileset.terrain.floorLit, 0.28);
        int marbleSide = mix(this.tileset.terrain.wallFace, this.tileset.terrain.wallDeep, 0.18);
        int gold = this.olympianMetal(patch.id * 3701 + 11);
        double h = hash(patch.id * 3709 + 3);
        double @base = ts * (0.075 + h * 0.02);
        double baseH = ts * 0.05;
        double shaftH = ts * (0.09 + h * 0.05);
        propShadow(builder, cx, cz, y0, @base * 1.4, @base * 0.8, 0.1 * patch.alpha);
        propBox(
            builder,
            cx - @base,
            cx + @base,
            cz - @base * 0.74,
            cz + @base * 0.74,
            y0,
            y0 + baseH,
            marbleTop,
            marbleSide,
            0.84);
        propBox(
            builder,
            cx - @base * 0.66,
            cx + @base * 0.66,
            cz - @base * 0.5,
            cz + @base * 0.5,
            y0 + baseH,
            y0 + baseH + shaftH,
            marbleTop,
            marbleSide,
            0.78);
        propBox(
            builder,
            cx - @base * 0.82,
            cx + @base * 0.82,
            cz - @base * 0.6,
            cz + @base * 0.6,
            y0 + baseH + shaftH,
            y0 + baseH + shaftH + ts * 0.028,
            mix(marbleTop, gold, 0.12),
            marbleSide,
            0.82);
        outlineBoxCap(
            builder,
            cx - @base * 0.82,
            cx + @base * 0.82,
            cz - @base * 0.6,
            cz + @base * 0.6,
            y0 + baseH + shaftH + ts * 0.028 + 0.05,
            gold,
            0.9,
            0.34);
        propFrustum(
            builder,
            cx,
            cz,
            y0 + baseH + shaftH + ts * 0.028,
            y0 + baseH + shaftH + ts * 0.055,
            @base * 0.3,
            @base * 0.38,
            6,
            patch.phase * PROP_TAU,
            gold,
            mix(gold, marbleSide, 0.5),
            0.86);
    }

    /// <summary>
    /// Candy-meadow dressing for Rainbowland: the ground between the ribbon totems is CONFECTIONERY — candy
    /// pebbles, lollipop sprouts, dropped prism shards and the icing that beads along every cliff foot. Sugar
    /// never moves; the totems' wind stays theirs.
    /// </summary>
    internal void buildRainbowlandDressing(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        int arcs = 0;
        foreach (TerrainWallStrandEffect strand in plan.effects.wallStrands)
        {
            if (arcs >= 5) break;
            TerrainCell? cell = dressingItemAt(terrain.cells, strand.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (cell.edges[strand.direction]!.contactType != TileType.Solid) continue;
            this.addRainbowFrostingArc(builder, frame, cell, strand);
            arcs++;
        }
        int pebbles = 0;
        int shards = 0;
        foreach (TerrainGrassPatchEffect patch in plan.effects.grassPatches)
        {
            TerrainCell? cell = dressingItemAt(terrain.cells, patch.id);
            if (cell == null || !this.ownCell(cell) || cell.type != TileType.Floor || !cell.walkable) continue;
            if (hash(patch.id * 3719 + 5) < 0.55)
            {
                if (pebbles >= 9) continue;
                this.addRainbowCandyPebbles(builder, frame, cell, patch);
                pebbles++;
            }
            else
            {
                if (shards >= 8) continue;
                this.addRainbowShardScatter(builder, frame, cell, patch);
                shards++;
            }
        }
    }

    /// <summary>
    /// A candy pebble family: glossy drop-shaped nubs, each its own prism hue with one sugar highlight — the
    /// gravel of a world paved in sweets.
    /// </summary>
    internal void addRainbowCandyPebbles(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        double count = 4 + Math.floor(hash(patch.id * 3727 + 3) * 3);
        propShadow(builder, cx, cz, y0, ts * 0.12, ts * 0.055, 0.07 * patch.alpha);
        IReadOnlyList<int> prism = TerrainGeometryCompilerTheme.TERRAIN_GEOMETRY_RAINBOW_COLORS;
        for (int i = 0; i < count; i++)
        {
            double h = hash(patch.id * 3733 + i * 43);
            double a = patch.phase * PROP_TAU + i * 2.399 + h * 0.4;
            double d = ts * patch.radius * (0.12 + h * 0.58);
            double px = cx + Math.cos(a) * d;
            double pz = cz + Math.sin(a) * d * 0.6;
            double r = ts * (0.02 + h * 0.018);
            int color = prism[(int)(Math.floor(h * 991) % prism.Count)];
            propFrustum(
                builder,
                px,
                pz,
                y0,
                y0 + r * 1.7,
                r,
                r * 0.55,
                6,
                a,
                mix(color, this.tileset.terrain.wallLit, 0.1),
                mix(color, this.tileset.terrain.wallDeep, 0.42),
                0.82);
            if (h > 0.4)
            {
                builder.addOverlayLineFlat(
                    y0 + r * 1.7 + 0.05,
                    px - r * 0.5,
                    pz + r * 0.14,
                    px + r * 0.4,
                    pz - r * 0.12,
                    0.5,
                    this.tileset.flood.foam,
                    0.34);
            }
        }
    }

    /// <summary>
    /// A lollipop sprout: pale sugar stem and a fat candy disc head wearing its white spiral — planted where
    /// other worlds would keep a torch.
    /// </summary>
    /// <summary>
    /// Dropped prism shards: hard glass triangles fanned across the sugar floor, each throwing one thin
    /// spectral glint — the totems shed, the ground keeps the light.
    /// </summary>
    internal void addRainbowShardScatter(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainGrassPatchEffect patch)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + patch.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + patch.oy) * ts;
        double y = cell.surfaceZ * ELEV + 0.11;
        double count = Math.min(6, Math.max(3, Math.round(patch.blades * 0.6)));
        IReadOnlyList<int> prism = TerrainGeometryCompilerTheme.TERRAIN_GEOMETRY_RAINBOW_COLORS;
        for (int i = 0; i < count; i++)
        {
            double h = hash(patch.id * 3761 + i * 37);
            double a = patch.phase * PROP_TAU + i * 1.43 + h * 0.6;
            double d = ts * patch.radius * (0.2 + h * 0.8);
            double px = cx + Math.cos(a) * d;
            double pz = cz + Math.sin(a) * d * 0.56;
            double len = ts * (0.04 + h * 0.035);
            double wid = len * (0.3 + h * 0.22);
            double dx = Math.cos(a) * len;
            double dz = Math.sin(a) * len * 0.5;
            double nx = -Math.sin(a) * wid;
            double nz = Math.cos(a) * wid * 0.5;
            int color = prism[(int)(Math.floor(h * 883) % prism.Count)];
            builder.addOverlay(
                new P3[]
                {
                    new P3(px - dx - nx * 0.25, y, pz - dz - nz * 0.25),
                    new P3(px + dx, y, pz + dz),
                    new P3(px - dx * 0.1 + nx, y, pz - dz * 0.1 + nz),
                },
                color,
                0.24 + patch.alpha * 0.14);
            if (h > 0.55)
            {
                builder.addOverlayLineFlat(
                    y + 0.02,
                    px + dx,
                    pz + dz,
                    px + dx * 2.1,
                    pz + dz * 2.1,
                    0.5,
                    this.tileset.flood.glint,
                    0.2);
            }
        }
    }

    /// <summary>
    /// Icing beaded along a cliff foot: a scalloped run of frosting bumps under a drip line, with the odd
    /// candy sprinkle set into the sugar — the wall visibly "frosted onto" the floor.
    /// </summary>
    internal void addRainbowFrostingArc(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand)
    {
        if (this.tileset == null) return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double y0 = cell.surfaceZ * ELEV + 0.08;
        bool alongX = strand.direction == "n" || strand.direction == "s";
        double wallX =
            strand.direction == "e"
                ? x0 + ts * 0.88
                : strand.direction == "w"
                    ? x0 + ts * 0.12
                    : x0 + ts * strand.t;
        double wallZ =
            strand.direction == "n"
                ? z0 + ts * 0.12
                : strand.direction == "s"
                    ? z0 + ts * 0.88
                    : z0 + ts * strand.t;
        int cream = mix(this.tileset.flood.foam, this.tileset.terrain.wallLit, 0.14);
        int creamSide = mix(cream, this.tileset.terrain.wallDeep, 0.3);
        double half = ts * (0.16 + strand.length * 0.08);
        const int bumps = 4;
        for (int i = 0; i < bumps; i++)
        {
            double h = hash(strand.id * 3767 + i * 29);
            double t = (i + 0.5) / bumps - 0.5;
            double px = wallX + (alongX ? t * half * 2 : (strand.direction == "e" ? -1 : 1) * (0.8 + h));
            double pz = wallZ + (alongX ? (strand.direction == "n" ? 1 : -1) * (0.8 + h) : t * half * 2);
            double r = ts * (0.022 + h * 0.016) * (i % 2 == 0 ? 1 : 0.74);
            propFrustum(
                builder,
                px,
                pz,
                y0,
                y0 + r * 1.5,
                r,
                r * 0.4,
                6,
                strand.phase * PROP_TAU + i,
                cream,
                creamSide,
                0.86);
        }
        builder.addOverlayLineFlat(
            y0 + 0.1,
            alongX ? wallX - half : wallX,
            alongX ? wallZ : wallZ - half,
            alongX ? wallX + half : wallX,
            alongX ? wallZ : wallZ + half,
            1.1,
            cream,
            0.26);
        IReadOnlyList<int> prism = TerrainGeometryCompilerTheme.TERRAIN_GEOMETRY_RAINBOW_COLORS;
        int sprinkle =
            prism[
                (int)(Math.floor(hash(strand.id * 3769 + 3) * prism.Count) %
                    prism.Count)];
        double st = hash(strand.id * 3779 + 9) - 0.5;
        double sx = wallX + (alongX ? st * half * 1.6 : 0);
        double sz = wallZ + (alongX ? 0 : st * half * 1.6);
        builder.addOverlay(
            new P3[]
            {
                new P3(sx - 0.9, y0 + 0.13, sz - 0.55),
                new P3(sx + 0.9, y0 + 0.13, sz - 0.55),
                new P3(sx + 0.9, y0 + 0.13, sz + 0.55),
                new P3(sx - 0.9, y0 + 0.13, sz + 0.55),
            },
            sprinkle,
            0.4);
    }

    /// <param name="tileset">TS default `this.tileset!` (applies when the argument is undefined).</param>
    internal void addProceduralTree(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainTreeDressingEffect tree,
        TerrainMaterialTileset? tileset = null)
    {
        tileset ??= this.tileset;
        // Silhouette selection (including the Olympian column ruin) belongs to the canopy dispatcher above; this
        // is the terminal generic form, so it never re-routes.
        if (tileset == null) return;
        double ts = frame.tileSize;
        double cx = frame.originX + (frame.i0 + cell.x + tree.ox) * ts;
        double cz = frame.originY + (frame.j0 + cell.y + tree.oy) * ts;
        // Fluitown comic look: the tree stands on the uneven ground (TerrainGeometryCompiler.Relief.cs).
        double y0 = cell.surfaceZ * ELEV + 0.08 + this.groundReliefLiftAtBuiltCell(frame, cell.x + tree.ox, cell.y + tree.oy);
        TreeGeometry.addTerrainTreeGeometry(builder, new TerrainTreeGeometryOptions
        {
            tileset = tileset,
            tree = dressingTreeLike(tree.id, tree),
            x = cx,
            z = cz,
            y0 = y0,
            tileSize = ts,
            canopy = this.runCanopyColor(tree, tileset),
            // A blade cap this low is the explicit software-WebGL safety tier. Preserve every authored tree and its
            // complete outer silhouette, while using the already-proven dense-grove topology policy for overlapped
            // interior twigs/lobes instead of feeding a CPU rasterizer invisible enrichment geometry.
            detail = this.vegetationBladeCap <= 4 || this.biome?.key == "hub" ? "dense-grove" : "full",
        });
    }

    /// <param name="tileset">TS default `this.tileset!` (applies when the argument is undefined).</param>
    internal int runCanopyColor(
        TerrainTreeDressingEffect tree,
        TerrainMaterialTileset? tileset = null)
    {
        tileset ??= this.tileset;
        if (tileset == null) return 0x6a8f5f;
        return TreeGeometry.terrainTreeCanopyColor(tileset, dressingTreeLike(tree.id, tree));
    }

    /* ── Ground / wall / bridge cells: real caps, faces, bevels ─────────────────────────────────────────── */

    /// <summary>
    /// A Cleft is a wall with a true negative-space slit, not a decal. Two independently capped rock masses keep
    /// the surrounding terrain material and silhouette while the dark, ground-visible gap communicates that sight
    /// and projectiles can cross. The slit remains deliberately narrower than an actor body.
    /// </summary>
    internal void buildCleftCell(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell)
    {
        TerrainCleftProfile? profile = cell.height.cleft;
        // `{ ...cell, type: Solid, surfaceZ, walkable: false, solid: true, blocksSight: true } as TerrainCell`
        TerrainCell wallCell = cell.Clone();
        wallCell.type = TileType.Solid;
        wallCell.surfaceZ = profile?.wallTopZ ?? cell.surfaceZ;
        wallCell.walkable = false;
        wallCell.solid = true;
        wallCell.blocksSight = true;
        TerrainMaterial material = this.materialForCell(wallCell, terrain);
        if (profile == null)
        {
            // Invalid authored data must remain a visible wall; validation reports the structural error separately.
            this.buildSolidCell(builder, frame, terrain, plan, wallCell, material);
            return;
        }

        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double cx = x0 + ts * 0.5;
        double cz = z0 + ts * 0.5;
        double alongX = profile.passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        double alongZ = profile.passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        double tangentX = profile.passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        double tangentZ = profile.passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        double groundY = profile.groundZ * ELEV;
        double topY = profile.wallTopZ * ELEV;
        P3 point(double along, double tangent, double y) => new P3(
            cx + (along - 0.5) * ts * alongX + tangent * ts * tangentX,
            y,
            cz + (along - 0.5) * ts * alongZ + tangent * ts * tangentZ);
        // A world-stable, asymmetric centreline prevents the split from reading like a manufactured doorway.
        double[] alongStops = { 0, 0.22, 0.47, 0.73, 1 };
        double[] negativeLip = { -0.16, -0.125, -0.19, -0.14, -0.17 };
        double[] positiveLip = { 0.18, 0.14, 0.2, 0.125, 0.165 };
        int topColor = TerrainRenderPlanModule.terrainMaterialSurfaceTopColor(material, wallCell);
        // `{ ...cell, type: Floor, elevation, surfaceZ, walkable: true, solid: false, blocksSight: false } as TerrainCell`
        TerrainCell floorCell = cell.Clone();
        floorCell.type = TileType.Floor;
        floorCell.elevation = profile.groundZ;
        floorCell.surfaceZ = profile.groundZ;
        floorCell.walkable = true;
        floorCell.solid = false;
        floorCell.blocksSight = false;
        TerrainMaterial floorMaterial = this.materialForCell(floorCell, terrain);
        int floorColor = TerrainRenderPlanModule.terrainMaterialSurfaceTopColor(floorMaterial, floorCell);
        int jambColor = mix(material.side, material.edgeDark, 0.38);
        int mouthColor = mix(material.side, material.topDark, 0.2);
        int edgeColor = mix(material.edgeLight, topColor, 0.46);
        double capShade = TerrainGeometryCompilerFields.terrainCapShade(frame.i0 + cell.x, frame.j0 + cell.y, true, false);

        for (int segment = 0; segment < alongStops.Length - 1; segment++)
        {
            double a0 = alongStops[segment];
            double a1 = alongStops[segment + 1];
            double n0 = negativeLip[segment];
            double n1 = negativeLip[segment + 1];
            double p0 = positiveLip[segment];
            double p1 = positiveLip[segment + 1];

            setShade4(capShade, capShade, capShade, capShade);
            builder.addSurface(
                new P3[] { point(a0, -0.5, topY), point(a1, -0.5, topY), point(a1, n1, topY), point(a0, n0, topY) },
                0,
                1,
                0,
                topColor,
                SURF.rockCap,
                0.15,
                SHADE4,
                UNIT_ZERO,
                true);
            builder.addSurface(
                new P3[] { point(a0, p0, topY), point(a1, p1, topY), point(a1, 0.5, topY), point(a0, 0.5, topY) },
                0,
                1,
                0,
                topColor,
                SURF.rockCap,
                0.15,
                SHADE4,
                UNIT_ZERO,
                true);

            // The opposed jambs are the semantic core: light can travel through the opening between two real masses.
            setShade4(0.72, 0.82, 0.96, 0.86);
            builder.addSurface(
                new P3[] { point(a0, n0, groundY), point(a1, n1, groundY), point(a1, n1, topY), point(a0, n0, topY) },
                tangentX,
                0,
                tangentZ,
                jambColor,
                SURF.rockFace,
                0.22,
                SHADE4,
                UNIT_ZERO,
                true,
                true);
            builder.addSurface(
                new P3[] { point(a1, p1, groundY), point(a0, p0, groundY), point(a0, p0, topY), point(a1, p1, topY) },
                -tangentX,
                0,
                -tangentZ,
                jambColor,
                SURF.rockFace,
                0.22,
                SHADE4,
                UNIT_ZERO,
                true,
                true);

            // The opening exposes opaque neighbouring ground before ambient occlusion is layered over it; the wall
            // can never reveal the clear colour or look hollow from a glancing camera angle.
            builder.addSurface(
                new P3[]
                {
                    point(a0, n0, groundY),
                    point(a1, n1, groundY),
                    point(a1, p1, groundY),
                    point(a0, p0, groundY),
                },
                0,
                1,
                0,
                floorColor,
                SURF.floor,
                0.12);
            builder.addOverlay(
                new P3[]
                {
                    point(a0, n0, groundY + 0.035),
                    point(a1, n1, groundY + 0.035),
                    point(a1, p1, groundY + 0.035),
                    point(a0, p0, groundY + 0.035),
                },
                material.edgeDark,
                0.42);
        }

        // Both mouths expose the broken wall section. A restrained crest line makes the two separate lips readable
        // even in pale biomes, without adding a foreign prop palette.
        foreach (int mouth in new[] { 0, 1 })
        {
            int lipIndex = mouth == 0 ? 0 : alongStops.Length - 1;
            double n = negativeLip[lipIndex];
            double p = positiveLip[lipIndex];
            int normal = mouth == 0 ? -1 : 1;
            setShade4(0.78, 0.86, 1, 0.92);
            builder.addSurface(
                new P3[]
                {
                    point(mouth, -0.5, groundY),
                    point(mouth, n, groundY),
                    point(mouth, n, topY),
                    point(mouth, -0.5, topY),
                },
                alongX * normal,
                0,
                alongZ * normal,
                mouthColor,
                SURF.rockFace,
                0.18,
                SHADE4,
                UNIT_ZERO,
                true,
                true);
            builder.addSurface(
                new P3[]
                {
                    point(mouth, p, groundY),
                    point(mouth, 0.5, groundY),
                    point(mouth, 0.5, topY),
                    point(mouth, p, topY),
                },
                alongX * normal,
                0,
                alongZ * normal,
                mouthColor,
                SURF.rockFace,
                0.18,
                SHADE4,

                UNIT_ZERO,
                true,
                true);
            foreach ((double @from, double to) in new[] { (n - 0.09, n), (p, p + 0.09) })
            {
                builder.addOverlayLineFlat(
                    topY + 0.045,
                    point(mouth, @from, topY).x,
                    point(mouth, @from, topY).z,
                    point(mouth, to, topY).x,
                    point(mouth, to, topY).z,
                    0.34,
                    edgeColor,
                    0.24);
            }
        }
    }

    /// <summary>
    /// Build a real bridge deck above an Underpass floor. Its flat slab, continuous dark soffit, transverse ribs,
    /// raised parapets and cast floor shadow form a readable two-level object. There are intentionally no portal
    /// posts in the passage: the only vertical supports are the validated +4 Solid banks on both ends of the span.
    /// </summary>
    internal void buildUnderpassBridge(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainCell cell)
    {
        TerrainOverheadVolume? volume = cell.height.overheadVolume;
        if (volume == null) return;

        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double cx = x0 + ts * 0.5;
        double cz = z0 + ts * 0.5;
        int alongX = volume.passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        int alongZ = volume.passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        int crossX = volume.passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        int crossZ = volume.passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        P3 point(double along, double cross, double y) => new P3(
            cx + (along - 0.5) * ts * alongX + (cross - 0.5) * ts * crossX,
            y,
            cz + (along - 0.5) * ts * alongZ + (cross - 0.5) * ts * crossZ);
        double bottomY = volume.bottom * ELEV;
        double topY = volume.top * ELEV;
        double floorY = cell.elevation * ELEV;
        // `{ ...cell, type: Bridge, surfaceZ: volume.top, walkable: false, solid: false, blocksSight: false } as TerrainCell`
        TerrainCell bridgeCell = cell.Clone();
        bridgeCell.type = TileType.Bridge;
        bridgeCell.surfaceZ = volume.top;
        bridgeCell.walkable = false;
        bridgeCell.solid = false;
        bridgeCell.blocksSight = false;
        TerrainMaterial material = this.materialForCell(bridgeCell, terrain);
        UnderpassSuspensionGeometryAudit suspension = TerrainSuspensionBridge.buildUnderpassSuspensionGeometry(
            new UnderpassSuspensionGeometryInput
            {
                builder = builder,
                passageAxis = volume.passageAxis,
                span = volume.span,
                spanIndex = volume.spanIndex,
                worldTileX = frame.i0 + cell.x,
                worldTileY = frame.j0 + cell.y,
                x0 = x0,
                z0 = z0,
                tileSize = ts,
                elevationStep = ELEV,
                floorY = floorY,
                deckTopY = topY,
                negativeSupportTopY = volume.negativeSupportTop * ELEV,
                positiveSupportTopY = volume.positiveSupportTop * ELEV,
                material = material,
                bridgeSurfaceKind = SURF.bridge,
                faceSurfaceKind = SURF.rockFace,
            });
        builder.underpassPlanks += suspension.planks;
        builder.underpassVisibleGaps += suspension.visibleGaps;
        builder.underpassCableSegments += suspension.cableSegments;
        builder.underpassHangers += suspension.hangers;
        builder.underpassAnchorPosts += suspension.anchorPosts;
        if (suspension.planks > 0) return;

        // Degrade-safe fallback for a malformed custom artifact that bypassed shared validation.
        int topColor = TerrainRenderPlanModule.terrainMaterialSurfaceTopColor(material, bridgeCell);
        int fasciaColor = mix(material.side, material.topDark, 0.18);
        int undersideColor = mix(material.edgeDark, material.side, 0.28);
        int ribColor = mix(material.edgeDark, material.side, 0.46);
        int edgeColor = mix(material.edgeLight, topColor, 0.42);
        double capShade = TerrainGeometryCompilerFields.terrainCapShade(frame.i0 + cell.x, frame.j0 + cell.y, true, false);

        setShade4(capShade, capShade, capShade, capShade);
        builder.addSurface(
            new P3[] { point(0, 0, topY), point(1, 0, topY), point(1, 1, topY), point(0, 1, topY) },
            0,
            1,
            0,
            topColor,
            SURF.rockCap,
            0.14,
            SHADE4,
            UNIT_ZERO,
            true);
        builder.addSurface(
            new P3[] { point(1, 0, bottomY), point(0, 0, bottomY), point(0, 1, bottomY), point(1, 1, bottomY) },
            0,
            -1,
            0,
            undersideColor,
            SURF.rockFace,
            0.22,
            UNIT_SHADE,
            UNIT_ZERO,
            true);

        // The two exposed slab edges frame the passage across its entire width and remain open below the soffit.
        foreach (int mouth in new[] { 0, 1 })
        {
            int normal = mouth == 0 ? -1 : 1;
            setShade4(0.82, 0.9, 1, 0.94);
            builder.addSurface(
                new P3[]
                {
                    point(mouth, 0, bottomY),
                    point(mouth, 1, bottomY),
                    point(mouth, 1, topY),
                    point(mouth, 0, topY),
                },
                alongX * normal,
                0,
                alongZ * normal,
                fasciaColor,
                SURF.rockFace,
                0.18,
                SHADE4,
                UNIT_ZERO,
                true,
                true);
            double bandBottom = topY - Math.min(ELEV * 0.1, (topY - bottomY) * 0.32);
            builder.addSurface(
                new P3[]
                {
                    point(mouth + normal * 0.001, 0, bandBottom),
                    point(mouth + normal * 0.001, 1, bandBottom),
                    point(mouth + normal * 0.001, 1, topY),
                    point(mouth + normal * 0.001, 0, topY),
                },
                alongX * normal,
                0,
                alongZ * normal,
                edgeColor,
                SURF.rockFace,
                0.1,
                UNIT_SHADE,
                UNIT_ZERO,
                true,
                true);
        }

        // Two full-span beams below the deck are visible from either approach and immediately signal overhead load.
        double ribDrop = ELEV * 0.18;
        const double ribHalfWidth = 0.045;
        foreach (double center in new[] { 0.27, 0.73 })
        {
            double a0 = center - ribHalfWidth;
            double a1 = center + ribHalfWidth;
            builder.addSurface(
                new P3[]
                {
                    point(a1, 0, bottomY - ribDrop),
                    point(a0, 0, bottomY - ribDrop),
                    point(a0, 1, bottomY - ribDrop),
                    point(a1, 1, bottomY - ribDrop),
                },
                0,
                -1,
                0,
                ribColor,
                SURF.rockFace,
                0.16,
                UNIT_SHADE,
                UNIT_ZERO,
                true);
            foreach (double edge in new[] { a0, a1 })
            {
                int normal = edge == a0 ? -1 : 1;
                builder.addSurface(
                    new P3[]
                    {
                        point(edge, 0, bottomY - ribDrop),
                        point(edge, 1, bottomY - ribDrop),
                        point(edge, 1, bottomY),
                        point(edge, 0, bottomY),
                    },
                    alongX * normal,
                    0,
                    alongZ * normal,
                    ribColor,
                    SURF.rockFace,
                    0.16,
                    UNIT_SHADE,
                    UNIT_ZERO,
                    true,
                    true);
            }
        }

        // Low parapets make the upper bridge route legible in silhouette while remaining native to every biome.
        double parapetTop = topY + ELEV * 0.36;
        const double parapetWidth = 0.095;
        foreach (int side in new[] { 0, 1 })
        {
            double a0 = side == 0 ? 0 : 1 - parapetWidth;
            double a1 = side == 0 ? parapetWidth : 1;
            builder.addSurface(
                new P3[]
                {
                    point(a0, 0, parapetTop),
                    point(a1, 0, parapetTop),
                    point(a1, 1, parapetTop),
                    point(a0, 1, parapetTop),
                },
                0,
                1,
                0,
                edgeColor,
                SURF.rockCap,
                0.12,
                UNIT_SHADE,
                UNIT_ZERO,
                true);
            foreach (double edge in new[] { a0, a1 })
            {
                int normal = edge == a0 ? -1 : 1;
                builder.addSurface(
                    new P3[]
                    {
                        point(edge, 0, topY),
                        point(edge, 1, topY),
                        point(edge, 1, parapetTop),
                        point(edge, 0, parapetTop),
                    },
                    alongX * normal,
                    0,
                    alongZ * normal,
                    side == edge ? fasciaColor : material.side,
                    SURF.rockFace,
                    0.14,
                    UNIT_SHADE,
                    UNIT_ZERO,
                    true,
                    true);
            }
        }

        // The floor remains the normal biome surface, but the deck paints one coherent ambient shadow over it.
        builder.addOverlay(
            new P3[]
            {
                point(0.04, 0.03, floorY + 0.04),
                point(0.96, 0.03, floorY + 0.04),
                point(0.96, 0.97, floorY + 0.04),
                point(0.04, 0.97, floorY + 0.04),
            },
            material.edgeDark,
            0.32);
    }
}
