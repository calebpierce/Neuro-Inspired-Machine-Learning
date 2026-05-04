# Sequential Forgetting Experiment

This project is now structured for a two-phase catastrophic-forgetting experiment.

## Scene setup

Add these components to the car root:

- `CarControlAdapter`
- `CarRayPerception`
- `CarDrivingAgent`
- `Behavior Parameters`
- `Decision Requester`

Add `DrivingTrainingManager` to a separate scene object and assign:

- `trackGenerator`
- `carAdapter`

## Behavior Parameters

Use the same car and same scene for both runs.

- Vector observation size: `40`
- Continuous actions: `2`

Behavior names by run:

- Standard model: `DriverStandard`
- Continual model: `DriverContinuous`

## Track workflow

Use named track files such as:

- `TrackA.json`
- `TrackB.json`

Saved tracks live under:

- `Assets/TrainingData/Tracks`

Recommended sequence:

1. Set the manager `Training Mode` to `StandardTrackOnly`.
2. Generate and save `TrackA`.
3. Train the standard model on `TrackA`.
4. Train the continual model on `TrackA`.
5. Generate and save `TrackB`.
6. Resume the standard model with:
   `Training Mode = StandardTrackOnly`
   `Current Training Track Id = TrackB`
7. Resume the continual model with:
   `Training Mode = ContinualReplay`
   `Current Training Track Id = TrackB`
   `Replay Track Ids = [TrackA]`

This gives the standard run only the new track, while the continual run still replays the old one.

Training reward now favors:

- getting closer to the next ordered checkpoint
- reaching checkpoints in sequence
- avoiding driving straight into close obstacles

## Evaluation workflow

To measure forgetting:

1. Set `Training Mode = Evaluation`
2. Set `Evaluation Track Ids = [TrackA, TrackB]`
3. Set `Evaluation Episodes Per Track` to a fixed count such as `10`
4. Run inference for each model separately

Evaluation results are appended to:

- `Assets/TrainingData/Results/evaluation_results.txt`

Each line includes:

- `track_id`
- `completed`
- `termination_reason`
- `total_reward`
- `steps`
- `elapsed_seconds`
- `max_progress`

## Example trainer commands

```bash
mlagents-learn Assets/TrainingConfigs/driver_standard.yaml --run-id=driver_standard_track_a
mlagents-learn Assets/TrainingConfigs/driver_continuous.yaml --run-id=driver_continuous_track_a
mlagents-learn Assets/TrainingConfigs/driver_standard.yaml --run-id=driver_standard_track_b --initialize-from=driver_standard_track_a
mlagents-learn Assets/TrainingConfigs/driver_continuous.yaml --run-id=driver_continuous_track_b --initialize-from=driver_continuous_track_a
```
