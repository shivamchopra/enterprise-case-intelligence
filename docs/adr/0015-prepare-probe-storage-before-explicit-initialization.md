# 0015: Prepare probe storage before explicit initialization

- Status: Accepted — implemented/tested locally; platform storage and deployment acceptance pending
- Date: 2026-10-30

## Context

The [deployment-path assessment](../tasks/free-tier-deployment-preflight.md) found a circular dependency: the documented container command path needs a running app, while the normal diagnostic host requires initialized keys. It also found that presumed platform storage does not establish the probe's directory-permission or locking contract. [ADR 0014](0014-isolate-managed-host-verification-in-an-operator-probe.md)'s independent operator tool remains the boundary; the case API must not become a bootstrap interface.

## Decision

Add the explicit `prepare` command to the independent probe. It hosts only authenticated `GET /probe/setup`, with the same bounded bearer check, no-store responses and browser/query denial rules as the normal diagnostic phase. It can run before the directory or protection certificate exists. It has no HTTP initialization, file-upload, arbitrary-path, configuration-dump or case endpoint. The operator's configured absolute path must stay outside the artifact/content root.

Retain the current storage contract: no directory symlink; Unix group/other permission bits refused; external ownership/ancestor/Windows ACL and platform app/SCM isolation review still required. No environment marker, shared mount or mode value proves platform isolation. A missing or unsafe directory is reported without creating paths/lease files, enumerating key contents or importing the protection certificate. Unsafe metadata is a stop condition, not a reason to relax the check.

The operator prepares a private empty directory and invokes the existing explicit `init-keys` command in the intended runtime/filesystem context. Preparation does not hold a writer lease while idle, allowing that separate command to run. Initialization retains its exclusive lease and no-overwrite behavior. Normal `serve` still refuses absent/uninitialized state; no application startup migration or key-reset fallback is added.

An authorized setup read can validate existing state using the same framework certificate/provider logic, with **automatic key generation disabled** and only an existing lease file opened exclusively. It checks bounded retained files, file/key count, every descriptor and a protection round trip. Only successful validation returns the key count, an aggregate SHA-256 fingerprint of sorted key filenames/file hashes and an opaque receipt under the normal diagnostic receipt purpose. No key contents or paths are returned. [Framework read-only key configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0#disable-automatic-key-generation-disableautomatickeygeneration).

This reconciles actual retained state after an uncertain command acknowledgement; it does not assert that a particular remote command exited successfully. Missing credentials, incompatible/corrupt state, an unavailable lease or unsupported filesystem produce no successful key proof. Inspection does not initialize, reset, roll over or repair keys. The operator stops preparation before selecting normal `serve`, preserving environment/revision, certificate and keys, then verifies the **preparation receipt** after promotion/replacement.

## Consequences and evidence

`setup-status` adds a fixed-route HTTPS operator-client command using existing ordinary TLS, redirect/cookie refusal, bounds and deadline. All reports retain `deploymentReady: false` and `platformIsolationVerified: false`; local key verification is not platform/eligibility approval. The API's shared provider factory defaults preserve normal key generation and its existing discriminator/purposes; only the setup inspector requests read-only behavior.

Disposable child-process tests cover preparation with missing credentials/directory, unchanged empty state, explicit initialization while preparation runs, read-only receipt/fingerprint checks, promotion from a copied artifact, competing access, invalid state/certificate refusal, future-activation keys, unsafe mode bits, symlinks, release-path refusal and authentication before inspection. The [runbook](../usage/operator-review-probe.md#explicit-preparation-and-initialization) defines the operator sequence and evidence limits.

A preparation-only deployment could now assess metadata without persisting keys, but no cloud resource or managed-filesystem exception is authorized by this decision. Exact free-tier allocation/policy, private credential delivery, actual directory isolation/lease behavior, ingress and cloud restart/replacement still require the [hosting gates](../tasks/managed-ingress-verification.md). If the actual storage contract fails, remain local. Any different managed isolation model needs a separate justified amendment; there is no implicit paid alternative.
