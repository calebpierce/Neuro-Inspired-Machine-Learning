using UnityEngine;

public class TrackTimerTrigger : MonoBehaviour
{
    public enum TriggerMode
    {
        Start,
        Finish
    }

    [SerializeField] private TriggerMode triggerMode;
    [SerializeField] private TrackTimer trackTimer;
    [SerializeField] private Transform targetCar;

    private bool consumed;

    public void Initialize(TriggerMode mode, TrackTimer timer, Transform car)
    {
        triggerMode = mode;
        trackTimer = timer;
        targetCar = car;
        consumed = false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (consumed || trackTimer == null || targetCar == null)
        {
            return;
        }

        Transform hitTransform = other.attachedRigidbody != null ? other.attachedRigidbody.transform : other.transform;
        if (hitTransform != targetCar)
        {
            return;
        }

        if (triggerMode == TriggerMode.Start)
        {
            trackTimer.StartTimer();
        }
        else
        {
            trackTimer.FinishTimer();
        }

        consumed = true;
    }
}
