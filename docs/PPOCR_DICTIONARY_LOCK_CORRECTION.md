# PP-OCR dictionary lock correction

## Evidence before the change

Commit `1477d486c06cc55e916a38e151b39d8ad07a36c6`, OCR CPU Differential run `34153349182`, job `101839983075` stopped before inference. The exact pinned inference.yml (55571 bytes, SHA-256 `66170210bad538e83fff3c4a3867e547d6bf20b50d64b20347c4b913f3034ea1`) was verified.

The production subset parser and independent PyYAML 6.0.2 loader produced identical 6904-entry dictionaries:

- untrimmed UTF-8 with one LF per entry: 27156 bytes, SHA-256 `c5cbe34ef40c29c4df07ed012bf96569cb69a2d2a01a07027e9f13cb832bd9cd`;
- applying Python str.strip to every token: 27153 bytes, SHA-256 `46e1b34ef45684cb46d75ac76d355341fe7f0a2c38d6ee02e63ae6b3878019fc` (the incorrect old lock).

This is not a download-corruption or parser-disagreement fix. Correct the two generated dictionary identity fields, not the upstream model, revision, metadata hashes, token count, class order, or preprocessing code. Do not add token trimming to match a bad fingerprint.

PaddleOCR's BaseRecLabelDecode removes CR/LF from file entries, not arbitrary whitespace. Its model exporter likewise strips only LF when building character_dict. Primary sources: `PaddlePaddle/PaddleOCR/ppocr/postprocess/rec_postprocess.py` and `ppocr/utils/export_model.py`. The corrected oracle records any affected whitespace codepoints and uses a serializer independent of production.

## Regression and migration

`test_ppocr_whitespace_contract.py` covers Unicode space preservation and its distinction from appended ASCII space. `PaddleOcrUnicodeWhitespaceTests` sends the same symbols through the actual Core dictionary parser and packed CTC decoder for LF and CRLF inputs. The full dictionary and model are not committed or uploaded.

Previously staged dictionaries/manifests with the old fingerprint are invalid. Regenerate them from the same revision-pinned YAML with `tools/extract_ppocr_dictionary.py`; do not manually edit a dictionary or bypass hash verification.

A passing dictionary identity gate is necessary but not evidence of model-inference, real-Unity, GPU, camera, or Quest success. The following OCR CPU differential and C# replay gates must execute independently. No device performance or natural-image recognition-quality claim follows from these tests.
