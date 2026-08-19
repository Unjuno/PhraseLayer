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
                                                  Unity/MR renderer (future)
```

Core invariants: selected spans do not overlap; untouched whitespace/punctuation is preserved; Core adds no brackets/arrows; Read and Listen modes share the same language pipeline; encounter stability is explicit.

The initial rule-based segmenter emits sentence, clause, longest-match MWE, and word spans. A later parser can add phrase/dependency spans without changing the planner contract.

The current planner is an MVP heuristic, not a claim of pedagogical optimality.
