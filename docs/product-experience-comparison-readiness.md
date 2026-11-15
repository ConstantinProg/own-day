# Comparison and pilot readiness investigation

S10 requires evidence-based achievements with comparable baseline/end reading
checks and writing rubric trends. S11 explicitly depends on pilot data, expert
calibration, held-out datasets, agreed metrics and version comparisons. These
requirements remain unchanged.

Verified current implementation:

- `EnglishLearningContent.Reading` is a single baseline choice item with a local
  authored key; there is no distinct end-reading item or equivalence validation.
- `EnglishLearningContent.Writing` checks replying to a guest-list request.
  `FirstLesson` additionally checks identifying the requested action in an agenda
  email. Their IDs, objectives, criteria and rubric text differ. Treating these
  aggregate booleans as a matched before/after measurement would be unsupported.
- `DiagnosticObservation` retains the original content/rubric, feedback, assistance
  and aggregate correctness. `LearningEvidence` retains source, conflict,
  assistance and content, but neither contract records structured outcomes for
  each writing rubric criterion. Extracting criterion scores retrospectively from
  feedback would require a new interpretation, not a deterministic projection.
- Existing `ProgressAsync` and `TelegramKnowledgePresenter.Status` provide owned
  route/skill progress and sourced observations without claiming CEFR or causal
  learning improvement. Those reports remain available.
- The English diagnostic feature documentation explicitly distinguishes technical
  rubric review from expert calibration. No expert pilot results or approved
  comparable end-check dataset were found in the investigated feature/acceptance
  documents. The user has been asked for their source, if available.

Minimal alternatives considered: comparing raw diagnostic/lesson positive rates
would hide the different tasks and assistance; comparing only equal content IDs
would produce no baseline/end pair for the current authored content; parsing rubric
scores from free text would introduce unverifiable derived evidence. None confirms
the required comparable measurement. No historical evidence should be rewritten.

Before claiming completion, supply or validate the paired content and the rubric
outcome/compatibility contract, then add deterministic owner/version/assistance/
conflict/legacy tests and a report that labels insufficient data explicitly.
S11 additionally requires actual opt-in pilot observations and agreed metrics;
synthetic fixtures cannot substitute for them. Manual E1 release checks remain
deferred; that deferral is not evidence of comparability or completed pilot work.
