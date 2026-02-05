# Texture to Vertex Baker

> **Bake textures into vertex colors in Unity — one click, zero texture lookups.**

<p align="center">
  <img src="image1.jpg" alt="Texture to Vertex Baker - Unity Editor Tool UI" width="60%"/>
  <br>
  <em>Tool UI</em>
</p>

<p align="center">
  <img src="image2.jpg" alt="Texture to Vertex Color Conversion Example - Before and After" width="60%"/>
  <br>
  <em>Usage Example</em>
</p>

A **Unity Editor tool** that bakes texture colors directly into **vertex colors**, eliminating texture sampling at runtime. Designed for **mobile**, **VR**, and **AR** development where every draw call and texture lookup counts. Convert your entire scene to use a **single shader** and **single material** — no textures required (except optional lightmap).

**Why vertex colors?** No texture memory, no UV sampling cost, massively reduced draw calls through batching, and full compatibility with Unity's static/dynamic batching and SRP Batcher.

## Features

- **Batch Processing** — Drag a parent GameObject to convert entire scene hierarchies, or process single objects
- **Single Material Output** — All converted meshes share one material, enabling maximum draw call batching
- **Prefab Support** — Automatically handles prefab instances
- **Lightmap Baking** — Bake Unity lightmaps directly into vertex colors alongside texture data
- **Automatic Mesh Read/Write Handling** — Detects and fixes mesh import settings automatically

### Post-Processing Options

- **Average Colors** — Smooth colors across the entire mesh for a unified look
- **Average Neighbor Colors** — Local color smoothing based on vertex proximity (radius-based)
- **Optimize Vertices** — Merge vertices with similar colors to reduce vertex count
- **Color Tint** — Apply a global color tint to all vertex colors
- **Texture Adjustments** — Control strength, contrast, and brightness of sampled textures
- **Keep Existing Colors** — Blend new colors with existing vertex colors

### Included Shaders

Two unlit shaders are included, compatible with **all render pipelines** (Built-in, URP, HDRP):

- `UnlitVertexLightmap.shader` — Unlit shader with vertex colors and lightmap support
- `FakeShadowVertex.shader` — Vertex color shader with fake shadow effects

Feel free to use your own shaders — just make sure they support vertex colors.

## Installation

### Option 1: Unity Package
Download the `.unitypackage` from [Releases](../../releases) and import into your project.

### Option 2: Clone Repository
Clone this repository and copy the `TextureToVertexBaker` folder into your project's `Assets/` directory.

## How to Use

1. Open the tool: **Tools > Texture to Vertex Baker**
2. Drag your target GameObject into the **Target Object** field
3. (Optional) Assign an output material or let the tool use the included one
4. Configure texture sampling and post-processing options as needed
5. Click **Process & Assign Vertex Colors**

### Tips

- For best results on complex scenes, enable **Optimize Vertices** to reduce vertex count
- Use **Average Neighbor Colors** for smoother color transitions between vertices
- Enable **Sample Lightmaps** to bake lighting information into vertex colors
- The tool includes an **Undo** button to revert the last operation

## Requirements

- Unity 2019.4 or later
- Works with Built-in, URP, and HDRP render pipelines

## Use Cases

- **Mobile games** — Reduce texture memory and GPU fill rate on low-end devices
- **VR / AR applications** — Hit frame rate targets by eliminating texture bottlenecks
- **Stylized / low-poly art** — Vertex colors pair naturally with flat-shaded and low-poly aesthetics
- **Scene optimization** — Combine with Unity batching for minimal draw calls across large environments
- **Prototyping** — Quickly visualize scenes without managing texture assets

## Early Access

This tool is in active development. Some bugs may occur — if you find one, please [open an issue](../../issues). Contributions and feedback are welcome!

## License

MIT License — free to use in personal and commercial projects.

---

**Keywords:** Unity vertex color, texture to vertex color converter, bake texture to mesh, vertex color baker, Unity mobile optimization, Unity VR optimization, reduce draw calls Unity, single material workflow, Unity Editor tool, vertex color shader, mesh color baker, GPU optimization, Unity performance, Unity AR optimization, low-poly vertex color, Unity batching optimization
- **Lightmap Baking** - Bake lightmaps directly into vertex colors along with textures
- **Automatic Mesh Read/Write Handling** - Detects and fixes mesh import settings automatically

### Post-Processing Options

- **Average Colors** - Smooth colors across the entire mesh for a unified look
- **Average Neighbor Colors** - Local color smoothing based on vertex proximity (radius-based)
- **Optimize Vertices** - Merge vertices with similar colors to reduce vertex count
- **Color Tint** - Apply a global color tint to all vertex colors
- **Texture Adjustments** - Control strength, contrast, and brightness of sampled textures
- **Keep Existing Colors** - Blend new colors with existing vertex colors

### Included Shaders

Two unlit shaders are included, compatible with **all render pipelines** (Built-in, URP, HDRP):

- `UnlitVertexLightmap.shader` - Unlit shader with vertex colors and lightmap support
- `FakeShadowVertex.shader` - Vertex color shader with fake shadow effects

Feel free to use your own shaders - just make sure they support vertex colors.

## Installation

### Option 1: Unity Package
Download the `.unitypackage` from [Releases](../../releases) and import into your project.

### Option 2: Clone Repository
Clone this repository and copy the `TextureToVertexBaker` folder into your project's `Assets/` directory.

## How to Use

1. Open the tool: **Tools > Texture to Vertex Baker**
2. Drag your target GameObject into the **Target Object** field
3. (Optional) Assign an output material or let the tool use the included one
4. Configure texture sampling and post-processing options as needed
5. Click **Process & Assign Vertex Colors**

### Tips

- For best results on complex scenes, enable **Optimize Vertices** to reduce vertex count
- Use **Average Neighbor Colors** for smoother color transitions between vertices
- Enable **Sample Lightmaps** to bake lighting information into vertex colors
- The tool includes an **Undo** button to revert the last operation

## Requirements

- Unity 2019.4 or later
- Works with Built-in, URP, and HDRP render pipelines

## Early version. 

Some bugs might occur 

## License

MIT License - feel free to use in personal and commercial projects.
