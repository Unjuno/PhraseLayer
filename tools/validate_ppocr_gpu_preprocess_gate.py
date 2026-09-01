#!/usr/bin/env python3
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DETECTOR = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityPaddleOcrDetectorRuntime.cs"
PREPROCESS = ROOT / "src" / "PhraseLayer.Core" / "PaddleOcrDetectionPreprocess.cs"
MANIFEST = ROOT / "unity" / "PhraseLayer.Unity" / "Packages" / "manifest.json"


def fail(message: str) -> None:
    raise SystemExit(message)


for path in (DETECTOR, PREPROCESS, MANIFEST):
    if not path.is_file():
        fail(f"missing required PP-OCR GPU preprocess gate input: {path.relative_to(ROOT)}")

text = DETECTOR.read_text(encoding="utf-8")
required_markers = (
    "UsesGpuTexturePreprocessing => true",
    "new Tensor<float>(inputShape)",
    ".SetTensorLayout(TensorLayout.NCHW)",
    ".SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)",
    ".SetChannelSwizzle(ChannelSwizzle.BGRA)",
    "TextureConverter.ToTensor(texture, inputTensor, textureTransform)",
    "var graph = new FunctionalGraph()",
    "var input = graph.AddInput(sourceModel, 0)",
    "PaddleOcrV6TinyDetectionPreprocess.MeanForChannel(0)",
    "PaddleOcrV6TinyDetectionPreprocess.StandardDeviationForChannel(0)",
    "var normalized = (input - mean) / standardDeviation",
    "Functional.Forward(sourceModel, normalized)",
    "graph.AddOutputs(outputs)",
    "ReadbackAndClone()",
)
for marker in required_markers:
    if marker not in text:
        fail(f"Unity PP-OCR GPU preprocess gate missing reviewed marker: {marker}")

for forbidden in (
    "Graphics.Blit(",
    ".ReadPixels(",
    ".GetPixels32(",
    "RenderTexture.active",
    "RenderTexture.GetTemporary(",
):
    if forbidden in text:
        fail(f"Unity PP-OCR detector input path regressed to CPU/readback preprocessing: {forbidden}")

preprocess_text = PREPROCESS.read_text(encoding="utf-8")
for marker in (
    "private static readonly float[] Means = { 0.485f, 0.456f, 0.406f }",
    "private static readonly float[] StandardDeviations = { 0.229f, 0.224f, 0.225f }",
    "return (value * PixelScale - Means[channelIndex]) / StandardDeviations[channelIndex]",
):
    if marker not in preprocess_text:
        fail(f"Core PP-OCR normalization contract drifted: {marker}")

manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
if manifest.get("dependencies", {}).get("com.unity.ai.inference") != "2.2.1":
    fail("GPU PP-OCR preprocess gate requires com.unity.ai.inference@2.2.1")

print(
    "PASS: PP-OCR detector input stays GPU-resident through TextureConverter, uses BGR NCHW/top-left semantics, "
    "and applies the reviewed mean/std normalization in the compiled FunctionalGraph"
)
