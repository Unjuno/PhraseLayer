#!/usr/bin/env python3
"""Static contract for local Japanese font and source-mask material staging.

The font binary is intentionally supplied outside git. This validator ensures the self-hosted path remains explicit,
hash-evidenced, git-ignored, and bound to the committed source-mask shader rather than silently bundling a font.
"""

from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
STAGER = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerLocalReadModeVisualAssets.cs"
SHADER = ROOT / "unity/PhraseLayer.Unity/Assets/Shaders/PhraseLayerSourceMask.shader"
SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerEditorSetup.cs"
GITIGNORE = ROOT / ".gitignore"
LOCAL_ROOT = ROOT / "unity/PhraseLayer.Unity/Assets/LocalReadModeAssets"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    stager = STAGER.read_text(encoding="utf-8")
    shader = SHADER.read_text(encoding="utf-8")
    setup = SETUP.read_text(encoding="utf-8")
    ignore = GITIGNORE.read_text(encoding="utf-8")

    for fragment in (
        'PHRASELAYER_JAPANESE_FONT_SOURCE',
        'extension != ".ttf" && extension != ".otf"',
        'Assets/LocalReadModeAssets',
        'ReviewedJapaneseFont',
        'SHA256.Create()',
        'font_sha256',
        'font_size_bytes',
        'Shader.Find(SourceMaskShaderName)',
        'PhraseLayer/SourceMask',
        'quest_read_mode_smoke_autorun',
        'PhraseLayerEditorSetup.CreateDemoScene(font, material, autoRunQuestReadModeSmoke)',
    ):
        require(stager, fragment, "visual asset stager")

    for fragment in (
        'Shader "PhraseLayer/SourceMask"',
        'Cull Off',
        'ZWrite On',
        'ZTest LEqual',
        'RenderType"="Opaque',
    ):
        require(shader, fragment, "source-mask shader")

    for fragment in (
        "CreateDemoScene(\n            Font reviewedJapaneseFont,\n            Material reviewedSourceMaskMaterial,\n            bool autoRunQuestReadModeSmoke)",
        "questReadModeSmoke.AutoRunOnStart = autoRunQuestReadModeSmoke",
        "worldTextRenderer.SetFont(reviewedJapaneseFont)",
        "worldTextSourceMask.SetMaskMaterial(reviewedSourceMaskMaterial)",
    ):
        require(setup, fragment, "demo scene setup")

    require(ignore, "unity/PhraseLayer.Unity/Assets/LocalReadModeAssets/", ".gitignore")

    if LOCAL_ROOT.exists():
        committed_like = [
            path for path in LOCAL_ROOT.rglob("*")
            if path.is_file() and path.suffix.lower() in {".ttf", ".otf"}
        ]
        if committed_like:
            raise GateError("LocalReadModeAssets contains font binaries; they must remain outside the repository")

    return {
        "status": "pass",
        "font_source_explicit": True,
        "font_hash_evidence_required": True,
        "font_binary_git_ignored": True,
        "mask_shader_committed": True,
        "mask_shader_double_sided": True,
        "scene_visual_assets_injected_deterministically": True,
        "quest_read_mode_smoke_autorun_explicit": True,
        "real_unity_import_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
