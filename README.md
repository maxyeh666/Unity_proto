# Web3D_proto

Unity 軌道相機（Orbit Camera）原型：滑鼠 / 觸控皆可繞著目標轉 + 縮放。
目前場景：`Assets/Scenes/SampleScene.unity`，目標是一顆 Cube。

- 環境：Unity `6000.3.22f1`，URP（`Mobile_RPAsset` / `PC_RPAsset`），Input System `1.20.0`
- 腳本：
  - `Assets/Script/CameraController.cs` — 軌道相機本體（掛在 Main Camera 上）
  - `Assets/Script/TouchDiagnostic.cs` — 觸控除錯用（Log 目前幾指 + 位置）
  - `Assets/CameraInput.inputactions` + `Assets/CameraInput.cs` — Input System 自動產生檔（勿手改）

## 快速開始

1. 開啟 `SampleScene`。
2. Main Camera 確認有掛 `CameraController`。
3. Inspector 對照：
   - `References / Camera Transform` → 拖自己（沒拖會自動補 `transform`）
   - `References / Target Transform` → 拖 Cube（沒拖相機不會動，也不會報錯）
4. Play：
   - 桌機：按住左鍵拖曳旋轉、滾輪縮放
   - 手機：單指拖曳旋轉、雙指捏合縮放

## 操作對照表

| 裝置 | 輸入 | 動作 | 實作位置 |
|---|---|---|---|
| 滑鼠 | 左鍵按住 + `Mouse/delta` | Orbit 旋轉 | `OnLookPerformed → Orbit()`（事件） |
| 滑鼠 | `Mouse/scroll/y` | Zoom 縮放 | `OnZoomPerformed → Zoom()`（事件） |
| 觸控 | 1 指拖曳 | Orbit 旋轉 | `LateUpdate → HandleTouch()`（輪詢） |
| 觸控 | 2 指距離變化 | Pinch 縮放 | `LateUpdate → HandleTouch()`（輪詢） |

`CameraInput.inputactions` 的綁定（`Camera` Map）：

| Action | Type | Binding |
|---|---|---|
| `Look` | Value(Vector2) | `<Mouse>/delta` |
| `Zoom` | Value(Axis float) | `<Mouse>/scroll/y` |
| `Rotate` | Button | `<Mouse>/leftButton` |
| `PrimaryTouchPosition/Contact`、`Touch0/1Position/Contact` | — | `<Touchscreen>/...`（已定義、目前未使用，觸控改走輪詢） |

> 為什麼觸控不用事件？`Touch0/Touch1` 是固定 Slot（見下），事件版一幀可能算兩次、回呼順序不定會抖。改成 `LateUpdate` 每幀數一次最穩。

## 核心設計（原本寫在程式註解裡的，搬到這裡）

1. **事件只改數據，位置統一在 `LateUpdate` 算**
   - `Orbit()` 只累加 `orbitYaw / orbitPitch`，`Zoom()` 只改 `targetCameraDistance`。
   - `LateUpdate` 每幀做 `Lerp + UpdateCameraPosition()`。平滑一定要連續跑，不能只在事件裡算一次。
2. **相機放 `LateUpdate`**
   - 保證 Cube / 玩家先 `Update` 走完，相機最後跟，不會慢一幀抖動。
3. **空值防呆**
   - `targetTransform / cameraTransform` 沒拖就 `return`，不會 `NullReferenceException` 炸掉。
   - `Awake` 沒拖 `cameraTransform` 會自動補自己；距離初始化要有 `target` 才記，避免開場跳到預設 5。
4. **縮放平滑**
   - `cameraDistance = Lerp(cameraDistance, target, zoomSmooth * deltaTime)`，`zoomSmooth` 越大越快貼到目標。
5. **第一幀只定位、不計算**
   - 單指 / 雙指切換時第一幀只記位置，下一幀才算 `delta`，否則會從舊位置瞬移。

## 生命週期順序

```text
Awake()      載入時一次：new CameraInput()、補 cameraTransform、記初始距離 / yaw-pitch
OnEnable()   每次啟用：註冊 Look / Zoom / Rotate 事件 + Enable
Start()      第一次 LateUpdate 前一次：桌機鎖游標（手機 / WebGL 不鎖）
LateUpdate() 每幀：HandleTouch() → Lerp 縮放 → UpdateCameraPosition()
OnDisable()  每次停用：取消註冊 + Disable（要跟 OnEnable 對稱）
OnDestroy()  銷毀時一次：Dispose()，避免切場景洩漏
```

`Start` vs `Awake`：`Awake` 載入就跑，`Start` 保證所有 `Awake` 跑完才跑。

## 觸控觀念：Slot ≠ 手指身份

- `Touchscreen.touches` 是 10 個固定插槽（Slot，類似 Index）。
- 手指按下去才佔一格，放開就空出來，下一根可能拿到同一格 → **不能拿編號當身份**。
- 正確做法：每幀掃全部 Slot，看「當下有幾格按著 + 在哪」：
  - `count == 1` → 單指 Orbit
  - `count == 2` → 雙指 Pinch（`Distance(p0, p1)` 差值丟給 `Zoom()`）
  - `0` 或 `3+` → 清掉狀態，當作放開

## 位置公式

```csharp
rot = Quaternion.Euler(orbitPitch, orbitYaw, 0);
pos = target.position + rot * Vector3.back * cameraDistance;
camera.rotation = LookRotation(target.position - camera.position);
```

- 上下滑鼠 / 觸控是反的，所以 `orbitPitch -= delta.y`。
- `orbitPitch` 夾在 `min/maxOrbitPitch`（預設 ±80°），避免翻到背面。
- 舊版第一人稱 `Rotate()` 已保留對照用（一次設 `Euler` 最乾淨；以前拆兩行疊加轉幾下就歪掉），目前沒人呼叫。

## Inspector 參數

| 群組 | 欄位 | 預設 | 說明 |
|---|---|---|---|
| Rotate（舊版保留） | `sensitivity` / `min/maxPitch` | 0.25 / ±89 | 只給舊 `Rotate()` 用 |
| Zoom | `zoomSpeed` | 1.5 | 每滾一格走多遠 |
| Zoom | `min/maxZoomDistance` | 2 / 10 | 避免穿過或飛太遠 |
| Zoom | `zoomSmooth` | 10 | Lerp 速度 |
| Pinch | `pinchSensitivity` | 0.01 | 雙指縮放靈敏度 |
| Pinch | `touchOrbitSensitivity` | 0.08 | 單指靈敏度（觸控 delta 很大，要比滑鼠小） |
| Orbit | `orbitSensitivity` | 0.25 | 滑鼠靈敏度（0.2~0.3 手感較好） |
| Orbit | `min/maxOrbitPitch` | ±80 | 垂直夾角 |

## 已知坑 / 除錯筆記

- 滾輪一格：Windows 舊版 `±120`，新版 / WebGL 常見 `±1`，`Zoom()` 用 `> 1.5f ? /120 : 直接用` 通吃。
- `eulerAngles.x` 是 `0~360`，要換成 `-180~180` 才好 `Clamp`。
- `Cursor.lockState = Locked` 只在桌機做，`#if !UNITY_ANDROID && !UNITY_IOS && !UNITY_WEBGL`，手機 / WebGL 鎖了會壞。
- `?.Dispose()`：有值才釋放，避免 null 報錯。
- `TouchDiagnostic` 只是除錯 Log（手指數量變化才印），出貨前可拔掉或關掉。

## TODO

- [ ] `TouchDiagnostic` 出貨前移除 / 預設停用
- [ ] 決定 `lookInput / zoomInput / yaw / pitch` 暫存欄位要留除錯還是刪除
- [ ] WebGL 手機瀏覽器手勢實測（pinch + 單指切換）
