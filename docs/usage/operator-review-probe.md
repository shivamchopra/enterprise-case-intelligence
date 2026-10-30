# Independent operator review probe

The [standalone .NET 10 probe](../../tools/EnterpriseCaseIntelligence.ReviewProbe) is implemented and tested locally. It gathers private transport/header observations and proves old-receipt retention with the same certificate loader and framework key-provider factory as the case application. It has no API/Domain project reference, case database, OIDC registration, reviewer login or case permission. [ADR 0014](../adr/0014-isolate-managed-host-verification-in-an-operator-probe.md) records that boundary.

**Cloud compatibility, eligibility, backend isolation and secure deployment remain unverified.** Do not deploy the case API or interpret a probe result as trusted ingress. The [managed-ingress gates](../tasks/managed-ingress-verification.md) and [$0 cost boundary](../tasks/zero-cost-review-hosting.md) still apply before Azure validation/creation review. No cloud actions run in these commands or CI.

The [2026-10-29 deployment-path assessment](../tasks/free-tier-deployment-preflight.md) found storage-isolation and first-initialization gaps before creation review. [ADR 0015](../adr/0015-prepare-probe-storage-before-explicit-initialization.md) now implements a locally tested preparation/initialization sequence. Actual cloud storage, command delivery and bootstrap acceptance remain unverified; permission and normal-startup missing-state checks remain enforced.

## Private preparation

Use a new, independent disposable RSA protection certificate, environment/revision, random credential and key directory. Never reuse the existing local-review certificate, Entra credentials, session keys or case store. Keep configuration, key files, receipts and observations outside Git, build/publish directories and synchronized/shared folders. Restrict their containing directories to the operator/runtime; on Unix use directory mode `0700`, configuration-file mode `0600`, and equivalent private ACLs on Windows. The tool checks Unix file/directory mode bits, not ownership, Windows ACLs, ancestor permissions, backup access or cloud isolation; the operator must verify those separately.

Supply one private JSON configuration file through the **path-only** `ECI_PROBE_CONFIGURATION` environment variable, or inject equivalent environment settings (`:` becomes `__`). Environment values override file values; use a clean dedicated process environment to avoid stale overrides. The JSON file is limited to 96 KiB and loaded once without reload. No configuration, URL or credential command arguments are supported. Do not print the file or use CLI debug/request tracing. Secrets remain in immutable configuration/process memory; the probe does not promise to erase every copy.

| Setting | Required value |
| --- | --- |
| `Probe:Enabled` | Literal `true`, explicit operator opt-in |
| `Probe:PublicOrigin` | Exact HTTPS origin, including a non-default port; no user information, path, query or fragment. For a future cloud probe, obtain the actual hostname privately from the control plane |
| `Probe:OperatorCredential` | Lowercase hex of **32 cryptographically random bytes**, independently generated and kept private. Format checking cannot prove entropy; do not use repeated/example characters |
| `Probe:EnvironmentId`, `Probe:Revision` | Independent 1–80 character ASCII letter/digit/hyphen/underscore identifiers; preserve for retained-receipt tests |
| `Probe:KeyDirectory` | Absolute path outside the artifact/content root. Preparation can report a missing directory; `init-keys` requires an existing private **empty** directory and `serve` requires initialized retained state |
| `Security:CertificateSource` | Literal `Configuration` for initialization/serving/key verification; certificate settings can be omitted during initial preparation metadata checks |
| `Security:CertificatePfx`, `Security:CertificatePassword` | Independent encrypted RSA PFX as single-line Base64 and its nonblank private password; [existing bounds/import rules](authenticated-case-workspace.md#protection-certificate-sources) apply. Omit `Security:CertificatePath` entirely |
| `urls` (or `ASPNETCORE_URLS`) | One explicit HTTP/HTTPS binding, with a nonzero port. Use a loopback binding for local fixtures. A future managed-host HTTP backend is observation-only, never an asserted trusted origin |
| `Kestrel:Certificates:Default:Path`, `...:Password` | Separate TLS server certificate when the probe itself serves HTTPS; ordinary client trust is required |

Generate the operator credential with a cryptographic generator, for example .NET's `RandomNumberGenerator.GetBytes(32)` encoded with `Convert.ToHexStringLower`, writing it directly into restricted configuration. Export the independently generated protection certificate with a strong random password through a private certificate tool; do not put password values in command arguments. The runtime imports PFX bytes directly without writing a PFX file.

All automatic forwarding flags must be absent/false. Hosting-startup assembly injection and configured Kestrel endpoint collections are refused. The probe never installs forwarding middleware, learns a trusted peer from requests or processes forwarded identity. Keep one writer per key directory; an exclusive `.probe.lock` handle covers initialization and the entire `serve` lifetime. Preparation holds an existing lease only during a key-verification request, allowing the separate initializer to run while it is idle. A retained lock file is expected and must not be deleted to force a second writer.

## Build and run

Publish the independent executable to a new artifact directory:

```sh
dotnet publish tools/EnterpriseCaseIntelligence.ReviewProbe --configuration Release --output /absolute/path/to/probe-artifact
```

Prepare the private configuration and empty retained-key directory first. Run from a separate content directory, so neither it nor the artifact contains the key directory. With `ECI_PROBE_CONFIGURATION` supplied privately:

```sh
# Once only, on an existing EMPTY directory. Does not start HTTP.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll init-keys
# Subsequent starts require the initialized retained directory.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll serve
```

Initialization refuses retained entries; startup refuses missing, empty, malformed, overlarge or incompatible key state rather than resetting it. The probe accepts at most 100 retained key files, each at most 64 KiB. Encrypted framework keys can still roll over normally within that single-process boundary. Startup verifies file/key count and every retained descriptor before a framework protection round trip. Wrong passwords or a different valid certificate cannot silently replace retained keys. Do not delete/reinitialize state to repair a failure; restore the original private configuration and retained files.

The server emits no request, key-provider or startup-detail logs; failures print a generic message without the raw exception, path or private value. Exit codes are `0` for a completed operation/normal shutdown, `1` for unavailable/incompatible configuration, state or evidence, and `2` for invalid arguments. Runtime denials have an empty body and no redirect/cookie; successful JSON and denials are no-store. An invalid but syntactically valid receipt returns `retainedReceiptValid: false` in a successful verification response, so the operator must inspect that field.

## Explicit preparation and initialization

Use this phase when the operator command channel needs a running diagnostic process before keys exist. `prepare` is explicitly selected and never initializes keys. It requires the normal private `Probe` settings and binding, but can start without a directory or protection certificate. It serves only authenticated `GET /probe/setup`; normal observation/verification and all mutation routes are absent. The case application is not involved.

With private configuration already supplied, run these commands in separate operator sessions as indicated:

```sh
# First session: runs the preparation-only diagnostic host.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll prepare
# Operator client: HTTPS only; save its private JSON response outside Git.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll setup-status
# Separate command in the intended runtime/filesystem view, once a private empty
# directory is prepared and Configuration-source certificate settings are supplied.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll init-keys
# Independently check retained state; command submission/acceptance is not proof.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll setup-status
```

Preparation does not create a directory or lease file and rejects release/content-root paths before hosting. A missing/unsafe path returns a bounded state without reading key contents or importing a certificate. The Unix mode flag checks group/other bits only; Windows returns `null`. Both `deploymentReady` and `platformIsolationVerified` are always false, including after a successful key check. Actual owner/ancestor/ACL, app/SCM isolation, storage persistence and cross-process lease behavior on the chosen host still need independent evidence. Do not deliver protection material or initialize an unsafe path; do not replace a failed check with `chmod` retries or an implicit managed-filesystem exception.

| `state` | Meaning and operator action |
| --- | --- |
| `missing_directory` | The configured directory was not observed. This is not proof of a healthy mount or permission to create one. Inspection did not create it; establish the expected storage/access/isolation contract first. |
| `unsafe_directory` | A directory link or group/other mode bits were found. No retained-key inspection or initialization is allowed. |
| `not_initialized` | No retained entries were observed; inspection did not initialize anything. Use the separate `init-keys` command only on a deliberately prepared private empty directory. |
| `unavailable` | Credentials, retained state, lease or filesystem access could not support verification. Inspect/recover privately; never delete/reinitialize state to turn this green. |
| `keys_verified` | Every retained descriptor decrypted and an existing-key protection round trip succeeded while an exclusive existing lease was held, with automatic key generation disabled. This is current-state reconciliation, not a remote command exit-code claim or cloud approval. |

Only `keys_verified` includes `retainedKeysVerified: true`, a bounded key count, aggregate `keyRingFingerprint` and opaque `receipt`. Other states include no successful key proof. A successful HTTP fetch/client exit `0` can still report a missing/unsafe/unavailable state; inspect the fields. Inspection opens only the initializer's existing lease file and refuses absent/incompatible state without generating, rolling over, repairing or resetting keys. Busy inspection/initialization can return unavailable; do not automatically retry a write or infer failure from an uncertain command acknowledgement.

Keep the fingerprint and receipt private. Stop the preparation host, select the explicit `serve` command with the same certificate/environment/revision/key directory, then verify the **preparation receipt** using the normal `verify` client. Repeat after artifact replacement. A fresh round trip or mere key-file presence is insufficient evidence. File fingerprints help detect changes; expected future framework rollover can add keys while old retained receipts remain valid. No file-hash match substitutes for cryptographic receipt verification.

The local tests use real child processes and a copied artifact to prove this sequence. No Azure startup setting, app setting, command channel or resource was changed. A future proposal must select `prepare` first, an authorized operator channel for directory preparation/explicit initialization, independently confirmed key state and a controlled restart to `serve`. The [deployment assessment](../tasks/free-tier-deployment-preflight.md) still requires exact free allocation/policy and owner creation approval. If the actual storage contract fails, remain local.

## Observe and verify privately

The operator client needs only the private `Probe:PublicOrigin` and `Probe:OperatorCredential` settings; it does not require the server's certificate password/key directory. It always uses ordinary TLS validation, refuses redirects including same-origin redirects, retains no cookies, bounds responses to 32 KiB and has a 15-second network deadline. Never work around an untrusted certificate with a validation bypass.

```sh
# Redirect output to an owner-only file in a restricted evidence directory.
umask 077
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll observe > /absolute/private/evidence/observation.json
# Supply only the previously extracted opaque receipt through stdin, never a URL/argument.
dotnet /absolute/path/to/probe-artifact/EnterpriseCaseIntelligence.ReviewProbe.dll verify < /absolute/private/evidence/receipt.txt
```

`GET /probe` requires exactly one `Authorization: Bearer <credential>` header, checked with a bounded constant-time comparison. It returns the **unmodified** scheme/transport peer, a Host-match boolean, fixed header names with bounded cardinality/classification, and an opaque purpose-bound receipt. It never returns raw Host, forwarding or identity header values, PFX/password, configuration or key contents. Peer addresses and receipts themselves remain private. Every report contains `deploymentReady: false`.

The probe intentionally observes an authenticated HTTP backend or mismatched Host without declaring it trusted. A forged `X-Forwarded-Proto: https` stays an observation while original HTTP remains HTTP. Identity headers confer no access. Establish the real edge/backend fence using the [platform acceptance matrix](../tasks/managed-ingress-verification.md#evidence-and-decision-gates); this diagnostic host is not an application ingress mode. Do not expose a direct HTTP listener to operators: the client sends its credential only over HTTPS. Local HTTP tests use disposable credentials on loopback exclusively.

`POST /probe/verify` takes only an opaque Base64url receipt in a `text/plain` body, at most 2,048 bytes on the server. It returns whether the old receipt can still be unprotected under the same environment/revision. Query strings, browser `Origin`, content encodings and malformed input are rejected. Other routes return 404 after authentication; unauthenticated requests to all paths return 401. Cookies, client-principal headers and browser preflight cannot authorize requests. Header transport limits are 8 KiB/32 headers, with a 10-second header timeout; header cardinality `9` means nine or more.

Record the first receipt and private hashes/count of encrypted key files. Stop the sole process, restart with identical configuration and verify **that old receipt**. Repeat from a replacement artifact directory with the retained directory/configuration unchanged. Locally, tests copy the same compiled artifact to a new release path and restart real child processes; this proves local path independence, not Azure deployment/tooling behavior or cross-version upgrade compatibility. Deliberately changing the environment/revision rejects the old receipt without rewriting keys; restoring the original values allows it again. Wrong password/certificate, corrupt or missing state must fail startup without replacing keys. Run such failures only against disposable probe state.

Actual F1 private credential delivery, permissions, persistence across cloud restart/release, peer behavior, cost eligibility and Entra/RBAC artifact deployment are still pending. The [resource envelope](../../infra/review-probe/README.md) remains undeployed; do not reapply its initial app-settings collection over private configuration.
