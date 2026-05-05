using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class TrackLayoutSnapshot
{
    public string trackId;
    public int seed;
    public int segmentCount;
    public float segmentLength;
    public float roadWidth;
    public float roadThickness;
    public float roadJointLength;
    public float lineLength;
    public float lineThickness;
    public bool generateBarriers;
    public float barrierHeight;
    public float barrierWidth;
    public bool generateRandomObstacles;
    public float obstacleSpawnChance;
    public float obstacleWidth;
    public float obstacleLength;
    public float obstacleHeight;
    public float obstacleSideMargin;
    public List<SerializableVector3> pathPoints = new List<SerializableVector3>();

    public static TrackLayoutSnapshot FromPath(string id, int trackSeed, IReadOnlyList<Vector3> points, ProceduralTrackGenerator generator)
    {
        var snapshot = new TrackLayoutSnapshot
        {
            trackId = id,
            seed = trackSeed,
            segmentCount = generator.SegmentCount,
            segmentLength = generator.SegmentLength,
            roadWidth = generator.RoadWidth,
            roadThickness = generator.RoadThickness,
            roadJointLength = generator.RoadJointLength,
            lineLength = generator.LineLength,
            lineThickness = generator.LineThickness,
            generateBarriers = generator.GenerateBarriers,
            barrierHeight = generator.BarrierHeight,
            barrierWidth = generator.BarrierWidth,
            generateRandomObstacles = generator.GenerateRandomObstacles,
            obstacleSpawnChance = generator.ObstacleSpawnChance,
            obstacleWidth = generator.ObstacleWidth,
            obstacleLength = generator.ObstacleLength,
            obstacleHeight = generator.ObstacleHeight,
            obstacleSideMargin = generator.ObstacleSideMargin
        };

        for (int i = 0; i < points.Count; i++)
        {
            snapshot.pathPoints.Add(points[i]);
        }

        return snapshot;
    }

    public List<Vector3> ToPathPoints()
    {
        var points = new List<Vector3>(pathPoints.Count);
        for (int i = 0; i < pathPoints.Count; i++)
        {
            points.Add(pathPoints[i]);
        }

        return points;
    }
}

[Serializable]
public struct SerializableVector3
{
    public float x;
    public float y;
    public float z;

    public SerializableVector3(float xValue, float yValue, float zValue)
    {
        x = xValue;
        y = yValue;
        z = zValue;
    }

    public static implicit operator SerializableVector3(Vector3 value)
    {
        return new SerializableVector3(value.x, value.y, value.z);
    }

    public static implicit operator Vector3(SerializableVector3 value)
    {
        return new Vector3(value.x, value.y, value.z);
    }
}
