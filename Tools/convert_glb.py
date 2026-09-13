# Convert a Marvel Rivals rip GLB into a Unity-ready humanoid FBX.
# Usage: blender -b --python convert_glb.py -- <in.glb> <out_dir> <CharName> <height_m>
#
# Pipeline (recreated from the SpiderMan/Venom conversion, 2026-07-14):
#  1. import GLB
#  2. strip _<digits> suffixes from bone names (UE-style names then automap in Unity)
#  3. bake the EVALUATED mesh (deform applied) + pose-as-rest on the armature —
#     these rips have pose != rest with pre-transformed vertices; nothing is
#     linear until the armature deform is identity
#  4. delete Sketchfab wrapper empties / non-skinned junk meshes
#  5. apply object transforms, then normalise height + feet-at-z0 via
#     data.transform() on BOTH armature and meshes (object-level scale does NOT
#     rescale skinned meshes at rest)
#  6. export packed textures as <Char>_Image_N.png + a materials json
#  7. export FBX with defaults + add_leaf_bones=False
import bpy, sys, os, re, json
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
# absolute paths only — Blender resolves relative paths against the blend file
# (unsaved in batch mode), so img.save() with a relative path goes nowhere
glb_path, out_dir = os.path.abspath(argv[0]), os.path.abspath(argv[1])
char_name, target_h = argv[2], float(argv[3])
tex_dir = os.path.join(out_dir, "Textures")
os.makedirs(tex_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb_path)

print("=== imported objects ===")
for o in bpy.data.objects:
    print(f"  {o.type:9} {o.name}  parent={o.parent.name if o.parent else None}")

arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
if len(arms) != 1:
    print(f"FATAL: expected 1 armature, found {len(arms)}"); sys.exit(1)
arm = arms[0]

meshes = [o for o in bpy.data.objects if o.type == 'MESH']
skinned = [m for m in meshes if any(mod.type == 'ARMATURE' for mod in m.modifiers)]
print(f"meshes={len(meshes)} skinned={len(skinned)}")
if not skinned:
    print("FATAL: no skinned meshes"); sys.exit(1)

# ---- 3. bake evaluated meshes, then pose-as-rest so deform becomes identity
#         (bone renaming MUST wait until after the bake: renaming while the
#          modifier is live can desync bone<->vertex-group names and the bake
#          then captures a mis-deformed mesh)
deps = bpy.context.evaluated_depsgraph_get()
for m in skinned:
    ev = m.evaluated_get(deps)
    baked = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=deps)
    old = m.data
    m.data = baked
    if old.users == 0:
        bpy.data.meshes.remove(old)

bpy.context.view_layer.objects.active = arm
arm.select_set(True)
bpy.ops.object.mode_set(mode='POSE')
bpy.ops.pose.select_all(action='SELECT')
bpy.ops.pose.armature_apply(selected=False)
bpy.ops.object.mode_set(mode='OBJECT')

# sanity: deform must now be identity — evaluated size == mesh data size
deps = bpy.context.evaluated_depsgraph_get()
for m in skinned[:1]:
    ev = m.evaluated_get(deps).to_mesh()
    print(f"post-bake sanity {m.name}: data_verts={len(m.data.vertices)} eval_verts={len(ev.vertices)}")
    m.evaluated_get(deps).to_mesh_clear()

# ---- 2 (moved). bone rename via ONE consistent map applied to bones AND groups
names = set(b.name for b in arm.data.bones)
bone_map = {}
for b in list(arm.data.bones):
    new = re.sub(r'_\d+$', '', b.name)
    if new == b.name or new in names:
        continue
    names.discard(b.name); names.add(new)
    bone_map[b.name] = new
    b.name = new  # may auto-sync vertex groups; manual pass below covers if not
for m in skinned:
    for vg in list(m.vertex_groups):
        tgt = bone_map.get(vg.name)
        if tgt and tgt not in m.vertex_groups:
            vg.name = tgt
print(f"renamed {len(bone_map)} bones")
mismatch = sum(1 for m in skinned for vg in m.vertex_groups if vg.name not in names)
print(f"vgroups not matching any bone: {mismatch}")

# ---- 4. junk removal: unparent ALL keepers to world (keep transform), delete the rest
keep = set(skinned) | {arm}
for o in keep:
    mw = o.matrix_world.copy()
    o.parent = None
    o.matrix_world = mw
for o in [o for o in bpy.data.objects if o not in keep]:
    print(f"deleting junk: {o.type} {o.name}")
    bpy.data.objects.remove(o, do_unlink=True)

# ---- 5. bake each object's OWN world matrix into its data, then normalise.
# Do NOT use ops.transform_apply here: applying the armature's transform shifts
# its (former) children in world space because Blender never updates their
# matrix_parent_inverse — mesh data then bakes in a different space than the
# bones and Unity shows a collapsed strand-mesh.
for o in [arm] + skinned:
    mw = o.matrix_world.copy()
    print(f"baking world matrix of {o.name}: {mw}")
    o.data.transform(mw)
    o.matrix_world = Matrix.Identity(4)
bpy.context.view_layer.update()

def bounds():
    lo = Vector((1e9, 1e9, 1e9)); hi = Vector((-1e9, -1e9, -1e9))
    for m in skinned:
        for v in m.data.vertices:
            for i in range(3):
                lo[i] = min(lo[i], v.co[i]); hi[i] = max(hi[i], v.co[i])
    return lo, hi

lo, hi = bounds()
h = hi.z - lo.z
s = target_h / h if h > 1e-6 else 1.0
cx, cy = (lo.x + hi.x) / 2, (lo.y + hi.y) / 2
M = Matrix.Translation(Vector((-cx * s, -cy * s, -lo.z * s))) @ (Matrix.Scale(s, 4))
arm.data.transform(M)
for m in skinned:
    m.data.transform(M)
lo, hi = bounds()
print(f"height was {h:.3f} -> scaled x{s:.4f}, bounds now z[{lo.z:.3f},{hi.z:.3f}] x[{lo.x:.2f},{hi.x:.2f}]")

# ---- 6. textures + materials json (image naming matches the Venom layout:
#         per material, base color first then normal)
mats = {}
img_name = {}
counter = 0

def role_images(mat):
    base = normal = None
    if not mat or not mat.use_nodes:
        return base, normal
    for link in mat.node_tree.links:
        n = link.from_node
        if n.type == 'TEX_IMAGE' and n.image is not None and n.image.size[0] > 0:
            if link.to_socket.name == 'Base Color':
                base = n.image
            elif link.to_node.type == 'NORMAL_MAP' and link.to_socket.name == 'Color':
                normal = n.image
    return base, normal

def export_img(img):
    global counter
    if img in img_name:
        return img_name[img]
    name = f"{char_name}_Image_{counter}"; counter += 1
    img_name[img] = name
    path = os.path.join(tex_dir, name + ".png")
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    if not os.path.exists(path):
        print(f"FATAL: texture did not land at {path}"); sys.exit(1)
    print(f"texture: {name}.png  ({img.size[0]}x{img.size[1]}) -> {path}")
    return name

seen = set()
for m in skinned:
    for slot in m.material_slots:
        mat = slot.material
        if mat is None or mat.name in seen:
            continue
        seen.add(mat.name)
        clean = re.sub(r'\.\d+$', '', mat.name).replace('.', '_')
        if clean != mat.name and clean not in bpy.data.materials:
            mat.name = clean
        base, normal = role_images(mat)
        mats[mat.name] = {
            "baseColor": ("Textures/" + export_img(base) + ".png") if base else None,
            "normal":    ("Textures/" + export_img(normal) + ".png") if normal else None,
        }

with open(os.path.join(out_dir, char_name + "_materials.json"), "w") as f:
    json.dump({"character": char_name, "targetHeightM": target_h, "materials": mats}, f, indent=1)
print("materials:", json.dumps(mats, indent=1))

# ---- 7. export
fbx = os.path.join(out_dir, char_name + ".fbx")
bpy.ops.export_scene.fbx(filepath=fbx, add_leaf_bones=False)
print(f"DONE: {fbx}")
