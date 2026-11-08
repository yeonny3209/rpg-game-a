# ============================================================
#  시작 마을 캐릭터 생성 스크립트 — Blender 4.2 LTS 이상
#  만드는 것: 마을 이장, 경비병, 늑대, 토끼 (각각 리그 + 애니메이션)
#  사용법: Scripting 탭 > 새 텍스트 > 붙여넣기 > Run Script (Alt+P)
#  결과: 캐릭터마다 FBX 하나씩 (village_chief / village_guard / wolf / rabbit)
#  주의: CLEAR_SCENE = True면 현재 씬의 오브젝트를 전부 지움
# ============================================================
import bpy, bmesh, os
from math import sin, cos, pi
from mathutils import Vector, Matrix

CLEAR_SCENE = True
EXPORT_FBX = True
OUT_DIR = None      # None이면 .blend가 있는 폴더, 저장 전이면 바탕화면
SPACING = 1.6       # 완성된 캐릭터를 옆으로 늘어놓는 간격(m)

FWD, UP = Vector((0, -1, 0)), Vector((0, 0, 1))   # 모든 캐릭터는 -Y 방향을 바라봄

# ---------------- 공통 도구 ----------------
if bpy.context.object and bpy.context.object.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')
if CLEAR_SCENE:
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete()

def select_only(*objs, active=None):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]

def LRS(loc, rot=None, scale=None):
    return Matrix.LocRotScale(Vector(loc), rot, Vector(scale) if scale else None)

def sphere(bm, loc, scale, seg=16, rot=None):
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=max(6, seg*3//4), radius=1.0,
                              matrix=LRS(loc, rot, scale))

def cyl(bm, a, b, r1, r2, seg=12, caps=True):
    """a에서 b까지 이어지는 원기둥(원뿔). r1은 a쪽 반지름, r2는 b쪽 반지름"""
    a, b = Vector(a), Vector(b)
    d = b - a
    rot = Vector((0, 0, 1)).rotation_difference(d)
    bmesh.ops.create_cone(bm, cap_ends=caps, cap_tris=False, segments=seg,
                          radius1=r1, radius2=r2, depth=d.length,
                          matrix=Matrix.LocRotScale((a + b) / 2, rot, None))

def box(bm, loc, half, rot=None):
    bmesh.ops.create_cube(bm, size=2.0, matrix=LRS(loc, rot, half))

def hair_cap(bm, loc, scale, back_z, front_z, front_y=-0.03):
    """닫힌 머리카락/투구 덩어리: 아래 정점을 끌어올려 속이 비어 보이지 않게"""
    sphere(bm, loc, scale, 20)
    for v in bm.verts:
        floor = front_z if v.co.y < loc[1] + front_y else back_z
        v.co.z = max(v.co.z, floor)


class Character:
    """스킨 모디파이어 몸 + 뼈대 + 부품 + 애니메이션을 묶어서 만드는 틀"""
    def __init__(self, name):
        self.name = name
        self.coll = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(self.coll)
        self.mats, self.parts, self.actions = {}, [], []

    def link(self, ob):
        self.coll.objects.link(ob)
        return ob

    def mat(self, key, color, metal=0.0, rough=0.5, sss=0.0, emit=0.0):
        m = bpy.data.materials.new(f"{self.name}_{key}")
        m.use_nodes = True
        m.diffuse_color = (*color, 1)
        b = m.node_tree.nodes.get("Principled BSDF")
        b.inputs["Base Color"].default_value = (*color, 1)
        b.inputs["Metallic"].default_value = metal
        b.inputs["Roughness"].default_value = rough
        if sss:
            k = "Subsurface Weight" if "Subsurface Weight" in b.inputs else "Subsurface"
            b.inputs[k].default_value = sss
        if emit:
            k = "Emission Color" if "Emission Color" in b.inputs else "Emission"
            b.inputs[k].default_value = (*color, 1)
            b.inputs["Emission Strength"].default_value = emit
        self.mats[key] = m
        return m

    def skin_body(self, joints, chains, root, body_mats, assign, subdiv=2, smoothing=0.6):
        names = list(joints)
        idx = {n: i for i, n in enumerate(names)}
        edges = [(idx[c[i]], idx[c[i+1]]) for c in chains for i in range(len(c)-1)]
        me = bpy.data.meshes.new(self.name + "Body")
        me.from_pydata([Vector(joints[n][0]) for n in names], edges, [])
        body = self.link(bpy.data.objects.new(self.name, me))
        select_only(body)
        sk = body.modifiers.new("Skin", 'SKIN')
        sk.branch_smoothing = smoothing
        sk.use_smooth_shade = True
        for i, n in enumerate(names):
            sv = me.skin_vertices[0].data[i]
            sv.radius = joints[n][1:3]
            sv.use_root = (n == root)
        sub = body.modifiers.new("Subsurf", 'SUBSURF')
        sub.levels = sub.render_levels = subdiv
        bpy.ops.object.modifier_apply(modifier="Skin")
        bpy.ops.object.modifier_apply(modifier="Subsurf")
        for k in body_mats:
            me.materials.append(self.mats[k])
        for p in me.polygons:
            p.material_index = assign(p)
            p.use_smooth = True
        self.body = body
        return body

    def rig(self, bones):
        """bones: (이름, 머리, 꼬리, 부모, 연결여부, 롤 방향)"""
        arm = bpy.data.armatures.new(self.name + "Rig")
        self.rig_ob = self.link(bpy.data.objects.new(self.name + "Rig", arm))
        self.rig_ob.show_in_front = True
        select_only(self.rig_ob)
        bpy.ops.object.mode_set(mode='EDIT')
        eb = arm.edit_bones
        for name, head, tail, parent, connect, roll in bones:
            b = eb.new(name)
            b.head, b.tail = Vector(head), Vector(tail)
            b.align_roll(roll)
            if parent:
                b.parent = eb[parent]
                b.use_connect = connect
        bpy.ops.object.mode_set(mode='OBJECT')
        select_only(self.body, self.rig_ob, active=self.rig_ob)
        bpy.ops.object.parent_set(type='ARMATURE_AUTO')
        for pb in self.rig_ob.pose.bones:
            pb.rotation_mode = 'XYZ'

    def part(self, name, mat, bone, build):
        bm = bmesh.new()
        build(bm)
        m = bpy.data.meshes.new(name)
        bm.to_mesh(m)
        bm.free()
        m.materials.append(self.mats[mat])
        for p in m.polygons:
            p.use_smooth = True
        ob = self.link(bpy.data.objects.new(name, m))
        vg = ob.vertex_groups.new(name=bone)
        vg.add(list(range(len(m.vertices))), 1.0, 'REPLACE')
        self.parts.append(ob)

    def join(self):
        select_only(self.body, *self.parts, active=self.body)
        bpy.ops.object.join()
        self.body.name = self.name

    # ---- 애니메이션 ----
    def action(self, name):
        rig = self.rig_ob
        rig.animation_data_create()
        for pb in rig.pose.bones:
            pb.rotation_euler = (0, 0, 0)
            pb.location = (0, 0, 0)
        act = bpy.data.actions.new(f"{self.name}_{name}")
        act.use_fake_user = True
        rig.animation_data.action = act
        for pb in rig.pose.bones:     # 모든 뼈를 기본자세로 먼저 기록
            pb.keyframe_insert("rotation_euler", frame=1)
            pb.keyframe_insert("location", frame=1)
        self.actions.append((name, act))
        return act

    def rot(self, bone, f, x=0.0, y=0.0, z=0.0):
        pb = self.rig_ob.pose.bones[bone]
        pb.rotation_euler = (x, y, z)
        pb.keyframe_insert("rotation_euler", frame=f)

    def loc(self, bone, f, x=0.0, y=0.0, z=0.0):
        pb = self.rig_ob.pose.bones[bone]
        pb.location = (x, y, z)
        pb.keyframe_insert("location", frame=f)

    def finish_actions(self):
        """동작마다 NLA 트랙을 만들어 둠 → FBX에서 캐릭터별로 자기 동작만 나감"""
        ad = self.rig_ob.animation_data
        for name, act in self.actions:
            tr = ad.nla_tracks.new()
            tr.name = name
            st = tr.strips.new(name, int(act.frame_range[0]), act)
            tr.mute = False
        ad.action = None
        for pb in self.rig_ob.pose.bones:
            pb.rotation_euler = (0, 0, 0)
            pb.location = (0, 0, 0)

    def export(self, folder, filename):
        path = os.path.join(folder, filename)
        select_only(self.rig_ob, self.body, active=self.rig_ob)
        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={'ARMATURE', 'MESH'},
            add_leaf_bones=False, apply_scale_options='FBX_SCALE_ALL',
            bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=True,
            bake_anim_force_startend_keying=True)
        print("FBX 저장:", path)


# ================= 사람 공통 (이장, 경비병) =================
def human_joints(over=None):
    J = {
        "pelvis": ((0, 0, 0.98), 0.150, 0.100), "spine": ((0, 0, 1.12), 0.135, 0.090),
        "chest": ((0, 0, 1.30), 0.165, 0.105), "upchest": ((0, 0, 1.45), 0.120, 0.085),
        "neck": ((0, -0.005, 1.54), 0.050, 0.050), "jaw": ((0, -0.010, 1.62), 0.075, 0.085),
        "crown": ((0, 0, 1.73), 0.085, 0.095),
    }
    for s, n in ((1, "L"), (-1, "R")):
        J[f"shoulder.{n}"] = ((s*0.19, 0, 1.43), 0.055, 0.055)
        J[f"elbow.{n}"] = ((s*0.25, 0.01, 1.16), 0.042, 0.042)
        J[f"wrist.{n}"] = ((s*0.29, -0.01, 0.92), 0.032, 0.026)
        J[f"hand.{n}"] = ((s*0.30, -0.02, 0.82), 0.035, 0.013)
        J[f"hip.{n}"] = ((s*0.09, 0, 0.93), 0.075, 0.075)
        J[f"knee.{n}"] = ((s*0.10, -0.01, 0.52), 0.052, 0.052)
        J[f"ankle.{n}"] = ((s*0.10, 0.01, 0.09), 0.038, 0.038)
        J[f"toe.{n}"] = ((s*0.10, -0.12, 0.03), 0.040, 0.022)
    J.update(over or {})
    return J

HUMAN_CHAINS = [["pelvis", "spine", "chest", "upchest", "neck", "jaw", "crown"]]
for _n in ("L", "R"):
    HUMAN_CHAINS.append(["upchest", f"shoulder.{_n}", f"elbow.{_n}", f"wrist.{_n}", f"hand.{_n}"])
    HUMAN_CHAINS.append(["pelvis", f"hip.{_n}", f"knee.{_n}", f"ankle.{_n}", f"toe.{_n}"])

def human_bones(J):
    P = lambda n: Vector(J[n][0])
    neck_top = P("neck").lerp(P("jaw"), 0.5)
    B = [("Hips", (0, 0, 0.93), P("spine"), None, False, FWD),
         ("Spine", P("spine"), P("chest"), "Hips", True, FWD),
         ("Chest", P("chest"), P("upchest"), "Spine", True, FWD),
         ("Neck", P("upchest"), neck_top, "Chest", True, FWD),
         ("Head", neck_top, P("crown") + Vector((0, 0, 0.1)), "Neck", True, FWD)]
    for n in ("L", "R"):
        B += [(f"UpperArm.{n}", P(f"shoulder.{n}"), P(f"elbow.{n}"), "Chest", False, FWD),
              (f"LowerArm.{n}", P(f"elbow.{n}"), P(f"wrist.{n}"), f"UpperArm.{n}", True, FWD),
              (f"Hand.{n}", P(f"wrist.{n}"), P(f"hand.{n}") + Vector((0, 0, -0.06)), f"LowerArm.{n}", True, FWD),
              (f"UpperLeg.{n}", P(f"hip.{n}"), P(f"knee.{n}"), "Hips", False, FWD),
              (f"LowerLeg.{n}", P(f"knee.{n}"), P(f"ankle.{n}"), f"UpperLeg.{n}", True, FWD),
              (f"Foot.{n}", P(f"ankle.{n}"), P(f"toe.{n}"), f"LowerLeg.{n}", True, UP)]
    return B

def human_face(c, crown, hair_key, hair_scale=(0.086, 0.098, 0.102), hair_back=1.655, hair_front=1.74):
    """crown(정수리 관절) 기준으로 눈·코·귀·머리카락 배치. 남자 주인공과 같은 얼굴 틀"""
    cx, cy, cz = crown
    off = lambda x, y, z: (cx + x, cy + y, cz + z)
    for s, n in ((1, "L"), (-1, "R")):
        c.part(f"Ear.{n}", "skin", "Head", lambda bm, s=s: sphere(bm, off(s*0.074, 0.005, -0.05), (0.012, 0.024, 0.03), 10))
        c.part(f"Eye.{n}", "eye", "Head", lambda bm, s=s: sphere(bm, off(s*0.031, -0.074, -0.04), (0.012,)*3, 10))
    c.part("Nose", "skin", "Head", lambda bm: sphere(bm, off(0, -0.083, -0.07), (0.013, 0.02, 0.026), 10))
    if hair_key:
        dz = cz - 1.73
        c.part("Hair", hair_key, "Head", lambda bm: hair_cap(bm, off(0, 0.008, -0.025), hair_scale, hair_back + dz, hair_front + dz))

def human_walk(c, name="Walk"):
    c.action(name)
    for i in range(9):
        f, p = 1 + i*3, i/8 * 2*pi
        c.rot("UpperLeg.L", f, x=sin(p)*0.45);  c.rot("UpperLeg.R", f, x=-sin(p)*0.45)
        c.rot("LowerLeg.L", f, x=-(max(0, cos(p))*0.9 + 0.05))
        c.rot("LowerLeg.R", f, x=-(max(0, -cos(p))*0.9 + 0.05))
        c.rot("UpperArm.L", f, x=-sin(p)*0.35); c.rot("UpperArm.R", f, x=sin(p)*0.35)
        c.rot("LowerArm.L", f, x=0.25);         c.rot("LowerArm.R", f, x=0.25)
        c.rot("Spine", f, y=sin(p)*0.07)
        c.loc("Hips", f, y=-abs(sin(p))*0.025)

def human_idle(c, lean=0.0):
    c.action("Idle")
    for f, a in ((1, 0.0), (31, 1.0), (61, 0.0)):
        c.rot("Chest", f, x=lean + 0.03*a)
        c.rot("Head", f, x=-lean*0.8 - 0.02*a)
        for n in ("L", "R"):
            c.rot(f"UpperArm.{n}", f, x=0.04*a)
            c.rot(f"LowerArm.{n}", f, x=0.2)

def human_assign(cloth_upper="cloth", boots=True):
    def assign(p):
        z, x = p.center.z, abs(p.center.x)
        if z > 1.48 or (x > 0.22 and z < 1.05):
            return 0                 # 피부
        if boots and z < 0.38:
            return 2                 # 부츠
        return 1                     # 옷
    return assign



# ============================================================
#  전직관(스승 NPC) 7명 + 허수아비 — village_characters_blender.py 의 도구를 이어서 사용
#  동양 검사 / 서양 검사 / 마법사 / 암살자 / 전사 / 궁수 / 신전 사제(버퍼) / 수련장 허수아비
#  모든 캐릭터: 뼈대 + Idle / Talk 동작 (허수아비는 Idle / Hit)
# ============================================================
from math import pi, sin, cos, atan2
from mathutils import Quaternion

def lerp(a, b, t): return a + (b - a) * t

def arc_panel(bm, z0, z1, r0, r1, a0, a1, seg=18, rows=5, ctr=(0.0, 0.0), ky=0.82):
    """원통 일부를 펼친 얇은 천 모양(망토·외투). 열린 면이라 재질은 양면으로 보임"""
    vs = []
    for j in range(rows + 1):
        z = lerp(z0, z1, j / rows); r = lerp(r0, r1, j / rows)
        vs.append([bm.verts.new((ctr[0] + r * cos(lerp(a0, a1, k / seg)), ctr[1] + r * ky * sin(lerp(a0, a1, k / seg)), z)) for k in range(seg + 1)])
    for j in range(rows):
        for k in range(seg):
            bm.faces.new((vs[j][k], vs[j][k+1], vs[j+1][k+1], vs[j+1][k]))

def star(bm, cx, y, cz, r, thick=0.004, n=5):
    pts = []
    for i in range(n * 2):
        a = pi / 2 + pi * i / n; rr = r if i % 2 == 0 else r * 0.42
        pts.append((cx + rr * cos(a), cz + rr * sin(a)))
    f = [bm.verts.new((x, y - thick / 2, z)) for x, z in pts]; b = [bm.verts.new((x, y + thick / 2, z)) for x, z in pts]
    m = len(pts)
    for k in range(m): bm.faces.new((f[k], f[(k+1) % m], b[(k+1) % m], b[k]))
    bm.faces.new(f); bm.faces.new(list(reversed(b)))

def poly_tube(bm, pts, r, seg=6):
    for a, b in zip(pts, pts[1:]): cyl(bm, a, b, r, r, seg, True)
    for p in pts[1:-1]: sphere(bm, p, (r, r, r), 6)

def flat_blade(bm, x, y, z0, z1, half_w, half_t, tip=0.10):
    """z0(칼끝)~z1(밑동) 납작한 칼날. 끝이 뾰족한 4각 단면"""
    rings = [[(x + sx * w, y + sy * t, z) for sx, sy in ((1, 1), (-1, 1), (-1, -1), (1, -1))]
             for w, t, z in ((half_w, half_t, z1), (half_w, half_t, z0 + tip), (half_w * 0.35, half_t * 0.5, z0 + tip * 0.35))]
    vr = [[bm.verts.new(p) for p in r] for r in rings]
    for a, b in zip(vr, vr[1:]):
        for k in range(4): bm.faces.new((a[k], a[(k+1) % 4], b[(k+1) % 4], b[k]))
    tv = bm.verts.new((x, y, z0))
    for k in range(4): bm.faces.new((vr[-1][k], vr[-1][(k+1) % 4], tv))
    bm.faces.new(list(reversed(vr[0])))

def std_mats(c):
    c.mat("skin", (0.58, 0.37, 0.28), rough=0.6, sss=0.12); c.mat("eye", (0.02, 0.018, 0.015), rough=0.15)
    c.mat("steel", (0.62, 0.64, 0.67), metal=1.0, rough=0.3); c.mat("gold", (0.80, 0.55, 0.20), metal=1.0, rough=0.35)
    c.mat("leather", (0.20, 0.10, 0.045), rough=0.7); c.mat("wood", (0.26, 0.15, 0.07), rough=0.85)
    c.mat("iron", (0.18, 0.18, 0.20), metal=1.0, rough=0.5)

def FJ(over=None):
    """여성 체형 관절(여자 주인공과 같은 비율)"""
    d = {"pelvis": ((0, 0, 0.98), 0.162, 0.108), "spine": ((0, 0, 1.13), 0.112, 0.080), "chest": ((0, 0, 1.30), 0.142, 0.100),
         "upchest": ((0, 0, 1.44), 0.100, 0.072), "neck": ((0, -0.005, 1.53), 0.045, 0.045), "jaw": ((0, -0.010, 1.62), 0.066, 0.078),
         "crown": ((0, 0, 1.725), 0.080, 0.090)}
    for s, n in ((1, "L"), (-1, "R")):
        d.update({f"shoulder.{n}": ((s*0.168, 0, 1.42), 0.045, 0.045), f"elbow.{n}": ((s*0.225, 0.01, 1.16), 0.035, 0.035),
                  f"wrist.{n}": ((s*0.262, -0.01, 0.93), 0.026, 0.021), f"hand.{n}": ((s*0.272, -0.02, 0.84), 0.030, 0.011),
                  f"hip.{n}": ((s*0.095, 0, 0.93), 0.078, 0.078), f"knee.{n}": ((s*0.095, -0.01, 0.52), 0.046, 0.046),
                  f"ankle.{n}": ((s*0.095, 0.01, 0.09), 0.032, 0.032), f"toe.{n}": ((s*0.095, -0.11, 0.03), 0.034, 0.019)})
    d.update(over or {}); return d

def talk(c, arm='R', lean=0.05):
    """말하는 동작: 한쪽 팔로 손짓 + 고개 끄덕임 (반대쪽 팔은 무기를 쥐고 있어서 고정)"""
    c.action("Talk"); zs = -0.15 if arm == 'R' else 0.15
    for f, ua, la, hd in ((1, 0.0, 0.2, 0.0), (10, 0.75, 1.3, 0.06), (20, 0.55, 1.0, -0.03), (30, 0.8, 1.35, 0.06), (40, 0.6, 1.05, 0.0), (50, 0.0, 0.2, 0.0)):
        c.rot(f"UpperArm.{arm}", f, x=ua, z=zs if ua else 0); c.rot(f"LowerArm.{arm}", f, x=la)
        c.rot("Head", f, x=-0.04 + hd); c.rot("Chest", f, x=lean)

def idle_one_arm_still(c, lean=0.0):
    human_idle(c, lean)

def hood(bm, loc, scale, open_w=0.062, open_top=1.78, open_bot=1.60, y_front=-0.03):
    """머리를 덮는 두건: 구를 만들고 앞쪽 얼굴 부분을 뚫음"""
    sphere(bm, loc, scale, 20)
    bad = [v for v in bm.verts if v.co.y < loc[1] + y_front and abs(v.co.x - loc[0]) < open_w and open_bot < v.co.z < open_top]
    bmesh.ops.delete(bm, geom=bad, context='VERTS')

# ================= 1. 허수아비 (수련장) =================
def build_scarecrow():
    c = Character("Scarecrow")
    c.mat("burlap", (0.50, 0.38, 0.22), rough=0.95); c.mat("straw", (0.80, 0.64, 0.26), rough=0.9)
    c.mat("shirt", (0.17, 0.22, 0.33), rough=0.95); c.mat("patch", (0.45, 0.10, 0.07), rough=0.95)
    c.mat("wood", (0.27, 0.17, 0.09), rough=0.9); c.mat("rope", (0.55, 0.45, 0.28), rough=0.9)
    c.mat("dark", (0.03, 0.025, 0.02), rough=0.8); c.mat("hat", (0.16, 0.12, 0.08), rough=0.9)
    c.mat("target", (0.55, 0.05, 0.04), rough=0.6); c.mat("dirt", (0.22, 0.15, 0.09), rough=1.0)
    J = {"base": ((0, 0, 0.05), 0.07, 0.07), "mid": ((0, 0, 0.78), 0.13, 0.10), "chest": ((0, 0, 1.22), 0.17, 0.11),
         "neck": ((0, 0, 1.42), 0.05, 0.05), "armL": ((0.58, 0, 1.30), 0.055, 0.055), "armR": ((-0.58, 0, 1.30), 0.055, 0.055)}
    chains = [["base", "mid", "chest", "neck"], ["chest", "armL"], ["chest", "armR"]]
    def assign(p):
        z = p.center.z
        return 1 if z < 0.80 else (0 if abs(p.center.x) < 0.9 else 0)
    c.skin_body(J, chains, "base", ["shirt", "straw"], assign, smoothing=0.8)
    c.rig([("Base", (0, 0, 0.0), (0, 0, 0.45), None, False, FWD), ("Post", (0, 0, 0.45), (0, 0, 1.40), "Base", True, FWD),
           ("Head", (0, 0, 1.40), (0, 0, 1.88), "Post", True, FWD),
           ("Arm.L", (0, 0, 1.30), (0.64, 0, 1.30), "Post", False, FWD), ("Arm.R", (0, 0, 1.30), (-0.64, 0, 1.30), "Post", False, FWD)])
    # 기둥과 가로대, 흙더미
    c.part("Post", "wood", "Base", lambda b: cyl(b, (0, 0.0, 0.0), (0, 0.0, 1.62), 0.048, 0.042, 12))
    c.part("Mound", "dirt", "Base", lambda b: sphere(b, (0, 0, 0.0), (0.38, 0.38, 0.09), 18))
    c.part("Crossbar", "wood", "Post", lambda b: cyl(b, (-0.66, 0.0, 1.30), (0.66, 0.0, 1.30), 0.034, 0.034, 10))
    # 밧줄 묶음, 누더기, 표적
    def ropes(b):
        for z in (0.80, 1.05, 1.42): cyl(b, (0, 0, z - 0.012), (0, 0, z + 0.012), 0.19 if z < 1.2 else 0.07, 0.19 if z < 1.2 else 0.07, 18, False)
        for x in (-0.50, 0.50): cyl(b, (x, 0, 1.30 - 0.02), (x, 0, 1.30 + 0.02), 0.075, 0.075, 12, False)
    c.part("Ropes", "rope", "Post", ropes)
    c.part("Patches", "patch", "Post", lambda b: [box(b, p, h) for p, h in (((0.10, -0.115, 1.12), (0.05, 0.006, 0.06)), ((-0.12, -0.10, 0.95), (0.045, 0.006, 0.05)), ((0.30, -0.055, 1.31), (0.04, 0.045, 0.035)))])
    c.part("Target", "target", "Post", lambda b: cyl(b, (0, -0.108, 1.20), (0, -0.118, 1.20), 0.10, 0.10, 28))
    def target_rings(b):
        for r in (0.065, 0.03): cyl(b, (0, -0.119, 1.20), (0, -0.123, 1.20), r, r, 24)
    c.part("TargetRings", "burlap", "Post", target_rings)
    for i in range(1):
        c.part("Slashes", "dark", "Post", lambda b: [box(b, (x, -0.123, z), (0.06, 0.003, 0.004), Quaternion((0, 1, 0), a)) for x, z, a in ((0.05, 1.30, 0.6), (-0.08, 1.12, -0.5), (0.0, 1.04, 0.9))])
    # 머리 자루(마대), 실로 꿰맨 입, 단추 눈, 코
    cz = 1.62
    c.part("Head", "burlap", "Head", lambda b: sphere(b, (0, 0, cz), (0.135, 0.125, 0.16), 22))
    c.part("Neck", "rope", "Head", lambda b: cyl(b, (0, 0, 1.46), (0, 0, 1.50), 0.085, 0.085, 16, False))
    c.part("Eyes", "dark", "Head", lambda b: [sphere(b, (0.05, -0.118, cz + 0.035), (0.017,) * 3, 10), sphere(b, (-0.05, -0.115, cz + 0.045), (0.024,) * 3, 10)])
    c.part("Nose", "burlap", "Head", lambda b: cyl(b, (0, -0.118, cz - 0.01), (0, -0.165, cz - 0.03), 0.026, 0.0, 6))
    def mouth(b):
        for i in range(7): box(b, (-0.06 + i * 0.02, -0.122, cz - 0.075 + (0.008 if i % 2 else 0)), (0.0035, 0.004, 0.018 if i % 2 else 0.012))
        box(b, (0, -0.122, cz - 0.075), (0.065, 0.003, 0.003))
    c.part("Mouth", "dark", "Head", mouth)
    # 낡은 모자, 삐져나온 지푸라기
    c.part("HatBrim", "hat", "Head", lambda b: cyl(b, (0, 0, cz + 0.115), (0, 0.0, cz + 0.13), 0.27, 0.27, 28))
    c.part("HatCrown", "hat", "Head", lambda b: cyl(b, (0, 0.0, cz + 0.12), (0.03, 0.02, cz + 0.30), 0.13, 0.085, 22))
    def straw(b):
        import random as _r; _r.seed(4)
        for i in range(34):
            a = 2 * pi * i / 34; h = _r.uniform(0.07, 0.15)
            cyl(b, (0.13 * cos(a), 0.12 * sin(a), cz + 0.10), (0.20 * cos(a), 0.18 * sin(a), cz + 0.10 - h), 0.007, 0.0, 4)
        for sx in (-1, 1):
            for i in range(14):
                a = _r.uniform(-0.5, 0.5)
                cyl(b, (sx * 0.64, 0, 1.30), (sx * (0.74 + _r.uniform(0, 0.08)), _r.uniform(-0.05, 0.05), 1.30 + _r.uniform(-0.1, 0.1)), 0.007, 0.0, 4)
        for i in range(26):
            a = 2 * pi * i / 26
            cyl(b, (0.13 * cos(a), 0.12 * sin(a), 0.80), (0.19 * cos(a), 0.18 * sin(a), 0.62 + _r.uniform(-0.05, 0.05)), 0.008, 0.0, 4)
    c.part("StrawTufts", "straw", "Head", straw)
    c.join()
    c.action("Idle")
    for f, a in ((1, 0.0), (45, 1.0), (90, 0.0)):
        c.rot("Post", f, x=0.015 * a, z=0.02 * a); c.rot("Head", f, x=-0.02 * a, z=-0.03 * a)
        c.rot("Arm.L", f, x=0.03 * a); c.rot("Arm.R", f, x=-0.03 * a)
    c.action("Hit")      # 맞으면 뒤로 크게 젖혔다가 감쇠하며 흔들림 (24프레임 이후 정지)
    for f, k in ((1, 0.0), (4, -0.34), (9, 0.22), (14, -0.12), (19, 0.06), (24, 0.0)):
        c.rot("Post", f, x=k, z=0.4 * k); c.rot("Head", f, x=k * 0.8, z=-0.6 * k); c.rot("Arm.L", f, x=-k * 1.3, z=-0.5 * k); c.rot("Arm.R", f, x=-k * 1.3, z=0.5 * k)
    c.finish_actions(); return c

# ================= 2. 동양 검사 전직관 =================
def build_east_master():
    c = Character("MasterEastSword"); std_mats(c)
    c.mat("robe", (0.05, 0.08, 0.17), rough=0.9); c.mat("hakama", (0.12, 0.12, 0.14), rough=0.9); c.mat("white", (0.80, 0.78, 0.72), rough=0.85)
    c.mat("obi", (0.28, 0.04, 0.04), rough=0.8); c.mat("lacquer", (0.015, 0.015, 0.02), rough=0.25); c.mat("hair", (0.62, 0.62, 0.64), rough=0.7)
    c.mat("geta", (0.20, 0.12, 0.06), rough=0.8)
    J = human_joints({"spine": ((0, -0.01, 1.12), 0.14, 0.105), "upchest": ((0, 0.015, 1.44), 0.115, 0.085), "neck": ((0, -0.01, 1.52), 0.05, 0.05),
                      "jaw": ((0, -0.03, 1.60), 0.074, 0.084), "crown": ((0, -0.025, 1.715), 0.084, 0.094)})
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "robe", "geta"], human_assign())
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, "hair", hair_scale=(0.088, 0.1, 0.09), hair_back=1.63, hair_front=1.75)
    c.part("TopKnot", "hair", "Head", lambda b: (sphere(b, (0, cy + 0.02, cz + 0.085), (0.034, 0.034, 0.04), 12), cyl(b, (0, cy + 0.02, cz + 0.03), (0, cy + 0.02, cz + 0.07), 0.018, 0.018, 8)))
    c.part("Goatee", "hair", "Head", lambda b: cyl(b, (0, cy - 0.065, cz - 0.11), (0, cy - 0.075, cz - 0.27), 0.032, 0.006, 10))
    c.part("Brows", "hair", "Head", lambda b: [box(b, (s * 0.032, cy - 0.083, cz - 0.015), (0.024, 0.008, 0.007)) for s in (-1, 1)])
    c.part("Hakama", "hakama", "Hips", lambda b: cyl(b, (0, 0, 0.05), (0, -0.005, 1.02), 0.25, 0.17, 30, False))
    c.part("Kimono", "robe", "Chest", lambda b: cyl(b, (0, 0, 0.98), (0, -0.003, 1.50), 0.170, 0.185, 28, False))
    c.part("Collar", "white", "Chest", lambda b: [box(b, (s * 0.04, -0.19, 1.43), (0.012, 0.01, 0.085), Quaternion((0, 1, 0), s * 0.5)) for s in (-1, 1)])
    for s, n in ((1, "L"), (-1, "R")):
        sh, wr = Vector(J[f"shoulder.{n}"][0]), Vector(J[f"wrist.{n}"][0])
        c.part(f"Sleeve.{n}", "robe", f"UpperArm.{n}", lambda b, sh=sh, wr=wr: cyl(b, sh + Vector((0, 0, 0.01)), wr + Vector((s * 0.02, 0, -0.05)), 0.075, 0.125, 16, False))
        c.part(f"Cuff.{n}", "white", f"LowerArm.{n}", lambda b, wr=wr, s=s: cyl(b, wr + Vector((s * 0.02, 0, -0.05)), wr + Vector((s * 0.02, 0, -0.06)), 0.126, 0.126, 16, False))
    c.part("Obi", "obi", "Hips", lambda b: cyl(b, (0, 0, 0.97), (0, 0, 1.07), 0.178, 0.176, 28, False))
    c.part("ObiKnot", "obi", "Hips", lambda b: box(b, (0, 0.20, 1.02), (0.06, 0.03, 0.05)))
    # 허리에 찬 도(刀): 흰 끈 손잡이, 코등이, 검은 칠 칼집, 금 끝장식
    H, T, S = Vector((0.20, -0.30, 1.05)), Vector((0.205, -0.03, 1.02)), Vector((0.255, 0.64, 0.84))
    c.part("Tsuka", "white", "Hips", lambda b: cyl(b, H, T, 0.015, 0.015, 10))
    c.part("TsukaWrap", "lacquer", "Hips", lambda b: [cyl(b, H.lerp(T, i / 9 + 0.02), H.lerp(T, i / 9 + 0.06), 0.0165, 0.0165, 10) for i in range(0, 9, 2)])
    c.part("Tsuba", "iron", "Hips", lambda b: cyl(b, T - Vector((0, 0.004, 0)), T + Vector((0, 0.006, 0)), 0.034, 0.034, 20))
    c.part("Saya", "lacquer", "Hips", lambda b: cyl(b, T, S, 0.017, 0.013, 12))
    c.part("Kojiri", "gold", "Hips", lambda b: (cyl(b, S - Vector((0, 0.02, 0.003)), S + Vector((0, 0.012, 0)), 0.0135, 0.0135, 12), cyl(b, T + Vector((0, 0.0, 0)), T + Vector((0, 0.03, 0)), 0.0185, 0.0185, 12)))
    c.join(); human_idle(c, lean=0.04); talk(c, 'R', 0.04); c.finish_actions(); return c

# ================= 3. 서양 검사 전직관 =================
def build_west_master():
    c = Character("MasterWestSword"); std_mats(c)
    c.mat("mail", (0.38, 0.40, 0.43), metal=1.0, rough=0.5); c.mat("blue", (0.05, 0.12, 0.38), rough=0.8); c.mat("white", (0.80, 0.78, 0.72), rough=0.8)
    c.mat("hair", (0.20, 0.12, 0.07), rough=0.7); c.mat("pants", (0.10, 0.10, 0.13), rough=0.9); c.mat("mana", (0.25, 0.58, 1.0), emit=4.0, rough=0.2)
    J = human_joints({"chest": ((0, 0, 1.30), 0.18, 0.115)})
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "pants", "leather"], human_assign())
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, None)
    c.part("Coif", "mail", "Head", lambda b: hood(b, (0, 0.008, 1.69), (0.108, 0.116, 0.118), 0.066, 1.76, 1.60))
    c.part("Stubble", "hair", "Head", lambda b: sphere(b, (0, cy - 0.045, cz - 0.115), (0.060, 0.045, 0.046), 12))
    c.part("MailShirt", "mail", "Chest", lambda b: cyl(b, (0, 0, 0.78), (0, 0, 1.49), 0.19, 0.20, 28, False))
    for s, n in ((1, "L"), (-1, "R")):
        sh, el = Vector(J[f"shoulder.{n}"][0]), Vector(J[f"elbow.{n}"][0])
        c.part(f"MailSleeve.{n}", "mail", f"UpperArm.{n}", lambda b, sh=sh, el=el: cyl(b, sh, el, 0.066, 0.058, 14, False))
        wr = Vector(J[f"wrist.{n}"][0])
        c.part(f"Vambrace.{n}", "steel", f"LowerArm.{n}", lambda b, el=el, wr=wr: cyl(b, el.lerp(wr, 0.32), wr.lerp(el, 0.02), 0.048, 0.040, 12))
        x = s * 0.10
        c.part(f"Greave.{n}", "steel", f"LowerLeg.{n}", lambda b, x=x: cyl(b, (x, -0.01, 0.12), (x, -0.012, 0.44), 0.056, 0.058, 12))
    c.part("Tabard", "blue", "Chest", lambda b: (box(b, (0, -0.20, 1.02), (0.15, 0.007, 0.40)), box(b, (0, 0.20, 1.02), (0.15, 0.007, 0.40))))
    c.part("Emblem", "white", "Chest", lambda b: (box(b, (0, -0.209, 1.20), (0.012, 0.004, 0.11)), box(b, (0, -0.209, 1.27), (0.055, 0.004, 0.012)), sphere(b, (0, -0.209, 1.06), (0.02, 0.005, 0.02), 8)))
    c.part("Belt", "leather", "Hips", lambda b: cyl(b, (0, 0, 0.96), (0, 0, 1.02), 0.20, 0.198, 26, False))
    c.part("Buckle", "gold", "Hips", lambda b: box(b, (0, -0.205, 0.99), (0.03, 0.008, 0.026)))
    c.part("Pauldron", "steel", "UpperArm.L", lambda b: hair_cap(b, (0.215, 0, 1.45), (0.09, 0.095, 0.075), 1.41, 1.41, front_y=-1))
    c.part("Gorget", "steel", "Chest", lambda b: cyl(b, (0, 0, 1.47), (0, 0, 1.53), 0.105, 0.085, 20, False))
    c.part("Mantle", "blue", "Chest", lambda b: arc_panel(b, 1.50, 0.82, 0.20, 0.24, pi * 0.12, pi * 0.88, 14, 4, (0, 0.0)))
    c.part("MantleTrim", "gold", "Chest", lambda b: arc_panel(b, 1.50, 1.48, 0.202, 0.203, pi * 0.12, pi * 0.88, 14, 1, (0, 0.0)))
    # 왼손 옆에 칼끝을 땅에 꽂은 장검 (손이 손잡이를 쥠)
    sx, sy = J["hand.L"][0][0] + 0.005, J["hand.L"][0][1] - 0.01
    c.part("Blade", "steel", "Hips", lambda b: flat_blade(b, sx, sy, 0.0, 0.86, 0.026, 0.0045, 0.16))
    c.part("Fuller", "mana", "Hips", lambda b: box(b, (sx, sy - 0.0048, 0.50), (0.0035, 0.0012, 0.30)))
    c.part("Guard", "iron", "Hips", lambda b: (cyl(b, (sx - 0.105, sy, 0.865), (sx + 0.105, sy, 0.865), 0.0095, 0.0095, 10), sphere(b, (sx - 0.108, sy, 0.865), (0.0125,) * 3, 8), sphere(b, (sx + 0.108, sy, 0.865), (0.0125,) * 3, 8)))
    c.part("Grip", "leather", "Hips", lambda b: cyl(b, (sx, sy, 0.875), (sx, sy, 1.02), 0.0145, 0.0145, 12))
    c.part("Pommel", "steel", "Hips", lambda b: sphere(b, (sx, sy, 1.04), (0.026,) * 3, 12))
    c.part("PommelGem", "mana", "Hips", lambda b: sphere(b, (sx, sy - 0.022, 1.04), (0.012, 0.006, 0.012), 8))
    c.join(); human_idle(c); talk(c, 'R'); c.finish_actions(); return c

# ================= 4. 마법사 전직관 =================
def build_mage_master():
    c = Character("MasterMage"); std_mats(c)
    c.mat("robe", (0.13, 0.07, 0.30), rough=0.85); c.mat("robe2", (0.30, 0.12, 0.45), rough=0.85); c.mat("white", (0.82, 0.82, 0.84), rough=0.85)
    c.mat("crystal", (0.55, 0.80, 1.0), emit=3.0, rough=0.05); c.mat("book", (0.28, 0.06, 0.06), rough=0.7)
    J = human_joints()
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "robe", "leather"], human_assign())
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, "white", hair_scale=(0.09, 0.105, 0.095), hair_back=1.62, hair_front=1.74)
    c.part("Beard", "white", "Head", lambda b: (cyl(b, (0, cy - 0.06, cz - 0.10), (0, cy - 0.10, cz - 0.52), 0.07, 0.008, 14), cyl(b, (0, cy - 0.075, cz - 0.075), (0, cy - 0.085, cz - 0.09), 0.05, 0.05, 12)))
    c.part("Mustache", "white", "Head", lambda b: [sphere(b, (s * 0.03, cy - 0.082, cz - 0.098), (0.034, 0.014, 0.012), 10) for s in (-1, 1)])
    c.part("Brows", "white", "Head", lambda b: [box(b, (s * 0.032, cy - 0.083, cz - 0.015), (0.026, 0.008, 0.008)) for s in (-1, 1)])
    c.part("Robe", "robe", "Hips", lambda b: cyl(b, (0, 0, 0.02), (0, -0.005, 1.46), 0.31, 0.165, 32, False))
    c.part("RobeHem", "robe2", "Hips", lambda b: cyl(b, (0, 0, 0.02), (0, 0, 0.09), 0.312, 0.30, 32, False))
    for s, n in ((1, "L"), (-1, "R")):
        sh, wr = Vector(J[f"shoulder.{n}"][0]), Vector(J[f"wrist.{n}"][0])
        c.part(f"Sleeve.{n}", "robe", f"UpperArm.{n}", lambda b, sh=sh, wr=wr, s=s: cyl(b, sh, wr + Vector((s * 0.01, 0, -0.05)), 0.07, 0.14, 16, False))
        c.part(f"Cuff.{n}", "robe2", f"LowerArm.{n}", lambda b, wr=wr, s=s: cyl(b, wr + Vector((s * 0.01, 0, -0.05)), wr + Vector((s * 0.01, 0, -0.075)), 0.143, 0.143, 16, False))
    c.part("Mantle", "robe2", "Chest", lambda b: cyl(b, (0, 0, 1.36), (0, 0, 1.50), 0.235, 0.12, 26, False))
    c.part("Rope", "gold", "Hips", lambda b: cyl(b, (0, 0, 0.96), (0, 0, 1.0), 0.18, 0.18, 26, False))
    c.part("Tassel", "gold", "Hips", lambda b: (cyl(b, (0.10, -0.18, 0.99), (0.10, -0.19, 0.72), 0.006, 0.006, 6), cyl(b, (0.10, -0.19, 0.78), (0.10, -0.19, 0.66), 0.022, 0.012, 8)))
    c.part("Stars", "gold", "Hips", lambda b: [star(b, x, -0.298 - 0.0 * x, z, 0.04) if False else star(b, x, -y, z, 0.045) for x, y, z in ((-0.12, 0.275, 0.45), (0.10, 0.285, 0.30), (0.0, 0.265, 0.62), (-0.15, 0.27, 0.22), (0.17, 0.27, 0.52))])
    # 별 모양 장식이 있는 뾰족 모자
    c.part("HatBrim", "robe", "Head", lambda b: cyl(b, (0, cy, cz + 0.045), (0, cy, cz + 0.06), 0.26, 0.26, 30))
    c.part("HatLower", "robe", "Head", lambda b: cyl(b, (0, cy, cz + 0.055), (0, cy + 0.01, cz + 0.30), 0.125, 0.075, 24))
    c.part("HatUpper", "robe", "Head", lambda b: cyl(b, (0, cy + 0.01, cz + 0.29), (0, cy + 0.10, cz + 0.52), 0.078, 0.0, 20))
    c.part("HatBand", "robe2", "Head", lambda b: cyl(b, (0, cy, cz + 0.06), (0, cy, cz + 0.095), 0.128, 0.126, 24, False))
    c.part("HatStar", "gold", "Head", lambda b: star(b, 0, cy - 0.128, cz + 0.20, 0.04))
    # 오른손에 쥔 지팡이 (끝에 수정)
    x, y = J["hand.R"][0][0] - 0.005, J["hand.R"][0][1]
    c.part("Staff", "wood", "Hips", lambda b: cyl(b, (x, y, 0.0), (x, y, 1.98), 0.019, 0.024, 10))
    c.part("StaffTop", "gold", "Hips", lambda b: (torus_ring(b, (x, y, 2.02), 0.05, 0.007), sphere(b, (x, y, 1.98), (0.03, 0.03, 0.02), 10)))
    c.part("StaffCrystal", "crystal", "Hips", lambda b: sphere(b, (x, y, 2.03), (0.028, 0.028, 0.055), 10))
    c.part("Book", "book", "Hips", lambda b: box(b, (0.21, -0.13, 0.93), (0.085, 0.03, 0.11), Quaternion((0, 0, 1), 0.2)))
    c.part("BookStrap", "gold", "Hips", lambda b: box(b, (0.21, -0.16, 0.93), (0.087, 0.004, 0.012), Quaternion((0, 0, 1), 0.2)))
    c.join(); human_idle(c, 0.03); talk(c, 'L', 0.03); c.finish_actions(); return c

def torus_ring(bm, c0, R, r, seg=24, rseg=8):
    """x-z 평면의 고리"""
    cx, cy, cz = c0; rings = []
    for i in range(seg):
        a = 2 * pi * i / seg; ring = []
        for j in range(rseg):
            t = 2 * pi * j / rseg; rr = R + r * cos(t)
            ring.append(bm.verts.new((cx + rr * cos(a), cy + r * sin(t), cz + rr * sin(a))))
        rings.append(ring)
    for i in range(seg):
        a0, a1 = rings[i], rings[(i + 1) % seg]
        for j in range(rseg): bm.faces.new((a0[j], a1[j], a1[(j+1) % rseg], a0[(j+1) % rseg]))

# ================= 5. 암살자 전직관 =================
def build_assassin_master():
    c = Character("MasterAssassin"); std_mats(c)
    c.mat("cloak", (0.035, 0.035, 0.045), rough=0.9); c.mat("cloth", (0.06, 0.06, 0.075), rough=0.9); c.mat("scarf", (0.35, 0.03, 0.03), rough=0.9)
    c.mat("dark", (0.05, 0.05, 0.06), metal=1.0, rough=0.35); c.mat("wrap", (0.10, 0.09, 0.08), rough=0.9)
    J = human_joints({"chest": ((0, -0.01, 1.30), 0.15, 0.10), "upchest": ((0, -0.015, 1.44), 0.11, 0.08), "neck": ((0, -0.04, 1.52), 0.05, 0.05),
                      "jaw": ((0, -0.07, 1.60), 0.074, 0.082), "crown": ((0, -0.06, 1.70), 0.084, 0.094)})
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "cloth", "wrap"], human_assign())
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, None)
    c.part("Hood", "cloak", "Head", lambda b: hood(b, (0, cy + 0.025, cz - 0.03), (0.125, 0.14, 0.14), 0.060, cz + 0.03, cz - 0.12, -0.02))
    c.part("Mask", "cloth", "Head", lambda b: hood(b, (0, cy - 0.01, cz - 0.10), (0.086, 0.090, 0.075), 0.0, 9, 9) if False else sphere(b, (0, cy - 0.025, cz - 0.098), (0.083, 0.082, 0.062), 16))
    c.part("Scarf", "scarf", "Chest", lambda b: (cyl(b, (0, -0.04, 1.455), (0, -0.04, 1.52), 0.115, 0.095, 20, False), box(b, (0.07, -0.12, 1.30), (0.03, 0.006, 0.15), Quaternion((0, 1, 0), 0.2))))
    c.part("Cloak", "cloak", "Chest", lambda b: arc_panel(b, 1.48, 0.38, 0.17, 0.30, -pi / 2 + 0.7, -pi / 2 + 2 * pi - 0.7, 20, 7, (0, -0.01)))
    c.part("CloakShoulder", "cloak", "Chest", lambda b: cyl(b, (0, -0.01, 1.40), (0, -0.01, 1.50), 0.23, 0.12, 22, False))
    for s, n in ((1, "L"), (-1, "R")):
        el, wr = Vector(J[f"elbow.{n}"][0]), Vector(J[f"wrist.{n}"][0])
        c.part(f"Wrap.{n}", "wrap", f"LowerArm.{n}", lambda b, el=el, wr=wr: cyl(b, el.lerp(wr, 0.18), wr.lerp(el, -0.03), 0.047, 0.04, 12))
        x = s * 0.10
        c.part(f"Boot.{n}", "wrap", f"LowerLeg.{n}", lambda b, x=x: cyl(b, (x, 0.0, 0.10), (x, -0.005, 0.42), 0.058, 0.064, 12))
    def harness(b):
        for s in (-1, 1): box(b, (0, -0.178, 1.28), (0.14, 0.006, 0.014), Quaternion((0, 1, 0), s * 0.72))
        for i in range(5): box(b, (-0.075 + i * 0.037, -0.19, 1.19 + i * 0.037), (0.008, 0.005, 0.034), Quaternion((0, 1, 0), -0.72))
    c.part("Harness", "leather", "Chest", harness)
    c.part("Knives", "dark", "Chest", lambda b: [flat_blade(b, -0.075 + i * 0.037, -0.198, 1.14 + i * 0.037, 1.25 + i * 0.037, 0.007, 0.002, 0.03) for i in range(5)])
    c.part("Belt", "leather", "Hips", lambda b: cyl(b, (0, 0, 0.95), (0, 0, 1.01), 0.165, 0.163, 24, False))
    c.part("Buckle", "iron", "Hips", lambda b: box(b, (0, -0.17, 0.98), (0.022, 0.006, 0.02)))
    # 허리 뒤쪽에 거꾸로 꽂은 단검 두 자루 + 한 자루는 허벅지
    def daggers(b):
        for s in (-1, 1):
            box(b, (s * 0.10, 0.21, 0.99), (0.02, 0.012, 0.013)); cyl(b, (s * 0.10, 0.215, 0.99), (s * 0.20, 0.215, 0.99), 0.012, 0.012, 8)
            sphere(b, (s * 0.205, 0.215, 0.99), (0.016,) * 3, 8)
    c.part("Daggers", "dark", "Hips", daggers)
    c.part("DaggerGrip", "scarf", "Hips", lambda b: [cyl(b, (s * 0.115, 0.215, 0.99), (s * 0.195, 0.215, 0.99), 0.0135, 0.0135, 8) for s in (-1, 1)])
    c.join(); human_idle(c, 0.07); talk(c, 'R', 0.07); c.finish_actions(); return c

# ================= 6. 전사 전직관 =================
def build_warrior_master():
    c = Character("MasterWarrior"); std_mats(c)
    c.mat("plate", (0.40, 0.41, 0.44), metal=1.0, rough=0.45); c.mat("cloth", (0.25, 0.07, 0.05), rough=0.9); c.mat("pants", (0.12, 0.10, 0.09), rough=0.9)
    c.mat("fur", (0.38, 0.35, 0.30), rough=1.0); c.mat("hair", (0.18, 0.10, 0.06), rough=0.8); c.mat("scar", (0.45, 0.22, 0.18), rough=0.7)
    c.mat("shieldp", (0.30, 0.05, 0.04), rough=0.6)
    O = {"chest": ((0, 0, 1.30), 0.225, 0.14), "upchest": ((0, 0, 1.45), 0.15, 0.10), "spine": ((0, 0, 1.12), 0.18, 0.12), "pelvis": ((0, 0, 0.98), 0.18, 0.12),
         "neck": ((0, -0.005, 1.54), 0.07, 0.07), "jaw": ((0, -0.01, 1.62), 0.092, 0.098), "crown": ((0, 0, 1.735), 0.100, 0.108)}
    for s, n in ((1, "L"), (-1, "R")):
        O.update({f"shoulder.{n}": ((s*0.255, 0, 1.43), 0.07, 0.07), f"elbow.{n}": ((s*0.31, 0.01, 1.16), 0.056, 0.056), f"wrist.{n}": ((s*0.345, -0.01, 0.92), 0.042, 0.036),
                  f"hand.{n}": ((s*0.355, -0.02, 0.82), 0.045, 0.02), f"hip.{n}": ((s*0.115, 0, 0.93), 0.09, 0.09), f"knee.{n}": ((s*0.12, -0.01, 0.52), 0.066, 0.066),
                  f"ankle.{n}": ((s*0.12, 0.01, 0.09), 0.046, 0.046), f"toe.{n}": ((s*0.12, -0.13, 0.03), 0.05, 0.028)})
    J = human_joints(O)
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "cloth", "leather"], lambda p: 0 if (p.center.z > 1.50 or (abs(p.center.x) > 0.30 and p.center.z < 1.05)) else (2 if p.center.z < 0.38 else 1))
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, None)
    c.part("Beard", "hair", "Head", lambda b: (sphere(b, (0, cy - 0.04, cz - 0.12), (0.085, 0.06, 0.07), 14), cyl(b, (-0.03, cy - 0.07, cz - 0.15), (-0.03, cy - 0.09, cz - 0.30), 0.016, 0.01, 8), cyl(b, (0.03, cy - 0.07, cz - 0.15), (0.03, cy - 0.09, cz - 0.30), 0.016, 0.01, 8)))
    c.part("Brows", "hair", "Head", lambda b: [box(b, (s * 0.034, cy - 0.086, cz - 0.012), (0.026, 0.009, 0.01)) for s in (-1, 1)])
    c.part("Scar", "scar", "Head", lambda b: box(b, (-0.03, cy - 0.088, cz - 0.045), (0.004, 0.003, 0.04), Quaternion((0, 1, 0), 0.4)))
    c.part("Breastplate", "plate", "Chest", lambda b: sphere(b, (0, -0.008, 1.31), (0.215, 0.145, 0.175), 22))
    c.part("Fauld", "plate", "Hips", lambda b: [cyl(b, (0, 0, 0.84 + i * 0.045), (0, 0, 0.88 + i * 0.045), 0.215 - i * 0.015, 0.215 - i * 0.015, 26, False) for i in range(3)])
    c.part("Tassets", "plate", "Hips", lambda b: [box(b, (s * 0.14, -0.16, 0.72), (0.075, 0.01, 0.11), Quaternion((0, 1, 0), s * 0.15)) for s in (-1, 1)])
    c.part("Pants", "pants", "Hips", lambda b: cyl(b, (0, 0, 0.40), (0, 0, 0.90), 0.205, 0.215, 24, False))
    for s, n in ((1, "L"), (-1, "R")):
        c.part(f"Pauldron.{n}", "plate", f"UpperArm.{n}", lambda b, s=s: hair_cap(b, (s * 0.265, 0, 1.44), (0.10, 0.10, 0.085), 1.40, 1.40, front_y=-1))
        c.part(f"PaulSpike.{n}", "plate", f"UpperArm.{n}", lambda b, s=s: cyl(b, (s * 0.29, 0, 1.49), (s * 0.31, 0, 1.58), 0.026, 0.0, 8))
        el, wr = Vector(J[f"elbow.{n}"][0]), Vector(J[f"wrist.{n}"][0])
        c.part(f"Gauntlet.{n}", "plate", f"LowerArm.{n}", lambda b, el=el, wr=wr: cyl(b, el.lerp(wr, 0.25), wr.lerp(el, -0.1), 0.066, 0.056, 12))
        x = s * 0.12
        c.part(f"Greave.{n}", "plate", f"LowerLeg.{n}", lambda b, x=x: cyl(b, (x, -0.01, 0.12), (x, -0.015, 0.46), 0.075, 0.078, 12))
        c.part(f"Knee.{n}", "plate", f"LowerLeg.{n}", lambda b, x=x: sphere(b, (x, -0.06, 0.52), (0.06, 0.04, 0.06), 10))
    c.part("FurCollar", "fur", "Chest", lambda b: [sphere(b, (0.14 * cos(a), 0.11 * sin(a), 1.47), (0.055, 0.05, 0.045), 8) for a in [2 * pi * i / 12 for i in range(12)]])
    c.part("Belt", "leather", "Hips", lambda b: cyl(b, (0, 0, 0.93), (0, 0, 1.0), 0.205, 0.203, 26, False))
    c.part("Buckle", "gold", "Hips", lambda b: box(b, (0, -0.21, 0.965), (0.04, 0.01, 0.032)))
    # 왼팔에 방패, 오른쪽 허리에 메이스
    el, wr = Vector(J["elbow.L"][0]), Vector(J["wrist.L"][0]); sc = el.lerp(wr, 0.5) + Vector((0.075, -0.03, 0))
    c.part("Shield", "shieldp", "LowerArm.L", lambda b: cyl(b, sc, sc + Vector((0.03, 0, 0)), 0.30, 0.30, 32))
    c.part("ShieldRim", "plate", "LowerArm.L", lambda b: cyl(b, sc - Vector((0.003, 0, 0)), sc + Vector((0.036, 0, 0)), 0.318, 0.318, 32, False))
    c.part("ShieldBoss", "gold", "LowerArm.L", lambda b: sphere(b, sc + Vector((0.04, 0, 0)), (0.04, 0.075, 0.075), 12))
    mx, my = -0.235, 0.05
    c.part("MaceHaft", "wood", "Hips", lambda b: cyl(b, (mx, my, 1.02), (mx - 0.04, my + 0.12, 0.50), 0.017, 0.017, 8))
    c.part("MaceHead", "iron", "Hips", lambda b: (sphere(b, (mx - 0.045, my + 0.14, 0.46), (0.05, 0.05, 0.06), 10), [cyl(b, (mx - 0.045 + 0.04 * cos(a), my + 0.14 + 0.04 * sin(a), 0.46), (mx - 0.045 + 0.075 * cos(a), my + 0.14 + 0.075 * sin(a), 0.46), 0.014, 0.003, 4) for a in [2 * pi * i / 6 for i in range(6)]]))
    c.join(); human_idle(c, 0.02); talk(c, 'R', 0.03); c.finish_actions(); return c

# ================= 7. 궁수 전직관 (여성) =================
def build_archer_master():
    c = Character("MasterArcher"); std_mats(c)
    c.mat("green", (0.07, 0.17, 0.09), rough=0.9); c.mat("pants", (0.10, 0.12, 0.08), rough=0.9); c.mat("vest", (0.26, 0.15, 0.07), rough=0.75)
    c.mat("hair", (0.30, 0.15, 0.06), rough=0.7); c.mat("feather", (0.65, 0.08, 0.06), rough=0.9); c.mat("cloth", (0.60, 0.55, 0.42), rough=0.9)
    c.mat("string", (0.80, 0.76, 0.62), rough=0.8); c.mat("shaft", (0.55, 0.40, 0.22), rough=0.7)
    c.mat("skin", (0.80, 0.55, 0.43), rough=0.55, sss=0.15)
    J = human_joints(FJ())
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "pants", "leather"], human_assign())
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, "hair", hair_scale=(0.088, 0.10, 0.10), hair_back=1.64, hair_front=1.735)
    c.part("Ponytail", "hair", "Head", lambda b: poly_tube(b, [(0, 0.075, 1.72), (0, 0.15, 1.66), (0.0, 0.19, 1.54), (0.02, 0.19, 1.42)], 0.03, 8))
    c.part("Cap", "green", "Head", lambda b: hair_cap(b, (0, 0.012, 1.71), (0.094, 0.104, 0.092), 1.668, 1.745))
    c.part("CapBrim", "green", "Head", lambda b: cyl(b, (0, -0.06, 1.745), (0, -0.14, 1.735), 0.065, 0.045, 14))
    c.part("Feather", "feather", "Head", lambda b: (poly_tube(b, [(0.07, 0.0, 1.75), (0.11, 0.03, 1.84), (0.10, 0.09, 1.93)], 0.006, 5), sphere(b, (0.095, 0.045, 1.865), (0.014, 0.05, 0.075), 8)))
    c.part("Scarf", "cloth", "Chest", lambda b: cyl(b, (0, -0.005, 1.455), (0, -0.005, 1.53), 0.092, 0.075, 18, False))
    c.part("Vest", "vest", "Chest", lambda b: cyl(b, (0, 0, 1.00), (0, 0, 1.46), 0.148, 0.130, 24, False))
    c.part("VestLace", "cloth", "Chest", lambda b: [box(b, (0, -0.152 + 0.0, 1.06 + i * 0.07), (0.03, 0.003, 0.004), Quaternion((0, 1, 0), 0.5 * (-1) ** i)) for i in range(6)])
    c.part("Skirt", "green", "Hips", lambda b: cyl(b, (0, 0, 0.66), (0, 0, 1.02), 0.215, 0.165, 26, False))
    for s, n in ((1, "L"), (-1, "R")):
        el, wr = Vector(J[f"elbow.{n}"][0]), Vector(J[f"wrist.{n}"][0])
        c.part(f"Bracer.{n}", "leather", f"LowerArm.{n}", lambda b, el=el, wr=wr: cyl(b, el.lerp(wr, 0.3), wr.lerp(el, 0.02), 0.038, 0.032, 12))
        x = s * 0.095
        c.part(f"Boot.{n}", "leather", f"LowerLeg.{n}", lambda b, x=x: cyl(b, (x, 0.005, 0.10), (x, 0.0, 0.40), 0.044, 0.050, 12))
    c.part("Cape", "green", "Chest", lambda b: arc_panel(b, 1.46, 0.62, 0.15, 0.24, pi * 0.18, pi * 0.82, 12, 6, (0, 0.0)))
    c.part("Belt", "leather", "Hips", lambda b: cyl(b, (0, 0, 0.98), (0, 0, 1.03), 0.168, 0.166, 24, False))
    c.part("Pouch", "leather", "Hips", lambda b: box(b, (0.16, -0.12, 0.95), (0.045, 0.035, 0.05)))
    # 등에 멘 화살통과 화살
    c.part("Quiver", "leather", "Chest", lambda b: cyl(b, (0.07, 0.18, 1.00), (0.0, 0.20, 1.50), 0.052, 0.058, 12))
    def arrows(b):
        for i in range(5):
            ox = -0.02 + 0.016 * i; cyl(b, (0.03 + ox * 0.5, 0.19, 1.45), (-0.02 + ox, 0.205, 1.74 + 0.02 * (i % 2)), 0.004, 0.004, 6)
    c.part("Arrows", "shaft", "Chest", arrows)
    c.part("Fletch", "feather", "Chest", lambda b: [cyl(b, (0.0 + 0.016 * (i - 2), 0.205, 1.64 + 0.02 * (i % 2)), (0.0 + 0.016 * (i - 2), 0.205, 1.74 + 0.02 * (i % 2)), 0.011, 0.011, 6) for i in range(5)])
    c.part("QuiverStrap", "leather", "Chest", lambda b: box(b, (0, -0.0, 1.30), (0.17, 0.158, 0.012), Quaternion((0, 1, 0), 0.0)) if False else cyl(b, (0.14, -0.04, 1.40), (-0.06, 0.17, 1.12), 0.012, 0.012, 6))
    # 왼손에 쥔 활 (위아래로 휘어짐, 시위는 몸 쪽)
    hx, hy = J["hand.L"][0][0], J["hand.L"][0][1]; zc = 0.84
    pts = [(hx, hy - 0.11 * (1 - u * u) ** 1.1 + 0.02 * (abs(u) > 0.85), zc + 0.62 * u) for u in [i / 10 - 1 + i / 10 for i in range(11)]]
    pts = [(hx, hy - 0.10 * (1 - ((i - 5) / 5.0) ** 2) - 0.0, zc + 0.62 * (i - 5) / 5.0) for i in range(11)]
    c.part("Bow", "wood", "Hips", lambda b: poly_tube(b, pts, 0.014, 6))
    c.part("BowString", "string", "Hips", lambda b: cyl(b, (hx, hy, zc - 0.62), (hx, hy, zc + 0.62), 0.0022, 0.0022, 6))
    c.part("BowGrip", "leather", "Hips", lambda b: cyl(b, (hx, hy - 0.10, zc - 0.07), (hx, hy - 0.10, zc + 0.07), 0.019, 0.019, 10))
    c.join(); human_idle(c, 0.0); talk(c, 'R', 0.02); c.finish_actions(); return c

# ================= 8. 신전 사제 (버퍼 전직관, 여성) =================
def build_priest_master():
    c = Character("MasterPriest"); std_mats(c)
    c.mat("white", (0.86, 0.85, 0.80), rough=0.8); c.mat("trim", (0.80, 0.55, 0.20), metal=1.0, rough=0.35); c.mat("hair", (0.76, 0.60, 0.30), rough=0.65)
    c.mat("marble", (0.88, 0.85, 0.78), rough=0.25); c.mat("sunstone", (1.0, 0.7, 0.25), emit=3.0, rough=0.1); c.mat("sash", (0.55, 0.08, 0.08), rough=0.8)
    c.mat("skin", (0.84, 0.62, 0.50), rough=0.55, sss=0.15)
    J = human_joints(FJ())
    c.skin_body(J, HUMAN_CHAINS, "pelvis", ["skin", "white", "leather"], human_assign())
    c.rig(human_bones(J)); crown = J["crown"][0]; cx, cy, cz = crown
    human_face(c, crown, "hair", hair_scale=(0.090, 0.104, 0.102), hair_back=1.62, hair_front=1.735)
    c.part("LongHair", "hair", "Head", lambda b: arc_panel(b, 1.70, 1.05, 0.085, 0.12, pi * 0.12, pi * 0.88, 12, 6, (0, 0.012)))
    c.part("Veil", "white", "Head", lambda b: arc_panel(b, 1.76, 1.30, 0.10, 0.17, pi * 0.05, pi * 0.95, 14, 4, (0, 0.015)))
    c.part("Tiara", "trim", "Head", lambda b: cyl(b, (0, 0.01, 1.775), (0, 0.01, 1.795), 0.098, 0.098, 24, False))
    c.part("SunHalo", "trim", "Head", lambda b: torus_ring(b, (0, 0.11, 1.80), 0.15, 0.008, 36, 8))
    def rays(b):
        for i in range(12):
            a = 2 * pi * i / 12
            cyl(b, (0.158 * cos(a), 0.11, 1.80 + 0.158 * sin(a)), (0.215 * cos(a), 0.11, 1.80 + 0.215 * sin(a)), 0.012, 0.0, 4)
    c.part("SunRays", "trim", "Head", rays)
    c.part("Gem", "sunstone", "Head", lambda b: sphere(b, (0, -0.095, 1.795), (0.012, 0.007, 0.016), 8))
    c.part("Robe", "white", "Hips", lambda b: cyl(b, (0, 0, 0.02), (0, -0.003, 1.05), 0.30, 0.185, 32, False))
    c.part("Bodice", "white", "Chest", lambda b: cyl(b, (0, 0, 0.98), (0, 0, 1.47), 0.168, 0.12, 26, False))
    for s, n in ((1, "L"), (-1, "R")):
        sh, wr = Vector(J[f"shoulder.{n}"][0]), Vector(J[f"wrist.{n}"][0])
        c.part(f"Sleeve.{n}", "white", f"UpperArm.{n}", lambda b, sh=sh, wr=wr, s=s: cyl(b, sh, wr + Vector((s * 0.01, 0, -0.04)), 0.055, 0.105, 16, False))
        c.part(f"Cuff.{n}", "trim", f"LowerArm.{n}", lambda b, wr=wr, s=s: cyl(b, wr + Vector((s * 0.01, 0, -0.04)), wr + Vector((s * 0.01, 0, -0.055)), 0.107, 0.107, 16, False))
    c.part("Stole", "trim", "Chest", lambda b: [box(b, (s * 0.062, -0.158, 0.80), (0.026, 0.006, 0.66), Quaternion((0, 1, 0), 0)) for s in (-1, 1)])
    c.part("StoleCross", "sash", "Chest", lambda b: [box(b, (s * 0.062, -0.164, 0.80), (0.012, 0.004, 0.66)) for s in (-1, 1)])
    c.part("Mantle", "white", "Chest", lambda b: cyl(b, (0, 0, 1.38), (0, 0, 1.51), 0.205, 0.105, 24, False))
    c.part("SunEmblem", "trim", "Chest", lambda b: (cyl(b, (0, -0.152, 1.28), (0, -0.157, 1.28), 0.034, 0.034, 20), [cyl(b, (0.04 * cos(a), -0.155, 1.28 + 0.04 * sin(a)), (0.058 * cos(a), -0.155, 1.28 + 0.058 * sin(a)), 0.007, 0.0, 4) for a in [2 * pi * i / 12 for i in range(12)]]))
    c.part("Sash", "sash", "Hips", lambda b: cyl(b, (0, 0, 0.97), (0, 0, 1.04), 0.172, 0.17, 26, False))
    # 왼손에 쥔 태양의 홀: 대리석 막대 + 금 태양
    hx, hy = J["hand.L"][0][0], J["hand.L"][0][1]; top = 1.22
    c.part("ScepterRod", "marble", "Hand.L", lambda b: cyl(b, (hx, hy, 0.46), (hx, hy, top), 0.0155, 0.0135, 14))
    c.part("ScepterGold", "trim", "Hand.L", lambda b: (sphere(b, (hx, hy, 0.455), (0.022, 0.022, 0.026), 10), torus_ring(b, (hx, hy, top + 0.075), 0.062, 0.0085, 28, 8)))
    c.part("ScepterRays", "trim", "Hand.L", lambda b: [cyl(b, (hx + 0.066 * cos(a), hy, top + 0.075 + 0.066 * sin(a)), (hx + (0.12 if i % 2 == 0 else 0.10) * cos(a), hy, top + 0.075 + (0.12 if i % 2 == 0 else 0.10) * sin(a)), 0.0105, 0.0, 4) for i, a in enumerate([2 * pi * i / 12 for i in range(12)])])
    c.part("ScepterGem", "sunstone", "Hand.L", lambda b: sphere(b, (hx, hy - 0.004, top + 0.075), (0.036, 0.02, 0.036), 14))
    c.join(); human_idle(c, 0.0); talk(c, 'R', 0.0); c.finish_actions(); return c

# ================= 실행 =================
if OUT_DIR is None:
    OUT_DIR = bpy.path.abspath("//") or os.path.join(os.path.expanduser("~"), "Desktop")
    if not os.path.isdir(OUT_DIR): OUT_DIR = os.path.expanduser("~")
BUILDERS = [(build_scarecrow, "scarecrow.fbx"), (build_east_master, "master_sword_east.fbx"), (build_west_master, "master_sword_west.fbx"),
            (build_mage_master, "master_mage.fbx"), (build_assassin_master, "master_assassin.fbx"), (build_warrior_master, "master_warrior.fbx"),
            (build_archer_master, "master_archer.fbx"), (build_priest_master, "master_priest.fbx")]
built = []
for i, (fn, fname) in enumerate(BUILDERS):
    ch = fn()
    if EXPORT_FBX and os.environ.get('SKIP_EXPORT') != '1': ch.export(OUT_DIR, fname)
    ch.rig_ob.location.x = (i - 3.5) * SPACING
    built.append(ch)
print("완료:", [(b.name, len(b.body.data.polygons)) for b in built])
