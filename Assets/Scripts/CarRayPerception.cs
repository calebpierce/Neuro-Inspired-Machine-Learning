using UnityEngine;

public class CarRayPerception : MonoBehaviour
{
    [SerializeField] [Range(4, 180)] private int rayCount = 36;
    [SerializeField] private float maxDistance = 60f;
    [SerializeField] private float originHeight = 1.25f;
    [SerializeField] private float originForwardOffset = 0.5f;
    [SerializeField] [Range(1f, 360f)] private float rayArcDegrees = 180f;
    [SerializeField] private LayerMask obstacleMask = ~0;
    [SerializeField] private bool requireRaycastObstacleMarker = true;
    [SerializeField] private bool drawDebugRays = true;
    [SerializeField] private bool showRaysInGame = true;
    [SerializeField] private float rayLineWidth = 0.08f;
    [SerializeField] private bool showSceneGizmos = true;
    [SerializeField] private bool previewWhenNotPlaying = true;

    private float[] rayDistances;
    private Collider[] selfColliders;
    private readonly RaycastHit[] raycastHits = new RaycastHit[16];
    private LineRenderer[] rayRenderers;
    private Transform rayVisualRoot;
    private Material rayLineMaterial;

    public float MaxDistance => maxDistance;
    public int ObservationCount => rayCount;

    private void Awake()
    {
        AllocateRayBuffer();
        CacheSelfColliders();
        EnsureRayVisuals();
    }

    private void OnValidate()
    {
        rayCount = Mathf.Clamp(rayCount, 4, 180);
        rayArcDegrees = Mathf.Clamp(rayArcDegrees, 1f, 360f);
        rayLineWidth = Mathf.Max(0.001f, rayLineWidth);
        AllocateRayBuffer();
    }

    private void Reset()
    {
        drawDebugRays = true;
        showRaysInGame = true;
    }

    private void OnDrawGizmos()
    {
        if (!showSceneGizmos)
        {
            return;
        }

        if (!Application.isPlaying && !previewWhenNotPlaying)
        {
            return;
        }

        DrawRayPreview();
    }

    public void UpdateObservations()
    {
        if (rayDistances == null || rayDistances.Length != rayCount)
        {
            AllocateRayBuffer();
        }

        if (selfColliders == null || selfColliders.Length == 0)
        {
            CacheSelfColliders();
        }

        EnsureRayVisuals();

        Vector3 planarForward = GetPlanarForward();
        Vector3 origin = transform.position + Vector3.up * originHeight + planarForward * originForwardOffset;
        float angleStep = rayCount > 1 ? rayArcDegrees / (rayCount - 1) : 0f;
        float startAngle = -rayArcDegrees * 0.5f;

        for (int i = 0; i < rayCount; i++)
        {
            float angle = startAngle + (i * angleStep);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * planarForward;
            float hitDistance = GetClosestObstacleDistance(origin, direction);
            rayDistances[i] = hitDistance;
            float normalizedDistance = Mathf.Clamp01(hitDistance / maxDistance);
            Color rayColor = Color.Lerp(Color.red, Color.green, normalizedDistance);

            if (drawDebugRays)
            {
                Debug.DrawRay(origin, direction * hitDistance, rayColor);
            }

            UpdateRayVisual(i, origin, direction, hitDistance, rayColor);
        }
    }

    public void WriteNormalizedObservations(float[] buffer, int startIndex)
    {
        for (int i = 0; i < rayCount; i++)
        {
            buffer[startIndex + i] = rayDistances[i] / maxDistance;
        }
    }

    public float[] GetDistances()
    {
        return rayDistances;
    }

    public float GetNormalizedDistanceAtAngle(float angleDegrees)
    {
        if (rayDistances == null || rayDistances.Length == 0)
        {
            return 1f;
        }

        float wrappedAngle = angleDegrees % 360f;
        if (wrappedAngle < 0f)
        {
            wrappedAngle += 360f;
        }

        float signedAngle = wrappedAngle > 180f ? wrappedAngle - 360f : wrappedAngle;
        float halfArc = rayArcDegrees * 0.5f;
        float clampedAngle = Mathf.Clamp(signedAngle, -halfArc, halfArc);
        float normalizedPosition = rayCount > 1
            ? Mathf.InverseLerp(-halfArc, halfArc, clampedAngle)
            : 0f;
        int index = Mathf.Clamp(Mathf.RoundToInt(normalizedPosition * (rayCount - 1)), 0, rayCount - 1);
        return rayDistances[index] / maxDistance;
    }

    private void AllocateRayBuffer()
    {
        rayDistances = new float[rayCount];
    }

    private void CacheSelfColliders()
    {
        selfColliders = GetComponentsInChildren<Collider>(true);
    }

    private float GetClosestObstacleDistance(Vector3 origin, Vector3 direction)
    {
        int hitCount = Physics.RaycastNonAlloc(origin, direction, raycastHits, maxDistance, obstacleMask, QueryTriggerInteraction.Ignore);
        float closestDistance = maxDistance;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = raycastHits[i].collider;
            if (hitCollider == null || IsSelfCollider(hitCollider) || !IsValidObstacle(hitCollider))
            {
                continue;
            }

            if (raycastHits[i].distance < closestDistance)
            {
                closestDistance = raycastHits[i].distance;
            }
        }

        return closestDistance;
    }

    private bool IsSelfCollider(Collider candidate)
    {
        for (int i = 0; i < selfColliders.Length; i++)
        {
            if (selfColliders[i] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsValidObstacle(Collider candidate)
    {
        if (!requireRaycastObstacleMarker)
        {
            return true;
        }

        return candidate.GetComponentInParent<RaycastObstacle>() != null;
    }

    private void EnsureRayVisuals()
    {
        if (!showRaysInGame)
        {
            if (rayVisualRoot != null)
            {
                rayVisualRoot.gameObject.SetActive(false);
            }

            return;
        }

        if (rayVisualRoot == null)
        {
            rayVisualRoot = new GameObject("Ray Visuals").transform;
            rayVisualRoot.SetParent(transform, false);
        }

        rayVisualRoot.gameObject.SetActive(true);

        if (rayRenderers != null && rayRenderers.Length == rayCount)
        {
            return;
        }

        if (rayVisualRoot.childCount > 0)
        {
            for (int i = rayVisualRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(rayVisualRoot.GetChild(i).gameObject);
            }
        }

        if (rayLineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            rayLineMaterial = new Material(shader);
        }

        rayRenderers = new LineRenderer[rayCount];
        for (int i = 0; i < rayCount; i++)
        {
            GameObject lineObject = new GameObject($"Ray {i:000}");
            lineObject.transform.SetParent(rayVisualRoot, false);

            LineRenderer lineRenderer = lineObject.AddComponent<LineRenderer>();
            lineRenderer.sharedMaterial = rayLineMaterial;
            lineRenderer.useWorldSpace = true;
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = rayLineWidth;
            lineRenderer.endWidth = rayLineWidth;
            lineRenderer.sortingOrder = 5000;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;
            lineRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            lineRenderer.textureMode = LineTextureMode.Stretch;
            lineRenderer.alignment = LineAlignment.View;
            rayRenderers[i] = lineRenderer;
        }
    }

    private void UpdateRayVisual(int index, Vector3 origin, Vector3 direction, float distance, Color rayColor)
    {
        if (!showRaysInGame || rayRenderers == null || index >= rayRenderers.Length || rayRenderers[index] == null)
        {
            return;
        }

        LineRenderer lineRenderer = rayRenderers[index];
        bool shouldShow = showRaysInGame;
        lineRenderer.enabled = shouldShow;
        if (!shouldShow)
        {
            return;
        }

        lineRenderer.startColor = rayColor;
        lineRenderer.endColor = rayColor;
        lineRenderer.startWidth = rayLineWidth;
        lineRenderer.endWidth = rayLineWidth;
        lineRenderer.SetPosition(0, origin);
        lineRenderer.SetPosition(1, origin + direction * distance);
    }

    private void DrawRayPreview()
    {
        Vector3 planarForward = GetPlanarForward();
        Vector3 origin = transform.position + Vector3.up * originHeight + planarForward * originForwardOffset;
        float angleStep = rayCount > 1 ? rayArcDegrees / (rayCount - 1) : 0f;
        float startAngle = -rayArcDegrees * 0.5f;

        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(origin, 0.12f);

        for (int i = 0; i < rayCount; i++)
        {
            float angle = startAngle + (i * angleStep);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * planarForward;
            float hitDistance = GetClosestObstacleDistance(origin, direction);
            float normalizedDistance = Mathf.Clamp01(hitDistance / maxDistance);
            Gizmos.color = Color.Lerp(Color.red, Color.green, normalizedDistance);
            Gizmos.DrawLine(origin, origin + direction * hitDistance);
        }
    }

    private Vector3 GetPlanarForward()
    {
        Vector3 planarForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        return planarForward.sqrMagnitude > 0.0001f ? planarForward : Vector3.forward;
    }
}
