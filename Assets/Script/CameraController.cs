using UnityEngine; // Unity 核心：MonoBehaviour、Transform、Vector2、Quaternion、Mathf、Cursor、Time
using UnityEngine.InputSystem; // 新版輸入系統：InputAction、Mouse、Keyboard

// ============================================================
// CameraController：軌道相機 Orbit（繞著 target 轉 + 滾輪縮放）
// 掛在 Main Camera 上，Inspector 要拖：Camera Transform（自己）、Target Transform（Cube）
//
// 【學習筆記：Unity 生命週期順序 - 常用】
//   Awake()      -> 載入時跑一次（最早，做 new / 抓參考 / 記初始距離）
//   OnEnable()   -> 每次啟用時跑（註冊輸入事件 +=、Enable 輸入）
//   Start()      -> 第一次 LateUpdate 前跑一次（鎖游標，手機/WebGL 不鎖）
//   LateUpdate() -> 每幀跑一次，所有 Update 跑完才跑（相機最後跟，才不會抖）
//                  縮放 Lerp 平滑 + 重算軌道位置都在這裡，每幀連續跑平滑才不會斷
//   OnDisable()  -> 每次停用時跑（取消註冊 -=、Disable 輸入）
//   OnDestroy()  -> 銷毀時跑一次（Dispose 輸入，避免切場景洩漏）
//
// 【學習筆記：運作流程（事件驅動版）】
//   1. Awake：new CameraInput() + 補 cameraTransform + 有 target 才記 cameraDistance + 記舊版 yaw/pitch
//   2. OnEnable：註冊 Look / Zoom / Rotate 三個桌機事件 + Enable（觸控改輪詢，不註冊事件）
//   3. 按住左鍵拖滑鼠 -> OnLookPerformed -> Orbit(delta) 只改 orbitYaw/orbitPitch，位置等 LateUpdate 統一算
//   4. 滾輪 -> OnZoomPerformed -> Zoom(value) 只改 targetCameraDistance，位置等 LateUpdate 統一算
//   5. LateUpdate：每幀 Lerp cameraDistance 往 target 靠 + UpdateCameraPosition() 重算位置
//   6. OnDisable / OnDestroy：取消註冊 + Disable + Dispose，避免重複觸發 / 洩漏
//
// 【學習筆記：設計觀念】
//   - 事件只改數據（yaw/pitch/distance），位置統一等 LateUpdate 算，平滑 Lerp 一定要連續跑
//   - 相機放 LateUpdate：保證 Cube/玩家先走完相機再跟，不會慢一幀抖動
//   - 空值防呆：沒拖 target/camera 就 return 不算，不會 NullReferenceException 炸掉整支
// ============================================================

// MonoBehaviour：Unity 腳本基底類別，繼承它才能掛在 GameObject 上，
// 並使用 Awake / Start / LateUpdate 等生命週期函式。
public class CameraController : MonoBehaviour
{
    // CameraInput：由 CameraInput.inputactions 自動產生的類別，專門讀玩家輸入。
    // 入門：它是 Unity 幫你包好的「輸入遙控器」，Look=滑鼠移動、Zoom=滾輪、Rotate=左鍵。
    //       觸控我們改用輪詢（LateUpdate 直接問 Touchscreen），所以這裡只留桌機三個。
    private CameraInput inputActions;

    [Header("Rotate (舊版第一人稱，保留學習對照用)")]
    [SerializeField] private float sensitivity = 0.25f; // 只給舊 Rotate() 用，Orbit 改用 orbitSensitivity / touchOrbitSensitivity
    [SerializeField] private float minPitch = -89f; // 只給舊 Rotate() 的 pitch 用
    [SerializeField] private float maxPitch = 89f;

    [Header("Zoom")]
    [SerializeField] private float zoomSpeed = 1.5f; // 每滾一格走多遠
    [SerializeField] private float minZoomDistance = 2f; // 最近
    [SerializeField] private float maxZoomDistance = 10f; // 最遠
    [SerializeField] private float zoomSmooth = 10f; // 越大越快貼到目標

    [Header("Pinch")]
    [SerializeField] private float pinchSensitivity = 0.01f; // 雙指縮放靈敏度
    [SerializeField] private float touchOrbitSensitivity = 0.08f; // 單指觸控旋轉靈敏度（手機 delta 很大，要比滑鼠小）

    // Transform：物件的位置 / 旋轉 / 縮放。這裡指要被轉的相機，Inspector 拖進來。
    [Header("References")]
    [Tooltip("要轉動的相機，通常拖自己")]
    [SerializeField] private Transform cameraTransform;

    // Vector2：2D 向量，x = 左右、y = 上下。OnLookPerformed 暫存用，馬上傳給 Orbit()。
    private Vector2 lookInput; // 目前沒再利用，可刪或留著除錯看
    // float：滾輪量，正 = 前滾（拉近）、負 = 後滾（拉遠）。OnZoomPerformed 暫存用。
    private float zoomInput; // 同上，Zoom(value) 馬上用掉，不用存也行
    private bool isRotating = false; // 左鍵是否按住（Rotate Action），沒按住 Orbit 就直接 return
    private float yaw; // 舊 Rotate() 專用，Orbit 改用 orbitYaw，下面 Awake 記的值目前沒人用
    private float pitch; // 舊 Rotate() 專用，同上
    private float cameraDistance = 5f; // 目前距離
    private float targetCameraDistance = 5f; // 目標距離（平滑用）

    // 觸控輪詢用（LateUpdate 直接讀 Touchscreen，不走事件，就不怕回呼順序亂掉）。
    // 入門：
    //   bool   = true/false 開關，記「上一幀是不是單指/雙指」。第一幀只定位不算，才不會瞬移。
    //   Vector2= 2D 座標 (x,y)，記「上一幀手指在哪」，這幀減掉就是移動 delta。
    //   float  = 小數，記「上一幀兩指距離」，這幀減掉就是捏合 delta。
    private bool singleTouchActive = false; // 上一幀是不是單指（是，這幀才算 delta 轉）
    private Vector2 lastSingleTouchPos; // 上一幀單指位置（這幀 current - last = 位移）
    private bool pinchActive = false; // 上一幀是不是雙指（是，這幀才算距離差縮放）
    private float lastPinchDist = 0f; // 上一幀雙指距離（這幀 current - last = 捏合量）

    [Header("Orbit (目前測試中)")]
    [Tooltip("要繞著看的目標，記得拖 Cube，沒拖 LateUpdate 會直接 return 不動")]
    [SerializeField] private Transform targetTransform;

    private float orbitYaw = 0f; // 繞目標的水平角度，Orbit() 累加 delta.x 來的
    private float orbitPitch = 0f; // 繞目標的垂直角度，Orbit() 累加 delta.y 來的（下滑鼠反轉所以用 -=）
    [SerializeField] private float minOrbitPitch = -80f;
    [SerializeField] private float maxOrbitPitch = 80f;
    [SerializeField] private float orbitSensitivity = 0.25f; // 滑鼠 delta 是像素，0.2~0.3 手感較剛好

    // Awake：載入時最先跑一次，比 Start() 早。
    // 只做：new 輸入、補 cameraTransform、記初始距離 + 初始 yaw/pitch。
    // 沒拖 target 不 return，下面 yaw/pitch 照樣記（空值防呆在 UpdateCameraPosition 擋）。
    // 距離初始化需要 target 才有意義，有 target 才記世界距離，避免開場跳到預設 5。
    private void Awake()
    {
        // new CameraInput()：建立輸入操作實例，之後讀 Look / Zoom 等動作。
        inputActions = new CameraInput();

        // 從目前角度讀出 yaw / pitch，避免掛上瞬間彈回 (0,0)
        // eulerAngles.x 是 0~360，要換成 -180~180 才好限制
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        // ?. 是空條件運算子：null 就跳過，不報錯。這裡是防呆，沒拖就用自己。
        // 順序一定要先判空才能用，不然 cameraTransform.position 那行直接炸。
        if (cameraTransform == null)
            cameraTransform = transform;

        // 沒拖 target 不直接 return，不然下面 yaw/pitch 初始化會被跳過。
        // 位置防呆交給 UpdateCameraPosition()。
        if (targetTransform != null)
        {
            // 記住場景擺好的距離，避免開場跳到預設 5
            float startD = (cameraTransform.position - targetTransform.position).magnitude;
            if (startD > 0.01f)
            {
                startD = Mathf.Clamp(startD, minZoomDistance, maxZoomDistance);
                cameraDistance = targetCameraDistance = startD;
            }
        }
    }

    // OnEnable：每次啟用時跑（含場景剛載入）。
    // 做 Enable + 註冊事件（+=）。停用時 OnDisable 一定要取消，避免重複觸發兩次。
    private void OnEnable()
    {
        // 防呆：避免執行順序問題導致 null。
        if (inputActions == null)
            inputActions = new CameraInput();

        // 先註冊再 Enable，避免第一幀漏接。
        // performed：Value 型動作（滑鼠 delta / 滾輪）有變化時觸發。
        // +=：把方法訂閱到動作上，有輸入就會被回呼。
        // 入門：觸控改輪詢後，這裡只留桌機三個（Look=移動、Zoom=滾輪、Rotate=左鍵）。
        inputActions.Camera.Look.performed += OnLookPerformed;
        inputActions.Camera.Zoom.performed += OnZoomPerformed;

        inputActions.Camera.Rotate.performed += OnRotatePerformed;
        inputActions.Camera.Rotate.canceled += OnRotateCanceled;

        inputActions.Enable(); // 沒 Enable 的話 performed 永遠不會觸發
    }

    // OnDisable：每次停用時跑（關物件、切場景）。
    // 做取消註冊（-=）+ Disable。註冊跟取消要對稱，少一個就會漏或重複。
    private void OnDisable()
    {
        // 防呆：沒建立過就直接 return，避免 NullReferenceException。
        if (inputActions == null) return;

        inputActions.Camera.Look.performed -= OnLookPerformed;
        inputActions.Camera.Zoom.performed -= OnZoomPerformed;

        inputActions.Camera.Rotate.performed -= OnRotatePerformed;
        inputActions.Camera.Rotate.canceled -= OnRotateCanceled;

        inputActions.Disable();
    }

    // CallbackContext：回呼帶的參數包，裝這次輸入的原始資料。
    // ReadValue<touch>()：讀出值，型別要跟 .inputactions 的 expectedControlType 對上。
    // Look 綁 <Mouse>/delta，是 Vector2 -> 用 ReadValue<Vector2>()。
    private void OnLookPerformed(InputAction.CallbackContext context)
    {
        // 沒按住左鍵就不轉，避免滑鼠經過相機就亂轉。
        if (!isRotating) return;

        lookInput = context.ReadValue<Vector2>(); // x = 左右、y = 上下

        // 桌機用滑鼠靈敏度（手機走 HandleTouch() 輪詢，用觸控靈敏度）
        Orbit(lookInput, orbitSensitivity);
    }

    // Zoom 綁 <Mouse>/scroll/y，是 Axis -> 用 ReadValue<float>()。
    // 正 = 前滾（拉近）、負 = 後滾（拉遠）。你機器上是一格 ±1。
    private void OnZoomPerformed(InputAction.CallbackContext context)
    {
        zoomInput = context.ReadValue<float>();
        Zoom(zoomInput);
    }

    private void OnRotatePerformed(InputAction.CallbackContext context)
    {
        // ReadValueAsButton()：Button 型動作讀成 bool，按下 true、放開 false。
        isRotating = context.ReadValueAsButton();
    }

    private void OnRotateCanceled(InputAction.CallbackContext context)
    {
        isRotating = false;
    }

    // ============================================================
    // 觸控輪詢：每幀數有幾根手指。1根=Orbit轉，2根=pinch縮。
    // 入門觀念：
    //   Touch0~Touch9 是 10 個固定插槽 Slot（像 Index），手指按下去才佔一格，放開就空出來，
    //   下一根按下去可能拿到同一格，所以不能拿編號當身份，只能看「當下有幾格是按著的 + 在哪」。
    //   事件版（一個一個綁 Touch0/Touch1）順序不定、一幀算兩次會抖，所以改輪詢一幀數一次。
    //   foreach = 把 touches 全部走一遍；continue = 這格沒按就跳過看下一格；break = 夠兩根就不用再數。
    // ============================================================
    private void HandleTouch()
    {
        // Touchscreen.current：現在這台裝置的觸控板。桌機沒有，直接回傳走滑鼠那條。
        var touchScreen = Touchscreen.current;
        if (touchScreen == null) return;

        // 數現在有幾根按著，順便記位置。超過兩根只取前兩根（第三根忽略）。
        int count = 0;
        Vector2 p0 = default; // default：Vector2 的預設值 (0,0)。先佔位，數到才有值。
        Vector2 p1 = default;
        foreach (var touch in touchScreen.touches) // touches：10 個 Slot 全掃一遍
        {
            if (!touch.press.isPressed) continue; // 這格沒按，跳過
            if (count == 0) p0 = touch.position.ReadValue(); // 第一根記到 p0
            else if (count == 1) p1 = touch.position.ReadValue(); // 第二根記到 p1
            count++;
            if (count >= 2) break; // 夠了，不用再數
        }

        if (count == 1)
        {
            // 單指：跟桌機同一條 Orbit，只是靈敏度分開（觸控 delta 很大，要調小）。
            // singleTouchActive：上一幀是不是也是單指。是才算 delta，第一幀只定位不算，才不會瞬移。
            if (singleTouchActive)
                Orbit(p0 - lastSingleTouchPos, touchOrbitSensitivity);
            lastSingleTouchPos = p0;
            singleTouchActive = true;
            pinchActive = false; // 切回單指，雙指狀態清掉，下次雙指第一幀才會只定位
        }
        else if (count == 2)
        {
            // 雙指：距離變大=拉近，變小=拉遠，丟給 Zoom（Zoom 會除 120、Clamp，下游 LateUpdate 平滑）。
            float cur = Vector2.Distance(p0, p1); // Distance：兩點距離（畢氏定理，Unity 幫你算好）
            if (pinchActive && cur > 0f && lastPinchDist > 0f)
                Zoom((cur - lastPinchDist) * pinchSensitivity);
            lastPinchDist = cur;
            pinchActive = true;
            singleTouchActive = false; // 切到雙指，單指狀態清掉，回去單指第一幀才會只定位
        }
        else
        {
            // 0根或3根以上：全清掉，當作放開。下次按第一幀只定位，不會從舊位置瞬移。
            singleTouchActive = false;
            pinchActive = false;
        }
    }
    // Start：第一次 LateUpdate 前跑一次（腳本要有啟用）。
    // 跟 Awake 差異：Awake 載入就跑，Start 保證所有 Awake 都跑完了。
    void Start()
    {
        // 只在桌機鎖游標。手機 / WebGL 鎖了會壞（觸控失效 / 瀏覽器報錯）。
        // CursorLockMode.Locked：游標鎖中間 + 隱藏，適合視角控制。
#if !UNITY_ANDROID && !UNITY_IOS && !UNITY_WEBGL
        Cursor.lockState = CursorLockMode.Locked;
#endif
    }

    // LateUpdate：每幀跑一次，所有 Update 跑完才跑，相機最後跟才不會抖。
    // 縮放 Lerp + 位置重算都在這裡，每幀連續跑，平滑才不會斷。
    // 入門：HandleTouch() 放最前面，先把 yaw/pitch/distance 改完，再 Lerp+擺位置。
    void LateUpdate()
    {
        HandleTouch(); // 觸控輪詢（桌機 Touchscreen.current 是 null，直接 return）
        // Lerp：平滑靠近目標距離，避免縮放瞬間跳動。
        cameraDistance = Mathf.Lerp(cameraDistance, targetCameraDistance, zoomSmooth * Time.deltaTime);
        UpdateCameraPosition(); // 有判空，沒拖 target 會直接 return，不會炸也不會洗位置
    }

    // 旋轉核心（舊版第一人稱，保留學習對照用，目前桌機/手機都走 Orbit，沒人呼叫 Rotate）。
    // 之前拆成兩行（一個轉左右、一個用 Rotate 疊加上下）多轉幾下就歪掉，現在一次設 Euler 最乾淨。
    // 空值防呆：cameraTransform 沒拖會炸，先擋。
    private void Rotate(Vector2 delta)
    {
        // 沒按左鍵就不轉。
        if (!isRotating) return;
        if (cameraTransform == null) return;

        yaw += delta.x * sensitivity; // 左右
        pitch -= delta.y * sensitivity; // 上下滑鼠是反的，用減的才會往上看
        // Clamp：把 pitch 限制在範圍內，避免翻到背面。
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        // Quaternion.Euler：把 (x,y,z) 角度轉成旋轉，一次設定最乾淨。
        cameraTransform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    // 縮放核心：滾輪格數 → 只更新目標距離，位置等 LateUpdate() 統一 Lerp + 重算。
    // Windows 舊版一格 ±120，新版 / WebGL 常見 ±1，通吃寫法。
    private void Zoom(float value)
    {
        float scrollSteps = Mathf.Abs(value) > 1.5f ? value / 120f : value;
        targetCameraDistance -= scrollSteps * zoomSpeed;
        targetCameraDistance = Mathf.Clamp(targetCameraDistance, minZoomDistance, maxZoomDistance); // 避免穿過或飛太遠
    }

    // Orbit 入口：delta 進來，只累加 yaw/pitch，位置等 LateUpdate() 統一算。
    // 上下滑鼠/觸控是反的，用 -= 才會往上看。靈敏度桌機/手機分開傳。
    private void Orbit(Vector2 delta, float sensitivity)
    {
        orbitYaw += delta.x * sensitivity;
        orbitPitch -= delta.y * sensitivity;

        orbitPitch = Mathf.Clamp(orbitPitch, minOrbitPitch, maxOrbitPitch);
    }

    // 軌道位置核心：yaw/pitch 轉成方向 * 距離，擺到 target 旁邊，再 LookAt 看回去。
    // 公式：pos = target.pos + rot * (0,0,-distance)，rot = Euler(pitch, yaw, 0)
    // 空值防呆：沒拖 target / camera 就直接 return，不算也不炸。
    private void UpdateCameraPosition()
    {
        if (targetTransform == null || cameraTransform == null) return;

        Quaternion orbitRotation = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
        Vector3 orbitDirection = orbitRotation * Vector3.back; // 計算相機朝向
        Vector3 rotationOffset = orbitDirection * cameraDistance;

        cameraTransform.position = targetTransform.position + rotationOffset;

        Vector3 direction = targetTransform.position - cameraTransform.position;
        cameraTransform.rotation = Quaternion.LookRotation(direction);
    }

    // OnDestroy：物件銷毀時跑一次（切場景、關遊戲）。
    // CameraInput 有佔用監聽資源，一定要 Dispose，不然切多次場景會洩漏。
    private void OnDestroy()
    {
        // ?.Dispose()：有值才釋放，避免 null 又報錯。
        inputActions?.Dispose();
    }
}