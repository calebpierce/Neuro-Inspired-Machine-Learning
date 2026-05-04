using UnityEngine;

[RequireComponent(typeof(PrometeoCarController))]
[RequireComponent(typeof(Rigidbody))]
public class CarControlAdapter : MonoBehaviour
{
    [Header("Reset")]
    [SerializeField] private float resetLift = 0.15f;

    private PrometeoCarController carController;
    private Rigidbody carRigidbody;

    public Rigidbody CarRigidbody => carRigidbody;
    public PrometeoCarController CarController => carController;

    private void Awake()
    {
        carController = GetComponent<PrometeoCarController>();
        carRigidbody = GetComponent<Rigidbody>();

        if (carController != null)
        {
            carController.useExternalInput = true;
            carController.useUI = false;
            carController.useSounds = false;
            carController.useEffects = false;
            carController.useTouchControls = false;
        }
    }

    public void ApplyAction(float steering, float throttle, float brake, float handbrake)
    {
        if (carController == null)
        {
            return;
        }

        carController.SetExternalInput(
            Mathf.Clamp(steering, -1f, 1f),
            Mathf.Clamp01(throttle),
            Mathf.Clamp01(brake),
            handbrake >= 0.5f);
    }

    public void ClearAction()
    {
        if (carController == null)
        {
            return;
        }

        carController.SetExternalInput(0f, 0f, 0f, false);
    }

    public void ResetVehiclePose(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position + Vector3.up * resetLift, rotation);

        if (carRigidbody != null)
        {
            carRigidbody.linearVelocity = Vector3.zero;
            carRigidbody.angularVelocity = Vector3.zero;
            carRigidbody.position = transform.position;
            carRigidbody.rotation = rotation;
            carRigidbody.Sleep();
        }

        if (carController != null)
        {
            carController.ResetVehicleState();
            carController.SetExternalInput(0f, 0f, 0f, false);
        }
    }
}
