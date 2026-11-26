# Five-client review rehearsal

The local rehearsal models five independent synthetic review sessions against one authenticated application host. Three Caseworker sessions perform writes, and two Reader sessions perform reads and attempted writes. Each session has its own protected test ticket and CSRF proof.

## Covered scenario

The test creates one synthetic case with a retained creation key, then starts five operations together. One Caseworker replays the key, another changes priority, both Readers inspect the case and history, and a Reader write is rejected. Two Caseworkers then contend on the same InReview transition, followed by two Caseworkers contending on Closed. Reader reads continue during both phases, and Reader writes remain denied.

The assertions verify the original creation result, High priority, one history entry for each real transition, a Closed final state, a cited closure brief and one retained creation-key binding. Same-status retries do not add duplicate history. The closure feed and receipt file remain in the separate LocalDemo walkthrough because the authenticated OIDC host deliberately does not expose that integration route.

## Run the rehearsal

From the repository root, use the pinned .NET SDK and run:

```sh
dotnet test tests/EnterpriseCaseIntelligence.Api.Tests \
  --configuration Release \
  --filter 'FullyQualifiedName~FiveClientReviewTests'
```

The test uses a disposable SQLite file, a generated certificate and a test-only OIDC provider. It does not call Microsoft Entra, require a tenant, create cloud resources or use real records. The complete non-browser suite includes this test. The browser workflow remains a separate CI job.

## Acceptance boundary

This rehearsal proves the local authorization, retry, priority, status and history behavior under five-client contention. It does not establish a published-host workload, real Entra propagation, public ingress, provider storage behavior, hosted diagnostics or recovery targets. The remaining private identity and published-build checks stay in the roadmap and must use redacted evidence.

[Hosted acceptance criteria](../requirements/phase-8-secure-hosting.md) · [Private identity checklist](../usage/entra-review-verification.md) · [Test implementation](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/FiveClientReviewTests.cs)
