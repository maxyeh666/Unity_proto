using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>軌道相機：繞 target 轉 + 縮放。設計說明見 README。</summary>
public class CameraController : MonoBehaviour
{
    // 由 CameraInput.inputactions 自動產生；觸控改走 HandleTouch() 輪詢，這裡只用 Look/Zoom/Rotate。
    private CameraInput inputActions;

    [Header("Rotate (舊版第一人稱，保留學習對照用)")]
    [SerializeField] private float sensitivity = 0.25f;
    [SerializeField] private float minPitch = -89f;
    [SerializeField] private float maxPitch = 89f;

    [Header("Zoom")]
    [SerializeField] private float zoomSpeed = 1.5f;
    [SerializeField] private float minZoomDistance = 2f;
    [SerializeField] private float maxZoomDistance = 10f;
    [SerializeField] private float zoomSmooth = 10f;

    [Header("Pinch")]
    [SerializeField] private float pinchSensitivity = 0.01f;
    [SerializeField] private float touchOrbitSensitivity = 0.08f;

    [Header("References")]
    [Tooltip("要轉動的相機，通常拖自己")]
    [SerializeField] private Transform cameraTransform;

    private Vector2 lookInput;
    private float zoomInput;
    private bool isRotating = false; // Rotate Action 是否按住
    private float yaw; // 舊 Rotate() 專用
    private float pitch;
    private float cameraDistance = 5f;
    private float targetCameraDistance = 5f; // 平滑目標距離

    // 觸控輪詢狀態：第一幀只定位，下一幀才算 delta（詳見 README）。
    private bool singleTouchActive = false;
    private Vector2 lastSingleTouchPos;
    private bool pinchActive = false;
    private float lastPinchDist = 0f;

    [Header("Orbit (目前測試中)")]
    [Tooltip("要繞著看的目標，記得拖 Cube")]
    [SerializeField] private Transform targetTransform;

    private float orbitYaw = 0f;
    private float orbitPitch = 0f;
    [SerializeField] private float minOrbitPitch = -80f;
    [SerializeField] private float maxOrbitPitch = 80f;
    [SerializeField] private float orbitSensitivity = 0.25f;

    private void Awake()
    {
        inputActions = new CameraInput();

        // 記住目前角度，避免掛上瞬間彈回 (0,0)；x 需由 0~360 換成 -180~180。
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        if (cameraTransform == null)
            cameraTransform = transform;

        // 有 target 才記初始距離，避免開場跳到預設值。
        if (targetTransform != null)
        {
            float startD = (cameraTransform.position - targetTransform.position).magnitude;
            if (startD > 0.01f)
            {
                startD = Mathf.Clamp(startD, minZoomDistance, maxZoomDistance);
                cameraDistance = targetCameraDistance = startD;
            }
        }
    }

    private void OnEnable()
    {
        if (inputActions == null)
            inputActions = new CameraInput();

        // 先註冊再 Enable；觸控改走輪詢，這裡只留桌機三個。
        inputActions.Camera.Look.performed += OnLookPerformed;
        inputActions.Camera.Zoom.performed += OnZoomPerformed;

        inputActions.Camera.Rotate.performed += OnRotatePerformed;
        inputActions.Camera.Rotate.canceled += OnRotateCanceled;

        inputActions.Enable();
    }

    private void OnDisable()
    {
        if (inputActions == null) return;

        inputActions.Camera.Look.performed -= OnLookPerformed;
        inputActions.Camera.Zoom.performed -= OnZoomPerformed;

        inputActions.Camera.Rotate.performed -= OnRotatePerformed;
        inputActions.Camera.Rotate.canceled -= OnRotateCanceled;

        inputActions.Disable();
    }

    // Look = <Mouse>/delta (Vector2)。
    private void OnLookPerformed(InputAction.CallbackContext context)
    {
        if (!isRotating) return;

        lookInput = context.ReadValue<Vector2>();
        Orbit(lookInput, orbitSensitivity);
    }

    // Zoom = <Mouse>/scroll/y (float)，正 = 前滾拉近。
    private void OnZoomPerformed(InputAction.CallbackContext context)
    {
        zoomInput = context.ReadValue<float>();
        Zoom(zoomInput);
    }

    private void OnRotatePerformed(InputAction.CallbackContext context)
    {
        isRotating = context.ReadValueAsButton();
    }

    private void OnRotateCanceled(InputAction.CallbackContext context)
    {
        isRotating = false;
    }

    /// <summary>觸控輪詢：1 指 Orbit、2 指 pinch（Slot 觀念見 README）。</summary>
    private void HandleTouch()
    {
        var touchScreen = Touchscreen.current;
        if (touchScreen == null) return;

        int count = 0;
        Vector2 p0 = default;
        Vector2 p1 = default;
        foreach (var touch in touchScreen.touches)
        {
            if (!touch.press.isPressed) continue;
            if (count == 0) p0 = touch.position.ReadValue();
            else if (count == 1) p1 = touch.position.ReadValue();
            count++;
            if (count >= 2) break;
        }

        if (count == 1)
        {
            if (singleTouchActive)
                Orbit(p0 - lastSingleTouchPos, touchOrbitSensitivity);
            lastSingleTouchPos = p0;
            singleTouchActive = true;
            pinchActive = false;
        }
        else if (count == 2)
        {
            float cur = Vector2.Distance(p0, p1);
            if (pinchActive && cur > 0f && lastPinchDist > 0f)
                Zoom((cur - lastPinchDist) * pinchSensitivity);
            lastPinchDist = cur;
            pinchActive = true;
            singleTouchActive = false;
        }
        else
        {
            // 0 根或 3 根以上：當作放開，下次第一幀只定位。
            singleTouchActive = false;
            pinchActive = false;
        }
    }

    void Start()
    {
        // 只在桌機鎖游標（手機 / WebGL 會壞）。
#if !UNITY_ANDROID && !UNITY_IOS && !UNITY_WEBGL
        Cursor.lockState = CursorLockMode.Locked;
#endif
    }

    void LateUpdate()
    {
        HandleTouch();
        cameraDistance = Mathf.Lerp(cameraDistance, targetCameraDistance, zoomSmooth * Time.deltaTime);
        UpdateCameraPosition();
    }

    /// <summary>舊版第一人稱，保留對照用（目前走 Orbit，無人呼叫）。</summary>
    private void Rotate(Vector2 delta)
    {
        if (!isRotating) return;
        if (cameraTransform == null) return;

        yaw += delta.x * sensitivity;
        pitch -= delta.y * sensitivity; // y 反轉才會往上看
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        cameraTransform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    /// <summary>只改 targetCameraDistance，位置等 LateUpdate 統一算。通吃 ±120 / ±1 滾輪。</summary>
    private void Zoom(float value)
    {
        float scrollSteps = Mathf.Abs(value) > 1.5f ? value / 120f : value;
        targetCameraDistance -= scrollSteps * zoomSpeed;
        targetCameraDistance = Mathf.Clamp(targetCameraDistance, minZoomDistance, maxZoomDistance);
    }

    /// <summary>只累加 yaw/pitch，位置等 LateUpdate 統一算。</summary>
    private void Orbit(Vector2 delta, float sensitivity)
    {
        orbitYaw += delta.x * sensitivity;
        orbitPitch -= delta.y * sensitivity;

        orbitPitch = Mathf.Clamp(orbitPitch, minOrbitPitch, maxOrbitPitch);
    }

    /// <summary>pos = target + Euler(pitch, yaw, 0) * back * distance，再看回 target。</summary>
    private void UpdateCameraPosition()
    {
        if (targetTransform == null || cameraTransform == null) return;

        Quaternion orbitRotation = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
        Vector3 orbitDirection = orbitRotation * Vector3.back;
        Vector3 rotationOffset = orbitDirection * cameraDistance;

        cameraTransform.position = targetTransform.position + rotationOffset;

        Vector3 direction = targetTransform.position - cameraTransform.position;
        cameraTransform.rotation = Quaternion.LookRotation(direction);
    }

    private void OnDestroy()
    {
        inputActions?.Dispose();
    }
}