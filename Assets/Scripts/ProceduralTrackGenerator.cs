using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class ProceduralTrackGenerator : MonoBehaviour
{
    private const float MaxConnectorAngle = 45f;

    [Header("Input")]
    [SerializeField] private KeyCode regenerateKey = KeyCode.N;

    [Header("Car")]
    [SerializeField] private Transform carTransform;
    [SerializeField] private float carSpawnHeight = 1.2f;

    [Header("Timing")]
    [SerializeField] private TrackTimer trackTimer;

    [Header("Track Shape")]
    [SerializeField] private int segmentCount = 18;
    [SerializeField] private float segmentLength = 14f;
    [SerializeField] private float maxTurnAngle = 45f;
    [SerializeField] private float laneStepLimit = 9f;
    [SerializeField] private float horizontalBounds = 28f;

    [Header("Road")]
    [SerializeField] private float roadWidth = 8f;
    [SerializeField] private float roadThickness = 0.4f;
    [SerializeField] private float roadJointLength = 6f;
    [SerializeField] private Material roadMaterial;
    [SerializeField] private PhysicsMaterial roadPhysicsMaterial;

    [Header("Markers")]
    [SerializeField] private float lineLength = 2.5f;
    [SerializeField] private float lineThickness = 0.05f;
    [SerializeField] private Color startLineColor = Color.yellow;
    [SerializeField] private Color finishLineColor = Color.yellow;

    [Header("Barriers")]
    [SerializeField] private bool generateBarriers = true;
    [SerializeField] private float barrierHeight = 1.5f;
    [SerializeField] private float barrierWidth = 0.35f;
    [SerializeField] private Color barrierColor = Color.red;

    [Header("Random Obstacles")]
    [SerializeField] private bool generateRandomObstacles = false;
    [SerializeField] [Range(0f, 1f)] private float obstacleSpawnChance = 0.75f;
    [SerializeField] private float obstacleWidth = 1.5f;
    [SerializeField] private float obstacleLength = 2.5f;
    [SerializeField] private float obstacleHeight = 1.2f;
    [SerializeField] private float obstacleSideMargin = 0.5f;
    [SerializeField] private Color obstacleColor = new Color(1f, 0.6f, 0.1f);

    [Header("Randomness")]
    [SerializeField] private bool randomizeSeedOnGenerate = true;
    [SerializeField] private int seed = 12345;

    [Header("Persistence")]
    [SerializeField] private string trackSaveFolder = "TrainingData/Tracks";

    [Header("Debug")]
    [SerializeField] private bool showCheckpointGizmos = true;
    [SerializeField] private float checkpointGizmoRadius = 0.6f;
    [SerializeField] private Color checkpointGizmoColor = new Color(1f, 0.5f, 0f, 0.9f);
    [SerializeField] private Color activeCheckpointGizmoColor = new Color(0f, 1f, 0f, 0.95f);

    private readonly List<Vector3> pathPoints = new List<Vector3>();
    private Transform trackRoot;
    private Rigidbody carRigidbody;
    private Material runtimeStartLineMaterial;
    private Material runtimeFinishLineMaterial;
    private Material runtimeBarrierMaterial;
    private Material runtimeObstacleMaterial;
    private bool raceStarted;
    private bool raceFinished;
    private Vector3 raceStartPoint;
    private Vector3 raceFinishPoint;
    private Vector3 raceFinishForward;
    private Vector3 previousTimingPosition;
    private float totalTrackLength;
    private string currentTrackId;

    public int SegmentCount => segmentCount;
    public float SegmentLength => segmentLength;
    public float RoadWidth => roadWidth;
    public float RoadThickness => roadThickness;
    public float RoadJointLength => roadJointLength;
    public float LineLength => lineLength;
    public float LineThickness => lineThickness;
    public bool GenerateBarriers => generateBarriers;
    public bool GenerateRandomObstacles => generateRandomObstacles;
    public float BarrierHeight => barrierHeight;
    public float BarrierWidth => barrierWidth;
    public float ObstacleSpawnChance => obstacleSpawnChance;
    public float ObstacleWidth => obstacleWidth;
    public float ObstacleLength => obstacleLength;
    public float ObstacleHeight => obstacleHeight;
    public float ObstacleSideMargin => obstacleSideMargin;
    public int Seed => seed;
    public IReadOnlyList<Vector3> PathPoints => pathPoints;
    public string CurrentTrackId => currentTrackId;
    public bool ShowCheckpointGizmos => showCheckpointGizmos;
    public float CheckpointGizmoRadius => checkpointGizmoRadius;
    public Color CheckpointGizmoColor => checkpointGizmoColor;
    public Color ActiveCheckpointGizmoColor => activeCheckpointGizmoColor;

    private void Start()
    {
        EnsureCarTransform();

        GenerateTrack();
    }

    private void Update()
    {
        UpdateRaceTimerState();

        if (Input.GetKeyDown(regenerateKey))
        {
            GenerateTrack();
        }
    }

    public void GenerateTrack()
    {
        EnsureCarTransform();
        EnsureTrackTimer();

        if (carTransform == null)
        {
            Debug.LogWarning("ProceduralTrackGenerator could not find a car with PrometeoCarController in the scene.");
            return;
        }

        if (segmentCount < 3)
        {
            segmentCount = 3;
        }

        if (segmentLength < 4f)
        {
            segmentLength = 4f;
        }

        maxTurnAngle = Mathf.Clamp(maxTurnAngle, 0f, MaxConnectorAngle);

        if (laneStepLimit < 0f)
        {
            laneStepLimit = 0f;
        }

        if (horizontalBounds < roadWidth)
        {
            horizontalBounds = roadWidth;
        }

        if (roadJointLength < barrierWidth)
        {
            roadJointLength = roadWidth * 0.75f;
        }

        obstacleSpawnChance = Mathf.Clamp01(obstacleSpawnChance);
        obstacleWidth = Mathf.Max(0.25f, obstacleWidth);
        obstacleLength = Mathf.Max(0.25f, obstacleLength);
        obstacleHeight = Mathf.Max(0.25f, obstacleHeight);
        obstacleSideMargin = Mathf.Max(0f, obstacleSideMargin);

        if (randomizeSeedOnGenerate)
        {
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        }

        var randomState = UnityEngine.Random.state;
        UnityEngine.Random.InitState(seed);

        BuildForwardPath();

        UnityEngine.Random.state = randomState;

        ClearPreviousTrack();
        trackRoot = new GameObject("Generated Track").transform;
        trackRoot.SetParent(transform, false);

        BuildRoadSegments();
        BuildCourseMarker(true);
        BuildCourseMarker(false);
        RecalculateTrackLength();
        RepositionCarAtStart();
        ResetTrackTimer();
        StartTrackTimer();
        raceStarted = true;
        raceFinished = false;
        raceStartPoint = pathPoints[1];
        raceFinishPoint = pathPoints[pathPoints.Count - 2];
        raceFinishForward = (pathPoints[pathPoints.Count - 1] - pathPoints[pathPoints.Count - 2]).normalized;
        previousTimingPosition = GetCarPositionForTiming();
        currentTrackId = null;
    }

    public bool TryLoadTrackFromJson(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"Track file not found: {filePath}");
            return false;
        }

        string json = File.ReadAllText(filePath);
        TrackLayoutSnapshot snapshot = JsonUtility.FromJson<TrackLayoutSnapshot>(json);
        if (snapshot == null || snapshot.pathPoints == null || snapshot.pathPoints.Count < 3)
        {
            Debug.LogWarning($"Track file is invalid: {filePath}");
            return false;
        }

        LoadTrack(snapshot);
        return true;
    }

    public string SaveCurrentTrackToJson(string trackId = null, string overrideFolder = null)
    {
        if (pathPoints.Count < 3)
        {
            return string.Empty;
        }

        string id = string.IsNullOrWhiteSpace(trackId)
            ? $"track_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Mathf.Abs(seed)}"
            : trackId.Trim();

        string folderPath = GetTrackFolderPath(overrideFolder);
        Directory.CreateDirectory(folderPath);

        TrackLayoutSnapshot snapshot = TrackLayoutSnapshot.FromPath(id, seed, pathPoints, this);
        string json = JsonUtility.ToJson(snapshot, true);
        string filePath = Path.Combine(folderPath, $"{id}.json");
        File.WriteAllText(filePath, json);
        currentTrackId = id;

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif

        return filePath;
    }

    public void LoadTrack(TrackLayoutSnapshot snapshot)
    {
        if (snapshot == null || snapshot.pathPoints == null || snapshot.pathPoints.Count < 3)
        {
            Debug.LogWarning("Cannot load a null or incomplete track snapshot.");
            return;
        }

        seed = snapshot.seed;
        currentTrackId = snapshot.trackId;
        segmentCount = Mathf.Max(3, snapshot.segmentCount);
        segmentLength = Mathf.Max(4f, snapshot.segmentLength);
        roadWidth = Mathf.Max(1f, snapshot.roadWidth);
        roadThickness = Mathf.Max(0.05f, snapshot.roadThickness);
        roadJointLength = Mathf.Max(0.1f, snapshot.roadJointLength);
        lineLength = Mathf.Max(0.1f, snapshot.lineLength);
        lineThickness = Mathf.Max(0.01f, snapshot.lineThickness);
        generateBarriers = snapshot.generateBarriers;
        barrierHeight = Mathf.Max(0.1f, snapshot.barrierHeight);
        barrierWidth = Mathf.Max(0.05f, snapshot.barrierWidth);
        generateRandomObstacles = snapshot.generateRandomObstacles;
        obstacleSpawnChance = Mathf.Clamp01(snapshot.obstacleSpawnChance);
        obstacleWidth = Mathf.Max(0.25f, snapshot.obstacleWidth);
        obstacleLength = Mathf.Max(0.25f, snapshot.obstacleLength);
        obstacleHeight = Mathf.Max(0.25f, snapshot.obstacleHeight);
        obstacleSideMargin = Mathf.Max(0f, snapshot.obstacleSideMargin);

        pathPoints.Clear();
        pathPoints.AddRange(snapshot.ToPathPoints());

        ClearPreviousTrack();
        trackRoot = new GameObject("Generated Track").transform;
        trackRoot.SetParent(transform, false);

        BuildRoadSegments();
        BuildCourseMarker(true);
        BuildCourseMarker(false);
        RecalculateTrackLength();
        RepositionCarAtStart();
        ResetTrackTimer();
        StartTrackTimer();
        raceStarted = true;
        raceFinished = false;
        raceStartPoint = pathPoints[1];
        raceFinishPoint = pathPoints[pathPoints.Count - 2];
        raceFinishForward = (pathPoints[pathPoints.Count - 1] - pathPoints[pathPoints.Count - 2]).normalized;
        previousTimingPosition = GetCarPositionForTiming();
    }

    private void BuildForwardPath()
    {
        pathPoints.Clear();

        float currentX = 0f;
        float currentZ = 0f;
        pathPoints.Add(new Vector3(currentX, 0f, currentZ - segmentLength));
        pathPoints.Add(new Vector3(currentX, 0f, currentZ));

        for (int i = 0; i < segmentCount; i++)
        {
            float maxStepFromAngle = Mathf.Tan(maxTurnAngle * Mathf.Deg2Rad) * segmentLength;
            float maxStep = Mathf.Min(laneStepLimit, maxStepFromAngle);
            float xOffset = UnityEngine.Random.Range(-maxStep, maxStep);
            currentX = Mathf.Clamp(currentX + xOffset, -horizontalBounds, horizontalBounds);
            currentZ += segmentLength;
            pathPoints.Add(new Vector3(currentX, 0f, currentZ));
        }
    }

    private void ClearPreviousTrack()
    {
        if (trackRoot == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(trackRoot.gameObject);
        }
        else
        {
#if UNITY_EDITOR
            DestroyImmediate(trackRoot.gameObject);
#else
            Destroy(trackRoot.gameObject);
#endif
        }

        trackRoot = null;
    }

    private void BuildRoadSegments()
    {
        for (int i = 0; i < pathPoints.Count - 1; i++)
        {
            Vector3 current = pathPoints[i];
            Vector3 next = pathPoints[i + 1];
            Vector3 direction = next - current;
            float length = direction.magnitude;

            if (length <= 0.01f)
            {
                continue;
            }

            GameObject roadSegment = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roadSegment.name = $"Road Segment {i:000}";
            roadSegment.transform.SetParent(trackRoot, false);
            roadSegment.transform.SetPositionAndRotation(
                (current + next) * 0.5f + Vector3.down * (roadThickness * 0.5f),
                Quaternion.LookRotation(direction.normalized, Vector3.up));
            roadSegment.transform.localScale = new Vector3(roadWidth, roadThickness, length + 0.5f);

            ApplyRoadSurfaceSettings(roadSegment);
        }

        BuildRoadJoints();

        if (generateBarriers)
        {
            BuildBarriers(true);
            BuildBarriers(false);
        }

        if (generateRandomObstacles)
        {
            BuildRandomObstacles();
        }
    }

    private void BuildRoadJoints()
    {
        for (int i = 1; i < pathPoints.Count - 1; i++)
        {
            Vector3 previous = pathPoints[i - 1];
            Vector3 current = pathPoints[i];
            Vector3 next = pathPoints[i + 1];
            Vector3 jointForward = GetJointForward(previous, current, next);

            GameObject roadJoint = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roadJoint.name = $"Road Joint {i:000}";
            roadJoint.transform.SetParent(trackRoot, false);
            roadJoint.transform.SetPositionAndRotation(
                current + Vector3.down * (roadThickness * 0.5f),
                Quaternion.LookRotation(jointForward, Vector3.up));
            roadJoint.transform.localScale = new Vector3(roadWidth, roadThickness, roadJointLength);

            ApplyRoadSurfaceSettings(roadJoint);
        }
    }

    private void BuildCourseMarker(bool isStart)
    {
        if (pathPoints.Count < 2 || trackRoot == null)
        {
            return;
        }

        int index = isStart ? 1 : pathPoints.Count - 2;
        Vector3 current = pathPoints[index];
        Vector3 next = pathPoints[index + 1];
        Vector3 forward = (next - current).normalized;

        GameObject line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = isStart ? "Start Line" : "Finish Line";
        line.transform.SetParent(trackRoot, false);
        line.transform.SetPositionAndRotation(
            current + Vector3.up * 0.01f,
            Quaternion.LookRotation(forward, Vector3.up));
        line.transform.localScale = new Vector3(roadWidth * 0.95f, lineThickness, lineLength);

        Collider lineCollider = line.GetComponent<Collider>();
        if (lineCollider != null)
        {
            lineCollider.isTrigger = true;
        }

        BoxCollider triggerCollider = line.GetComponent<BoxCollider>();
        if (triggerCollider != null)
        {
            triggerCollider.size = new Vector3(roadWidth * 0.95f, 3f, lineLength);
            triggerCollider.center = new Vector3(0f, 1.5f, 0f);
        }

        Renderer renderer = line.GetComponent<Renderer>();
        if (renderer == null)
        {
            return;
        }

        renderer.sharedMaterial = isStart ? GetOrCreateStartLineMaterial() : GetOrCreateFinishLineMaterial();

        TrackTimerTrigger trigger = line.AddComponent<TrackTimerTrigger>();
        trigger.Initialize(
            isStart ? TrackTimerTrigger.TriggerMode.Start : TrackTimerTrigger.TriggerMode.Finish,
            trackTimer,
            carTransform != null ? carTransform : ResolveCarTransform());
        trigger.enabled = false;
    }

    private Material GetOrCreateStartLineMaterial()
    {
        if (runtimeStartLineMaterial == null)
        {
            runtimeStartLineMaterial = CreateMarkerMaterial(startLineColor);
        }
        else
        {
            runtimeStartLineMaterial.color = startLineColor;
        }

        return runtimeStartLineMaterial;
    }

    private Material GetOrCreateFinishLineMaterial()
    {
        if (runtimeFinishLineMaterial == null)
        {
            runtimeFinishLineMaterial = CreateMarkerMaterial(finishLineColor);
        }
        else
        {
            runtimeFinishLineMaterial.color = finishLineColor;
        }

        return runtimeFinishLineMaterial;
    }

    private static Material CreateMarkerMaterial(Color color)
    {
        Shader shader = Shader.Find("Standard");
        Material material = new Material(shader);
        material.color = color;
        return material;
    }

    private void ApplyRoadSurfaceSettings(GameObject surface)
    {
        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer != null && roadMaterial != null)
        {
            renderer.sharedMaterial = roadMaterial;
        }

        BoxCollider collider = surface.GetComponent<BoxCollider>();
        if (collider != null && roadPhysicsMaterial != null)
        {
            collider.sharedMaterial = roadPhysicsMaterial;
        }
    }

    private void ApplyBarrierSurfaceSettings(GameObject surface)
    {
        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetOrCreateBarrierMaterial();
        }

        Collider collider = surface.GetComponent<Collider>();
        if (collider != null && roadPhysicsMaterial != null)
        {
            collider.sharedMaterial = roadPhysicsMaterial;
        }
    }

    private void BuildBarriers(bool leftSide)
    {
        for (int segmentIndex = 0; segmentIndex < pathPoints.Count - 1; segmentIndex++)
        {
            Vector3 start = GetBarrierCornerPoint(segmentIndex, leftSide, true);
            Vector3 end = GetBarrierCornerPoint(segmentIndex, leftSide, false);
            Vector3 connector = end - start;
            float length = connector.magnitude;

            if (length <= 0.01f)
            {
                continue;
            }

            GameObject barrier = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrier.name = leftSide ? $"Barrier L {segmentIndex:000}" : $"Barrier R {segmentIndex:000}";
            barrier.transform.SetParent(trackRoot, false);
            barrier.transform.SetPositionAndRotation(
                (start + end) * 0.5f,
                Quaternion.LookRotation(connector.normalized, Vector3.up));
            barrier.transform.localScale = new Vector3(barrierWidth, barrierHeight, length);
            barrier.AddComponent<RaycastObstacle>();

            ApplyBarrierSurfaceSettings(barrier);
        }
    }

    private Material GetOrCreateBarrierMaterial()
    {
        if (runtimeBarrierMaterial == null)
        {
            runtimeBarrierMaterial = CreateMarkerMaterial(barrierColor);
        }
        else
        {
            runtimeBarrierMaterial.color = barrierColor;
        }

        return runtimeBarrierMaterial;
    }

    private Material GetOrCreateObstacleMaterial()
    {
        if (runtimeObstacleMaterial == null)
        {
            runtimeObstacleMaterial = CreateMarkerMaterial(obstacleColor);
        }
        else
        {
            runtimeObstacleMaterial.color = obstacleColor;
        }

        return runtimeObstacleMaterial;
    }

    private void ApplyObstacleSurfaceSettings(GameObject surface)
    {
        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetOrCreateObstacleMaterial();
        }

        Collider collider = surface.GetComponent<Collider>();
        if (collider != null && roadPhysicsMaterial != null)
        {
            collider.sharedMaterial = roadPhysicsMaterial;
        }
    }

    private void BuildRandomObstacles()
    {
        if (trackRoot == null || pathPoints.Count < 4)
        {
            return;
        }

        var obstacleRandom = new System.Random(seed ^ 0x5F3759DF);

        for (int segmentIndex = 1; segmentIndex < pathPoints.Count - 2; segmentIndex++)
        {
            if (obstacleRandom.NextDouble() > obstacleSpawnChance)
            {
                continue;
            }

            Vector3 start = pathPoints[segmentIndex];
            Vector3 end = pathPoints[segmentIndex + 1];
            Vector3 segment = end - start;
            float segmentLengthValue = segment.magnitude;
            if (segmentLengthValue <= 0.01f)
            {
                continue;
            }

            Vector3 forward = segment / segmentLengthValue;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            float maxLateralOffset = Mathf.Max(0f, (roadWidth * 0.5f) - (obstacleWidth * 0.5f) - obstacleSideMargin);
            float lateralOffset = maxLateralOffset > 0f
                ? Mathf.Lerp(-maxLateralOffset, maxLateralOffset, (float)obstacleRandom.NextDouble())
                : 0f;

            GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = $"Segment Obstacle {segmentIndex:000}";
            obstacle.transform.SetParent(trackRoot, false);
            obstacle.transform.SetPositionAndRotation(
                Vector3.Lerp(start, end, 0.5f) + right * lateralOffset + Vector3.up * ((obstacleHeight * 0.5f) - (roadThickness * 0.5f)),
                Quaternion.LookRotation(forward, Vector3.up));
            obstacle.transform.localScale = new Vector3(
                Mathf.Max(0.25f, Mathf.Min(obstacleWidth, roadWidth - obstacleSideMargin * 2f)),
                obstacleHeight,
                Mathf.Min(obstacleLength, segmentLengthValue));
            obstacle.AddComponent<RaycastObstacle>();

            ApplyObstacleSurfaceSettings(obstacle);
        }
    }

    private Vector3 GetBarrierCornerPoint(int segmentIndex, bool leftSide, bool atStart)
    {
        int cornerIndex = atStart ? segmentIndex : segmentIndex + 1;
        float side = leftSide ? -1f : 1f;
        float lateralOffset = (roadWidth * 0.5f) + (barrierWidth * 0.5f);
        float verticalOffset = (barrierHeight * 0.5f) - (roadThickness * 0.5f);

        if (cornerIndex <= 0)
        {
            Vector3 forward = (pathPoints[1] - pathPoints[0]).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            return pathPoints[0] + right * side * lateralOffset + Vector3.up * verticalOffset;
        }

        if (cornerIndex >= pathPoints.Count - 1)
        {
            int last = pathPoints.Count - 1;
            Vector3 forward = (pathPoints[last] - pathPoints[last - 1]).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            return pathPoints[last] + right * side * lateralOffset + Vector3.up * verticalOffset;
        }

        Vector3 previousPoint = pathPoints[cornerIndex - 1];
        Vector3 cornerPoint = pathPoints[cornerIndex];
        Vector3 nextPoint = pathPoints[cornerIndex + 1];

        Vector3 incomingDirection = (cornerPoint - previousPoint).normalized;
        Vector3 outgoingDirection = (nextPoint - cornerPoint).normalized;

        Vector3 incomingRight = Vector3.Cross(Vector3.up, incomingDirection).normalized;
        Vector3 outgoingRight = Vector3.Cross(Vector3.up, outgoingDirection).normalized;

        Vector3 incomingOffsetPoint = cornerPoint + incomingRight * side * lateralOffset;
        Vector3 outgoingOffsetPoint = cornerPoint + outgoingRight * side * lateralOffset;

        if (TryIntersectOffsetLines(incomingOffsetPoint, incomingDirection, outgoingOffsetPoint, outgoingDirection, out Vector3 intersection))
        {
            float maxMiterDistance = lateralOffset / Mathf.Max(0.1f, Mathf.Cos(Vector3.Angle(incomingDirection, outgoingDirection) * 0.5f * Mathf.Deg2Rad));
            float intersectionDistance = Vector3.Distance(cornerPoint, intersection);
            if (intersectionDistance <= maxMiterDistance * 1.5f)
            {
                return intersection + Vector3.up * verticalOffset;
            }
        }

        Vector3 fallback = (incomingOffsetPoint + outgoingOffsetPoint) * 0.5f;
        return fallback + Vector3.up * verticalOffset;
    }

    private static bool TryIntersectOffsetLines(Vector3 pointA, Vector3 directionA, Vector3 pointB, Vector3 directionB, out Vector3 intersection)
    {
        Vector2 aPoint = new Vector2(pointA.x, pointA.z);
        Vector2 aDir = new Vector2(directionA.x, directionA.z);
        Vector2 bPoint = new Vector2(pointB.x, pointB.z);
        Vector2 bDir = new Vector2(directionB.x, directionB.z);

        float denominator = (aDir.x * bDir.y) - (aDir.y * bDir.x);
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            intersection = Vector3.zero;
            return false;
        }

        Vector2 delta = bPoint - aPoint;
        float t = ((delta.x * bDir.y) - (delta.y * bDir.x)) / denominator;
        Vector2 hit = aPoint + (aDir * t);
        intersection = new Vector3(hit.x, 0f, hit.y);
        return true;
    }

    private static Vector3 GetJointForward(Vector3 previous, Vector3 current, Vector3 next)
    {
        Vector3 incoming = (current - previous).normalized;
        Vector3 outgoing = (next - current).normalized;
        Vector3 jointForward = (incoming + outgoing).normalized;

        if (jointForward.sqrMagnitude < 0.0001f)
        {
            jointForward = outgoing.sqrMagnitude > 0.0001f ? outgoing : Vector3.forward;
        }

        return jointForward;
    }

    private void RepositionCarAtStart()
    {
        if (carTransform == null || pathPoints.Count < 3)
        {
            return;
        }

        Vector3 start = pathPoints[1];
        Vector3 next = pathPoints[2];
        Vector3 forward = (next - start).normalized;
        Quaternion spawnRotation = Quaternion.LookRotation(forward, Vector3.up);
        Vector3 spawnPosition = start + Vector3.up * (carSpawnHeight + lineThickness);

        carTransform.SetPositionAndRotation(spawnPosition, spawnRotation);

        if (carRigidbody == null)
        {
            carRigidbody = carTransform.GetComponent<Rigidbody>();
        }

        if (carRigidbody != null)
        {
            carRigidbody.linearVelocity = Vector3.zero;
            carRigidbody.angularVelocity = Vector3.zero;
            carRigidbody.position = spawnPosition;
            carRigidbody.rotation = spawnRotation;
            carRigidbody.Sleep();
        }
    }

    public Vector3 GetCarSpawnPosition()
    {
        if (pathPoints.Count < 3)
        {
            return transform.position;
        }

        return pathPoints[1] + Vector3.up * (carSpawnHeight + lineThickness);
    }

    public Quaternion GetCarSpawnRotation()
    {
        if (pathPoints.Count < 3)
        {
            return transform.rotation;
        }

        Vector3 start = pathPoints[1];
        Vector3 next = pathPoints[2];
        return Quaternion.LookRotation((next - start).normalized, Vector3.up);
    }

    public float GetProgressAlongTrack(Vector3 position)
    {
        if (pathPoints.Count < 2)
        {
            return 0f;
        }

        float bestDistance = float.MaxValue;
        float bestProgress = 0f;
        float accumulatedLength = 0f;

        for (int i = 0; i < pathPoints.Count - 1; i++)
        {
            Vector3 start = pathPoints[i];
            Vector3 end = pathPoints[i + 1];
            Vector3 segment = end - start;
            float segmentLengthValue = segment.magnitude;
            if (segmentLengthValue <= 0.001f)
            {
                continue;
            }

            float t = Mathf.Clamp01(Vector3.Dot(position - start, segment) / segment.sqrMagnitude);
            Vector3 projectedPoint = Vector3.Lerp(start, end, t);
            float distance = Vector3.Distance(position, projectedPoint);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestProgress = accumulatedLength + (segmentLengthValue * t);
            }

            accumulatedLength += segmentLengthValue;
        }

        return bestProgress;
    }

    public float GetDistanceFromCenterline(Vector3 position)
    {
        if (pathPoints.Count < 2)
        {
            return float.MaxValue;
        }

        float bestDistance = float.MaxValue;

        for (int i = 0; i < pathPoints.Count - 1; i++)
        {
            Vector3 start = pathPoints[i];
            Vector3 end = pathPoints[i + 1];
            Vector3 segment = end - start;
            if (segment.sqrMagnitude <= 0.001f)
            {
                continue;
            }

            float t = Mathf.Clamp01(Vector3.Dot(position - start, segment) / segment.sqrMagnitude);
            Vector3 projectedPoint = Vector3.Lerp(start, end, t);
            float distance = Vector3.Distance(position, projectedPoint);
            if (distance < bestDistance)
            {
                bestDistance = distance;
            }
        }

        return bestDistance;
    }

    public bool IsOffTrack(Vector3 position)
    {
        return GetDistanceFromCenterline(position) > (roadWidth * 0.6f);
    }

    public bool HasFinishedLap(Vector3 currentPosition)
    {
        if (raceFinished)
        {
            return true;
        }

        if (HasCrossedFinishLine(previousTimingPosition, currentPosition))
        {
            raceFinished = true;
            previousTimingPosition = currentPosition;
            return true;
        }

        previousTimingPosition = currentPosition;
        return false;
    }

    public int GetCheckpointCount()
    {
        return Mathf.Max(0, pathPoints.Count - 3);
    }

    public Vector3 GetCheckpointPosition(int checkpointIndex)
    {
        int checkpointCount = GetCheckpointCount();
        if (checkpointCount == 0)
        {
            return raceFinishPoint;
        }

        int pointIndex = Mathf.Clamp(checkpointIndex, 0, checkpointCount - 1) + 2;
        return pathPoints[pointIndex];
    }

    public Vector3 GetCheckpointForward(int checkpointIndex)
    {
        int checkpointCount = GetCheckpointCount();
        if (checkpointCount == 0)
        {
            return raceFinishForward.sqrMagnitude > 0.0001f ? raceFinishForward : Vector3.forward;
        }

        int pointIndex = Mathf.Clamp(checkpointIndex, 0, checkpointCount - 1) + 2;
        int previousIndex = Mathf.Max(0, pointIndex - 1);
        int nextIndex = Mathf.Min(pathPoints.Count - 1, pointIndex + 1);
        return GetJointForward(pathPoints[previousIndex], pathPoints[pointIndex], pathPoints[nextIndex]);
    }

    public Vector3 GetFinishPoint()
    {
        return raceFinishPoint;
    }

    public float GetDistanceToFinish(Vector3 position)
    {
        return Vector3.Distance(position, raceFinishPoint);
    }

    public float GetSignedDistanceFromStartLine(Vector3 position)
    {
        if (pathPoints.Count < 3)
        {
            return 0f;
        }

        Vector3 startPoint = pathPoints[1];
        Vector3 startForward = (pathPoints[2] - pathPoints[1]).normalized;
        Quaternion startFrame = Quaternion.LookRotation(startForward, Vector3.up);
        Vector3 localPosition = Quaternion.Inverse(startFrame) * (position - startPoint);
        return localPosition.z;
    }

    private void ResetTrackTimer()
    {
        if (trackTimer != null)
        {
            trackTimer.ResetTimer();
        }
    }

    private void StartTrackTimer()
    {
        if (trackTimer != null)
        {
            trackTimer.StartTimer();
        }
    }

    private void UpdateRaceTimerState()
    {
        if (trackTimer == null || carTransform == null || pathPoints.Count < 3 || raceFinished)
        {
            return;
        }

        Vector3 currentTimingPosition = GetCarPositionForTiming();

        if (raceStarted && HasCrossedFinishLine(previousTimingPosition, currentTimingPosition))
        {
            trackTimer.FinishTimer();
            raceFinished = true;
        }

        previousTimingPosition = currentTimingPosition;
    }

    private bool HasCrossedFinishLine(Vector3 previousPosition, Vector3 currentPosition)
    {
        Quaternion finishFrame = Quaternion.LookRotation(raceFinishForward, Vector3.up);
        Vector3 previousLocal = Quaternion.Inverse(finishFrame) * (previousPosition - raceFinishPoint);
        Vector3 currentLocal = Quaternion.Inverse(finishFrame) * (currentPosition - raceFinishPoint);

        bool crossedPlane = previousLocal.z < 0f && currentLocal.z >= 0f;
        bool insideWidth = Mathf.Abs(currentLocal.x) <= (roadWidth * 1.5f) || Mathf.Abs(previousLocal.x) <= (roadWidth * 1.5f);

        return crossedPlane && insideWidth;
    }

    private Vector3 GetCarPositionForTiming()
    {
        Transform resolvedCarTransform = ResolveCarTransform();
        if (resolvedCarTransform == null)
        {
            return Vector3.zero;
        }

        if (carRigidbody != null)
        {
            return carRigidbody.position;
        }

        return resolvedCarTransform.position;
    }

    private void RecalculateTrackLength()
    {
        totalTrackLength = 0f;

        for (int i = 0; i < pathPoints.Count - 1; i++)
        {
            totalTrackLength += Vector3.Distance(pathPoints[i], pathPoints[i + 1]);
        }
    }

    private string GetTrackFolderPath(string overrideFolder = null)
    {
        string relativeFolder = string.IsNullOrWhiteSpace(overrideFolder) ? trackSaveFolder : overrideFolder;
        return Path.Combine(Application.dataPath, relativeFolder);
    }

    private void EnsureTrackTimer()
    {
        if (trackTimer != null)
        {
            return;
        }

        trackTimer = FindFirstObjectByType<TrackTimer>();
        if (trackTimer != null)
        {
            return;
        }

        GameObject timerObject = new GameObject("Track Timer");
        trackTimer = timerObject.AddComponent<TrackTimer>();
    }

    private void EnsureCarTransform()
    {
        Transform resolvedCarTransform = ResolveCarTransform();
        if (resolvedCarTransform != null)
        {
            carTransform = resolvedCarTransform;
            carRigidbody = carTransform.GetComponent<Rigidbody>();
        }
    }

    private Transform ResolveCarTransform()
    {
        if (carTransform != null)
        {
            return carTransform;
        }

        PrometeoCarController carController = FindFirstObjectByType<PrometeoCarController>();
        if (carController != null)
        {
            return carController.transform;
        }

        return null;
    }

    private void OnDrawGizmos()
    {
        if (!showCheckpointGizmos || pathPoints == null || pathPoints.Count < 4)
        {
            return;
        }

        float radius = Mathf.Max(0.05f, checkpointGizmoRadius);
        for (int i = 0; i < GetCheckpointCount(); i++)
        {
            Gizmos.color = checkpointGizmoColor;
            Gizmos.DrawSphere(GetCheckpointPosition(i), radius);
        }
    }
}
