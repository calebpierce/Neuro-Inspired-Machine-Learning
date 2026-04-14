using UnityEngine;

public class CameraRollLock : MonoBehaviour
{
    private float lockedWorldPitch;

    private void Start()
    {
        lockedWorldPitch = transform.eulerAngles.x;
    }

    private void LateUpdate()
    {
        Vector3 euler = transform.eulerAngles;
        transform.rotation = Quaternion.Euler(lockedWorldPitch, euler.y, 0f);
    }
}
