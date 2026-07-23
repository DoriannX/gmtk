using UnityEngine;

// Slowly drifts a cloud across the sky and wraps it back around.
[DisallowMultipleComponent]
public class CloudDrift : MonoBehaviour
{
    public Vector3 velocity = new Vector3(3f, 0f, 0f);
    [Tooltip("Total travel before wrapping back to the start.")]
    public float wrapDistance = 600f;

    Vector3 _start;

    void Start() => _start = transform.position;

    void Update()
    {
        transform.position += velocity * Time.deltaTime;
        Vector3 dir = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : Vector3.right;
        float travelled = Vector3.Dot(transform.position - _start, dir);
        if (travelled > wrapDistance)
            transform.position -= dir * (wrapDistance * 2f);
    }
}
