using UnityEngine;
using UnityEngine.InputSystem;

public class SlowMotion : MonoBehaviour
{
    [Header("Settings")]
    public float slowScale = 0.1f;
    public float slowDuration = 5f;
    public float recoverSpeed = 2f;

    private InputAction slowMoAction;
    private float slowMoTimer;
    private bool isSlowMo;

    void Awake()
    {
        slowMoAction = new InputAction("SlowMo", binding: "<Mouse>/rightButton");
        slowMoAction.Enable();
    }

    void Update()
    {
        if (slowMoAction.WasPressedThisFrame() && !isSlowMo)
            StartSlowMo();

        if (isSlowMo)
        {
            slowMoTimer -= Time.unscaledDeltaTime;
            if (slowMoTimer <= 0f)
                EndSlowMo();
        }

        // Return to normal time after slow-mo ends
        if (!isSlowMo && Time.timeScale < 1f)
        {
            Time.timeScale = Mathf.Min(Time.timeScale + recoverSpeed
                * Time.unscaledDeltaTime, 1f);
            Time.fixedDeltaTime = Time.timeScale * 0.02f;
        }
    }

    void StartSlowMo()
    {
        isSlowMo = true;
        slowMoTimer = slowDuration;
        Time.timeScale = slowScale;
        Time.fixedDeltaTime = Time.timeScale * 0.02f;
    }

    void EndSlowMo()
    {
        isSlowMo = false;
    }

    void OnDestroy() => slowMoAction.Disable();
}
