using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(CarRayPerception))]
[RequireComponent(typeof(BehaviorParameters))]
public class CarDrivingAgent : Agent
{
    public struct EpisodeResult
    {
        public string trackId;
        public bool completed;
        public string terminationReason;
        public float totalReward;
        public int stepCount;
        public float elapsedSeconds;
        public float maxProgress;
        public float averageSpeed;
    }

    [Header("References")]
    [SerializeField] private DrivingTrainingManager trainingManager;
    [SerializeField] private CarControlAdapter carAdapter;
    [SerializeField] private CarRayPerception rayPerception;

    [Header("Rewards")]
    [SerializeField] private float progressRewardScale = 2f;
    [SerializeField] private float collisionPenalty = -4f;
    [SerializeField] private float terminalCollisionPenalty = -12f;
    [SerializeField] private float terminalCollisionAngleDegrees = 10f;
    [SerializeField] private float offTrackPenalty = -0.75f;
    [SerializeField] private float checkpointReachedReward = 5f;
    [SerializeField] private float finishReward = 20f;
    [SerializeField] private float checkpointTimeBonusScale = 2f;
    [SerializeField] private float checkpointSpeedRewardPerMetersPerSecond = 0.5f;
    [SerializeField] private float finishTimeBonusScale = 10f;
    [SerializeField] private float finishSpeedRewardPerMetersPerSecond = 1f;
    [SerializeField] private float checkpointReachDistance = 3f;
    [SerializeField] private float idlePenaltyThreshold = 0.35f;
    [SerializeField] private float noProgressTimeoutSeconds = 6f;
    [SerializeField] private float minProgressDeltaForTimeoutReset = 1f;
    [SerializeField] private float noProgressPenalty = -0.5f;
    [SerializeField] private float collisionPenaltyCooldownSeconds = 0.5f;
    [SerializeField] private float throttleDeadZone = 0.05f;
    [SerializeField] private float stallGraceSeconds = 1.5f;

    private Rigidbody carRigidbody;
    private BehaviorParameters behaviorParameters;
    private PrometeoCarController carController;
    private float previousDistanceToCheckpoint;
    private float bestDistanceToCheckpoint;
    private float lastMeaningfulDistanceToCheckpoint;
    private float previousCheckpointPlaneDistance;
    private float episodeElapsedSeconds;
    private float lastCheckpointRewardTime;
    private float lastMeaningfulProgressTime;
    private float lastCollisionPenaltyTime;
    private float cumulativeSpeedMagnitude;
    private int speedSampleCount;
    private int episodeSteps;
    private int nextCheckpointIndex;
    private int bestCheckpointIndex;
    private string pendingTerminationReason;
    private bool episodeResultRecorded;

    public override void Initialize()
    {
        if (trainingManager == null)
        {
            trainingManager = FindFirstObjectByType<DrivingTrainingManager>();
        }

        if (carAdapter == null)
        {
            carAdapter = GetComponent<CarControlAdapter>();
        }

        if (rayPerception == null)
        {
            rayPerception = GetComponent<CarRayPerception>();
        }

        behaviorParameters = GetComponent<BehaviorParameters>();
        carController = carAdapter != null ? carAdapter.CarController : GetComponent<PrometeoCarController>();
        carRigidbody = carAdapter != null ? carAdapter.CarRigidbody : GetComponent<Rigidbody>();

        if (behaviorParameters != null)
        {
            Debug.Log(
                $"CarDrivingAgent on '{name}' using behavior '{behaviorParameters.BehaviorName}' " +
                $"with {behaviorParameters.BrainParameters.ActionSpec.NumContinuousActions} continuous and " +
                $"{behaviorParameters.BrainParameters.ActionSpec.NumDiscreteActions} discrete actions.",
                this);
        }
    }

    public override void OnEpisodeBegin()
    {
        if (trainingManager == null)
        {
            return;
        }

        trainingManager.ResetEnvironment();
        nextCheckpointIndex = 0;
        bestCheckpointIndex = 0;
        previousDistanceToCheckpoint = GetDistanceToNextCheckpoint();
        bestDistanceToCheckpoint = previousDistanceToCheckpoint;
        lastMeaningfulDistanceToCheckpoint = previousDistanceToCheckpoint;
        previousCheckpointPlaneDistance = GetSignedDistanceToNextCheckpointPlane();
        episodeElapsedSeconds = 0f;
        lastCheckpointRewardTime = 0f;
        lastMeaningfulProgressTime = 0f;
        lastCollisionPenaltyTime = float.NegativeInfinity;
        cumulativeSpeedMagnitude = 0f;
        speedSampleCount = 0;
        episodeSteps = 0;
        pendingTerminationReason = "running";
        episodeResultRecorded = false;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (trainingManager == null || rayPerception == null || carRigidbody == null)
        {
            return;
        }

        rayPerception.UpdateObservations();
        float[] distances = rayPerception.GetDistances();
        for (int i = 0; i < distances.Length; i++)
        {
            sensor.AddObservation(distances[i] / rayPerception.MaxDistance);
        }

        sensor.AddObservation(carRigidbody.linearVelocity.magnitude / 30f);

        float normalizedSteerAngle = 0f;
        if (carController != null && carController.maxSteeringAngle > 0)
        {
            normalizedSteerAngle = carController.frontLeftCollider.steerAngle / carController.maxSteeringAngle;
        }

        sensor.AddObservation(normalizedSteerAngle);

        Vector3 localDirectionToCheckpoint = GetLocalDirectionToNextCheckpoint();
        sensor.AddObservation(localDirectionToCheckpoint.x);
        sensor.AddObservation(localDirectionToCheckpoint.z);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (trainingManager == null || carAdapter == null)
        {
            return;
        }

        if (carRigidbody == null)
        {
            carRigidbody = carAdapter.CarRigidbody != null ? carAdapter.CarRigidbody : GetComponent<Rigidbody>();
            if (carRigidbody == null)
            {
                Debug.LogError($"CarDrivingAgent on '{name}' could not find a Rigidbody.", this);
                return;
            }
        }

        if (trainingManager.TrackGenerator == null)
        {
            Debug.LogError($"CarDrivingAgent on '{name}' is missing a TrackGenerator reference through DrivingTrainingManager.", this);
            return;
        }

        ActionSegment<float> continuousActions = actions.ContinuousActions;
        if (continuousActions.Length < 2)
        {
            Debug.LogError(
                $"CarDrivingAgent expected 2 continuous actions but received {continuousActions.Length} on '{name}'. " +
                $"Behavior: '{(behaviorParameters != null ? behaviorParameters.BehaviorName : "missing")}'.",
                this);
            return;
        }

        float steering = continuousActions[0];
        float throttle = NormalizePositiveAction(continuousActions[1], throttleDeadZone);
        carAdapter.ApplyAction(steering, throttle, 0f, 0f);
        episodeSteps++;
        episodeElapsedSeconds += Time.fixedDeltaTime;

        float currentDistanceToCheckpoint = GetDistanceToNextCheckpoint();
        float distanceImprovement = Mathf.Max(0f, previousDistanceToCheckpoint - currentDistanceToCheckpoint);
        bestDistanceToCheckpoint = Mathf.Min(bestDistanceToCheckpoint, currentDistanceToCheckpoint);
        float currentCheckpointPlaneDistance = GetSignedDistanceToNextCheckpointPlane();
        float speedMagnitude = carRigidbody.linearVelocity.magnitude;
        cumulativeSpeedMagnitude += speedMagnitude;
        speedSampleCount++;
        int checkpointCount = trainingManager.TrackGenerator.GetCheckpointCount();
        bool hasRemainingCheckpoint = checkpointCount > 0 && nextCheckpointIndex < checkpointCount;

        if (hasRemainingCheckpoint && currentDistanceToCheckpoint <= lastMeaningfulDistanceToCheckpoint - minProgressDeltaForTimeoutReset)
        {
            lastMeaningfulDistanceToCheckpoint = currentDistanceToCheckpoint;
            lastMeaningfulProgressTime = episodeElapsedSeconds;
        }

        if (hasRemainingCheckpoint)
        {
            AddReward(distanceImprovement * progressRewardScale);

            if (HasPassedNextCheckpoint(currentDistanceToCheckpoint, currentCheckpointPlaneDistance))
            {
                AddReward(checkpointReachedReward);
                AddReward(CalculateCheckpointTimeBonus());
                AddReward(CalculateSpeedBonus(speedMagnitude, checkpointSpeedRewardPerMetersPerSecond));
                nextCheckpointIndex++;
                bestCheckpointIndex = Mathf.Max(bestCheckpointIndex, nextCheckpointIndex);
                previousDistanceToCheckpoint = GetDistanceToNextCheckpoint();
                bestDistanceToCheckpoint = previousDistanceToCheckpoint;
                lastMeaningfulDistanceToCheckpoint = previousDistanceToCheckpoint;
                previousCheckpointPlaneDistance = GetSignedDistanceToNextCheckpointPlane();
                lastCheckpointRewardTime = episodeElapsedSeconds;
                lastMeaningfulProgressTime = episodeElapsedSeconds;
            }
            else
            {
                previousDistanceToCheckpoint = currentDistanceToCheckpoint;
                previousCheckpointPlaneDistance = currentCheckpointPlaneDistance;
            }
        }
        else
        {
            previousDistanceToCheckpoint = currentDistanceToCheckpoint;
            previousCheckpointPlaneDistance = currentCheckpointPlaneDistance;
        }

        if (episodeElapsedSeconds >= stallGraceSeconds && speedMagnitude < idlePenaltyThreshold * 0.8f)
        {
            AddReward(noProgressPenalty * 0.02f);
        }

        if (trainingManager.TrackGenerator.HasFinishedLap(transform.position))
        {
            AddReward(finishReward);
            AddReward(CalculateFinishTimeBonus());
            AddReward(CalculateSpeedBonus(speedMagnitude, finishSpeedRewardPerMetersPerSecond));
            pendingTerminationReason = "finish";
            RecordEpisodeResult(true);
            EndEpisode();
            return;
        }

        if (trainingManager.TrackGenerator.IsOffTrack(transform.position))
        {
            AddReward(offTrackPenalty);
            pendingTerminationReason = "off_track";
            RecordEpisodeResult(false);
            EndEpisode();
            return;
        }

        if (episodeElapsedSeconds - lastMeaningfulProgressTime >= noProgressTimeoutSeconds)
        {
            AddReward(noProgressPenalty);
            pendingTerminationReason = "no_progress_timeout";
            RecordEpisodeResult(false);
            EndEpisode();
            return;
        }

        if (MaxStep > 0 && StepCount >= MaxStep - 1)
        {
            pendingTerminationReason = "max_step";
            RecordEpisodeResult(false);
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<float> continuousActions = actionsOut.ContinuousActions;
        if (continuousActions.Length < 2)
        {
            return;
        }

        continuousActions[0] = Input.GetKey(KeyCode.A) ? -1f : Input.GetKey(KeyCode.D) ? 1f : 0f;
        continuousActions[1] = Input.GetKey(KeyCode.W) ? 1f : 0f;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!enabled || collision.collider == null)
        {
            return;
        }

        string hitName = collision.collider.name;
        bool hitBarrier = hitName.StartsWith("Barrier");
        if (!hitBarrier && trainingManager != null && !trainingManager.TrackGenerator.IsOffTrack(transform.position))
        {
            return;
        }

        if (episodeElapsedSeconds - lastCollisionPenaltyTime < collisionPenaltyCooldownSeconds)
        {
            return;
        }

        lastCollisionPenaltyTime = episodeElapsedSeconds;
        AddReward(collisionPenalty);

        if (IsTerminalBarrierImpact(collision))
        {
            AddReward(terminalCollisionPenalty);
            pendingTerminationReason = "barrier_collision";
            RecordEpisodeResult(false);
            EndEpisode();
        }
    }

    private void RecordEpisodeResult(bool completed)
    {
        if (episodeResultRecorded || trainingManager == null)
        {
            return;
        }

        episodeResultRecorded = true;
        var result = new EpisodeResult
        {
            trackId = string.IsNullOrWhiteSpace(trainingManager.ActiveTrackId) ? trainingManager.TrackGenerator.CurrentTrackId : trainingManager.ActiveTrackId,
            completed = completed,
            terminationReason = pendingTerminationReason,
            totalReward = GetCumulativeReward(),
            stepCount = episodeSteps,
            elapsedSeconds = episodeElapsedSeconds,
            maxProgress = bestCheckpointIndex,
            averageSpeed = speedSampleCount > 0 ? cumulativeSpeedMagnitude / speedSampleCount : 0f
        };

        trainingManager.RecordEpisodeResult(result);
    }

    private float GetDistanceToNextCheckpoint()
    {
        if (trainingManager == null || trainingManager.TrackGenerator == null)
        {
            return 0f;
        }

        int checkpointCount = trainingManager.TrackGenerator.GetCheckpointCount();
        if (checkpointCount <= 0 || nextCheckpointIndex >= checkpointCount)
        {
            return trainingManager.TrackGenerator.GetDistanceToFinish(transform.position);
        }

        return Vector3.Distance(transform.position, trainingManager.TrackGenerator.GetCheckpointPosition(nextCheckpointIndex));
    }

    private Vector3 GetLocalDirectionToNextCheckpoint()
    {
        if (trainingManager == null || trainingManager.TrackGenerator == null)
        {
            return Vector3.forward;
        }

        int checkpointCount = trainingManager.TrackGenerator.GetCheckpointCount();
        Vector3 targetPosition = checkpointCount <= 0 || nextCheckpointIndex >= checkpointCount
            ? trainingManager.TrackGenerator.GetFinishPoint()
            : trainingManager.TrackGenerator.GetCheckpointPosition(nextCheckpointIndex);

        Vector3 localTarget = transform.InverseTransformPoint(targetPosition);
        Vector3 planarDirection = new Vector3(localTarget.x, 0f, localTarget.z);
        if (planarDirection.sqrMagnitude <= 0.0001f)
        {
            return Vector3.forward;
        }

        return planarDirection.normalized;
    }

    private float GetSignedDistanceToNextCheckpointPlane()
    {
        if (trainingManager == null || trainingManager.TrackGenerator == null)
        {
            return 0f;
        }

        ProceduralTrackGenerator generator = trainingManager.TrackGenerator;
        int checkpointCount = generator.GetCheckpointCount();
        Vector3 checkpointPosition;
        Vector3 checkpointForward;

        if (checkpointCount <= 0 || nextCheckpointIndex >= checkpointCount)
        {
            checkpointPosition = generator.GetFinishPoint();
            checkpointForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (checkpointForward.sqrMagnitude <= 0.0001f)
            {
                checkpointForward = Vector3.forward;
            }
        }
        else
        {
            checkpointPosition = generator.GetCheckpointPosition(nextCheckpointIndex);
            checkpointForward = generator.GetCheckpointForward(nextCheckpointIndex);
        }

        return Vector3.Dot(transform.position - checkpointPosition, checkpointForward);
    }

    private bool HasPassedNextCheckpoint(float currentDistanceToCheckpoint, float currentCheckpointPlaneDistance)
    {
        if (currentDistanceToCheckpoint <= checkpointReachDistance)
        {
            return true;
        }

        bool crossedPlane = previousCheckpointPlaneDistance < 0f && currentCheckpointPlaneDistance >= 0f;
        return crossedPlane && currentDistanceToCheckpoint <= checkpointReachDistance * 2f;
    }

    private void OnDrawGizmos()
    {
        if (trainingManager == null)
        {
            trainingManager = FindFirstObjectByType<DrivingTrainingManager>();
        }

        if (trainingManager == null || trainingManager.TrackGenerator == null)
        {
            return;
        }

        ProceduralTrackGenerator generator = trainingManager.TrackGenerator;
        if (!generator.ShowCheckpointGizmos || generator.GetCheckpointCount() <= 0)
        {
            return;
        }

        int clampedCheckpointIndex = Mathf.Clamp(nextCheckpointIndex, 0, generator.GetCheckpointCount() - 1);
        Vector3 checkpointPosition = generator.GetCheckpointPosition(clampedCheckpointIndex);
        float radius = Mathf.Max(generator.CheckpointGizmoRadius * 1.35f, generator.CheckpointGizmoRadius + 0.1f);

        Gizmos.color = generator.ActiveCheckpointGizmoColor;
        Gizmos.DrawSphere(checkpointPosition, radius);

#if UNITY_EDITOR
        Handles.color = generator.ActiveCheckpointGizmoColor;
        Handles.Label(checkpointPosition + Vector3.up * (radius + 0.2f), $"Next {clampedCheckpointIndex}");
#endif
    }

    private static float NormalizePositiveAction(float value, float activationThreshold)
    {
        float positiveValue = Mathf.Clamp01(value);
        if (positiveValue <= activationThreshold)
        {
            return 0f;
        }

        float normalizedRange = 1f - activationThreshold;
        return normalizedRange <= 0.0001f ? 1f : (positiveValue - activationThreshold) / normalizedRange;
    }

    private float CalculateCheckpointTimeBonus()
    {
        float segmentElapsedSeconds = episodeElapsedSeconds - lastCheckpointRewardTime;
        return checkpointTimeBonusScale / Mathf.Max(segmentElapsedSeconds, 1f);
    }

    private float CalculateFinishTimeBonus()
    {
        float segmentElapsedSeconds = episodeElapsedSeconds - lastCheckpointRewardTime;
        return finishTimeBonusScale / Mathf.Max(segmentElapsedSeconds, 1f);
    }

    private static float CalculateSpeedBonus(float speedMagnitude, float scale)
    {
        return Mathf.Max(0f, speedMagnitude) * scale;
    }

    private bool IsTerminalBarrierImpact(Collision collision)
    {
        if (collision.contactCount == 0 || carRigidbody == null)
        {
            return false;
        }

        Vector3 impactVelocity = carRigidbody.linearVelocity;
        impactVelocity.y = 0f;
        if (impactVelocity.sqrMagnitude <= 0.01f)
        {
            return false;
        }

        Vector3 averageNormal = Vector3.zero;
        for (int i = 0; i < collision.contactCount; i++)
        {
            averageNormal += collision.GetContact(i).normal;
        }

        averageNormal /= collision.contactCount;
        averageNormal = Vector3.ProjectOnPlane(averageNormal, Vector3.up).normalized;
        if (averageNormal.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        Vector3 incomingDirection = -impactVelocity.normalized;
        float angleToNormal = Vector3.Angle(incomingDirection, averageNormal);
        float angleFromSurface = Mathf.Abs(90f - angleToNormal);
        return angleFromSurface > terminalCollisionAngleDegrees;
    }
}
