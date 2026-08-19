# Product specification

PhraseLayer changes **where** English and Japanese appear according to estimated comprehension. It does not mechanically translate N percent of characters.

A target assistance ratio is an internal budget. Difficult semantic units are selected first. A whole clause can be selected when the clause as a unit is poorly understood; otherwise difficult MWEs/words are selected.

Modes: Auto, Easy, Balanced, Challenge, Immersion. Auto derives the support budget from current estimated difficulty.

The same encounter remains visually stable. A later encounter may use less or more assistance after learner state changes.

The initial learner model is deliberately simple: an understanding score per normalized expression. Later implementations may include context, retrieval history, response latency, explicit help requests, and probabilistic knowledge tracing.
