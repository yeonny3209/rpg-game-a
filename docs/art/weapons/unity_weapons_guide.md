# 기본 직업 무기 — Unity 적용 가이드

## 파일 구성
- `weapon_<이름>_game.fbx` : 무기 9종. 파일 하나에 `_LOD0 / _LOD1 / _LOD2` 가 들어 있어 Unity가 LOD Group을 자동으로 만든다.
- `textures/` : 무기마다 3장 (1024×1024)
  - `<이름>_BaseColor.png`  (sRGB 켬)
  - `<이름>_Normal.png`      (Texture Type = Normal map)
  - `<이름>_MetallicSmoothness.png` (sRGB 끔. R = 금속성, A = 부드러움)
  - `textures/separate/` : 금속성과 거칠기를 따로 쓰고 싶을 때(Unreal, Godot 등)

## Unity에서 재질 만들기 (URP Lit / Standard 공통)
1. FBX와 textures 폴더를 Assets에 넣는다.
2. 재질 하나에 Base Map = BaseColor, Normal Map = Normal, Metallic Map = MetallicSmoothness 를 넣는다.
3. Smoothness Source = **Metallic Alpha**. (재질의 Smoothness 슬라이더는 1로)
4. UV는 무기마다 한 장의 아틀라스라서 Tiling은 1, Offset은 0 그대로 쓴다.

## 삼각형 수
| 무기 | LOD0 | LOD1 | LOD2 |
|---|---|---|---|
| 도 | 12,494 | 4,996 | 1,874 |
| 장검 | 16,032 | 6,412 | 2,404 |
| 지팡이 | 13,124 | 5,248 | 1,968 |
| 단검 | 9,190 | 3,676 | 1,378 |
| 메이스 | 8,832 | 3,532 | 1,324 |
| 방패 | 16,752 | 6,700 | 2,512 |
| 활 | 4,992 | 1,996 | 748 |
| 화살 | 1,656 | 661 | 247 |
| 홀 | 18,738 | 7,494 | 2,810 |

멀리 있는 다른 플레이어는 LOD2만 써도 된다. LOD Group의 전환 거리를 화면 비율 기준 60% / 25% / 8% 정도로 시작해서 조정한다.

## 알아 둘 점
- 수정·보석·마나 선·호박색 보석은 FBX에서 투명 유리와 발광을 못 담는다. BaseColor에는 색만 들어 있으니 Unity에서 Emission(발광)을 직접 켜 준다. 대상: 지팡이 수정, 장검 칼날 홈과 폼멜 보석, 홀 가운데 보석.
- 화살 깃은 한 겹짜리 면이다. 재질의 Render Face를 Both로 설정한다.
- 법선 맵은 Blender에서 구운 값(OpenGL 방식)이라 Unity에서 그대로 쓰면 된다. 언리얼은 초록 채널을 뒤집어야 한다.
- 무기의 원점은 손잡이 근처, 머리 방향이 +Z(Blender 기준)이다. 캐릭터 손 뼈(Hand.R / Hand.L)에 붙일 때 손에 쥐는 위치만 맞춰 주면 된다.
