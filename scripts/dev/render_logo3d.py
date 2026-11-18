# Renders the WAI Link mark as an inflated glossy 3D ribbon and writes the
# intro and loop frame sequences used by the app's loader and start screen.
#
#   blender -b --factory-startup -P render_logo3d.py -- <logo.png> <out_dir> [test]
#
# The shape is derived from the logo's own alpha channel (distance transform ->
# rounded height field), so the 3D mark always matches the 2D brand asset.
import bpy, sys, math, os
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:]
LOGO, OUT = argv[0], argv[1]
TEST = len(argv) > 2
WIDTH, HEIGHT = 320, 240
INTRO_FRAMES, LOOP_FRAMES = 40, 48
os.makedirs(OUT, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

image = bpy.data.images.load(LOGO)
w, h = image.size
pixels = np.array(image.pixels[:], dtype=np.float32).reshape(h, w, 4)
alpha = pixels[..., 3]
mask = alpha > 0.5

# Two-pass chamfer distance to the outside of the mark, in pixels.
INF = 1e6
dist = np.where(mask, INF, 0.0).astype(np.float32)
for y in range(h):
    row = dist[y]
    up = dist[y - 1] if y > 0 else None
    for x in range(w):
        if row[x] == 0: continue
        best = row[x]
        if x > 0: best = min(best, row[x - 1] + 1)
        else: best = min(best, 1)
        if up is not None:
            best = min(best, up[x] + 1)
            if x > 0: best = min(best, up[x - 1] + 1.4142)
            if x < w - 1: best = min(best, up[x + 1] + 1.4142)
        else: best = min(best, 1)
        row[x] = best
for y in range(h - 1, -1, -1):
    row = dist[y]
    down = dist[y + 1] if y < h - 1 else None
    for x in range(w - 1, -1, -1):
        if row[x] == 0: continue
        best = row[x]
        if x < w - 1: best = min(best, row[x + 1] + 1)
        else: best = min(best, 1)
        if down is not None:
            best = min(best, down[x] + 1)
            if x > 0: best = min(best, down[x - 1] + 1.4142)
            if x < w - 1: best = min(best, down[x + 1] + 1.4142)
        else: best = min(best, 1)
        row[x] = best

radius = float(np.percentile(dist[mask], 97))
t = np.clip(dist / radius, 0, 1)
height = np.sqrt(np.clip(1 - (1 - t) ** 2, 0, 1)) * radius * 0.62
for _ in range(3):  # soften ridges where bands cross
    padded = np.pad(height, 1, mode="edge")
    height = (padded[:-2, 1:-1] + padded[2:, 1:-1] + padded[1:-1, :-2] + padded[1:-1, 2:] + 4 * padded[1:-1, 1:-1]) / 8
height = np.where(dist > 0, height, 0)

STEP = 2
xs = np.arange(0, w, STEP); ys = np.arange(0, h, STEP)
gw, gh = len(xs), len(ys)
gx, gy = np.meshgrid(xs, ys)
gz = height[gy, gx] / w * 2
vx = (gx / w - 0.5) * 2
vy = (gy / h - 0.5) * 2 * h / w
front = np.stack([vx, vy, gz], axis=-1).reshape(-1, 3)
back = np.stack([vx, vy, -gz], axis=-1).reshape(-1, 3)
verts = np.concatenate([front, back])
near = np.pad(dist, 2, mode="constant")
near = np.maximum.reduce([near[2:-2, 2:-2], near[:-4, 2:-2], near[4:, 2:-2], near[2:-2, :-4], near[2:-2, 4:]])
cell = near[gy, gx] > 0
faces = []
n = gw * gh
for j in range(gh - 1):
    for i in range(gw - 1):
        if not (cell[j, i] or cell[j, i + 1] or cell[j + 1, i] or cell[j + 1, i + 1]): continue
        a = j * gw + i; b = a + 1; c = a + gw + 1; d = a + gw
        faces.append((a, b, c, d))
        faces.append((n + a, n + d, n + c, n + b))
mesh = bpy.data.meshes.new("Mark")
mesh.from_pydata(verts.tolist(), [], faces)
mesh.update()
uv = mesh.uv_layers.new(name="UV")
loop_verts = np.zeros(len(mesh.loops), dtype=np.int32)
mesh.loops.foreach_get("vertex_index", loop_verts)
vu = np.tile(np.stack([gx / w, gy / h], axis=-1).reshape(-1, 2), (2, 1))
uv.data.foreach_set("uv", vu[loop_verts].astype(np.float32).ravel())
mesh.polygons.foreach_set("use_smooth", [True] * len(mesh.polygons))
mark = bpy.data.objects.new("Mark", mesh)
scene.collection.objects.link(mark)

material = bpy.data.materials.new("Glass")
material.use_nodes = True
nodes, links = material.node_tree.nodes, material.node_tree.links
bsdf = nodes["Principled BSDF"]
tex = nodes.new("ShaderNodeTexImage")
tex.image = image; tex.interpolation = "Cubic"; tex.extension = "CLIP"
links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
ramp = nodes.new("ShaderNodeValToRGB")  # tighten the alpha edge so dithering stays a thin band
ramp.color_ramp.elements[0].position = 0.38
ramp.color_ramp.elements[1].position = 0.62
links.new(tex.outputs["Alpha"], ramp.inputs["Fac"])
links.new(ramp.outputs["Color"], bsdf.inputs["Alpha"])
links.new(tex.outputs["Color"], bsdf.inputs["Emission Color"])
bsdf.inputs["Emission Strength"].default_value = 0.16
bsdf.inputs["Roughness"].default_value = 0.20
bsdf.inputs["Coat Weight"].default_value = 1.0
bsdf.inputs["Coat Roughness"].default_value = 0.04
bsdf.inputs["Specular IOR Level"].default_value = 0.65
material.surface_render_method = "DITHERED"
mesh.materials.append(material)

def light(name, location, energy, size, color=(1, 1, 1)):
    data = bpy.data.lights.new(name, "AREA")
    data.energy = energy; data.size = size; data.color = color
    obj = bpy.data.objects.new(name, data)
    obj.location = location
    scene.collection.objects.link(obj)
    direction = -obj.location
    obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    return obj

light("Key", (-2.6, 3.0, 4.2), 300, 3.5)
light("Fill", (3.2, -1.8, 3.6), 140, 4.0, (0.80, 0.90, 1.0))
light("RimA", (3.0, 2.2, -3.2), 520, 2.5, (0.55, 0.85, 1.0))
light("RimB", (-3.0, -1.5, -3.4), 420, 2.5, (0.95, 0.65, 1.0))

world = bpy.data.worlds.new("World")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.20, 0.23, 0.30, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.55
scene.world = world

cam_data = bpy.data.cameras.new("Camera")
cam_data.lens = 85
camera = bpy.data.objects.new("Camera", cam_data)
camera.location = (0, 0, 6.6)
scene.collection.objects.link(camera)
scene.camera = camera

scene.render.engine = "BLENDER_EEVEE_NEXT"
scene.eevee.taa_render_samples = 72
try: scene.eevee.use_raytracing = True
except Exception: pass
scene.render.film_transparent = True
scene.render.resolution_x, scene.render.resolution_y = WIDTH, HEIGHT
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.render.image_settings.compression = 90
scene.view_settings.view_transform = "Standard"

def smooth(x):
    x = min(1.0, max(0.0, x))
    return x * x * x * (x * (x * 6 - 15) + 10)

def back_out(x):
    c1, c3 = 1.35, 2.35
    return 1 + c3 * (x - 1) ** 3 + c1 * (x - 1) ** 2

def pose(spin_deg, tilt_deg, scale, lift=0.0):
    mark.rotation_euler = (math.radians(tilt_deg), math.radians(spin_deg), 0)
    mark.scale = (scale, scale, scale)
    mark.location = (0, lift, 0)

def render(name):
    scene.render.filepath = os.path.join(OUT, name)
    bpy.ops.render.render(write_still=True)

intro = range(INTRO_FRAMES) if not TEST else [0, 14, INTRO_FRAMES - 1]
for i in intro:
    x = i / (INTRO_FRAMES - 1)
    e = back_out(x)
    pose(-210 * (1 - e), 26 * (1 - smooth(x)), 0.30 + 0.70 * e)
    render(f"intro_{i:03d}.png")

loop = range(LOOP_FRAMES) if not TEST else [12, 20]
for i in loop:
    x = i / LOOP_FRAMES
    spin = 360 * smooth((x - 0.08) / 0.84)
    pose(spin, 7 * math.sin(2 * math.pi * x), 1.0, 0.035 * math.sin(2 * math.pi * x))
    render(f"loop_{i:03d}.png")
print("LOGO3D_DONE")
