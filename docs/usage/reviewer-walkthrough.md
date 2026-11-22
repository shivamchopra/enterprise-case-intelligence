# Review one case from creation to receipt

Follow one fictional case through the browser workspace, check the sources behind its closure brief, then verify that pulling the closure twice produces one durable receipt. This local demo uses two disposable SQLite files and runs without a cloud account. Use a browser with JavaScript, a POSIX-style shell and the stable .NET 10 SDK specified in [global.json](../../global.json).

## Start the local demo

If you’re starting outside the repository, clone it into a new directory.

```sh
git clone https://github.com/shivamchopra/enterprise-case-intelligence.git
cd enterprise-case-intelligence
```

Run the remaining commands at the repository root in the same terminal. Use an unused port on the numeric loopback address for `ECI_ORIGIN`.

```sh
dotnet restore EnterpriseCaseIntelligence.slnx
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release --no-restore

ECI_REVIEW_DIR="$(mktemp -d "${TMPDIR:-/tmp}/eci-review.XXXXXXXX")"
ECI_CASE_FILE="${ECI_REVIEW_DIR:?}/cases.db"
ECI_RECEIPT_FILE="$ECI_REVIEW_DIR/receipts.db"
ECI_ORIGIN='http://127.0.0.1:5080'
ECI_API='src/EnterpriseCaseIntelligence.Api/bin/Release/net10.0/EnterpriseCaseIntelligence.Api.dll'
intake() {
  dotnet tools/EnterpriseCaseIntelligence.RecordsIntake/bin/Release/net10.0/EnterpriseCaseIntelligence.RecordsIntake.dll \
    "$@" --receipt-file "$ECI_RECEIPT_FILE"
}

dotnet "$ECI_API" init-store --CaseStorage:DatabasePath "$ECI_CASE_FILE"
intake init-receipts
```

Once both initializers succeed, start the API against the new case file. Wait for this process to print `Now listening on` with your chosen origin. If the port is occupied, change `ECI_ORIGIN` and rerun just this block.

```sh
dotnet "$ECI_API" --CaseStorage:DatabasePath "$ECI_CASE_FILE" \
  --Security:Mode LocalDemo --urls "$ECI_ORIGIN" &
ECI_API_PID=$!
```

## Close a case and check its brief

Open `/workspace` at the listening address in your browser. With the example port, open [the local workspace](http://127.0.0.1:5080/workspace).

1. Choose **+ New case**. Enter `Correct the sample guide` for **Title** and `Update the fictional onboarding example.` for **Description**. Choose **Create case**. The workspace opens the saved case with status **New** and priority **Normal**.
2. Choose **High** under **Change priority**, then **Save priority**. Check that **Current priority** reads **High**. Choose **Start review**, wait for **In review**, then choose **Close case**. Check that **Recorded history** shows creation and both status changes.
3. Choose **View closure brief**. Confirm the current status is **Closed** and the previous status is **In review**. Follow **Case status** to the case details and **History entry 3** to the closing transition. Compare its UTC timestamp with **Recorded closure time** in the brief.

## Verify one durable receipt

Return to the same terminal. The API remains running while these commands reuse the receipt file initialized above.

```sh
intake pull --origin "$ECI_ORIGIN"
intake pull --origin "$ECI_ORIGIN"
intake inspect-receipts
```

| Command | Expected result |
| --- | --- |
| First pull | `received` and `applied` are both `1`. The checkpoint `after` is `"1"` and `hasMore` is `false`. |
| Second pull | `received` and `applied` are both `0`. The same `streamId` and `after` show where intake resumes. |
| Inspect | `receipts` is `1`, with the same `streamId` and `after` of `"1"`. |

Stop the API and wait for it to release the case file. To discard this review, remove the two disposable files and their private directory after shutdown.

```sh
kill "$ECI_API_PID"
wait "$ECI_API_PID"
rm -i "$ECI_CASE_FILE" "$ECI_RECEIPT_FILE"
rmdir "$ECI_REVIEW_DIR"
```

For a failed pull or a checkpoint mismatch, use the [intake recovery guide](local-records-intake.md#read-results-and-recover) before removing the files.
