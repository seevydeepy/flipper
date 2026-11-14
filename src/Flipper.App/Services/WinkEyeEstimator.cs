using System.Numerics;
using Flipper.Core.Reader;
using Microsoft.ML.OnnxRuntime;

namespace Flipper.App.Services;

internal readonly record struct WinkEyeObservation(
    EyeState Left,
    EyeState Right,
    float? LeftOpenProbability,
    float? RightOpenProbability,
    float? EyeDistance,
    string TrackingStatus,
    int RotationQuarterTurns = 0);

internal sealed class WinkEyeEstimator : IDisposable
{
    private const int FaceInputSize = 640;
    private const int EyeInputSize = 32;
    private const float FaceConfidence = 0.8f;
    private const float ClosedEyeConfidence = 0.7f;
    private const float OpenEyeConfidence = 0.4f;
    private const float DuplicateFaceOverlap = 0.3f;
    private const float MinimumEyeDistance = 24f;
    private const float EyeCropWidthRatio = 0.72f;

    private static readonly int[] FaceStrides = [8, 16, 32];
    private static readonly string[] FaceOutputs = FaceStrides.SelectMany(stride =>
        new[] { $"cls_{stride}", $"obj_{stride}", $"bbox_{stride}", $"kps_{stride}" }).ToArray();
    private static readonly long[] FaceShape = [1, 3, FaceInputSize, FaceInputSize];
    private static readonly long[] EyeShape = [1, 3, EyeInputSize, EyeInputSize];

    private readonly InferenceSession _faceModel;
    private readonly InferenceSession _eyeModel;
    private readonly float[] _faceInput = new float[3 * FaceInputSize * FaceInputSize];
    private readonly float[] _eyeInput = new float[3 * EyeInputSize * EyeInputSize];
    private readonly List<Face> _faces = [];
    private (int Width, int Height) _sourceSize;
    private int _rotationQuarterTurns;

    private readonly record struct Face(float Score, Vector2 TopLeft, Vector2 Size, Vector2 LeftEye, Vector2 RightEye);

    public WinkEyeEstimator()
    {
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Wink");
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _faceModel = new InferenceSession(Path.Combine(modelDirectory, "face_detection_yunet_2023mar.onnx"), options);
        try
        {
            options.IntraOpNumThreads = 1;
            _eyeModel = new InferenceSession(Path.Combine(modelDirectory, "open_closed_eye.onnx"), options);
        }
        catch
        {
            _faceModel.Dispose();
            throw;
        }
    }

    public (EyeState Left, EyeState Right) Observe(byte[] bgra, int width, int height)
    {
        var eyes = ObserveDetailed(bgra, width, height);
        return (eyes.Left, eyes.Right);
    }

    internal WinkEyeObservation ObserveDetailed(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > bgra.Length / 4)
        {
            return Untracked("invalid_frame", 0);
        }

        if (_sourceSize != (width, height))
        {
            _sourceSize = (width, height);
            _rotationQuarterTurns = 0;
        }

        var rotation = _rotationQuarterTurns;
        var (frameWidth, frameHeight) = WinkFrameRotation.GetSize(width, height, rotation);
        var scale = (float)FaceInputSize / Math.Max(frameWidth, frameHeight);
        var resizedWidth = Math.Max(1, (int)MathF.Round(frameWidth * scale));
        var resizedHeight = Math.Max(1, (int)MathF.Round(frameHeight * scale));
        var offsetX = (FaceInputSize - resizedWidth) / 2;
        var offsetY = (FaceInputSize - resizedHeight) / 2;
        var pixelScale = new Vector2((float)frameWidth / resizedWidth, (float)frameHeight / resizedHeight);
        var offset = new Vector2(offsetX, offsetY);

        Array.Clear(_faceInput);
        for (var y = 0; y < resizedHeight; y++)
        {
            var frameY = Math.Clamp((y + 0.5f) * pixelScale.Y - 0.5f, 0, frameHeight - 1);
            for (var x = 0; x < resizedWidth; x++)
            {
                var frameX = Math.Clamp((x + 0.5f) * pixelScale.X - 0.5f, 0, frameWidth - 1);
                var source = WinkFrameRotation.ToSource(new Vector2(frameX, frameY), width, height, rotation);
                var index = (y + offsetY) * FaceInputSize + x + offsetX;
                SampleBgr(bgra, width, height, source.X, source.Y, _faceInput, index, FaceInputSize * FaceInputSize);
            }
        }

        using var options = new RunOptions();
        using var input = OrtValue.CreateTensorValueFromMemory(_faceInput, FaceShape);
        using var outputs = _faceModel.Run(options, _faceModel.InputNames, [input], FaceOutputs);
        var face = FindSingleFace(outputs.ToArray(), out var faceStatus);
        if (face is not { } detected)
        {
            // Keep each frame to one inference and only search on a missing face.
            if (faceStatus == "no_face")
            {
                _rotationQuarterTurns = (rotation + 1) % 4;
            }

            return Untracked(faceStatus, rotation);
        }

        var leftEye = (detected.LeftEye - offset + new Vector2(0.5f)) * pixelScale - new Vector2(0.5f);
        var rightEye = (detected.RightEye - offset + new Vector2(0.5f)) * pixelScale - new Vector2(0.5f);
        var eyeLine = leftEye - rightEye;
        var eyeDistance = eyeLine.Length();
        if (!float.IsFinite(eyeDistance) || eyeDistance < MinimumEyeDistance)
        {
            return Untracked("eyes_too_small", rotation, float.IsFinite(eyeDistance) ? eyeDistance : null);
        }

        var across = eyeLine * (EyeCropWidthRatio / EyeInputSize);
        var down = new Vector2(-across.Y, across.X);
        var halfExtent = (Vector2.Abs(across) + Vector2.Abs(down)) * ((EyeInputSize - 1) * 0.5f);
        var frameMax = new Vector2(frameWidth - 1, frameHeight - 1);
        if (!CropFits(leftEye, halfExtent, frameMax) || !CropFits(rightEye, halfExtent, frameMax))
        {
            return Untracked("eye_crop_outside_frame", rotation, eyeDistance);
        }

        var left = ClassifyEye(bgra, width, height, rotation, leftEye, across, down, options);
        var right = ClassifyEye(bgra, width, height, rotation, rightEye, across, down, options);
        return new(left.State, right.State, left.OpenProbability, right.OpenProbability, eyeDistance, faceStatus, rotation);
    }

    private static WinkEyeObservation Untracked(string status, int rotation, float? eyeDistance = null) =>
        new(EyeState.Unknown, EyeState.Unknown, null, null, eyeDistance, status, rotation);

    // The YuNet grid and landmark decoding follows the OpenCV FaceDetectorYN implementation.
    // Adapted in C# for ONNX Runtime. See Wink/NOTICE.txt for the source and licence.
    private Face? FindSingleFace(OrtValue[] outputs, out string status)
    {
        _faces.Clear();
        for (var layer = 0; layer < FaceStrides.Length; layer++)
        {
            var stride = FaceStrides[layer];
            var gridWidth = FaceInputSize / stride;
            var scores = outputs[layer * 4].GetTensorDataAsSpan<float>();
            var objectness = outputs[layer * 4 + 1].GetTensorDataAsSpan<float>();
            var boxes = outputs[layer * 4 + 2].GetTensorDataAsSpan<float>();
            var landmarks = outputs[layer * 4 + 3].GetTensorDataAsSpan<float>();
            for (var cell = 0; cell < scores.Length; cell++)
            {
                var score = MathF.Sqrt(Math.Clamp(scores[cell], 0, 1) * Math.Clamp(objectness[cell], 0, 1));
                if (!(score >= FaceConfidence))
                {
                    continue;
                }

                var grid = new Vector2(cell % gridWidth, cell / gridWidth);
                var box = cell * 4;
                var centre = (grid + new Vector2(boxes[box], boxes[box + 1])) * stride;
                var size = new Vector2(MathF.Exp(boxes[box + 2]), MathF.Exp(boxes[box + 3])) * stride;
                var point = cell * 10;
                // YuNet gives the subject's right eye first in an unmirrored frame.
                var rightEye = (grid + new Vector2(landmarks[point], landmarks[point + 1])) * stride;
                var leftEye = (grid + new Vector2(landmarks[point + 2], landmarks[point + 3])) * stride;
                _faces.Add(new Face(score, centre - size * 0.5f, size, leftEye, rightEye));
            }
        }

        if (_faces.Count == 0)
        {
            status = "no_face";
            return null;
        }

        var bestIndex = 0;
        for (var index = 1; index < _faces.Count; index++)
        {
            if (_faces[index].Score > _faces[bestIndex].Score)
            {
                bestIndex = index;
            }
        }

        var best = _faces[bestIndex];
        // If the highest-scoring box cannot suppress every other box, there is more than one face.
        for (var index = 0; index < _faces.Count; index++)
        {
            if (index != bestIndex && !(Overlap(best, _faces[index]) > DuplicateFaceOverlap))
            {
                status = "multiple_faces";
                return null;
            }
        }

        status = "tracked";
        return best;
    }

    private static float Overlap(Face first, Face second)
    {
        var start = Vector2.Max(first.TopLeft, second.TopLeft);
        var end = Vector2.Min(first.TopLeft + first.Size, second.TopLeft + second.Size);
        var sharedSize = Vector2.Max(Vector2.Zero, end - start);
        var sharedArea = sharedSize.X * sharedSize.Y;
        return sharedArea / (first.Size.X * first.Size.Y + second.Size.X * second.Size.Y - sharedArea);
    }

    private static bool CropFits(Vector2 centre, Vector2 halfExtent, Vector2 frameMax) =>
        centre.X - halfExtent.X >= 0 && centre.Y - halfExtent.Y >= 0 &&
        centre.X + halfExtent.X <= frameMax.X && centre.Y + halfExtent.Y <= frameMax.Y;

    private (EyeState State, float OpenProbability) ClassifyEye(byte[] bgra, int width, int height, int rotation,
        Vector2 centre, Vector2 across, Vector2 down, RunOptions options)
    {
        var firstPixel = centre - (across + down) * ((EyeInputSize - 1) * 0.5f);
        for (var y = 0; y < EyeInputSize; y++)
        {
            for (var x = 0; x < EyeInputSize; x++)
            {
                var sample = firstPixel + x * across + y * down;
                var source = WinkFrameRotation.ToSource(sample, width, height, rotation);
                SampleBgr(bgra, width, height, source.X, source.Y, _eyeInput, y * EyeInputSize + x, EyeInputSize * EyeInputSize);
            }
        }

        for (var index = 0; index < _eyeInput.Length; index++)
        {
            _eyeInput[index] = (_eyeInput[index] - 127f) / 255f;
        }

        using var input = OrtValue.CreateTensorValueFromMemory(_eyeInput, EyeShape);
        using var outputs = _eyeModel.Run(options, _eyeModel.InputNames, [input], _eyeModel.OutputNames);
        var probabilities = outputs.First().GetTensorDataAsSpan<float>();
        // The bundled model's closed and open sample images give indices 0 and 1 respectively.
        var openProbability = probabilities[1];
        if (probabilities[0] >= ClosedEyeConfidence)
        {
            return (EyeState.Closed, openProbability);
        }

        return (openProbability >= OpenEyeConfidence ? EyeState.Open : EyeState.Unknown, openProbability);
    }

    private static void SampleBgr(byte[] bgra, int width, int height, float x, float y, float[] planes, int index, int planeSize)
    {
        var left = (int)x;
        var top = (int)y;
        var right = Math.Min(left + 1, width - 1);
        var bottom = Math.Min(top + 1, height - 1);
        var blendX = x - left;
        var blendY = y - top;
        var topLeft = (top * width + left) * 4;
        var topRight = (top * width + right) * 4;
        var bottomLeft = (bottom * width + left) * 4;
        var bottomRight = (bottom * width + right) * 4;
        for (var channel = 0; channel < 3; channel++)
        {
            var upper = bgra[topLeft + channel] + (bgra[topRight + channel] - bgra[topLeft + channel]) * blendX;
            var lower = bgra[bottomLeft + channel] + (bgra[bottomRight + channel] - bgra[bottomLeft + channel]) * blendX;
            planes[index + channel * planeSize] = upper + (lower - upper) * blendY;
        }
    }

    public void Dispose()
    {
        _eyeModel.Dispose();
        _faceModel.Dispose();
    }
}
