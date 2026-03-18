using UnityEngine;

public class CameraMovement : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 10f;
    public float sprintMultiplier = 2f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2f;
    public float smoothTime = 0.05f;

    private float yaw;
    private float pitch;

    private Vector2 currentMouseDelta;
    private Vector2 currentMouseDeltaVelocity;

    void Start()
    {
        Vector3 angles = transform.eulerAngles;
        yaw = angles.y;
        pitch = angles.x;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
        HandleCursor();
    }

    void HandleMouseLook()
    {
        Vector2 targetMouseDelta = new Vector2(
            Input.GetAxis("Mouse X"),
            Input.GetAxis("Mouse Y")
        );

        currentMouseDelta = Vector2.SmoothDamp(
            currentMouseDelta,
            targetMouseDelta,
            ref currentMouseDeltaVelocity,
            smoothTime
        );

        yaw += currentMouseDelta.x * mouseSensitivity * 100f * Time.deltaTime;
        pitch -= currentMouseDelta.y * mouseSensitivity * 100f * Time.deltaTime;

        pitch = Mathf.Clamp(pitch, -90f, 90f);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void HandleMovement()
    {
        float speed = moveSpeed;

        if (Input.GetKey(KeyCode.LeftShift))
            speed *= sprintMultiplier;

        Vector3 direction = new Vector3(
            Input.GetAxis("Horizontal"),   // A/D
            0,
            Input.GetAxis("Vertical")      // W/S
        );

        Vector3 move = transform.right * direction.x + transform.forward * direction.z;

        // Up/Down (Q/E)
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

        transform.position += move * speed * Time.deltaTime;
    }

    void HandleCursor()
    {
        // Press ESC to unlock cursor
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Click to lock again
        if (Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}