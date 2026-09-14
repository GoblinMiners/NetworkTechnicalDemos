using UnityEngine;

public class PlayerMiner : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 10f;
    public float lookSpeed = 2f;
    
    private float _pitch = 0f;
    private float _yaw = 0f;

    [Header("Mining Settings")]
    public float reach = 10f;
    public float mineRadius = 2f;
    
    // Set to 5f so it instantly carves solid rock (which is set to 1f) to 0f
    public float minePower = 5f; 

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        
        Vector3 angles = transform.eulerAngles;
        _pitch = angles.x;
        _yaw = angles.y;
    }

    void Update()
    {
        HandleCameraLook();
        HandleMovement();

        if (Input.GetMouseButton(0)) MineRock();
        
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void HandleCameraLook()
    {
        _yaw += Input.GetAxis("Mouse X") * lookSpeed;
        _pitch -= Input.GetAxis("Mouse Y") * lookSpeed;
        _pitch = Mathf.Clamp(_pitch, -90f, 90f); 
        transform.eulerAngles = new Vector3(_pitch, _yaw, 0f);
    }

    private void HandleMovement()
    {
        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");
        float y = 0f;

        if (Input.GetKey(KeyCode.Space)) y = 1f;
        if (Input.GetKey(KeyCode.LeftShift)) y = -1f;

        Vector3 move = transform.right * x + transform.up * y + transform.forward * z;
        transform.position += move * (moveSpeed * Time.deltaTime);
    }

    private void MineRock()
    {
        Ray ray = new Ray(transform.position, transform.forward);
        
        if (Physics.Raycast(ray, out RaycastHit hit, reach))
        {
            // By multiplying power by Time.deltaTime, it melts smoothly instead of flashing
            World.Instance.ModifyTerrain(hit.point, mineRadius, minePower * Time.deltaTime);
        }
    }
}