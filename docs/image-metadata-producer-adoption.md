# Offline File image-plan metadata producer adoption

The File identity-plan consumer adopts protected Workflows main
`0159e67a033712a7120d52173819e8bde214cf6e` and the exact canonical-LF producer
SHA-256 `4f09e547e58569847479fc5d94925cd3b32cde6693c7cc05c0ad21557a8a1d4e`.
The workflow dependency checkout and its structural regression checks use the
same immutable revision. The consumer still rejects unavailable or changed
producer bytes before invoking its recording IAM reader or image builder.

The accepted producer checks optional HTTP, TCP and gRPC probe shapes and known
single-key `valueFrom` objects on the selected existing container. It rejects
exec probes, HTTP headers, unknown/multiple handlers, missing ports, scalar
metadata and unknown/multiple reference forms. It preserves arbitrary literal
environment values, singleton arrays and all non-image settings. This is a
bounded metadata admission/copy contract: it does not certify reference payloads,
port ranges, Kubernetes validity, reference existence or runtime health. Probes
remain optional for the existing `legacy-file-image-only/v1` contract.

Nineteen new consumer scenarios retain all 56 existing identity, IAM, source and
producer-drift scenarios. Positive cases use the actual accepted producer through
`New-LegacyFileIdentityPlan`, controlled recording callbacks and isolated Git
fixtures. They verify input immutability and exact resource ordering/settings
except the one selected image. The native C# scenario also parses the actual
dormant `deploy/disabled/file-deployment.template.yaml` with the existing
YamlDotNet dependency. Its replicas remain zero and pod token mounting remains
disabled. The template itself is not edited.

New malformed metadata is rejected at the existing image-plan transformation
boundary, after the recording IAM and controlled image callbacks. Tests require
that exact two-callback order and opaque errors; they do not claim early metadata
rejection before image construction. Existing identity/source/producer failures
still require zero callbacks, and IAM failures retain their earlier stop point.

No default cloud/image adapter, deployment operation, live IAM/bucket acceptance,
provider action or resource mutation is introduced. Returned plans continue to
have deploymentAllowed=false and liveAccepted=false. The new native scenario
inventory is 75; the expected full suite is 857, derived from accepted 838 plus
19 new cases. These counts require actual hosted execution before acceptance.

The paired source bridge pin is canonical-LF SHA-256
`06e0d8710b4de9b9e2b59ba1cf1dc013547d15848827674d5d5b30688297e85c`.
The Python source guard module and image-plan producer remain unchanged.

The subsequent token-intent correction requires the unchanged real dormant
template to fail identity admission before callbacks because its Pod token
mounting is disabled. Metadata admission/copy guards remain intact for eligible
identity plans. See [token intent and ordered recording](token-intent-and-resource-recording.md)
for the current 83 identity-plan cases and separate recording boundary; the 75/857
inventory above describes the accepted File67 adoption baseline.
