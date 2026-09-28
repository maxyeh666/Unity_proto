using UnityEngine;
using UnityEngine.InputSystem;

public class TouchDiagnostic : MonoBehaviour
{
    private int lastActiveTouchCounts = -1;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        var touchScreen = Touchscreen.current;
        int activeCounts = 0;

        if (touchScreen == null) return;

        for (int i = 0; i < touchScreen.touches.Count; i++)
        {
            var touch = touchScreen.touches[i];
            if (touch.press.isPressed)
            {
                activeCounts++;
            }
        }

        if (activeCounts != lastActiveTouchCounts) 
        {
            lastActiveTouchCounts = activeCounts;

            Debug.Log($"Active Touch Count = {activeCounts}");

            for (int i = 0; i < touchScreen.touches.Count; i++)
            {
                var touch = touchScreen.touches[i];
                if (touch.press.isPressed)
                {
                    Debug.Log($"Touch Slot = {i} Position = {touch.position.ReadValue()}");
                }
                
            }
        }
    }
}
