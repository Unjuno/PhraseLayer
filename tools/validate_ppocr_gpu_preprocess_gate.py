#!/usr/bin/env python3
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DETECTOR = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityPaddleOcrDetectorRuntime.cs"
RECOGNIZER = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityPaddleOcrRecognizerRuntime.cs"
RECOGNIZER_SHADER = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Resources" / "PaddleOcrRecognizerPreprocess.shader"
RECOGNIZER_PROBE = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Editor" / "PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe.cs"
RECOGNIZER_PROBE_SHELL = ROOT / "tools" / "unity" / "verify-recognizer-gpu-preprocess.sh"
GUARDED_CSPROJ = ROOT / "tests" / "PhraseLayer.UnityOcrInferenceShell.Compile" / "PhraseLayer.UnityOcrInferenceShell.Compile.csproj"
DETECTION_PREPROCESS = ROOT / "src" / "PhraseLayer.Core" / "PaddleOcrDetectionPreprocess.cs"
RECOGNITION_PREPROCESS = ROOT / "src" / "PhraseLayer.Core" / "PaddleOcrRecognition.cs"
MANIFEST = ROOT / "unity" / "PhraseLayer.Unity" / "Packages" / "manifest.json"


def fail(message: str) -> None:
    raise SystemExit(message)


def require(text: str, marker: str, label: str) -> None:
    if marker not in text:
        fail(f"{label} missing reviewed marker: {marker}")


def forbid(text: str, marker: str, label: str) -> None:
    if marker in text:
        fail(f"{label} regressed to forbidden CPU image preprocessing/readback marker: {marker}")


for path in (
    DETECTOR,
    RECOGNIZER,
    RECOGNIZER_SHADER,
    RECOGNIZER_PROBE,
    RECOGNIZER_PROBE_SHELL,
    GUARDED_CSPROJ,
    DETECTION_PREPROCESS,
    RECOGNITION_PREPROCESS,
    MANIFEST,
):
    if not path.is_file():
        fail(f"missing required PP-OCR GPU preprocess gate input: {path.relative_to(ROOT)}")


detector = DETECTOR.read_text(encoding="utf-8")
for marker in (
    "UsesGpuTexturePreprocessing => true",
    "public static TextureTransform CreateReviewedTextureTransform",
    "public static FunctionalTensor ApplyReviewedNormalization",
    "new Tensor<float>(inputShape)",
    ".SetTensorLayout(TensorLayout.NCHW)",
    ".SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)",
    ".SetChannelSwizzle(ChannelSwizzle.BGRA)",
    "var textureTransform = CreateReviewedTextureTransform(flipReadbackRows)",
    "TextureConverter.ToTensor(texture, inputTensor, textureTransform)",
    "var graph = new FunctionalGraph()",
    "var input = graph.AddInput(sourceModel, 0)",
    "PaddleOcrV6TinyDetectionPreprocess.MeanForChannel(0)",
    "PaddleOcrV6TinyDetectionPreprocess.StandardDeviationForChannel(0)",
    "return (input - mean) / standardDeviation",
    "var normalized = ApplyReviewedNormalization(input)",
    "Functional.Forward(sourceModel, normalized)",
    "graph.AddOutputs(outputs)",
    "ReadbackAndClone()",
):
    require(detector, marker, "Unity PP-OCR detector GPU preprocess gate")

for marker in (
    "Graphics.Blit(",
    ".ReadPixels(",
    ".GetPixels32(",
    "RenderTexture.active",
    "RenderTexture.GetTemporary(",
):
    forbid(detector, marker, "Unity PP-OCR detector input path")

recognizer = RECOGNIZER.read_text(encoding="utf-8")
for marker in (
    "UsesGpuTexturePreprocessing => true",
    'PreprocessShaderResourceName = "PaddleOcrRecognizerPreprocess"',
    "public static TextureTransform CreateReviewedTextureTransform",
    "public static Material CreateReviewedPreprocessMaterial()",
    "public static void PopulateReviewedInputTensor(",
    ".SetTensorLayout(TensorLayout.NCHW)",
    ".SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)",
    ".SetChannelSwizzle(ChannelSwizzle.BGRA)",
    "RenderTexture.GetTemporary(",
    "RenderTextureFormat.ARGBHalf",
    "RenderTextureReadWrite.Linear",
    'material.SetFloat("_ValidRatio", (float)resizeTransform.ValidRatio)',
    "Graphics.Blit(rectifiedCrop, normalizedTexture, material, 0)",
    "TextureConverter.ToTensor(",
    "normalizedTexture,",
    "CreateReviewedTextureTransform(flipReadbackRows)",
    "PopulateReviewedInputTensor(",
    "reducedOutputWorker.Schedule(inputTensor)",
    "parityWorker.Schedule(inputTensor)",
    "RetainsFullOutputWorker => false",
    "ReadbackAndClone()",
):
    require(recognizer, marker, "Unity PP-OCR recognizer GPU preprocess gate")

for marker in (
    ".ReadPixels(",
    ".GetPixels32(",
    "RenderTexture.active",
    "new Texture2D(",
    "private readonly Worker fullOutputWorker",
):
    forbid(recognizer, marker, "Unity PP-OCR recognizer input path")

shader = RECOGNIZER_SHADER.read_text(encoding="utf-8")
for marker in (
    'Shader "Hidden/PhraseLayer/PaddleOcrRecognizerPreprocess"',
    "float _ValidRatio;",
    "UNITY_COLORSPACE_GAMMA",
    "LinearToGammaSpace(sampledRgb)",
    "if (validRatio <= 0.0 || i.uv.x >= validRatio)",
    "return float4(0.0, 0.0, 0.0, 1.0)",
    "float2 sourceUv = float2(saturate(i.uv.x / validRatio), i.uv.y)",
    "float3 normalized = (encoded - 0.5) / 0.5",
):
    require(shader, marker, "PP-OCR recognizer preprocessing shader")

probe = RECOGNIZER_PROBE.read_text(encoding="utf-8")
for marker in (
    "SourceWidth = 64",
    "ModelWidth = 96",
    "PaddleOcrV6TinyRecognitionPreprocess.DefaultModelHeight",
    "UnityPaddleOcrRecognizerRuntime.CreateReviewedPreprocessMaterial()",
    "UnityPaddleOcrRecognizerRuntime.PopulateReviewedInputTensor(",
    "PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.b)",
    "PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.g)",
    "PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.r)",
    "values[FlatIndex(channel, point.x, point.y)]",
    '"padding channel " + channel',
    "PhraseLayer PP-OCR recognizer GPU preprocess parity PASS",
    "RunBatch()",
):
    require(probe, marker, "real-Unity PP-OCR recognizer GPU parity probe")

probe_shell = RECOGNIZER_PROBE_SHELL.read_text(encoding="utf-8")
for marker in (
    "UNITY_EDITOR",
    "PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe.RunBatch",
    "real Unity PP-OCR recognizer GPU preprocessing parity",
):
    require(probe_shell, marker, "recognizer GPU parity shell")
if "-nographics" in probe_shell:
    fail("recognizer GPU parity shell must run with a real graphics device; -nographics is forbidden")

guarded = GUARDED_CSPROJ.read_text(encoding="utf-8")
for marker in (
    "UnityPaddleOcrDetectorRuntime.cs",
    "UnityPaddleOcrRecognizerRuntime.cs",
    "PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe.cs",
    "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.cs",
    "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
):
    require(guarded, marker, "guarded PP-OCR Unity inference compile project")

detection_preprocess = DETECTION_PREPROCESS.read_text(encoding="utf-8")
for marker in (
    "private static readonly float[] Means = { 0.485f, 0.456f, 0.406f }",
    "private static readonly float[] StandardDeviations = { 0.229f, 0.224f, 0.225f }",
    "return (value * PixelScale - Means[channelIndex]) / StandardDeviations[channelIndex]",
):
    require(detection_preprocess, marker, "Core PP-OCR detector normalization contract")

recognition_preprocess = RECOGNITION_PREPROCESS.read_text(encoding="utf-8")
for marker in (
    "public const int Channels = 3",
    "public const int DefaultModelHeight = 48",
    "public const int DefaultModelWidth = 320",
    "public const float Mean = 0.5f",
    "public const float StandardDeviation = 0.5f",
    "return (value * PixelScale - Mean) / StandardDeviation",
    "public int RightPaddingWidth => ModelWidth - ResizedWidth",
    "public double ValidRatio => Math.Min(1.0, ResizedWidth / (double)ModelWidth)",
):
    require(recognition_preprocess, marker, "Core PP-OCR recognizer preprocessing contract")

manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
if manifest.get("dependencies", {}).get("com.unity.ai.inference") != "2.2.1":
    fail("GPU PP-OCR preprocess gate requires com.unity.ai.inference@2.2.1")

print(
    "PASS: PP-OCR detector and recognizer image preprocessing stay GPU-side; detector uses reviewed TextureConverter "
    "BGR NCHW/top-left mean/std normalization, recognizer uses reviewed GPU resize/right-pad/(x-0.5)/0.5 preprocessing, "
    "the production recognizer retains only its reduced-output worker, real-Unity numerical parity is required, and "
    "CPU image ReadPixels/GetPixels32 paths are forbidden"
)
