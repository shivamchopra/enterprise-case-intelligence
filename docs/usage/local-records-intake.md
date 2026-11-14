# Run the local records intake

The intake command pulls one page of synthetic case closures from LocalDemo into a separate SQLite file. Keep both files on an ordinary local filesystem. The case store keeps the producer's history and notices. The receipt store keeps the applied notices and its checkpoint. The walkthrough requires .NET 10, curl and Python 3 for extracting the synthetic case IDs.

## Start with two disposable files

Build from the repository root. Choose an unused loopback port and start the API. Wait for it to listen before running the intake commands.

```sh
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release
ECI_DEMO_DIR="$(mktemp -d)"
ECI_CASE_FILE="$ECI_DEMO_DIR/cases.db"
ECI_RECEIPT_FILE="$ECI_DEMO_DIR/receipts.db"
ECI_ORIGIN='http://127.0.0.1:5080'

dotnet src/EnterpriseCaseIntelligence.Api/bin/Release/net10.0/EnterpriseCaseIntelligence.Api.dll \
  init-store --CaseStorage:DatabasePath "$ECI_CASE_FILE"
dotnet src/EnterpriseCaseIntelligence.Api/bin/Release/net10.0/EnterpriseCaseIntelligence.Api.dll \
  --CaseStorage:DatabasePath "$ECI_CASE_FILE" --Security:Mode LocalDemo --urls "$ECI_ORIGIN" &
ECI_API_PID=$!
```

Use the intake executable to initialize the new receipt file and make the first pull. A successful empty pull binds the stream and reports zero received and applied receipts at position `"0"`.

```sh
intake() {
  dotnet tools/EnterpriseCaseIntelligence.RecordsIntake/bin/Release/net10.0/EnterpriseCaseIntelligence.RecordsIntake.dll \
    "$@" --receipt-file "$ECI_RECEIPT_FILE"
}
intake init-receipts
intake pull --origin "$ECI_ORIGIN"
```

## Close cases and resume intake

Close two synthetic cases while there is no intake process running. Each command invocation exits after one page.

```sh
close_synthetic_case() {
  ECI_CASE_ID="$(
    curl --fail --silent --show-error "$ECI_ORIGIN/cases" \
      --header 'Content-Type: application/json' --data "$1" |
    python3 -c 'import json, sys; print(json.load(sys.stdin)["id"])'
  )" || return
  curl --fail --silent --show-error --output /dev/null -X PUT "$ECI_ORIGIN/cases/$ECI_CASE_ID/status" \
    --header 'Content-Type: application/json' --data '{"status":"InReview"}' &&
  curl --fail --silent --show-error --output /dev/null -X PUT "$ECI_ORIGIN/cases/$ECI_CASE_ID/status" \
    --header 'Content-Type: application/json' --data '{"status":"Closed"}'
}

close_synthetic_case '{"title":"Review the fictional handbook","description":"Synthetic documentation example."}'
close_synthetic_case '{"title":"Review the fictional appendix","description":"Synthetic records example."}'

intake pull --origin "$ECI_ORIGIN" --limit 1
intake pull --origin "$ECI_ORIGIN" --limit 1
intake pull --origin "$ECI_ORIGIN"
intake inspect-receipts
```

The first two pulls apply one receipt each and advance to positions `"1"` and `"2"`. The first reports `hasMore: true`, and the second reports `hasMore: false`. The final pull is empty and inspection reports two receipts at position `"2"` on the same stream. A later pull can pick up future closures from that checkpoint. Stop the demo API with `kill "$ECI_API_PID"` and retain both files if you want to repeat the check after restart.

## Read results and recover

The command accepts a canonical numeric loopback HTTP origin with an explicit port, such as `http://127.0.0.1:5080` or `http://[::1]:5080`. It calls the fixed feed path directly with a five-second deadline and a 64 KiB body limit. The default page limit is 20 and the maximum is 100. `hasMore` describes the fetched page. A successful retry of a page applied by another attempt reports zero applied and the current durable position.

Success prints one JSON line on standard output. Failures print a stage, category and operator action on standard error. Invalid arguments exit with status 2. Operation failures exit with status 1.

| Failure or uncertain outcome | Operator action |
| --- | --- |
| Interrupted pull or missing success output | Run `intake inspect-receipts` first. The retained receipt count and position show the last committed page. Then pull again with the same receipt file and source. |
| `source_unavailable` or `source_timeout` | Verify the original LocalDemo process is listening on the supplied loopback port. Pull again when it is available. |
| `source_response` or `invalid_page` | Verify the configured origin serves the closure feed. The receipt transaction starts only after page validation. |
| `source_conflict` | Check that the original case file is open and the retained checkpoint still exists on its stream. |
| `checkpoint_conflict` or `receipt_store` | Inspect the retained file before retrying. If inspection fails, check its path, permissions and integrity while preserving the file and its journal. |

For a restore, use a matched case and receipt snapshot from the same stopped copy point. A later checkpoint can refer to different evidence after restoring an older source. [Local restore rehearsal](local-restore-rehearsal.md).

Receipt initialization reserves a new path exclusively. Keep an existing receipt file for resume, and use a new receipt path if you deliberately start intake for a different case stream. [Phase 10 contract](../requirements/phase-10-case-closure-handoff.md) · [ADR 0017](../adr/0017-use-a-transactional-closure-feed.md)
