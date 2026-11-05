# Historical Inbox evaluation scripts

These Python 3 scripts preserve the construction of the versioned synthetic datasets and the published
2026-10-25 report. They use only the standard library and make no provider calls. Normal evaluation uses the
[.NET runner](../../../docs/features/inbox/classification-and-canonicalization.md).

## Rebuild historical files

Run from the repository root in a disposable checkout: these scripts write the versioned files directly.
Compare the generated files with the originals before accepting changes to an existing version.

```powershell
python tools/OwnDay.InboxEvaluation/scripts/build_golden.py
python tools/OwnDay.InboxEvaluation/scripts/refine_golden.py
python tools/OwnDay.InboxEvaluation/scripts/archive_evaluation.py
```

- `build_golden.py` constructs `evaluation/inbox/golden-v1.json` from authored synthetic scenarios.
- `refine_golden.py` derives `golden-v2.json` from v1 with the three documented scenario corrections.
- `archive_evaluation.py` assembles `evaluation/inbox/reports/2026-10-25.json` from existing local run data.
  It requires `live-baseline-v1`, `live-candidate-v2`, `live-candidate-v3`, `baseline-v1-vs-v3.json` and
  `v2-vs-v3.json` under `evaluation-results/`.

## Restore local run artifacts

The repository cleanup preserved the full local evaluation results in
`artifacts/inbox-evaluation/2026-10-25.zip`, verifying every file against the archive before removing the
uncompressed copies. This archive is local and ignored by Git; it is not distributed with a fresh checkout.
It contains raw responses, manifests, review packets and comparisons, including the unsuccessful transport runs.

Restore into a new or empty `evaluation-results/` directory from the repository root:

```powershell
Expand-Archive -LiteralPath artifacts/inbox-evaluation/2026-10-25.zip -DestinationPath evaluation-results
```

The published Markdown and JSON reports remain versioned beside each other under `evaluation/inbox/reports/`.
