using UnityEngine;

/// <summary>
/// First-person fly camera. Attach to a GameObject that also has a Camera component.
/// Set VoxelWorld.player to this Transform so chunk streaming follows the camera.
///
/// Controls:
///   Mouse       — look around
///   W/S         — forward / backward
///   A/D         — strafe left / right
///   E           — fly up
///   Q           — fly down
///   Left Shift  — sprint (10× speed)
///   Escape      — release cursor
///   Left click  — re-capture cursor
/// </summary>
[RequireComponent(typeof(Camera))]
public class FlyCamera : MonoBehaviour
{
    [Header("Look")]
    public float mouseSensitivity = 2f;
    [Range(-90f, 0f)]  public float minPitch = -80f;
    [Range(0f,  90f)]  public float maxPitch =  80f;

    [Header("Movement")]
    [Tooltip("Normal fly speed in voxels/second.")]
    public float moveSpeed   = 30f;
    [Tooltip("Speed when holding Left Shift.")]
    public float sprintSpeed = 150f;

    // ── State ─────────────────────────────────────────────────────────────────

    private float _yaw;
    private float _pitch;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Initialise from current transform so the world doesn't jump on play
        _yaw   = transform.eulerAngles.y;
        _pitch = transform.eulerAngles.x;
        LockCursor();
    }

    private void Update()
    {
        HandleCursorToggle();

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            HandleLook();
            HandleMovement();
        }
    }

    // ── Input handlers ────────────────────────────────────────────────────────

    private void HandleCursorToggle()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            ReleaseCursor();

        if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
            LockCursor();
    }

    private void HandleLook()
    {
        _yaw   += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
        _pitch -= Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
        _pitch  = Mathf.Clamp(_pitch, minPitch, maxPitch);
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    private void HandleMovement()
    {
        bool sprinting = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        float speed = sprinting ? sprintSpeed : moveSpeed;

        float vertical   = Input.GetKey(KeyCode.E) ? 1f : Input.GetKey(KeyCode.Q) ? -1f : 0f;

        Vector3 dir =
            transform.forward * Input.GetAxisRaw("Vertical")   +  // W / S
            transform.right   * Input.GetAxisRaw("Horizontal") +  // A / D
            Vector3.up        * vertical;                         // E / Q

        // Normalise only if > 1 to preserve single-axis speed while allowing diagonals
        if (dir.sqrMagnitude > 1f) dir.Normalize();

        transform.position += dir * speed * Time.deltaTime;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
    }

    private static void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
    }
}
