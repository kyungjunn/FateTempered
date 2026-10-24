using UnityEngine;

public class PlayerMovement : MonoBehaviour
{
    public float moveSpeed = 5f; // 플레이어 이동속도 (PlayerStats가 없을 때만 사용)
    public Transform cameraTransform;

    public float jumpForce = 5f;
    private Rigidbody rb;
    private PlayerStats stats;

    // 이동속도 증강/페널티가 실제 이동에 반영되도록 PlayerStats를 우선 사용
    private float MoveSpeed => stats != null ? stats.MoveSpeed : moveSpeed;

    // 현재 땅에 닿아있으면 true, 공중이면 false를 반환하는 함수
    public bool CheckGrounded()
    {
        // 캐릭터 중심에서 아래쪽으로 1.1f 길이의 레이저를 쏴서 땅이 있는지 확인합니다.
        return Physics.Raycast(transform.position, Vector3.down, 1.1f);
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        stats = GetComponent<PlayerStats>();
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        //Move();
    }

    public void Move(Vector2 input)
    {
        if (!NetworkPlayerMotor.CanSimulate(gameObject)) return;
        // 카메라 기준 방향
        Vector3 forward = cameraTransform.forward;
        Vector3 right = cameraTransform.right;

        // y값 제거 (땅 기준 이동)
        forward.y = 0f;
        right.y = 0f;

        forward.Normalize();
        right.Normalize();

        // 카메라 기준 이동 벡터 생성
        Vector3 move = forward * input.y + right * input.x;

        // 대각선 속도 보정
        move = move.normalized;

        transform.Translate(move * MoveSpeed * Time.deltaTime, Space.World);
    }

    // Network simulation uses server stats and server time, never a client camera transform.
    public void MoveAuthoritative(Vector2 input, float yaw, float deltaTime)
    {
        if (!NetworkPlayerMotor.CanSimulate(gameObject)) return;
        Vector3 direction = Quaternion.Euler(0f, yaw, 0f) *
            new Vector3(input.x, 0f, input.y);
        rb.MovePosition(rb.position + direction.normalized * MoveSpeed * deltaTime);
    }

    public bool Jump()
    {
        if (!NetworkPlayerMotor.CanSimulate(gameObject)) return false;
        if (CheckGrounded()) 
        {
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
            return true;
        }

        return false;
    }

    private void OnValidate()
    {
        moveSpeed = Mathf.Max(0f, moveSpeed);
        jumpForce = Mathf.Max(0f, jumpForce);
    }
}
    