# Phase 10 case closure handoff

**Producer implemented locally.** Format 6 and the LocalDemo feed are available. The separate records intake command and its receipt-store verification are the next slice.

## Scenario

A Caseworker closes a fictional documentation case. A separate records clerk can run a local intake command later and record that the case arrived in the records queue. The Caseworker can finish while intake is stopped. After intake starts again, it can resume where it left off and demonstrate one durable receipt per closure. Both roles and all sample records are synthetic.

## Producer

- Emit one `case.closed.v1` notice when an actual `InReview → Closed` transition commits. Commit status, its existing history entry and the notice in the same transaction. A failed notice write fails the entire transition.
- Creation, creation replay, priority edits, `New → InReview`, repeated `Closed` requests and rejected transitions produce zero closure notices. If the close response is lost after commit, the existing same-state request returns the current case and the existing notice remains available.
- Give each source file a durable stream ID and allocate increasing positive positions for committed notices. Positions express commit order within a stream. A case ID identifies the case and the history sequence identifies the recorded transition. The timestamp comes from that same history entry. Equal or regressing clock values must still produce the correct stream order.
- A new or explicitly upgraded source starts with an empty stream. Existing Closed cases and their recorded histories remain readable. Future closures on retained New or InReview cases qualify after upgrade.

## Pull contract

Endpoint `GET /integrations/case-closures` is served through the existing `LocalDemo` loopback boundary. The first request may supply `limit` alone. Continuations require both `streamId` and `after`, with `after=0` allowed before the first notice. Each field can occur once. Use the current list bounds of default 20 and maximum 100. Require a canonical lowercase nonempty UUID for the stream and canonical nonnegative Int64 decimal for `after`. Read stream identity, validate a positive cursor against a retained notice, then query strictly after it in ascending position order within the same read transaction. Fetch at most `limit + 1` rows for `hasMore`.

A successful page exposes `streamId`, `items`, `nextAfter` and `hasMore`. A first page with a single available closure would return the following.

```json
{
  "streamId": "42ec2c52-3678-4f2c-9138-b67832462418",
  "items": [
    {
      "position": "1",
      "type": "case.closed.v1",
      "caseId": "57a9162f-4b3c-4516-859f-dc7b682833cd",
      "historySequence": 3,
      "closedAtUtc": "2026-11-05T18:00:00.0000000Z"
    }
  ],
  "nextAfter": "1",
  "hasMore": false
}
```

The last returned position is the next cursor. An empty page preserves the requested position or returns `"0"` on the initial read. `hasMore` describes the observed page and the same cursor can be used later to check for additional committed closures. Decimal strings preserve exact position values across JSON clients. The event payload consists of the five fields shown above.

Reuse safe, no-store problem responses. Invalid query shape returns `400 validation_failed` before storage access. A different stream returns `409 handoff_source_changed` and an unrecognized positive cursor returns `409 handoff_checkpoint_unknown`. Storage and encountered data failures use the existing generic server error. The OIDC host reserves this integration surface for a later service authorization decision and serves no closure feed route in this phase.

## Planned records intake

An operator explicitly initializes a separate receipt file and invokes a one-page pull. The pull uses an explicit numeric loopback HTTP origin, a fixed feed path, a five-second request deadline, a 64 KiB response limit and direct connections. A redirect is a failed pull. The caller supplies the origin and receipt-file path. The receipt store retains its source stream and last applied position. The first successful pull binds the stream even when the page is empty.

Validate the complete page before applying it, including version, field shapes, increasing positions, page bounds and cursor consistency. Within one receipt-store transaction, verify the expected checkpoint, insert the receipts and advance to the last fully applied position. The event identity is `(streamId, position)`. If a checkpoint has already moved, accept a fully applied nonempty page only when every receipt matches exactly, with the checkpoint left at its current position. A conflicting duplicate, inconsistent page, unexpected stream or other stale checkpoint fails without partial application. Recovery after a lost response or failed process uses the same durable receipt file to determine what committed. A new pull starts from the persisted checkpoint. An explicit inspection command reports the current checkpoint and receipt count so the operator can reconcile an uncertain outcome.

The receipt is the consumer's business effect for this slice. The delivery model permits a page to be fetched or processed again. The local receipt transaction ensures a single durable receipt per notice within the intact source and receipt files. Application transactions and retained-file restarts define the recovery boundary. File size grows with closures. Later phases can establish retention, backup restoration and authenticated machine access requirements.

Success output reports received and newly applied counts plus the resulting checkpoint. Failure output identifies the failed stage with a stable category and directs the operator to inspect or retry. Case content, credentials and raw response/error bodies stay outside this output. A successful empty read is distinguishable from an unavailable source.

## Format and verification

The producer uses EF Core Code First with a scaffolded migration and snapshot. Its offline lifecycle supports formats 1–5 as upgrade sources. Independent format-5 catalogs capture the fresh and inherited layouts. Real SQLite tests verify STRICT tables, the history foreign key, transaction rollback and retained case, priority, history and key values. The receipt file will use Code First with an explicit initializer and tracked receipt/checkpoint transaction. [ADR 0017](../adr/0017-use-a-transactional-closure-feed.md)

| ID | Acceptance evidence required for implementation |
| --- | --- |
| CH-01 | Closing a retained InReview case appends one notice linked to the exact existing history entry. Other mutations and retries append zero. Concurrent closes and lost status responses preserve a single notice. |
| CH-02 | Fail and interrupt status, history, notice and commit boundaries. The surviving source has either the prior case with no new notice or the fully committed close with its history and notice. |
| CH-03 | Real SQLite queries page by committed position under small/default/maximum limits. A newly closed case below a saved case ID boundary still arrives. Equal and regressing clocks preserve order and a restarted producer serves the same positions. |
| CH-04 | Fresh and empty streams, continuing at zero, page ends and later arrivals have distinct tested behavior. Invalid input, unknown checkpoints, switched streams and invalid retained rows fail safely. |
| CH-05 | Serve the feed only through `LocalDemo` loopback. Prove denied ingress and absent OIDC route for Reader and Caseworker. Prove bounded payload and safe failure output for the consumer, including redirect rejection. |
| CH-06 | With the consumer stopped, commit multiple closures. Restart it against the same receipt file and drain with one-page invocations. Verify exact receipts, checkpoint progression, identical redelivery and refusal of conflicting or foreign input. |
| CH-07 | Interrupt receipt writes and checkpoint updates before commit, then interrupt after commit before output. Reconcile by inspecting the sink and prove the next pull applies each closure once to that file. Test two attempts using the same checkpoint. |
| CH-08 | Exercise fresh initialization and explicit formats 1–5 adoption. An upgraded Closed case generates no notice, while a later close on an upgraded open case does. Startup refuses older format until offline upgrade and initialization refuses existing files. |
| CH-09 | Run existing case and browser regressions, EF snapshot agreement and offline hosting-envelope checks. Document a repeatable synthetic demo with two retained files and operator recovery steps. |

The producer evidence covers CH-01 through CH-04 and CH-08. CH-05 has LocalDemo ingress and OIDC route coverage. CH-09 has the producer regression gates and a one-file walkthrough. Consumer checks, the two-file walkthrough and CH-06/07 belong to the next slice. [Feed tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseClosureFeedTests.cs) · [Upgrade and process tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseUpgradeProcessTests.cs) · [Producer walkthrough](../usage/local-case-workflow.md#read-the-local-closure-feed)

## Evidence from the existing API

A Release build at `5e21a7b` ran on loopback against a disposable format-5 file on November 5, 2026. Three synthetic cases were created through HTTP and sorted by canonical ID as A, B and C. The experiment closed B and C, then read `GET /cases?status=Closed&limit=1`. The page returned B with B as `nextAfter`. After A closed, continuing with B returned only C and reached the end. A fresh filtered list returned all three cases, and `GET /cases/{A}/history` confirmed A's closure. Reproduce the sequence on a fresh disposable file using the creation and status commands. The CH-03 feed tests cover the same ID-order case using a stream position. [Local case walkthrough](../usage/local-case-workflow.md)
