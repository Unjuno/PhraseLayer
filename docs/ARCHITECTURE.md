# Architecture

```text
Camera -> OCR ---------\
                       -> source text -> semantic segmenter -> learner model
Microphone -> ASR ----/                                      |
                                                               v
                                                    AssistancePlanner
                                                               |
                                                     selected semantic spans
                                                               |
                                                      TranslationEngine
                                                               |
                                                     MixedLanguagePlan
                                                               |
                                                  Unity/MR renderer
```

Core invariants: selected spans do not overlap; untouched whitespace/punctuation is preserved; Core adds no brackets/arrows; Read and Listen modes share the same language pipeline; encounter stability is explicit.

The bootstrap `RuleBasedSemanticSegmenter` now emits multiple sentence spans per source document, clause spans within each sentence, configurable longest-match phrase spans, longest-match multiword-expression spans, and words. Decimal points are not treated as sentence boundaries, and sentence-final punctuation remains outside clause replacement spans.

Configured phrase patterns are deliberately an explicit bootstrap lexicon, not a claim of general syntactic parsing. A later dependency/constituency parser can replace `ISemanticSegmenter` without changing `AssistancePlanner` or the mixed-language renderer.

`AssistancePlanner` uses a hierarchy: a sufficiently difficult short clause can be replaced as a clause; otherwise a difficult configured phrase can be replaced as one semantic span; otherwise difficult MWE/word atoms remain eligible. The phrase/clause thresholds are MVP policy values, not claims of pedagogical optimality.
