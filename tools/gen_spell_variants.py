"""Builds the per-element mage spells (11 elements x 6 slots) into data/shared/skills.json.

Each variant is a normal skill with the id "<base>.<element>" (mg_bolt.fire ...). The job still lists the six base ids;
the server / client swap in the variant of the caster's chosen element (SharedData.SkillFor). Run again to regenerate:
    python -I tools/gen_spell_variants.py
"""
import json, os, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
path = os.path.join(root, "data", "shared", "skills.json")
data = json.load(open(path, encoding="utf-8"))
elements = json.load(open(os.path.join(root, "data", "shared", "elements.json"), encoding="utf-8"))["elements"]["list"]
color = {e["id"]: e["color"] for e in elements}

skills = [s for s in data["skills"] if "." not in s["id"]]       # drop variants from an earlier run
base = {s["id"]: s for s in skills if s["id"].startswith("mg_")}
order = ["mg_bolt", "mg_shield", "mg_blast", "mg_pillar", "mg_chain", "mg_field"]

# name, description, and the fields that differ from the base spell. fx = how the client dresses it up.
V = {
 "fire": [
  dict(name="화염탄", desc="불덩이를 쏴 화상을 입힌다.", kind="projectile", coefficient=1.4, speed=28, range=22, radius=.45, fx="orb"),
  dict(name="불꽃 각성", desc="8초 동안 내 모든 피해가 20% 늘어난다.", kind="buff", duration=8, cooldown=16, manaCost=15, effects=[dict(type="buff", stat="damage", amount=.2)], fx="aura"),
  dict(name="폭염", desc="앞쪽 땅을 폭발시켜 범위 안을 태운다.", kind="ground", coefficient=2.0, distance=7, radius=3.6, delay=.35, fx="burst"),
  dict(name="화염 기둥", desc="잠시 뒤 불기둥이 솟아 한 곳을 강하게 태운다.", kind="ground", coefficient=3.4, distance=8, radius=2.2, delay=.9, fx="column"),
  dict(name="불꽃 부채", desc="불꽃 다섯 줄기를 부채꼴로 쏜다.", kind="volley", coefficient=1.3, projectiles=5, spreadDeg=50, speed=26, range=18, radius=.6, pierce=1, fx="orb"),
  dict(name="용암 지대", desc="용암을 깔아 6초 동안 닿는 적을 태운다.", kind="zone", coefficient=.9, ticks=6, radius=3.5, fx="pool"),
 ],
 "plant": [
  dict(name="가시 세갈래", desc="가시 세 개를 퍼뜨려 쏜다. 맞으면 잠깐 묶인다.", kind="volley", coefficient=.75, projectiles=3, spreadDeg=24, speed=22, range=18, radius=.4, pierce=1, manaCost=10, fx="thorn"),
  dict(name="넝쿨 방벽", desc="넝쿨이 8초 동안 받는 피해를 흡수한다.", kind="shield", coefficient=1.5, duration=8, cooldown=14, manaCost=15, fx="aura"),
  dict(name="가시 덩굴밭", desc="땅에서 덩굴이 올라와 4초 동안 가시로 찌른다.", kind="zone", coefficient=.7, ticks=4, radius=3, distance=6, manaCost=22, cooldown=9, fx="spikes"),
  dict(name="가시 창", desc="커다란 가시 창을 날려 여럿을 꿰뚫는다.", kind="projectile", coefficient=3.0, speed=20, range=14, radius=.9, pierce=3, fx="thorn"),
  dict(name="포자 폭발", desc="포자를 터뜨려 넓게 피해를 주고 묶는다.", kind="ground", coefficient=2.2, distance=7, radius=3.8, delay=.6, fx="burst"),
  dict(name="가시 숲", desc="가시 숲을 일으켜 5초 동안 안의 적을 묶고 벤다.", kind="zone", coefficient=.8, ticks=5, radius=4, fx="spikes"),
 ],
 "earth": [
  dict(name="바위 포탄", desc="느리지만 묵직한 바위를 던진다.", kind="projectile", coefficient=2.0, speed=15, range=16, radius=.8, cooldown=2.2, manaCost=12, fx="rock"),
  dict(name="바위 갑옷", desc="6초 동안 아주 큰 보호막을 두른다.", kind="shield", coefficient=3.5, duration=6, cooldown=18, manaCost=18, fx="aura"),
  dict(name="지진", desc="땅을 흔들어 넓은 범위를 때린다.", kind="ground", coefficient=1.6, distance=6, radius=4, delay=.5, fx="quake"),
  dict(name="바위 낙하", desc="거대한 바위를 떨어뜨려 큰 피해를 준다.", kind="ground", coefficient=3.8, distance=7, radius=3, delay=1.1, maxTargets=6, fx="rockfall"),
  dict(name="진동", desc="몸 주변 땅을 울려 둘러싼 적을 때린다.", kind="circle", coefficient=2.4, range=6, maxTargets=8, fx="quake"),
  dict(name="유사 늪", desc="모래 늪이 4초 동안 안의 적을 천천히 으깬다.", kind="zone", coefficient=1.1, ticks=4, tickInterval=1.2, radius=3, fx="pool"),
 ],
 "water": [
  dict(name="물대포", desc="물줄기가 여럿을 뚫고 밀어낸다.", kind="projectile", coefficient=1.0, speed=34, range=24, radius=.5, pierce=3, fx="jet"),
  dict(name="물의 장막", desc="5초 동안 물 보호막을 두른다.", kind="shield", coefficient=2.4, duration=5, cooldown=12, manaCost=15, fx="aura"),
  dict(name="해일", desc="내 주변으로 파도를 일으켜 밀어낸다.", kind="circle", coefficient=1.5, range=5, maxTargets=8, cooldown=7, manaCost=22, fx="wave"),
  dict(name="대수류", desc="거대한 물살이 길게 휩쓴다.", kind="projectile", coefficient=2.4, speed=22, range=20, radius=1.1, pierce=6, fx="jet"),
  dict(name="물뱀", desc="물뱀이 적 사이를 네 번 건너뛴다.", kind="chain", coefficient=1.8, chainCount=4, chainFalloff=.8, fx="chain"),
  dict(name="소용돌이 늪", desc="소용돌이가 5초 동안 안의 적을 휘감는다.", kind="zone", coefficient=.7, ticks=5, radius=4.5, fx="swirl"),
 ],
 "lightning": [
  dict(name="번개 화살", desc="아주 빠른 번개를 쏜다. 근처로 번개가 튄다.", kind="projectile", coefficient=1.2, speed=60, range=28, radius=.35, cooldown=1.2, fx="bolt"),
  dict(name="번개 점멸", desc="번개처럼 적 뒤로 순간이동해 찌른다. 적이 없으면 앞으로 도약한다.", kind="blink", coefficient=1.0, range=6, arcDeg=120, distance=8, cooldown=10, manaCost=18, fx="blink"),
  dict(name="낙뢰", desc="번개가 곧바로 내리꽂힌다.", kind="ground", coefficient=2.2, distance=7, radius=2, delay=.2, fx="strike"),
  dict(name="벼락", desc="강한 벼락 한 줄기가 한 곳을 부순다.", kind="ground", coefficient=4.0, distance=9, radius=1.8, delay=.5, maxTargets=3, fx="strike"),
  dict(name="뇌전 연쇄", desc="번개가 다섯 번 튄다.", kind="chain", coefficient=2.2, chainCount=5, chainFalloff=.85, fx="chain"),
  dict(name="뇌운", desc="구름이 5초 동안 번개를 연달아 떨어뜨린다.", kind="zone", coefficient=.7, ticks=8, tickInterval=.6, radius=3, fx="strike"),
 ],
 "metal": [
  dict(name="강철 파편", desc="파편 다섯 개를 좁게 쏜다. 방어를 파고든다.", kind="volley", coefficient=.55, projectiles=5, spreadDeg=30, speed=30, range=20, radius=.3, pierce=1, fx="shard"),
  dict(name="강철 피부", desc="8초 동안 받는 피해가 25% 줄어든다.", kind="buff", duration=8, cooldown=16, manaCost=15, effects=[dict(type="buff", stat="damageTaken", amount=.25)], fx="aura"),
  dict(name="강철 폭풍", desc="파편 일곱 개를 넓게 흩뿌린다.", kind="volley", coefficient=.5, projectiles=7, spreadDeg=80, speed=30, range=14, radius=.4, pierce=1, cooldown=7, manaCost=22, fx="shard"),
  dict(name="강철 창우", desc="강철 창이 비처럼 뻗어 여럿을 꿰뚫는다.", kind="volley", coefficient=1.1, projectiles=5, spreadDeg=20, speed=36, range=24, radius=.35, pierce=3, fx="shard"),
  dict(name="반사 파편", desc="파편이 적 사이를 세 번 튕긴다.", kind="chain", coefficient=2.0, chainCount=3, chainFalloff=.9, fx="chain"),
  dict(name="칼날 지대", desc="칼날이 6초 동안 땅에서 솟는다.", kind="zone", coefficient=.85, ticks=6, tickInterval=.8, radius=3.2, fx="spikes"),
 ],
 "ice": [
  dict(name="얼음 창", desc="얼음 창이 둘을 꿰뚫고 느리게 만든다.", kind="projectile", coefficient=1.2, speed=24, range=22, radius=.5, pierce=2, fx="icicle"),
  dict(name="서리 파동", desc="몸 주변에 냉기를 터뜨려 적을 얼려 붙인다.", kind="circle", coefficient=1.0, range=4, maxTargets=8, cooldown=10, manaCost=18, fx="wave"),
  dict(name="서리 폭발", desc="앞쪽 땅이 얼어붙으며 터진다.", kind="ground", coefficient=1.7, distance=7, radius=3.2, delay=.35, fx="burst"),
  dict(name="빙주", desc="거대한 얼음 기둥이 솟아오른다.", kind="ground", coefficient=3.2, distance=8, radius=2.6, delay=.8, fx="spikes"),
  dict(name="눈보라", desc="얼음 조각 일곱 개를 넓게 쏟아낸다.", kind="volley", coefficient=.8, projectiles=7, spreadDeg=90, speed=24, range=14, radius=.45, pierce=2, fx="icicle"),
  dict(name="눈보라 지대", desc="5초 동안 안의 적이 얼어붙는다.", kind="zone", coefficient=.8, ticks=5, radius=4, fx="swirl"),
 ],
 "wind": [
  dict(name="바람 칼날", desc="바람 칼날 세 개를 넓게 날린다.", kind="volley", coefficient=.6, projectiles=3, spreadDeg=70, speed=26, range=14, radius=.5, pierce=3, fx="blade"),
  dict(name="바람 도약", desc="바람을 타고 앞으로 훌쩍 날아간다.", kind="blink", coefficient=.8, range=3, arcDeg=90, distance=7, cooldown=8, manaCost=14, fx="blink"),
  dict(name="돌풍", desc="내 주변에 돌풍을 일으켜 쓸어낸다.", kind="circle", coefficient=1.4, range=6, maxTargets=8, fx="wave"),
  dict(name="회오리", desc="커다란 회오리가 주변을 휩쓴다.", kind="circle", coefficient=2.6, range=8, maxTargets=8, cooldown=12, fx="swirl"),
  dict(name="바람 연쇄", desc="바람이 넓은 간격으로 네 번 건너뛴다.", kind="chain", coefficient=1.9, chainCount=4, chainRange=8, chainFalloff=.8, fx="chain"),
  dict(name="폭풍 지대", desc="폭풍이 7초 가까이 안의 적을 몰아친다.", kind="zone", coefficient=.7, ticks=7, tickInterval=.7, radius=3.5, fx="swirl"),
 ],
 "light": [
  dict(name="빛줄기", desc="다섯 명까지 꿰뚫는 빠른 빛.", kind="projectile", coefficient=1.1, speed=52, range=30, radius=.3, pierce=4, fx="beam"),
  dict(name="성광 결계", desc="10초 동안 오래가는 빛의 보호막.", kind="shield", coefficient=1.8, duration=10, cooldown=20, manaCost=18, fx="aura"),
  dict(name="광휘", desc="빛이 바로 내리쬐어 터진다.", kind="ground", coefficient=2.0, distance=8, radius=2.6, delay=.15, fx="column"),
  dict(name="성광포", desc="굵은 빛줄기로 일직선의 적을 모두 꿰뚫는다.", kind="projectile", coefficient=2.8, speed=60, range=30, radius=.6, pierce=8, fx="beam"),
  dict(name="성광 도약", desc="빛이 적 사이를 여섯 번 건너뛴다.", kind="chain", coefficient=1.7, chainCount=6, chainFalloff=.85, fx="chain"),
  dict(name="성역", desc="5초 동안 빛의 영역이 적을 지진다.", kind="zone", coefficient=.9, ticks=5, radius=4, fx="pool"),
 ],
 "dark": [
  dict(name="암흑구", desc="느리지만 강한 어둠 덩어리.", kind="projectile", coefficient=1.8, speed=18, range=20, radius=.7, cooldown=2.0, manaCost=11, fx="orb"),
  dict(name="그림자 장막", desc="짧은 쿨타임의 어둠 보호막.", kind="shield", coefficient=1.2, duration=4, cooldown=8, manaCost=12, fx="aura"),
  dict(name="암흑 폭발", desc="어둠이 잠시 모였다가 터진다.", kind="ground", coefficient=1.9, distance=6, radius=3.4, delay=.6, fx="burst"),
  dict(name="심연의 손", desc="심연이 한 곳을 움켜쥐어 큰 피해를 준다.", kind="ground", coefficient=3.6, distance=8, radius=2.4, delay=1.0, fx="grasp"),
  dict(name="영혼 약탈", desc="어둠이 적 사이를 세 번 건너뛰며 강하게 벤다.", kind="chain", coefficient=2.4, chainCount=3, chainFalloff=.75, fx="chain"),
  dict(name="암흑 지대", desc="어둠이 5초 동안 안의 적을 갉아먹는다.", kind="zone", coefficient=1.0, ticks=5, radius=3.5, fx="pool"),
 ],
 "void": [
  dict(name="공허 파동", desc="상성을 무시하고 여섯 명까지 꿰뚫는다.", kind="projectile", coefficient=1.2, speed=22, range=22, radius=.5, pierce=6, fx="orb"),
  dict(name="공허 방벽", desc="3초 동안 매우 두꺼운 방벽.", kind="shield", coefficient=3.0, duration=3, cooldown=22, manaCost=20, fx="aura"),
  dict(name="공허 파쇄", desc="주변 공간을 부숴 둘러싼 적에게 큰 피해를 준다.", kind="circle", coefficient=2.2, range=4.5, maxTargets=8, cooldown=8, manaCost=24, fx="burst"),
  dict(name="공허 붕괴", desc="한 점을 무너뜨려 세 명에게 가장 큰 피해를 준다.", kind="ground", coefficient=4.2, distance=6, radius=2, delay=1.2, maxTargets=3, fx="grasp"),
  dict(name="공허 광파", desc="모든 방향으로 넓게 터지는 파동.", kind="circle", coefficient=2.0, range=9, maxTargets=10, fx="wave"),
  dict(name="공허의 균열", desc="균열이 4초 동안 강하게 갉아먹는다.", kind="zone", coefficient=1.2, ticks=4, radius=3, fx="pool"),
 ],
}

out = []
for el, lst in V.items():
    assert el in color, el
    assert len(lst) == 6, el
    for i, v in enumerate(lst):
        b = dict(base[order[i]])
        s = dict(b)
        s["id"] = f"{order[i]}.{el}"
        s["name"] = v["name"]
        s["description"] = v["desc"]
        s["color"] = color[el]
        s["element"] = el
        for k, val in v.items():
            if k in ("name", "desc"):
                continue
            s[k] = val
        # fields that only make sense for the base kind must not leak into the variant
        keep = {"id", "name", "job", "unlockLevel", "slot", "circle", "color", "description", "kind", "cooldown", "manaCost",
                "coefficient", "element", "fx", "useElement"}
        keep |= set(v.keys()) - {"name", "desc"}
        s = {k: x for k, x in s.items() if k in keep}
        s["useElement"] = s["kind"] not in ("shield", "buff")
        s.setdefault("maxTargets", {"ground": 5, "zone": 8, "circle": 8, "blink": 1}.get(s["kind"], 3))
        if s["kind"] == "chain":
            s.setdefault("range", 14); s.setdefault("arcDeg", 100); s.setdefault("chainRange", 6)
        if s["kind"] == "blink":
            s.setdefault("range", 6); s.setdefault("arcDeg", 120)
        if s["kind"] in ("ground", "zone"):
            s.setdefault("delay", .35)
            s.setdefault("distance", 7)
            s.setdefault("radius", 3)
        out.append(s)

data["skills"] = skills + out
json.dump(data, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("variants:", len(out), "total skills:", len(data["skills"]))
