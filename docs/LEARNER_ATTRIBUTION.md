# Automatic learning-evidence attribution

## Measured failure and conservative policy change

The baseline audit at `89adfd50f76fc4a9d14da0cfbb95faf19e0f38cc` (Experimental Audit `34142826019`, job `101808390649`) observed automatic exposure after an empty translation and 16 positive updates for one completion event on 16 occurrences of `go`. Scores were respectively 0.55 to 0.5575 and 0.5 to 0.8683031941277058 (Known). These synthetic scores do not establish human mastery.

Automatic exposure now requires a selected unit to have a matching assisted display segment with nonempty output different from normalized source text. An unchanged proper name is not declared a translation failure; it simply does not provide evidence of displayed translation assistance. Case/whitespace-only changes do not qualify. This is a textual availability check, not verification that the user actually saw the display or that the translation is accurate.

Selected but untranslated spans remain excluded from automatic unassisted-completion rewards. A translation failure must not turn into positive evidence merely by changing its label.

One encounter completion creates at most one automatic update per normalized learner key, even if the same word appears many times. This is a conservative engineering policy because those occurrences are not independent user responses. It is not an empirically calibrated learning model. Explicit evidence on any occurrence suppresses automatic evidence for that key. Separate explicit events keep their existing per-span semantics.

The old assistance sweep used an empty translation dictionary while calling its trajectory passive exposure. It now uses a clearly labeled, nonempty synthetic translator, and its scenario name states that successful unassisted completion is also assumed. No translation quality or human learning claim is permitted.

Regression coverage includes missing/null/empty/unchanged translations, case and whitespace variants, 1/2/4/8/16/32 repeated occurrences, explicit failed recall overriding automatic duplicate rewards, independent distinct keys, and positive exposure bounded below Known. A separate 2048-case mixed-event test checks batch arithmetic against sequential application; that comparison does not validate the learning-rate choices.
